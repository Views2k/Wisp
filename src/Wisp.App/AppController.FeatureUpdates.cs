namespace Wisp.App;

public sealed partial class AppController
{
    private CancellationTokenSource _applicationUpdateFeatureLifetime = new();
    private bool _updatesFeatureAvailable = true;
    internal CancellationToken ApplicationUpdateFeatureToken => _applicationUpdateFeatureLifetime.Token;
    private volatile bool _windowsSessionEnding;
    internal bool CanBeginApplicationUpdate(CancellationToken permission) =>
        !_disposed && FeatureAllowed("updates") && !permission.IsCancellationRequested && !_windowsSessionEnding;

    // True from Windows' sign-out or shutdown notice until it withdraws the notice (SessionEndSentinel).
    // No update starts in between: on 2.7.3 a hand-off begun during a sign-out started a helper that
    // could not initialize (0xC000026B), and an installer could be stopped part-way the same way.
    internal void SetWindowsSessionEnding(bool ending) => _windowsSessionEnding = ending;

    private void ApplyApplicationUpdateFeatureControl()
    {
        var available = FeatureAllowed("updates");
        ViewModel.SetApplicationUpdateFeatureAvailable(available, FeatureControls.Reason("updates"));
        if (available == _updatesFeatureAvailable) return;
        _updatesFeatureAvailable = available;
        if (!available)
        {
            Supplementary.ApplicationUpdateTelemetry.Record("paused");
            _applicationUpdateTimer.Stop();
            _applicationUpdateFeatureLifetime.Cancel();
        }
        else
        {
            _applicationUpdateFeatureLifetime.Dispose();
            _applicationUpdateFeatureLifetime = new();
            RestoreAutomaticApplicationUpdateStatus();
            BeginStartupApplicationUpdateCheck();
        }
    }
}
