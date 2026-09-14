using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueGaugeViewModelTests
{
    [Fact]
    public void OutputOptionsAndIndependentColorsInitializeAndNotifyTheirBrushes()
    {
        var model = new DiagnosticsViewModel(new AppSettings
        {
            PowerGaugeAttached = false,
            TorqueGaugeAttached = true,
            PowerTorqueSmoothingMilliseconds = 900,
            PowerTorqueShowNegative = true,
            PowerGaugeColorNumber = true,
            CustomPowerLowColor = "#FF102030",
            CustomTorqueHighColor = "#FF405060"
        });
        Assert.False(model.PowerGaugeAttached);
        Assert.True(model.TorqueGaugeAttached);
        Assert.Equal(900, model.PowerTorqueSmoothingMilliseconds);
        Assert.True(model.PowerTorqueShowNegative);
        Assert.True(model.PowerGaugeColorNumber);
        Assert.False(model.TorqueGaugeColorNumber);
        Assert.Equal("#FF102030", model.CustomPowerLowColor);
        Assert.Equal("#FF405060", model.CustomTorqueHighColor);
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        model.CustomPowerLowColor = "#FF708090";
        Assert.Contains(nameof(model.PowerGaugeLowBrush), changes);
        Assert.DoesNotContain(nameof(model.TorqueGaugeHighBrush), changes);
        Assert.Equal("#FF405060", model.CustomTorqueHighColor);
        model.PowerTorqueSmoothingMilliseconds = double.NaN;
        Assert.Equal(500, model.PowerTorqueSmoothingMilliseconds);
    }

    [Fact]
    public void TorqueUnitChangesReformatTheMaximumWithoutChangingItsPhysicalRange()
    {
        var model = new DiagnosticsViewModel(new AppSettings { TorqueGaugeMaximumNm = 2000 });
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        model.TorqueUnitSelectionIndex = 1;
        Assert.Equal(1475.1242985545312, model.TorqueGaugeMaximum, 8);
        Assert.Equal(2000, model.TorqueGaugeMaximumNm);
        Assert.Contains(nameof(model.TorqueGaugeMaximum), changes);
        model.TorqueUnitSelectionIndex = 0;
        Assert.Equal(2000, model.TorqueGaugeMaximum);
    }

    [Fact]
    public void GaugeOptionsInitializeAndRejectNonfiniteChanges()
    {
        var model = new DiagnosticsViewModel(new AppSettings
        {
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = false,
            PowerGaugeMaximum = 1800,
            TorqueGaugeMaximumNm = 2500,
            PowerTorqueGaugeScale = 1.25
        });
        Assert.True(model.PowerGaugeEnabled);
        Assert.False(model.TorqueGaugeEnabled);
        Assert.Equal(1800, model.PowerGaugeMaximum);
        Assert.Equal(2500, model.TorqueGaugeMaximumNm);
        Assert.Equal(1.25, model.PowerTorqueGaugeScale);
        model.PowerGaugeMaximum = double.NaN;
        model.TorqueGaugeMaximumNm = double.PositiveInfinity;
        model.PowerTorqueGaugeScale = double.NegativeInfinity;
        Assert.Equal(1000, model.PowerGaugeMaximum);
        Assert.Equal(1200, model.TorqueGaugeMaximumNm);
        Assert.Equal(1, model.PowerTorqueGaugeScale);
    }
}
