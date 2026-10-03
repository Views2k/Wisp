namespace Wisp.App.CrashDiagnostics;

internal sealed class PreviousRunRecovery(CrashReportStore reports, IWindowsFaultEventSource source)
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
