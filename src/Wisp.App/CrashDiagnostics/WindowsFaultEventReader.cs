using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Wisp.App.CrashDiagnostics;

internal enum WindowsFaultLookupStatus { NoMatchingEvent, Matched, AccessDenied, TimedOut, ReadFailed, ScanLimitReached }
internal sealed record WindowsFaultEvidence(DateTimeOffset TimeUtc, uint ExceptionCode, ulong? FaultOffset,
    string? Module, bool CorrelatedWerReport);
internal sealed record WindowsFaultLookup(WindowsFaultLookupStatus Status, WindowsFaultEvidence? Fault = null);
internal sealed record RecoveredExitInfo(DateTimeOffset RunStartedAtUtc, DateTimeOffset DetectedAtUtc,
    WindowsFaultLookupStatus LookupStatus, WindowsFaultEvidence? Fault)
{
    internal RecoveredExitInfo? Sanitize()
    {
        if (RunStartedAtUtc.Offset != TimeSpan.Zero || RunStartedAtUtc.Ticks <= 0 ||
            DetectedAtUtc.Offset != TimeSpan.Zero || DetectedAtUtc < RunStartedAtUtc || !Enum.IsDefined(LookupStatus)) return null;
        if (LookupStatus == WindowsFaultLookupStatus.Matched)
        {
            if (Fault is not { } fault || fault.TimeUtc.Offset != TimeSpan.Zero ||
                fault.TimeUtc < RunStartedAtUtc || fault.TimeUtc > DetectedAtUtc) return null;
            return this with { Fault = fault with { Module = WindowsFaultEventReader.SafeModule(fault.Module) } };
        }
        return this with { Fault = null };
    }
}
internal interface IWindowsFaultEventSource
{
    WindowsFaultLookup Read(RunExitMarker previous, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken);
}

internal sealed class WindowsFaultEventReader : IWindowsFaultEventSource
{
    internal const int MaximumEvents = 64, MaximumXmlCharacters = 65536;
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private static readonly HashSet<string> Modules = new(StringComparer.OrdinalIgnoreCase)
    {
        "wisp.exe", "wisp.dll", "wisp.nativerenderer.dll", "libmpv-2.dll", "libvlc.dll", "libvlccore.dll",
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "ucrtbase.dll", "vcruntime140.dll", "vcruntime140_1.dll",
        "msvcp140.dll", "coreclr.dll", "clrjit.dll", "d3d11.dll", "dxgi.dll", "dcomp.dll"
    };

    public WindowsFaultLookup Read(RunExitMarker previous, DateTimeOffset nextRunStartedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var earliest = previous.StartedAtUtc > nextRunStartedAtUtc.AddDays(-7)
            ? previous.StartedAtUtc : nextRunStartedAtUtc.AddDays(-7);
        var from = earliest.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var until = nextRunStartedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var query = $"*[System[TimeCreated[@SystemTime >= '{from}' and @SystemTime <= '{until}'] and " +
            "((EventID=1000 and Provider[@Name='Application Error']) or " +
            "(EventID=1001 and Provider[@Name='Windows Error Reporting']))] and " +
            "EventData[Data[@Name='AppName']='Wisp.exe' or Data[@Name='P1']='Wisp.exe']]";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var events = new List<string>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query) { ReverseDirection = true });
            using var registration = deadline.Token.Register(() =>
            {
                try { reader.CancelReading(); }
                catch (Exception) { }
            });
            while (events.Count < MaximumEvents && !deadline.IsCancellationRequested)
            {
                using var record = reader.ReadEvent(TimeSpan.FromMilliseconds(150));
                if (record is null) break;
                var xml = record.ToXml();
                if (xml.Length <= MaximumXmlCharacters) events.Add(xml);
                else events.Add(string.Empty); // Oversized entries still consume the scan budget.
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Correlate(events, previous, nextRunStartedAtUtc,
                deadline.IsCancellationRequested ? WindowsFaultLookupStatus.TimedOut :
                events.Count == MaximumEvents ? WindowsFaultLookupStatus.ScanLimitReached : WindowsFaultLookupStatus.NoMatchingEvent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return new(WindowsFaultLookupStatus.AccessDenied); }
        catch (Exception error) when (error is EventLogException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Correlate(events, previous, nextRunStartedAtUtc, deadline.IsCancellationRequested
                ? WindowsFaultLookupStatus.TimedOut : (error.HResult & 0xffff) == 5
                    ? WindowsFaultLookupStatus.AccessDenied : WindowsFaultLookupStatus.ReadFailed);
        }
    }

    internal static WindowsFaultLookup Correlate(IEnumerable<string> events, RunExitMarker previous,
        DateTimeOffset nextRunStartedAtUtc, WindowsFaultLookupStatus absent = WindowsFaultLookupStatus.NoMatchingEvent)
    {
        WindowsFaultEvidence? fault = null;
        Guid? faultReport = null;
        var werReports = new HashSet<Guid>();
        foreach (var xml in events.Take(MaximumEvents))
        {
            if (xml.Length is 0 or > MaximumXmlCharacters) continue;
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
                    time < previous.StartedAtUtc || time > nextRunStartedAtUtc) continue;
                var fields = root.Element(Namespace + "EventData")?.Elements(Namespace + "Data")
                    .ToDictionary(element => (string?)element.Attribute("Name") ?? "", element => element.Value, StringComparer.Ordinal);
                if (fields is null || fields.Count > 64) continue;
                string? Value(string key) => fields.GetValueOrDefault(key);
                var provider = (string?)system.Element(Namespace + "Provider")?.Attribute("Name");
                var id = (string?)system.Element(Namespace + "EventID");
                if (id == "1001" && provider == "Windows Error Reporting" && Value("P1") == "Wisp.exe" &&
                    Guid.TryParse(Value("ReportId"), out var wer) && wer != Guid.Empty)
                { werReports.Add(wer); continue; }
                if (id != "1000" || provider != "Application Error" || Value("AppName") != "Wisp.exe" ||
                    Value("AppVersion") != previous.ExecutableVersion ||
                    !Unsigned(Value("ProcessId"), out var processId) || processId != (ulong)previous.ProcessId ||
                    !Unsigned(Value("ProcessCreationTime"), out var creation) || creation != (ulong)previous.ProcessCreationFileTime ||
                    !Hex(Value("ExceptionCode"), out var code) || code > uint.MaxValue) continue;
                if (fault is not null && fault.TimeUtc >= time) continue;
                fault = new(time, (uint)code, Hex(Value("FaultingOffset"), out var offset) ? offset : null,
                    SafeModule(Value("ModuleName")), false);
                faultReport = Guid.TryParse(Value("IntegratorReportId"), out var reportId) && reportId != Guid.Empty ? reportId : null;
            }
            catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException) { }
        }
        return fault is null ? new(absent) : new(WindowsFaultLookupStatus.Matched,
            fault with { CorrelatedWerReport = faultReport is { } report && werReports.Contains(report) });
    }

    internal static string? SafeModule(string? module) => module is not null && Modules.Contains(module) ? module.ToLowerInvariant() : null;
    private static bool Unsigned(string? value, out ulong number) => value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true
        ? Hex(value, out number) : ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    private static bool Hex(string? value, out ulong number)
    {
        number = 0;
        if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true) value = value[2..];
        return value is { Length: > 0 and <= 16 } && value.All(char.IsAsciiHexDigit) &&
            ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
    }
}
