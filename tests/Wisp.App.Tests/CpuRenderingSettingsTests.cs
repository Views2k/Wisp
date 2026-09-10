using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CpuRenderingSettingsTests
{
    [Fact]
    public void ExistingSettingsDefaultToGpuWithoutChangingLayout()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"SettingsRevision\":9,\"OverlayOpacity\":0.7,\"NativeGaugeMode\":1}")!;
        settings.MigrateSettings();
        Assert.False(settings.CpuRenderingEnabled);
        Assert.Equal(0.7, settings.OverlayOpacity);
        Assert.Equal(NativeGaugeMode.Analogue, settings.NativeGaugeMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendererSelectionRoundTripsAndOnlyAppliesAfterRestart(bool activeCpu)
    {
        var settings = new AppSettings { CpuRenderingEnabled = activeCpu };
        var viewModel = new DiagnosticsViewModel(settings);
        viewModel.UpdateCpuRendering(!activeCpu, saved: true);
        Assert.Equal(activeCpu, viewModel.ActiveCpuRendering);
        Assert.Equal(!activeCpu, viewModel.CpuRenderingEnabled);
        Assert.Contains("Restart Wisp", viewModel.CpuRenderingChangeStatus);
        settings.CpuRenderingEnabled = !activeCpu;
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        var restarted = new DiagnosticsViewModel(restored);
        Assert.Equal(!activeCpu, restarted.ActiveCpuRendering);
        Assert.Empty(restarted.CpuRenderingChangeStatus);
        viewModel.UpdateCpuRendering(activeCpu, saved: true);
        Assert.Empty(viewModel.CpuRenderingChangeStatus);
    }
    internal static void AssertControllerPersistenceOnCurrentDispatcher()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false,
            SetupCompletion = new SetupCompletionRecord
            {
                Version = SetupCompletionRecord.CurrentVersion,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ValidatedUdpPort = 5500,
                ValidatedPackets = SetupCompletionRecord.MinimumPackets,
                MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
                ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
                DataOutConfirmed = true,
                DisplayModeConfirmed = true,
                StockHudConfirmed = true
            }
        };
        bool failSave = false;
        bool? savedCpu = null;
        var controller = new AppController(settings, value =>
        {
            if (failSave) throw new System.IO.IOException("Test storage unavailable");
            savedCpu = value.CpuRenderingEnabled;
        }, new NoStartupRegistration());
        try
        {
            controller.SetCpuRenderingEnabled(true);
            Assert.True(savedCpu);
            Assert.True(settings.CpuRenderingEnabled);
            Assert.True(controller.ViewModel.CpuRenderingEnabled);
            Assert.False(controller.ViewModel.ActiveCpuRendering);
            failSave = true;
            controller.SetCpuRenderingEnabled(false);
            Assert.True(settings.CpuRenderingEnabled);
            Assert.True(controller.ViewModel.CpuRenderingEnabled);
            Assert.Contains("Could not save", controller.ViewModel.CpuRenderingChangeStatus);
            failSave = false;
            controller.SetCpuRenderingEnabled(false);
            Assert.False(savedCpu);
            Assert.Empty(controller.ViewModel.CpuRenderingChangeStatus);
        }
        finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
