using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class NativeAssistTests
{
    [Fact]
    public void UnavailableHudHasNoGameplayVisibilityObservation()
    {
        var snapshot = NativeHudSnapshot.Unavailable(NativeAssistProviderStatus.ReadFailure, 7, 314);

        Assert.Equal(NativeGameplayVisibility.Unknown, snapshot.GameplayVisibility);
        Assert.Equal(0L, snapshot.VisibilityObservedTimestamp);
        Assert.False(snapshot.HasAvailableCapabilities);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible, 1L, true)]
    [InlineData(NativeGameplayVisibility.Hidden, 1L, true)]
    [InlineData(NativeGameplayVisibility.Visible, long.MaxValue, true)]
    [InlineData(NativeGameplayVisibility.Hidden, long.MaxValue, true)]
    [InlineData(NativeGameplayVisibility.Visible, 0L, false)]
    [InlineData(NativeGameplayVisibility.Hidden, 0L, false)]
    [InlineData(NativeGameplayVisibility.Visible, -1L, false)]
    [InlineData(NativeGameplayVisibility.Hidden, -1L, false)]
    [InlineData(NativeGameplayVisibility.Unknown, 0L, false)]
    [InlineData(NativeGameplayVisibility.Unknown, 1L, false)]
    [InlineData((NativeGameplayVisibility)99, 1L, false)]
    public void OnlyKnownTimestampedVisibilityIsAnIndependentCapability(
        NativeGameplayVisibility visibility,
        long observedTimestamp,
        bool expected)
    {
        var snapshot = NativeHudSnapshot.Unavailable(NativeAssistProviderStatus.ReadFailure, 7, 314) with
        {
            GameplayVisibility = visibility,
            VisibilityObservedTimestamp = observedTimestamp
        };

        Assert.Equal(expected, snapshot.HasAvailableCapabilities);
        Assert.False(snapshot.Available);
        Assert.False(snapshot.Assists.Available);
        Assert.Equal(7UL, snapshot.Generation);
        Assert.Equal(314, snapshot.CarOrdinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExistingCapabilitiesDoNotRequireGameplayVisibility(bool tachometerAvailable, bool assistsAvailable)
    {
        var snapshot = NativeHudSnapshot.Unavailable() with
        {
            Available = tachometerAvailable,
            Assists = NativeAssistSnapshot.Unavailable() with { Available = assistsAvailable }
        };

        Assert.True(snapshot.HasAvailableCapabilities);
        Assert.Equal(tachometerAvailable, snapshot.Available);
        Assert.Equal(assistsAvailable, snapshot.Assists.Available);
        Assert.Equal(NativeGameplayVisibility.Unknown, snapshot.GameplayVisibility);
    }

    [Fact]
    public void NativeDisplayedSpeedRequiresValidDigitsAndAKnownUnit()
    {
        var valid = new NativeDisplayedSpeedState(
            true, 1, 2, 3, false, false, false, SpeedUnit.MilesPerHour);
        var invalidDigit = valid with { Ones = 10 };
        var missingUnit = valid with { Unit = null };

        Assert.True(valid.IsUsable);
        Assert.Equal(123, valid.Value);
        Assert.False(invalidDigit.IsUsable);
        Assert.False(missingUnit.IsUsable);
        Assert.True((NativeHudSnapshot.Unavailable() with { DisplayedSpeedState = valid })
            .HasAvailableCapabilities);
        Assert.False((NativeHudSnapshot.Unavailable() with { DisplayedSpeedState = missingUnit })
            .HasAvailableCapabilities);
    }

    [Theory]
    [InlineData(0.1f, 0.2f, true)]
    [InlineData(0.1f, 0.1f, false)]
    [InlineData(0.2f, 0.1f, false)]
    public void UnorderedOrLessThanMatchesOrderedComparison(float left, float right, bool expected) =>
        Assert.Equal(expected, NativeAssistStateCalculator.IsUnorderedOrLessThan(left, right));

    [Fact]
    public void UnorderedOrLessThanTreatsEveryUnorderedComparisonAsTrue()
    {
        Assert.True(NativeAssistStateCalculator.IsUnorderedOrLessThan(float.NaN, 0.1f));
        Assert.True(NativeAssistStateCalculator.IsUnorderedOrLessThan(0.1f, float.NaN));
    }

    [Theory]
    [InlineData(0.2f, 0u, 0.0f, false)]
    [InlineData(0.0f, 2u, 0.0f, true)]
    [InlineData(0.2f, 2u, 0.0f, false)]
    [InlineData(0.2f, 2u, 0.2f, true)]
    [InlineData(float.NaN, 2u, 0.0f, false)]
    [InlineData(0.2f, 2u, float.NaN, true)]
    public void LaunchControlStateMatchesNativeBranching(
        float primary,
        uint mode,
        float secondary,
        bool expected)
    {
        var result = NativeAssistStateCalculator.Calculate(
            Raw(lcAvailable: true, lcPrimary: primary, lcMode: mode, lcSecondary: secondary),
            0.1f,
            1,
            314);

        Assert.Equal(expected, result.IsLCOn);
    }

    [Fact]
    public void AvailabilityGatesEveryRenderedOnState()
    {
        var result = NativeAssistStateCalculator.Calculate(
            Raw(
                absState: 1,
                tcrOn: true,
                stmState: 1,
                lcPrimary: 0,
                lcMode: 2),
            0.1f,
            2,
            314);

        Assert.True(result.Available);
        Assert.False(result.IsABSOn);
        Assert.False(result.IsTCROn);
        Assert.False(result.IsSTMOn);
        Assert.False(result.IsLCOn);
    }

    [Fact]
    public void TractionControlUsesDirectAndMappedWheelValuesWithNativeNaNSemantics()
    {
        var direct = NativeAssistStateCalculator.Calculate(
            Raw(tcrAvailable: true, tcrPrimary: 0.1001f),
            0.1f,
            1,
            1);
        var wheel = NativeAssistStateCalculator.Calculate(
            Raw(tcrAvailable: true, wheelValues: [0, 0, 0.1001f, 0]),
            0.1f,
            1,
            1);
        var unordered = NativeAssistStateCalculator.Calculate(
            Raw(tcrAvailable: true, tcrSecondary: float.NaN),
            0.1f,
            1,
            1);
        var equal = NativeAssistStateCalculator.Calculate(
            Raw(tcrAvailable: true, tcrTertiary: 0.1f, wheelValues: [0.1f, 0.1f, 0.1f, 0.1f]),
            0.1f,
            1,
            1);

        Assert.True(direct.IsTCROn);
        Assert.True(wheel.IsTCROn);
        Assert.True(unordered.IsTCROn);
        Assert.False(equal.IsTCROn);
    }

    [Fact]
    public void AbsRequiresAvailabilityAndNonzeroNativeState()
    {
        Assert.False(NativeAssistStateCalculator.Calculate(
            Raw(absAvailable: true, absState: 0), 0.1f, 1, 1).IsABSOn);
        Assert.True(NativeAssistStateCalculator.Calculate(
            Raw(absAvailable: true, absState: 7), 0.1f, 1, 1).IsABSOn);
        Assert.False(NativeAssistStateCalculator.Calculate(
            Raw(absAvailable: false, absState: 7), 0.1f, 1, 1).IsABSOn);
    }

    [Fact]
    public void StabilityManagementRequiresAvailabilityAndNonzeroNativeState()
    {
        Assert.False(NativeAssistStateCalculator.Calculate(
            Raw(stmAvailable: true, stmState: 0), 0.1f, 1, 1).IsSTMOn);
        Assert.True(NativeAssistStateCalculator.Calculate(
            Raw(stmAvailable: true, stmState: 1), 0.1f, 1, 1).IsSTMOn);
        Assert.False(NativeAssistStateCalculator.Calculate(
            Raw(stmAvailable: false, stmState: 1), 0.1f, 1, 1).IsSTMOn);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void WheelIdMappingMatchesNativeFallback(int requested, int expected) =>
        Assert.Equal(expected, NativeAssistStateCalculator.MapWheelIndex(requested, 0, 1, 2));

    [Fact]
    public void AnalogAnglesUseExactNativeAvailabilityOrderAndSpacing()
    {
        Assert.Equal(new NativeAssistAngleSet(60, 20, -20, -60),
            NativeAssistAngles.Calculate(true, true, true, true));
        Assert.Equal(new NativeAssistAngleSet(40, 0, 0, -40),
            NativeAssistAngles.Calculate(true, false, true, true));
        Assert.Equal(new NativeAssistAngleSet(20, -20, 0, 0),
            NativeAssistAngles.Calculate(true, true, false, false));
        Assert.Equal(new NativeAssistAngleSet(0, 0, 0, 0),
            NativeAssistAngles.Calculate(false, false, false, true));
    }

    [Theory]
    [InlineData(false, false, false, false, 0, 0, 0, 0)]
    [InlineData(true, false, false, false, 0, 0, 0, 0)]
    [InlineData(false, true, false, false, 0, 0, 0, 0)]
    [InlineData(false, false, true, false, 0, 0, 0, 0)]
    [InlineData(false, false, false, true, 0, 0, 0, 0)]
    [InlineData(true, true, false, false, 20, -20, 0, 0)]
    [InlineData(true, false, true, false, 20, 0, -20, 0)]
    [InlineData(true, false, false, true, 20, 0, 0, -20)]
    [InlineData(false, true, true, false, 0, 20, -20, 0)]
    [InlineData(false, true, false, true, 0, 20, 0, -20)]
    [InlineData(false, false, true, true, 0, 0, 20, -20)]
    [InlineData(true, true, true, false, 40, 0, -40, 0)]
    [InlineData(true, true, false, true, 40, 0, 0, -40)]
    [InlineData(true, false, true, true, 40, 0, 0, -40)]
    [InlineData(false, true, true, true, 0, 40, 0, -40)]
    [InlineData(true, true, true, true, 60, 20, -20, -60)]
    public void EveryAvailabilityCombinationUsesNativeAngles(
        bool abs,
        bool tcr,
        bool stm,
        bool lc,
        double absAngle,
        double tcrAngle,
        double stmAngle,
        double lcAngle) =>
        Assert.Equal(
            new NativeAssistAngleSet(absAngle, tcrAngle, stmAngle, lcAngle),
            NativeAssistAngles.Calculate(abs, tcr, stm, lc));

    private static NativeAssistRawState Raw(
        bool absAvailable = false,
        bool tcrAvailable = false,
        bool stmAvailable = false,
        bool lcAvailable = false,
        uint absState = 0,
        bool tcrOn = false,
        float tcrPrimary = 0,
        float tcrSecondary = 0,
        float tcrTertiary = 0,
        IReadOnlyList<float>? wheelValues = null,
        uint stmState = 0,
        float lcPrimary = 1,
        uint lcMode = 0,
        float lcSecondary = 0) =>
        new(
            absAvailable,
            tcrAvailable,
            stmAvailable,
            lcAvailable,
            absState,
            tcrPrimary,
            tcrSecondary,
            tcrTertiary,
            wheelValues ?? [0, 0, 0, 0],
            stmState,
            lcPrimary,
            lcMode,
            lcSecondary);
}
