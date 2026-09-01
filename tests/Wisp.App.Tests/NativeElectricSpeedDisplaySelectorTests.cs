using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeElectricSpeedDisplaySelectorTests
{
    [Theory]
    [InlineData(SpeedUnit.MilesPerHour)]
    [InlineData(SpeedUnit.KilometersPerHour)]
    public void MatchingFreshFh6StateUsesNativeDigitsAndFadeFlags(SpeedUnit unit)
    {
        var frame = Frame(unit);

        var display = NativeElectricSpeedDisplaySelector.Resolve(frame, Timestamp(120));

        Assert.True(display.UsesNativeState);
        Assert.Equal((2, 4, 6), (display.Hundreds, display.Tens, display.Ones));
        Assert.True(display.SpeedLessOrEqualOne);
        Assert.False(display.SpeedLessTen);
        Assert.True(display.SpeedLessHundred);
    }

    [Fact]
    public void WheelIndicatedSourceKeepsItsOwnSpeed()
    {
        var frame = Frame(SpeedUnit.MilesPerHour) with
        {
            SpeedSource = SpeedSourceMode.WheelIndicated
        };

        var display = NativeElectricSpeedDisplaySelector.Resolve(frame, Timestamp(120));

        Assert.False(display.UsesNativeState);
        Assert.Equal((1, 2, 3), (display.Hundreds, display.Tens, display.Ones));
    }

    [Theory]
    [InlineData(false, SpeedUnit.MilesPerHour, 120)]
    [InlineData(true, SpeedUnit.KilometersPerHour, 120)]
    [InlineData(true, SpeedUnit.MilesPerHour, 250)]
    public void InvalidContextFallsBackToTelemetryDigits(
        bool isElectric,
        SpeedUnit unit,
        int nowMilliseconds)
    {
        var frame = Frame(SpeedUnit.MilesPerHour) with
        {
            IsElectric = isElectric,
            Unit = unit
        };

        var display = NativeElectricSpeedDisplaySelector.Resolve(frame, Timestamp(nowMilliseconds));

        Assert.False(display.UsesNativeState);
        Assert.Equal((1, 2, 3), (display.Hundreds, display.Tens, display.Ones));
    }

    [Fact]
    public void InvalidatedNativeSourceFallsBackImmediately()
    {
        var frame = Frame(SpeedUnit.MilesPerHour) with
        {
            NativeGaugeSourceInvalidated = true
        };

        Assert.False(NativeElectricSpeedDisplaySelector.Resolve(frame, Timestamp(120)).UsesNativeState);
    }

    private static NativeGaugeFrame Frame(SpeedUnit unit) => new(
        true,
        123,
        0,
        0,
        TransmissionGear.First,
        unit,
        ExactRedlineResult.Unavailable(),
        IsElectric: true,
        NativeGaugeObservedTimestamp: Timestamp(100),
        DisplayedSpeedState: new NativeDisplayedSpeedState(
            true,
            2,
            4,
            6,
            true,
            false,
            true,
            unit),
        SpeedSource: SpeedSourceMode.Fh6VehicleSpeed);

    private static long Timestamp(int milliseconds) =>
        (long)Math.Round(Stopwatch.Frequency * milliseconds / 1_000d);
}
