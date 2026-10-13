using System.IO;
using Wisp.App.Supplementary;

namespace Wisp.App;

public partial class App
{
    private FeatureControlClient? _featureControlClient;

    private async Task StartFeatureControlsAsync()
    {
        if (!SupplementaryRuntime.IsConfigured || _controller is null) return;
        var ownedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp");
        var store = await Task.Run(() => new FeatureControlStore(new(SupplementaryRuntime.PublicKeys),
            Path.Combine(ownedRoot, "Supplementary", "Controls"), ownedRoot));
        var controller = _controller;
        var application = new FeatureControlApplication(controller.ApplyFeatureControlsAsync);
        var client = new FeatureControlClient(SupplementaryRuntime.Configuration, store,
            ApplicationVersionInfo.MachineVersion,
            ApplicationVersionInfo.IsPrivateCandidate || ApplicationVersionInfo.DiagnosticBuildId is not null ? "private" : "stable",
            state =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.InvokeAsync(() =>
                {
                    application.Apply(state);
                    NotifySupplementaryContentChanged();
                }).Task.GetAwaiter().GetResult();
            });
        // Apply the signed local receipt before starting any optional runtime feature.
        application.Apply(client.State);
        _featureControlClient = client;
        client.Start();
    }
}
