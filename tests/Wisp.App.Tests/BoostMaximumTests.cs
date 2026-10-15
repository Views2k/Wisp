using Xunit;

namespace Wisp.App.Tests;

public sealed class BoostMaximumTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(70)]
    [InlineData(90)]
    [InlineData(160)]
    [InlineData(200)]
    public void ScaleAndTicksCoverBothUnitsAndVacuum(double maximum)
    {
        foreach (var unit in new[] { BoostPressureUnit.Psi, BoostPressureUnit.Bar })
            foreach (var vacuum in new[] { false, true })
            {
                var ticks = BoostPressureUnits.MajorTicks(unit, vacuum, maximum);
                Assert.Equal(BoostPressureUnits.AnalogMinimum(unit, vacuum), ticks[0]);
                Assert.Equal(BoostPressureUnits.AnalogMaximum(unit, maximum), ticks[^1]);
                Assert.Contains(0d, ticks);
                Assert.Equal(ticks.Count, ticks.Distinct().Count());
                Assert.InRange(ticks.Count, 3, 10);
                Assert.Equal(ticks.OrderBy(value => value), ticks);
                var endPsi = BoostPressureUnits.AnalogMaximumPsi(unit, maximum);
                Assert.Equal(1, BoostPressureUnits.GaugeFraction(endPsi, unit, vacuum, maximum), 8);
                Assert.Equal(.5, BoostPressureUnits.GaugeFraction(endPsi / 2, unit, false, maximum), 8);
            }
    }

    [Fact]
    public void SettingUpdatesThePreviewAndRoundTripsThroughPresets()
    {
        var settings = new AppSettings { BoostMaximumPsi = 200 };
        var vm = new DiagnosticsViewModel(settings);
        Assert.Equal(200, vm.PreviewBoostDisplay.ScaleMaximumPsi);
        var preset = HudPreset.Capture(settings, "High boost");
        var restored = new AppSettings();
        preset.ApplyTo(restored);
        Assert.Equal(200, restored.BoostMaximumPsi);
        vm.BoostMaximumPsi = 90;
        Assert.Equal(90, vm.PreviewBoostDisplay.ScaleMaximumPsi);
        Assert.Equal(200, new BoostDisplayModel().Calculate(1, false, 150, maximumPsi: 200).ScaleMaximumPsi);
        Assert.Equal(70, BoostPressureUnits.NormalizeMaximumPsi(double.NaN));
        Assert.Equal(200, BoostPressureUnits.NormalizeMaximumPsi(300));
        Assert.Equal(20, BoostPressureUnits.NormalizeMaximumPsi(0));
    }
}
