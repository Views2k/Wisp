namespace Wisp.App;

public sealed partial class AppController
{
    private CancellationTokenSource _supportFeatureLifetime = new();
    private bool _supportFeatureAvailable = true;
    internal CancellationToken SupportFeatureToken => _supportFeatureLifetime.Token;

    private void ApplySupportFeatureControl()
    {
        var available = FeatureAllowed("support");
        if (_supportFeatureAvailable == available) return;
        _supportFeatureAvailable = available;
        if (!available) _supportFeatureLifetime.Cancel();
        else
        {
            _supportFeatureLifetime.Dispose();
            _supportFeatureLifetime = new();
        }
    }
}
