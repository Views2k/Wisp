using System.IO;
using Wisp.App.Supplementary;

namespace Wisp.App;

public partial class App
{
    private FeatureControlClient? _featureControlClient;
    private FeatureControlContext _featureControlContext = FeatureControlContext.Empty;

    // Called by the existing background collector; no additional game or graphics inventory.
    private void SetFeatureControlContext(string? gameBuild, IEnumerable<uint>? gpuVendorIds)
    {
        var previous = Volatile.Read(ref _featureControlContext);
        Volatile.Write(ref _featureControlContext, new(
            SupplementarySchema.NumericVersion(gameBuild, false) ? gameBuild : null,
            gpuVendorIds is null ? previous.GpuVendors : FeatureControlContext.FromVendorIds(gpuVendorIds)));
    }

    private FeatureControlContext GetFeatureControlContext()
    {
        var cached = Volatile.Read(ref _featureControlContext);
        var identity = GetCurrentGameIdentity();
        var build = identity.Verification.Inspection == "complete" ? identity.GameBuild : null;
        return cached with { GameBuild = SupplementarySchema.NumericVersion(build, false) ? build : null };
    }

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
            }, context: GetFeatureControlContext);
        // Apply the signed local receipt before starting any optional runtime feature.
        application.Apply(client.State);
        _featureControlClient = client;
        client.Start();
    }
}
