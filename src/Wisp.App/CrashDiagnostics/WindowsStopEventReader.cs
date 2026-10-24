using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Wisp.App.CrashDiagnostics;

internal interface IWindowsStopEventSource
{
    bool StoppedUnexpectedly(DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken);
    // Windows shut down or signed the user out shortly after the run was last seen, while Wisp was still running.
    bool ShutDownNormally(DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken) => false;
}

// At the first boot after Windows stopped without shutting down (a power loss, a hard reset or a system crash),
// it logs Kernel-Power 41 and EventLog 6008. When that boot closely follows the last checkpoint of a run,
// Windows ended the run while Wisp was still running.
internal sealed class WindowsStopEventReader : IWindowsStopEventSource
{
    // Wisp checkpoints every 10 s, and a crashed or reset PC is back within a few minutes. A longer gap leaves
    // room for the run to have ended on its own before Windows stopped.
    internal static readonly TimeSpan MaximumBootDelay = TimeSpan.FromMinutes(10);
    // Both unclean-stop events are written early in the boot that follows the stop.
    internal static readonly TimeSpan MaximumEventDelay = TimeSpan.FromMinutes(5);
    internal const int MaximumEvents = 64, MaximumXmlCharacters = 16384;
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(10);
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    // A shutdown that ends a running Wisp follows its last checkpoint (every 10 s) within the time Windows
    // gives applications to close. A wider window would turn a crash followed by a reboot into a shutdown.
    internal static readonly TimeSpan MaximumShutdownDelay = TimeSpan.FromSeconds(90);

    public bool StoppedUnexpectedly(DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = lastSeenUtc + MaximumBootDelay + MaximumEventDelay;
        var events = ReadEvents(Query(lastSeenUtc, latest < nextRunStartedAtUtc ? latest : nextRunStartedAtUtc,
            "(EventID=12 and Provider[@Name='Microsoft-Windows-Kernel-General']) or " +
            "(EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power']) or " +
            "(EventID=6008 and Provider[@Name='EventLog'])"), cancellationToken);
        // Events are read oldest first, so a lookup cut short can miss the unclean-stop record but never invent one.
        cancellationToken.ThrowIfCancellationRequested();
        return Correlate(events, lastSeenUtc, nextRunStartedAtUtc);
    }

    // EventLog 6006 marks a normal shutdown; User32 1074 records the process that started a shutdown or restart.
    public bool ShutDownNormally(DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = lastSeenUtc + MaximumShutdownDelay;
        var events = ReadEvents(Query(lastSeenUtc, latest < nextRunStartedAtUtc ? latest : nextRunStartedAtUtc,
            "(EventID=6006 and Provider[@Name='EventLog']) or (EventID=1074 and Provider[@Name='User32'])"), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CorrelateShutdown(events, lastSeenUtc, nextRunStartedAtUtc);
    }

    private static string Query(DateTimeOffset fromUtc, DateTimeOffset untilUtc, string selection)
    {
        var from = fromUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var until = untilUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return $"*[System[TimeCreated[@SystemTime >= '{from}' and @SystemTime <= '{until}'] and ({selection})]]";
    }

    private static List<string> ReadEvents(string query, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupBudget);
        var events = new List<string>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            using var registration = deadline.Token.Register(() =>
            {
                try { reader.CancelReading(); }
                catch (Exception) { }
            });
            while (events.Count < MaximumEvents && !deadline.IsCancellationRequested)
            {
                using var record = reader.ReadEvent(LookupBudget);
                if (record is null) break;
                var xml = record.ToXml();
                events.Add(xml.Length <= MaximumXmlCharacters ? xml : string.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or UnauthorizedAccessException or EventLogException or
            IOException or InvalidOperationException or System.Security.SecurityException)
        { }
        return events;
    }

    private static (string? Provider, string? Id, DateTimeOffset Time)? Parse(string xml, DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc)
    {
        if (xml.Length is 0 or > MaximumXmlCharacters) return null;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumXmlCharacters });
            var root = XDocument.Load(reader).Root;
            var system = root?.Element(Namespace + "System");
            if (root?.Name != Namespace + "Event" || system is null ||
                !DateTimeOffset.TryParse((string?)system.Element(Namespace + "TimeCreated")?.Attribute("SystemTime"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ||
                time < lastSeenUtc || time > nextRunStartedAtUtc) return null;
            return ((string?)system.Element(Namespace + "Provider")?.Attribute("Name"), (string?)system.Element(Namespace + "EventID"), time);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static bool CorrelateShutdown(IEnumerable<string> events, DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc)
    {
        foreach (var xml in events.Take(MaximumEvents))
        {
            if (Parse(xml, lastSeenUtc, nextRunStartedAtUtc) is not { } record) continue;
            if ((record.Provider == "EventLog" && record.Id == "6006" || record.Provider == "User32" && record.Id == "1074") &&
                record.Time - lastSeenUtc <= MaximumShutdownDelay) return true;
        }
        return false;
    }

    internal static bool Correlate(IEnumerable<string> events, DateTimeOffset lastSeenUtc, DateTimeOffset nextRunStartedAtUtc)
    {
        var boots = new List<DateTimeOffset>();
        var unclean = new List<DateTimeOffset>();
        foreach (var xml in events.Take(MaximumEvents))
        {
            if (Parse(xml, lastSeenUtc, nextRunStartedAtUtc) is not { } record) continue;
            if (record.Provider == "Microsoft-Windows-Kernel-General" && record.Id == "12") boots.Add(record.Time);
            else if (record.Provider == "Microsoft-Windows-Kernel-Power" && record.Id == "41" || record.Provider == "EventLog" && record.Id == "6008") unclean.Add(record.Time);
        }
        if (boots.Count == 0) return false;
        var boot = boots.Min();
        if (boot - lastSeenUtc > MaximumBootDelay) return false;
        var nextBoot = boots.Where(time => time > boot).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        return unclean.Any(time => time >= boot && time < nextBoot && time - boot <= MaximumEventDelay);
    }
}
