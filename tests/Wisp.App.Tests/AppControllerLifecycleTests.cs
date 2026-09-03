using System.Diagnostics;
using Wisp.App;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AppControllerLifecycleTests
{
    [Theory]
    [InlineData(HudLayoutMode.Native, 0, 0)]
    [InlineData(HudLayoutMode.Native, 1, 1)]
    [InlineData(HudLayoutMode.Minimal, 0.35, 0.35)]
    [InlineData(HudLayoutMode.Combined, 2, 1)]
    public void SpeedSmoothingIsAppliedAndClampedForEveryLayout(
        HudLayoutMode layoutMode,
        double configuredSmoothing,
        double expected)
    {
        Assert.Equal(
            expected,
            AppController.ResolveSpeedSmoothing(layoutMode, configuredSmoothing),
            10);
    }

    [Fact]
    public void FrozenRaceOnTimestampDoesNotRefreshHudActivity()
    {
        var previous = TestState(timestamp: 500, raceOn: true);
        var duplicate = TestState(timestamp: 500, raceOn: true);

        Assert.False(AppController.ShouldRecordTelemetryActivity(previous, duplicate));
    }

    [Fact]
    public void AdvancingSimulationOrRaceOffRefreshesHudActivity()
    {
        var previous = TestState(timestamp: 500, raceOn: true);

        Assert.True(AppController.ShouldRecordTelemetryActivity(
            previous,
            TestState(timestamp: 501, raceOn: true)));
        Assert.True(AppController.ShouldRecordTelemetryActivity(
            previous,
            TestState(timestamp: 500, raceOn: false)));
    }

    [Fact]
    public void TransientRaceOffRetainsActiveHudInsideHysteresis()
    {
        var now = DateTimeOffset.UtcNow;

        var transition = AppController.EvaluateNativeHudTelemetryTransition(
            nativeHudTelemetryActive: true,
            raceOffObservedAtUtc: null,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now);

        Assert.True(transition.Active);
        Assert.True(transition.HoldForRaceOffHysteresis);
        Assert.Equal(now, transition.RaceOffObservedAtUtc);
        Assert.False(transition.ActiveChanged);
    }

    [Fact]
    public void RaceOnCancelsPendingRaceOff()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = AppController.EvaluateNativeHudTelemetryTransition(
            nativeHudTelemetryActive: true,
            raceOffObservedAtUtc: null,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now);

        var recovered = AppController.EvaluateNativeHudTelemetryTransition(
            pending.Active,
            pending.RaceOffObservedAtUtc,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: true,
            now: now + TimeSpan.FromMilliseconds(50));

        Assert.True(recovered.Active);
        Assert.False(recovered.HoldForRaceOffHysteresis);
        Assert.Null(recovered.RaceOffObservedAtUtc);
        Assert.False(recovered.ActiveChanged);
    }

    [Fact]
    public void SustainedRaceOffDeactivatesAfterHysteresis()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = AppController.EvaluateNativeHudTelemetryTransition(
            nativeHudTelemetryActive: true,
            raceOffObservedAtUtc: null,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now);

        var sustained = AppController.EvaluateNativeHudTelemetryTransition(
            pending.Active,
            pending.RaceOffObservedAtUtc,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now + TimeSpan.FromMilliseconds(100));

        Assert.False(sustained.Active);
        Assert.False(sustained.HoldForRaceOffHysteresis);
        Assert.Null(sustained.RaceOffObservedAtUtc);
        Assert.True(sustained.ActiveChanged);
    }

    [Fact]
    public void SustainedRaceOffSignalsDeactivationOnlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var first = AppController.EvaluateNativeHudTelemetryTransition(
            nativeHudTelemetryActive: true,
            raceOffObservedAtUtc: now - TimeSpan.FromMilliseconds(100),
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now);
        var repeated = AppController.EvaluateNativeHudTelemetryTransition(
            first.Active,
            first.RaceOffObservedAtUtc,
            wasDrivingConnected: false,
            hasFreshTelemetry: true,
            isRaceOn: false,
            now: now + TimeSpan.FromMilliseconds(50));

        Assert.True(first.ActiveChanged);
        Assert.False(repeated.ActiveChanged);
        Assert.False(repeated.Active);
    }

    [Fact]
    public void StaleForegroundAfterAltTabPreservesSessionUntilFreshRaceOn()
    {
        var now = DateTimeOffset.UtcNow;
        var backgroundStale = AppController.ShouldPreserveHudVisuals(
            nativeHudTelemetryActive: true,
            connectionState: TelemetryConnectionState.Lost,
            forzaForeground: false,
            forzaRunning: true,
            forzaWindowKnown: true);
        var foregroundStillStale = AppController.ShouldPreserveHudVisuals(
            nativeHudTelemetryActive: true,
            connectionState: TelemetryConnectionState.Lost,
            forzaForeground: true,
            forzaRunning: true,
            forzaWindowKnown: true);
        var freshRaceOn = AppController.EvaluateNativeHudTelemetryTransition(
            nativeHudTelemetryActive: true,
            raceOffObservedAtUtc: null,
            wasDrivingConnected: true,
            hasFreshTelemetry: true,
            isRaceOn: true,
            now: now);

        Assert.True(backgroundStale);
        Assert.True(foregroundStillStale);
        Assert.True(freshRaceOn.Active);
        Assert.False(freshRaceOn.ActiveChanged);
        Assert.Null(freshRaceOn.RaceOffObservedAtUtc);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible, -1, true)]
    [InlineData(NativeGameplayVisibility.Visible, 0, true)]
    [InlineData(NativeGameplayVisibility.Visible, 1, false)]
    [InlineData(NativeGameplayVisibility.Hidden, -1, true)]
    [InlineData(NativeGameplayVisibility.Hidden, 0, true)]
    [InlineData(NativeGameplayVisibility.Hidden, 1, false)]
    public void NativeGameplayVisibilityHasAnExact250MillisecondFreshnessLimit(
        NativeGameplayVisibility visibility,
        int boundaryOffsetTicks,
        bool expectedFresh)
    {
        var observedAt = long.MaxValue - Stopwatch.Frequency;
        var now = observedAt + Stopwatch.Frequency / 4 + boundaryOffsetTicks;
        var snapshot = VisibilitySnapshot(visibility, observedAt);

        var observation = AppController.EvaluateNativeGameplayVisibility(snapshot, now);

        Assert.Equal(visibility, observation.Visibility);
        Assert.Equal(expectedFresh, observation.Fresh);
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    [InlineData(100L, 99L)]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(1L, long.MinValue)]
    public void InvalidOrFutureNativeObservationCannotBecomeAnAltTabRetentionState(long observedAt, long now)
    {
        var observation = AppController.EvaluateNativeGameplayVisibility(
            VisibilitySnapshot(NativeGameplayVisibility.Visible, observedAt), now);

        Assert.Equal(NativeGameplayVisibility.Unknown, observation.Visibility);
        Assert.False(observation.Fresh);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Unknown)]
    [InlineData((NativeGameplayVisibility)99)]
    public void UnknownOrInvalidVisibilityCannotAcquireFreshnessFromATimestamp(NativeGameplayVisibility visibility)
    {
        var observation = AppController.EvaluateNativeGameplayVisibility(VisibilitySnapshot(visibility, 100), 100);

        Assert.Equal(NativeGameplayVisibility.Unknown, observation.Visibility);
        Assert.False(observation.Fresh);
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible, true)]
    [InlineData(NativeGameplayVisibility.Hidden, false)]
    [InlineData(NativeGameplayVisibility.Unknown, false)]
    public void StaleBackgroundObservationRetainsOnlyKnownVisibleGameplay(
        NativeGameplayVisibility visibility,
        bool expectedVisible)
    {
        var observation = AppController.EvaluateNativeGameplayVisibility(
            VisibilitySnapshot(visibility, 1),
            1 + Stopwatch.Frequency);

        Assert.False(observation.Fresh);
        Assert.Equal(expectedVisible, OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: false,
            gameAwareVisibility: true,
            forzaForeground: false,
            forzaWindowKnown: true,
            editMode: false,
            forzaRunning: true,
            overlayForeground: false,
            nativeGameplayVisibility: observation.Visibility,
            nativeVisibilityFresh: observation.Fresh));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpiredForegroundObservationHidesDespiteFreshTelemetry(bool gameAwareVisibility)
    {
        var observation = AppController.EvaluateNativeGameplayVisibility(
            VisibilitySnapshot(NativeGameplayVisibility.Visible, 1),
            2 + Stopwatch.Frequency / 4);

        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility,
            forzaForeground: true,
            forzaWindowKnown: true,
            editMode: true,
            forzaRunning: true,
            overlayForeground: true,
            nativeGameplayVisibility: observation.Visibility,
            nativeVisibilityFresh: observation.Fresh));
    }

    private static NativeHudSnapshot VisibilitySnapshot(NativeGameplayVisibility visibility, long observedTimestamp) =>
        NativeHudSnapshot.Unavailable() with
        {
            GameplayVisibility = visibility,
            VisibilityObservedTimestamp = observedTimestamp
        };

    private static VehicleState TestState(uint timestamp, bool raceOn) => new()
    {
        IsRaceOn = raceOn,
        GameTimestampMilliseconds = timestamp,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        CarOrdinal = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 0,
        EngineMaximumRpm = 0,
        Gear = TransmissionGear.Neutral,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };
}
