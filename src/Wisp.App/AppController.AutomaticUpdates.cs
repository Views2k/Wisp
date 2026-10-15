using System.Windows.Threading;
using Wisp.App.Supplementary;
using Wisp.Update;

namespace Wisp.App;

public sealed partial class AppController
{
    private DispatcherTimer? _automaticInstallTimer;
    private AutomaticUpdateCoordinator? _automaticInstaller;

    internal bool HasApplicationUpdateWorkInProgress =>
        Runs.HasPendingMetadata || Runs.IsBusy || Runs.IsRecording || Clips.IsBusy || Clips.IsExporting;

    internal void EnableAutomaticApplicationUpdates(Func<VerifiedInstaller, Task<bool>> apply)
    {
        // Private candidates stay available for their intended test session.
        if (ApplicationVersionInfo.DiagnosticBuildId is not null || _automaticInstaller is not null) return;
        _automaticInstaller = new(
            () => !_disposed && FeatureAllowed("updates"),
            () => _pendingInstaller is not null || _availableApplicationRelease is not null,
            () => Task.Run(ForzaFocusService.IsOpenForApplicationUpdate),
            () => HasApplicationUpdateWorkInProgress,
            () => PrepareApplicationUpdateAsync(),
            apply,
            message => ViewModel.UpdateApplicationUpdateStatus(message, "Check again", true, true),
            waiting: state => ApplicationUpdateTelemetry.Record(state, (_pendingInstaller?.Version ?? _availableApplicationRelease?.Version)?.ToString()));
        _automaticInstallTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _automaticInstallTimer.Tick += async (_, _) => await TryAutomaticApplicationUpdateAsync();
        _automaticInstallTimer.Start();
    }

    private async Task TryAutomaticApplicationUpdateAsync()
    {
        if (_automaticInstaller is null) return;
        try { await _automaticInstaller.RunAsync(); }
        catch (Exception error)
        {
            ApplicationUpdateTelemetry.Record("failed", (_pendingInstaller?.Version ?? _availableApplicationRelease?.Version)?.ToString(), reason: "handoff");
            using var operation = SupplementaryObservations.Begin("update", "update", "install-handoff");
            operation?.Fail(error);
            ViewModel.UpdateApplicationUpdateStatus("Wisp could not complete the update. It will try again automatically.", "Try again", true, true);
        }
    }
}
