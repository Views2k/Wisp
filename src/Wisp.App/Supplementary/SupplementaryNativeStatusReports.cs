using System.Diagnostics;
using Wisp.App.DebugLogging;
using Wisp.Core;

namespace Wisp.App.Supplementary;

// A running game process by ID and start time. Process metadata only; never opens game memory.
internal readonly record struct NativeGameProcess(int Id, DateTime? StartedUtc)
{
    internal static NativeGameProcess? Find()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName("ForzaHorizon6");
            return processes.Select(process => new NativeGameProcess(process.Id, StartedAt(process)))
                .OrderBy(process => process.StartedUtc ?? DateTime.MaxValue).ThenBy(process => process.Id)
                .Cast<NativeGameProcess?>().FirstOrDefault();
        }
        catch (Exception error) when (NativeHudFingerprintCache.IsExpectedFailure(error)) { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static DateTime? StartedAt(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception error) when (NativeHudFingerprintCache.IsExpectedFailure(error)) { return null; }
    }
}

// Access and compatibility findings describe the game process, not a telemetry session. The native
// reader detaches on every pause or menu and re-attaches on resume, so each status is reported once
// per game process per Wisp session. Only the reporting collector calls this.
internal sealed class SupplementaryNativeStatusReports
{
    private readonly HashSet<(NativeGameProcess? Process, NativeAssistProviderStatus Status)> _reported = [];
    private NativeGameProcess? _current, _previous;

    internal static bool Reportable(NativeAssistProviderStatus status) =>
        status is NativeAssistProviderStatus.AccessDenied or NativeAssistProviderStatus.UnsupportedBuild;

    // The process running when the collector looked. None running keeps the last one seen.
    internal void Observe(NativeGameProcess? process)
    {
        if (process is null || process == _current) return;
        _previous = _current;
        _current = process;
    }

    internal bool ShouldReport(HealthContextSample sample) =>
        Reportable(sample.NativeStatus) && !_reported.Contains(Key(sample));

    internal void MarkReported(HealthContextSample sample) => _reported.Add(Key(sample));

    // A sample from before the current process started belongs to the process seen before it.
    private (NativeGameProcess?, NativeAssistProviderStatus) Key(HealthContextSample sample) =>
        (_current is { StartedUtc: { } started } && sample.TimestampUtc.UtcDateTime < started ? _previous : _current, sample.NativeStatus);
}
