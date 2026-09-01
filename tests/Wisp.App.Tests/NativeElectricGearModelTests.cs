using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeElectricGearModelTests
{
    [Theory]
    [InlineData(false, "1")]
    [InlineData(true, "Drive")]
    public void SourceFlagControlsOnlyFirstGear(bool useDriveFor1, string expected)
    {
        var state = State(gear: 1, useDriveFor1: useDriveFor1);

        Assert.Equal(expected, NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Analogue));
        Assert.Equal(expected, NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Digital));
    }

    [Fact]
    public void SourceFlagDoesNotReplaceLaterForwardGearsWithDrive()
    {
        var state = State(gear: 2, useDriveFor1: true);

        Assert.Equal("2", NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Analogue));
        Assert.Equal("2", NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Digital));
    }

    [Theory]
    [InlineData(0, "R")]
    [InlineData(11, "N")]
    public void DigitalMappingUsesNativeReverseAndNeutralKeys(int gear, string expected)
    {
        Assert.Equal(
            expected,
            NativeElectricGearModel.CurrentToken(State(gear: gear), NativeGaugeMode.Digital));
    }

    [Fact]
    public void ElectricMappingUsesNativeReverseAssetToken()
    {
        Assert.Equal(
            "Reverse",
            NativeElectricGearModel.CurrentToken(State(gear: 0), NativeGaugeMode.Analogue));
    }

    [Fact]
    public void AdjacentGearsComeFromNativeFields()
    {
        var state = State(gear: 2, next: 3, previous: 1, useDriveFor1: true);

        Assert.Equal("3", NativeElectricGearModel.AdjacentToken(state, next: true));
        Assert.Equal("Drive", NativeElectricGearModel.AdjacentToken(state, next: false));
    }

    [Theory]
    [InlineData(-1, false, null, null)]
    [InlineData(0, true, "GearGauge0.png", "HUD_EV_Digital_Bar_0bar.png")]
    [InlineData(3, true, "GearGauge3.png", "HUD_EV_Digital_Bar_3bar.png")]
    [InlineData(4, true, "GearGauge4.png", "HUD_EV_Digital_Bar_max.png")]
    public void GaugeStateControlsGaugeAssetsAndMultiGearLayout(
        int gaugeState,
        bool multiGear,
        string? analogueAsset,
        string? digitalAsset)
    {
        var state = State(gaugeState: gaugeState);

        Assert.Equal(multiGear, NativeElectricGearModel.IsMultiGear(state));
        Assert.Equal(analogueAsset, NativeElectricGearModel.GaugeAsset(state, digital: false));
        Assert.Equal(digitalAsset, NativeElectricGearModel.GaugeAsset(state, digital: true));
    }

    [Fact]
    public void UnavailableStateDoesNotGuessFromTelemetry()
    {
        var state = NativeElectricGearState.Unavailable;

        Assert.Null(NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Analogue));
        Assert.Null(NativeElectricGearModel.CurrentToken(state, NativeGaugeMode.Digital));
        Assert.Null(NativeElectricGearModel.AdjacentToken(state, next: true));
        Assert.False(NativeElectricGearModel.IsMultiGear(state));
    }

    private static NativeElectricGearState State(
        int gear = 1,
        int next = 2,
        int previous = 0,
        int gaugeState = -1,
        bool useDriveFor1 = false) =>
        new(true, gear, next, previous, gaugeState, useDriveFor1);
}
