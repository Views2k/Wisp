using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueGaugeViewModelTests
{
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
