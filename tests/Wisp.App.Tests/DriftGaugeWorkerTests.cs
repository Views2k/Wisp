using System.Diagnostics;
using Wisp.App.Drift;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftGaugeWorkerTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(249, true)]
    [InlineData(251, false)]
    [InlineData(-1, false)]
    public void MonotonicAgeRejectsStaleAndFutureSamples(double ageMilliseconds, bool available)
    {
        var now = Stopwatch.Frequency * 10;
        var state = State() with { ReceivedTimestamp = now - (long)(ageMilliseconds * Stopwatch.Frequency / 1000) };
        var reading = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, now, DateTimeOffset.UnixEpoch);
        Assert.Equal(available, reading.SignedDegrees.HasValue);
        if (!available) Assert.Equal(DriftGuidanceState.Unavailable, reading.State);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(300, false)]
    [InlineData(-10, false)]
    public void LegacySamplesWithoutStopwatchTimeUseUtcFreshness(double ageMilliseconds, bool available)
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var state = State() with { ReceivedAtUtc = now.AddMilliseconds(-ageMilliseconds), ReceivedTimestamp = null };
        var reading = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, Stopwatch.Frequency, now);
        Assert.Equal(available, reading.SignedDegrees.HasValue);
    }

    [Fact]
    public void MonotonicTimeTakesPriorityOverWallClockChanges()
    {
        var state = State() with { ReceivedTimestamp = Stopwatch.Frequency, ReceivedAtUtc = DateTimeOffset.UnixEpoch };
        var reading = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, Stopwatch.Frequency, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(DriftGuidanceState.OnTarget, reading.State);
        Assert.Equal(45, reading.SignedDegrees!.Value, 6);
    }

    [Fact]
    public void ReadingPreservesAnglesBeyondTheMarkersDisplayRange()
    {
        var state = State() with { LocalVelocityZMetersPerSecond = -10, ReceivedTimestamp = Stopwatch.Frequency };
        var reading = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, Stopwatch.Frequency, DateTimeOffset.UnixEpoch);
        Assert.Equal(DriftGuidanceState.AboveTarget, reading.State);
        Assert.Equal(135, reading.SignedDegrees!.Value, 6);
    }

    [Fact]
    public void ZoneModeUsesVerticalVelocityAndRejectsStaleBonusClaims()
    {
        var now = Stopwatch.Frequency * 10;
        var state = State() with
        {
            LocalVelocityXMetersPerSecond = 8,
            LocalVelocityYMetersPerSecond = 12,
            LocalVelocityZMetersPerSecond = 8,
            GroundSpeedMetersPerSecond = 17,
            ReceivedTimestamp = now
        };
        var profile = DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn);
        var reading = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, now, DateTimeOffset.UnixEpoch,
            DriftGaugeGuidanceMode.DriftZoneAngleBonus, profile);
        Assert.True(reading.IsZoneGuidance);
        Assert.Equal(DriftGuidanceState.MaximumAngleBonus, reading.State);
        Assert.Equal(Math.Acos(8 / Math.Sqrt(272)) * 180 / Math.PI, reading.MagnitudeDegrees!.Value, 8);
        var custom = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, now, DateTimeOffset.UnixEpoch);
        Assert.Equal(45, custom.SignedDegrees!.Value, 8);

        var stale = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, now + Stopwatch.Frequency,
            DateTimeOffset.UnixEpoch, DriftGaugeGuidanceMode.DriftZoneAngleBonus, profile);
        Assert.True(stale.IsZoneGuidance);
        Assert.Equal(DriftGuidanceState.Unavailable, stale.State);
        Assert.Null(stale.MagnitudeDegrees);
        Assert.Null(stale.SignedDegrees);

        var unknownBuild = DriftGaugeRenderWorker.ReadFresh(state, 40, 10, now, DateTimeOffset.UnixEpoch,
            DriftGaugeGuidanceMode.DriftZoneAngleBonus);
        Assert.Equal(DriftGuidanceState.ProfileUnavailable, unknownBuild.State);
        Assert.Equal(reading.MagnitudeDegrees, unknownBuild.MagnitudeDegrees);
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void GroundSpeedThresholdIsFiveMphInclusiveInBothModes(DriftGaugeGuidanceMode mode)
    {
        var threshold = DriftAngle.MinimumPlanarSpeed;
        foreach (var speed in new[] { 0, 2.2351f, MathF.BitDecrement(threshold), threshold, MathF.BitIncrement(threshold), 2.5f })
        {
            var state = State() with
            {
                GroundSpeedMetersPerSecond = speed,
                LocalVelocityXMetersPerSecond = 0,
                LocalVelocityYMetersPerSecond = 0,
                LocalVelocityZMetersPerSecond = speed
            };
            var reading = Read(state, mode);
            Assert.Equal(mode == DriftGaugeGuidanceMode.DriftZoneAngleBonus, reading.IsZoneGuidance);
            Assert.Equal(speed >= threshold, reading.SignedDegrees.HasValue);
            if (speed < threshold) AssertLowSpeed(reading);
            else Assert.Equal(0, reading.SignedDegrees!.Value);
        }
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void StationarySlopeNoiseAndWheelspinNeverExposeAnglesOrBonus(DriftGaugeGuidanceMode mode)
    {
        foreach (var (x, y, z, ground) in new[]
        {
            (.005f, .03f, -.01f, .04f),
            (-.35f, .65f, -.4f, .85f),
            (.01f, -.8f, .001f, .81f),
            (0f, 0f, 0f, 0f)
        })
        {
            var state = State() with
            {
                GroundSpeedMetersPerSecond = ground,
                LocalVelocityXMetersPerSecond = x,
                LocalVelocityYMetersPerSecond = y,
                LocalVelocityZMetersPerSecond = z,
                WheelRotationRadiansPerSecond = new(500, 500, 500, 500),
                EngineRpm = 8000
            };
            AssertLowSpeed(Read(state, mode));
        }
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void VerticalMovementDoesNotReplacePlanarGroundMovement(DriftGaugeGuidanceMode mode)
    {
        var state = State() with
        {
            GroundSpeedMetersPerSecond = 12,
            LocalVelocityXMetersPerSecond = .01f,
            LocalVelocityYMetersPerSecond = 12,
            LocalVelocityZMetersPerSecond = .01f
        };
        AssertLowSpeed(Read(state, mode));
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void FallingBelowThresholdClearsImmediatelyAndReacquiresTheNewDirection(DriftGaugeGuidanceMode mode)
    {
        var state = State() with { LocalVelocityYMetersPerSecond = 0 };
        Assert.Equal(45, Read(state, mode).SignedDegrees!.Value, 6);
        AssertLowSpeed(Read(state with
        {
            GroundSpeedMetersPerSecond = MathF.BitDecrement(DriftAngle.MinimumPlanarSpeed),
            LocalVelocityXMetersPerSecond = -.1f,
            LocalVelocityZMetersPerSecond = -.1f
        }, mode));
        AssertLowSpeed(Read(state with
        {
            GroundSpeedMetersPerSecond = 0,
            LocalVelocityXMetersPerSecond = 0,
            LocalVelocityZMetersPerSecond = 0
        }, mode));
        var resumed = Read(state with { LocalVelocityXMetersPerSecond = -10 }, mode);
        Assert.Equal(-45, resumed.SignedDegrees!.Value, 6);
        if (mode == DriftGaugeGuidanceMode.DriftZoneAngleBonus)
            Assert.Equal(45, resumed.MagnitudeDegrees!.Value, 6);
    }

    [Theory]
    [InlineData(DriftGaugeGuidanceMode.CustomTarget)]
    [InlineData(DriftGaugeGuidanceMode.DriftZoneAngleBonus)]
    public void StaleAndMenuSamplesRemainUnavailableAtLowSpeed(DriftGaugeGuidanceMode mode)
    {
        var stopped = State() with
        {
            GroundSpeedMetersPerSecond = 0,
            LocalVelocityXMetersPerSecond = 0,
            LocalVelocityYMetersPerSecond = 0,
            LocalVelocityZMetersPerSecond = 0
        };
        Assert.Equal(DriftGuidanceState.Unavailable, Read(stopped with { IsRaceOn = false }, mode).State);
        Assert.Equal(DriftGuidanceState.Unavailable,
            Read(stopped with { ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(-1) }, mode).State);
    }

    private static DriftAngleReading Read(VehicleState state, DriftGaugeGuidanceMode mode) =>
        DriftGaugeRenderWorker.ReadFresh(state, 40, 10, Stopwatch.Frequency, DateTimeOffset.UnixEpoch,
            mode, DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn));

    private static void AssertLowSpeed(DriftAngleReading reading)
    {
        Assert.Equal(DriftGuidanceState.LowSpeed, reading.State);
        Assert.Null(reading.SignedDegrees);
        Assert.Null(reading.MagnitudeDegrees);
    }

    private static VehicleState State() => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = 100,
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        CarOrdinal = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 15,
        LocalVelocityXMetersPerSecond = 10,
        LocalVelocityZMetersPerSecond = 10,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 5000,
        EngineMaximumRpm = 8000,
        Gear = TransmissionGear.Second,
        Steering = 0,
        Accelerator = 255,
        Brake = 0
    };
}
