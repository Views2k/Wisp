namespace Wisp.App.CrashDiagnostics;

internal sealed class PreviousRunRecovery(CrashReportStore reports, IWindowsFaultEventSource source,
    IWindowsStopEventSource? stops = null)
{
    internal Task<CrashReport?> RecoverAsync(RunExitMarker previous, DateTimeOffset nextRunStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var generation = reports.RecoveryGeneration;
        return Task.Run(() => Recover(previous, nextRunStartedAtUtc, generation, cancellationToken), cancellationToken);
    }

    private CrashReport? Recover(RunExitMarker previous, DateTimeOffset nextRunStartedAtUtc,
        long generation, CancellationToken cancellationToken)
    {
        try
        {
            if (previous.Sanitize() is not { CleanExit: false } safe || nextRunStartedAtUtc.Offset != TimeSpan.Zero ||
                safe.StartedAtUtc >= nextRunStartedAtUtc) return null;
            cancellationToken.ThrowIfCancellationRequested();
            if (reports.TryReadAll(out var saved) && saved.Any(report => report.RunId == safe.RunId && report.IsTerminating &&
                report.Origin is CrashOrigin.UiDispatcher or CrashOrigin.BackgroundThread)) return null;
            WindowsFaultLookup lookup;
            try { lookup = source.Read(safe, nextRunStartedAtUtc, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { lookup = new(WindowsFaultLookupStatus.ReadFailed); }
            cancellationToken.ThrowIfCancellationRequested();
            var exit = new RecoveredExitInfo(safe.StartedAtUtc, nextRunStartedAtUtc, lookup.Status, lookup.Fault).Sanitize();
            if (exit is null) exit = new(safe.StartedAtUtc, nextRunStartedAtUtc, WindowsFaultLookupStatus.ReadFailed, null);
            exit = exit with { Native = NativeCrashCapture.TryRead(reports.StoreDirectory, safe.RunId) };
            // A recorded close request is not a crash. A correlated Windows fault still wins.
            if (safe.ShutdownIntent != ShutdownIntent.None && exit.Fault is null) return null;
            // Windows stopping right after the last checkpoint ended the run, unless Wisp itself recorded a fault.
            if (exit.Fault is null && exit.Native is null && stops is not null)
            {
                bool stopped;
                try { stopped = stops.StoppedUnexpectedly(safe.Context?.CapturedAtUtc ?? safe.StartedAtUtc, nextRunStartedAtUtc, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { stopped = false; }
                cancellationToken.ThrowIfCancellationRequested();
                exit = exit with { WindowsStopped = stopped };
            }
            var report = safe.ToReport(exit.Fault?.TimeUtc ?? nextRunStartedAtUtc) with
            {
                Origin = exit.Fault is null ? CrashOrigin.UnexpectedExit : CrashOrigin.WindowsApplicationFault,
                RecoveredExit = exit
            };
            return reports.TrySaveRecovered(report, generation) ? report : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; } // Failure recovery cannot prevent this launch.
    }
}
