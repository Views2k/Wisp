using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueGaugeAttachmentTests
{
    [Fact]
    public void AttachmentControlsTrackLayoutModeAndEachGaugeWithoutChangingPreferences()
    {
        var settings = new AppSettings
        {
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true,
            PowerGaugeAttached = true,
            TorqueGaugeAttached = true
        };
        var model = new DiagnosticsViewModel(settings);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        foreach (var layout in Enum.GetValues<HudLayoutMode>())
            foreach (var mode in Enum.GetValues<NativeGaugeMode>())
            {
                model.LayoutSelectionIndex = (int)layout;
                model.NativeGaugeSelectionIndex = (int)mode;
                var canAttach = layout == HudLayoutMode.Native && mode == NativeGaugeMode.Analogue;
                Assert.Equal(canAttach, model.CanAttachPowerGauge);
                Assert.Equal(canAttach, model.CanAttachTorqueGauge);
                Assert.True(model.PowerGaugeAttached);
                Assert.True(model.TorqueGaugeAttached);
                Assert.True(settings.PowerGaugeAttached);
                Assert.True(settings.TorqueGaugeAttached);
            }

        model.LayoutSelectionIndex = (int)HudLayoutMode.Native;
        model.NativeGaugeSelectionIndex = (int)NativeGaugeMode.Analogue;
        changes.Clear();
        model.PowerGaugeEnabled = false;
        Assert.False(model.CanAttachPowerGauge);
        Assert.True(model.CanAttachTorqueGauge);
        Assert.Contains(nameof(model.CanAttachPowerGauge), changes);
        changes.Clear();
        model.PowerGaugeEnabled = true;
        model.TorqueGaugeEnabled = false;
        Assert.True(model.CanAttachPowerGauge);
        Assert.False(model.CanAttachTorqueGauge);
        Assert.Contains(nameof(model.CanAttachTorqueGauge), changes);

        model.TorqueGaugeEnabled = true;
        changes.Clear();
        model.NativeGaugeSelectionIndex = (int)NativeGaugeMode.Digital;
        Assert.False(model.CanAttachPowerGauge);
        Assert.False(model.CanAttachTorqueGauge);
        Assert.Contains(nameof(model.CanAttachPowerGauge), changes);
        Assert.Contains(nameof(model.CanAttachTorqueGauge), changes);
        model.NativeGaugeSelectionIndex = (int)NativeGaugeMode.Analogue;
        changes.Clear();
        model.LayoutSelectionIndex = (int)HudLayoutMode.Combined;
        Assert.False(model.CanAttachPowerGauge);
        Assert.False(model.CanAttachTorqueGauge);
        Assert.Contains(nameof(model.CanAttachPowerGauge), changes);
        Assert.Contains(nameof(model.CanAttachTorqueGauge), changes);
    }
}
