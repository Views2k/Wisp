namespace Wisp.App;

public sealed partial class AppController
{
    private CancellationTokenSource _applicationUpdateFeatureLifetime = new();
    private bool _updatesFeatureAvailable = true;
    internal CancellationToken ApplicationUpdateFeatureToken => _applicationUpdateFeatureLifetime.Token;
    internal bool CanBeginApplicationUpdate(CancellationToken permission) =>
        !_disposed && FeatureAllowed("updates") && !permission.IsCancellationRequested;

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
