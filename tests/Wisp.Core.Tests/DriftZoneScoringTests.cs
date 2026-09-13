using Xunit;

namespace Wisp.Core.Tests;

public sealed class DriftZoneScoringTests
{
    private static readonly DriftZoneScoringProfile VerifiedProfile = new(10, 110, (double)59.4f, 30, 40);

    [Theory]
    [InlineData(0, DriftGuidanceState.BelowScoringAngle)]
    [InlineData(9.999, DriftGuidanceState.BelowScoringAngle)]
    [InlineData(10.001, DriftGuidanceState.AngleBonusIncreasing)]
    [InlineData(59.399, DriftGuidanceState.AngleBonusIncreasing)]
    [InlineData(59.401, DriftGuidanceState.MaximumAngleBonus)]
    [InlineData(90, DriftGuidanceState.MaximumAngleBonus)]
    [InlineData(109.999, DriftGuidanceState.MaximumAngleBonus)]
    [InlineData(110.001, DriftGuidanceState.AboveScoringAngle)]
    [InlineData(135, DriftGuidanceState.AboveScoringAngle)]
    public void NativeAngleRangeAndSaturationApplyInBothDirections(double angle, DriftGuidanceState expected)
    {
        var radians = angle * Math.PI / 180;
        foreach (var direction in new[] { -1, 1 })
        {
            var state = State((float)(direction * 20 * Math.Sin(radians)), 0, (float)(20 * Math.Cos(radians)));
            var reading = DriftZoneScoring.Measure(state, VerifiedProfile);
            Assert.True(reading.IsZoneGuidance);
            Assert.Equal(expected, reading.State);
            Assert.Equal(angle, reading.MagnitudeDegrees!.Value, 4);
            Assert.Equal(direction * angle, reading.SignedDegrees!.Value, 4);
        }
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 30)]
    [InlineData(90, 40)]
    [InlineData(110, 40)]
    [InlineData(110.001, 40)]
    [InlineData(180, 40)]
    public void AngleMultiplierClampsWithoutClaimingScoringEligibility(double angle, double expected)
    {
        Assert.Equal(expected, DriftZoneScoring.EvaluateAngleMultiplier(angle, VerifiedProfile));
    }

    [Fact]
    public void ExactCapturedSaturationAndLinearMidpointProduceTheExpectedMultipliers()
    {
        Assert.Equal(40d, DriftZoneScoring.EvaluateAngleMultiplier((double)59.4f, VerifiedProfile));
        Assert.Equal(35d, DriftZoneScoring.EvaluateAngleMultiplier((10 + (double)59.4f) / 2, VerifiedProfile));
        Assert.Equal(40d, DriftZoneScoring.EvaluateAngleMultiplier(Math.BitIncrement((double)59.4f), VerifiedProfile));
        Assert.True(DriftZoneScoring.EvaluateAngleMultiplier((double)59.4f - .001, VerifiedProfile) < 40);
    }

    [Fact]
    public void NonplanarVelocityChangesTheScoringAngleWithoutChangingCustomSideslip()
    {
        var state = State(20, 20, 20);
        var zone = DriftZoneScoring.Measure(state, VerifiedProfile);
        Assert.Equal(54.735610317245346, zone.MagnitudeDegrees!.Value, 6);
        Assert.Equal(DriftGuidanceState.AngleBonusIncreasing, zone.State);
        var custom = DriftAngle.Measure(state, 40, 10);
        Assert.Equal(45d, custom.SignedDegrees!.Value, 6);
        Assert.False(custom.IsZoneGuidance);
        Assert.Null(custom.MagnitudeDegrees);
    }

    [Theory]
    [InlineData(20, 0, 90)]
    [InlineData(-20, 0, 90)]
    [InlineData(20, 20, 45)]
    [InlineData(0, -20, 180)]
    public void VelocityWithoutALateralDirectionNeverInventsALeftOrRightSign(float y, float z, double expected)
    {
        var reading = DriftZoneScoring.Measure(State(0, y, z), VerifiedProfile);
        Assert.Equal(expected, reading.MagnitudeDegrees!.Value, 6);
        Assert.Null(reading.SignedDegrees);
    }

    [Theory]
    [InlineData(0, DriftGuidanceState.LowSpeed)]
    [InlineData(.009f, DriftGuidanceState.LowSpeed)]
    [InlineData(.011f, DriftGuidanceState.BelowScoringAngle)]
    [InlineData(1f, DriftGuidanceState.BelowScoringAngle)]
    public void ZoneGeometryRetainsTheNativeNearZeroThresholdIndependentlyOfTheDisplayGate(float speed, DriftGuidanceState expected)
    {
        var reading = DriftZoneScoring.Measure(State(0, 0, speed) with { GroundSpeedMetersPerSecond = speed }, VerifiedProfile);
        Assert.Equal(expected, reading.State);
    }

    [Fact]
    public void SelectedReverseGearDoesNotReplaceTheMeasuredAngleWithACustomModeGate()
    {
        var state = State(20, 0, 20);
        Assert.Equal(DriftZoneScoring.Measure(state, VerifiedProfile),
            DriftZoneScoring.Measure(state with { Gear = TransmissionGear.Reverse }, VerifiedProfile));
    }

    [Fact]
    public void MissingProfileRetainsGeometryButMakesNoBonusClaim()
    {
        var reading = DriftZoneScoring.Measure(State(-20, 0, 20), null);
        Assert.Equal(DriftGuidanceState.ProfileUnavailable, reading.State);
        Assert.Equal(45d, reading.MagnitudeDegrees!.Value, 6);
        Assert.Equal(-45d, reading.SignedDegrees!.Value, 6);
        Assert.True(reading.IsZoneGuidance);
        Assert.Null(DriftZoneScoring.EvaluateAngleMultiplier(45, null));
    }

    [Fact]
    public void MissingChannelsMalformedTelemetryAndMenusCannotProduceZoneGuidance()
    {
        var state = State(20, 0, 20);
        foreach (var invalid in new VehicleState?[]
        {
            null, state with { IsRaceOn = false }, state with { LocalVelocityXMetersPerSecond = null },
            state with { LocalVelocityYMetersPerSecond = null }, state with { LocalVelocityZMetersPerSecond = null },
            state with { LocalVelocityXMetersPerSecond = float.NaN }, state with { LocalVelocityYMetersPerSecond = float.PositiveInfinity },
            state with { LocalVelocityZMetersPerSecond = float.NegativeInfinity }, state with { LocalVelocityYMetersPerSecond = 501 },
            state with { GroundSpeedMetersPerSecond = float.NaN }, state with { GroundSpeedMetersPerSecond = 501 },
            state with { GroundSpeedMetersPerSecond = 4 }
        })
        {
            var reading = DriftZoneScoring.Measure(invalid, VerifiedProfile);
            Assert.Equal(DriftGuidanceState.Unavailable, reading.State);
            Assert.True(reading.IsZoneGuidance);
            Assert.Null(reading.SignedDegrees);
            Assert.Null(reading.MagnitudeDegrees);
        }
    }

    [Fact]
    public void InvalidProfilesCannotProduceGeometryOrMultiplierClaims()
    {
        foreach (var invalid in new[]
        {
            VerifiedProfile with { MinimumAngleDegrees = double.NaN },
            VerifiedProfile with { MinimumAngleDegrees = -1 },
            VerifiedProfile with { MaximumAngleDegrees = 181 },
            VerifiedProfile with { MaximumAngleDegrees = double.PositiveInfinity },
            VerifiedProfile with { SaturationAngleDegrees = 10 },
            VerifiedProfile with { SaturationAngleDegrees = 111 },
            VerifiedProfile with { MinimumAngleMultiplier = -1 },
            VerifiedProfile with { MaximumAngleMultiplier = double.NaN },
            VerifiedProfile with { MaximumAngleMultiplier = 0 },
            VerifiedProfile with { MaximumAngleMultiplier = 29 }
        })
        {
            Assert.False(invalid.IsValid);
            Assert.Null(DriftZoneScoring.EvaluateAngleMultiplier(45, invalid));
            var reading = DriftZoneScoring.Measure(State(20, 0, 20), invalid);
            Assert.Equal(DriftGuidanceState.Unavailable, reading.State);
            Assert.Null(reading.MagnitudeDegrees);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(181)]
    public void InvalidAngleHasNoMultiplier(double angle) => Assert.Null(DriftZoneScoring.EvaluateAngleMultiplier(angle, VerifiedProfile));

    private static VehicleState State(float x, float y, float z) => TestVehicleState.Create(groundSpeed: 40) with
    {
        LocalVelocityXMetersPerSecond = x,
        LocalVelocityYMetersPerSecond = y,
        LocalVelocityZMetersPerSecond = z
    };
}
