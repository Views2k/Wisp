using System.Text.Json;
using Wisp.App;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudGpuPrioritySettingsTests
{
    [Fact]
    public void ExistingSettingsLeaveTheGpuPriorityOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"SettingsRevision\":9,\"CpuRenderingEnabled\":false}")!;
        settings.MigrateSettings();
        Assert.False(settings.HudGpuPriority);
        Assert.False(new AppSettings().HudGpuPriority);
    }

    [Fact]
    public void ChoiceRoundTripsThroughStoredSettings()
    {
        var settings = new AppSettings { HudGpuPriority = true };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        Assert.True(restored.HudGpuPriority);
        var viewModel = new DiagnosticsViewModel(restored);
        Assert.True(viewModel.HudGpuPriorityEnabled);
        Assert.True(viewModel.HudGpuPriorityActive);
        Assert.Empty(viewModel.HudGpuPriorityChangeStatus);
    }

    [Fact]
    public void ViewModelSeparatesTheSavedChoiceFromWhatTheRendererApplies()
    {
        var viewModel = new DiagnosticsViewModel(new AppSettings());
        viewModel.UpdateHudGpuPriority(true, false, "GPU priority is temporarily paused.");
        Assert.True(viewModel.HudGpuPriorityEnabled);
        Assert.False(viewModel.HudGpuPriorityActive);
        Assert.Contains("paused", viewModel.HudGpuPriorityChangeStatus);
        viewModel.UpdateHudGpuPriority(true, true, "");
        Assert.True(viewModel.HudGpuPriorityActive);
        Assert.Empty(viewModel.HudGpuPriorityChangeStatus);
    }

    [Fact]
    public void ToolsSnapshotReportsTheChoice()
    {
        var projected = SupplementarySettings.Capture(new AppSettings { HudGpuPriority = true });
        Assert.True(projected.HudGpuPriority);
        Assert.True(projected.IsValid);
        Assert.Contains("\"hudGpuPriority\":true", JsonSerializer.Serialize(projected, SupplementarySchema.Json));
        Assert.False(SupplementarySettings.Capture(new AppSettings()).HudGpuPriority);
    }

    [Fact]
    public void ToolsCanPauseTheFeature()
    {
        Assert.Contains("gpu-priority", ControlledFeatures.All);
    }
}
