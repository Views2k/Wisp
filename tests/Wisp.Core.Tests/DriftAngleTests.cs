using Xunit;

namespace Wisp.Core.Tests;

public sealed class DriftAngleTests
{
    [Theory]
    [InlineData(0, 20, 0, DriftGuidanceState.BelowTarget)]
    [InlineData(20, 20, 45, DriftGuidanceState.OnTarget)]
    [InlineData(-20, 20, -45, DriftGuidanceState.OnTarget)]
    [InlineData(20, 0, 90, DriftGuidanceState.AboveTarget)]
    [InlineData(20, -20, 135, DriftGuidanceState.AboveTarget)]
    public void BodyAngleUsesVelocityAndPreservesTheCorrectQuadrant(float x, float z, double expected, DriftGuidanceState status)
    {
        var state = TestVehicleState.Create(groundSpeed: 30) with { LocalVelocityXMetersPerSecond = x, LocalVelocityZMetersPerSecond = z };
        var reading = DriftAngle.Measure(state, 40, 10);
        Assert.Equal(expected, reading.SignedDegrees!.Value, 6);
        Assert.Equal(status, reading.State);
        Assert.Equal(reading, DriftAngle.Measure(state with { TireSlipAngle = new(500, -500, 100, -100), Steering = 127 }, 40, 10));
    }

    [Theory]
    [InlineData(20, 1, DriftGuidanceState.OnTarget)]
    [InlineData(-20, 1, DriftGuidanceState.OnTarget)]
    [InlineData(20, 0, DriftGuidanceState.OnTarget)]
    [InlineData(-20, 0, DriftGuidanceState.OnTarget)]
    [InlineData(20, -1, DriftGuidanceState.AboveTarget)]
    [InlineData(-20, -1, DriftGuidanceState.AboveTarget)]
    [InlineData(20, 20, DriftGuidanceState.BelowTarget)]
    public void MaximumTargetBandIncludesNinetyDegreesInBothDirections(float x, float z, DriftGuidanceState expected)
    {
        var state = TestVehicleState.Create(groundSpeed: 30) with { LocalVelocityXMetersPerSecond = x, LocalVelocityZMetersPerSecond = z };
        Assert.Equal(expected, DriftAngle.Measure(state, 75, 15).State);
    }

    [Fact]
    public void TargetBandIncludesItsLowerEndpointAndClampsAtZero()
    {
        var state = TestVehicleState.Create(groundSpeed: 30) with { LocalVelocityXMetersPerSecond = 20, LocalVelocityZMetersPerSecond = 20 };
        Assert.Equal(DriftGuidanceState.OnTarget, DriftAngle.Measure(state, 60, 15).State);
        Assert.Equal(DriftGuidanceState.BelowTarget, DriftAngle.Measure(state with { LocalVelocityXMetersPerSecond = 19 }, 60, 15).State);
        Assert.Equal(DriftGuidanceState.OnTarget, DriftAngle.Measure(state with { LocalVelocityXMetersPerSecond = 0 }, 10, 15).State);
    }

    [Fact]
    public void MissingOldRunChannelsMenusAndMalformedVelocityDoNotProduceGuidance()
    {
        var state = TestVehicleState.Create() with { LocalVelocityXMetersPerSecond = 20, LocalVelocityZMetersPerSecond = 20 };
        foreach (var invalid in new VehicleState?[] { null, TestVehicleState.Create(), state with { IsRaceOn = false },
            state with { LocalVelocityXMetersPerSecond = float.NaN }, state with { LocalVelocityZMetersPerSecond = float.PositiveInfinity },
            state with { LocalVelocityXMetersPerSecond = 501 }, state with { GroundSpeedMetersPerSecond = 4 } })
        {
            var reading = DriftAngle.Measure(invalid, 40, 10);
            Assert.Null(reading.SignedDegrees);
            Assert.Equal(DriftGuidanceState.Unavailable, reading.State);
        }
    }

    [Fact]
    public void LowSpeedAndReverseCannotBeMistakenForAGoodDrift()
    {
        var state = TestVehicleState.Create(groundSpeed: 2) with { LocalVelocityXMetersPerSecond = 1, LocalVelocityZMetersPerSecond = 1 };
        Assert.Equal(DriftGuidanceState.LowSpeed, DriftAngle.Measure(state, 40, 10).State);
        state = state with { GroundSpeedMetersPerSecond = 30, LocalVelocityXMetersPerSecond = 20, LocalVelocityZMetersPerSecond = -20, Gear = TransmissionGear.Reverse };
        Assert.Equal(new DriftAngleReading(null, DriftGuidanceState.Reversing), DriftAngle.Measure(state, 40, 10));
    }

    [Fact]
    public void FiveMphIsInclusiveAtPacketPrecision()
    {
        Assert.Equal(2.2352, DriftAngle.MinimumPlanarSpeed, 6);
        var minimum = DriftAngle.MinimumPlanarSpeed;
        foreach (var speed in new[] { MathF.BitDecrement(minimum), minimum, MathF.BitIncrement(minimum) })
        {
            var state = TestVehicleState.Create(groundSpeed: speed) with
            {
                LocalVelocityXMetersPerSecond = 0,
                LocalVelocityZMetersPerSecond = speed
            };
            var reading = DriftAngle.Measure(state, 40, 10);
            Assert.Equal(speed >= minimum, reading.SignedDegrees.HasValue);
            Assert.Equal(speed < minimum ? DriftGuidanceState.LowSpeed : DriftGuidanceState.BelowTarget, reading.State);
        }
    }

    [Fact]
    public void FiveMphRestrictionChecksGroundMovementAndPlanarVelocityIndependently()
    {
        var state = TestVehicleState.Create(groundSpeed: 0) with
        {
            LocalVelocityXMetersPerSecond = 2,
            LocalVelocityZMetersPerSecond = 2,
            WheelRotationRadiansPerSecond = new(500, 500, 500, 500)
        };
        Assert.Equal(DriftGuidanceState.LowSpeed, DriftAngle.GetMovementRestriction(state));
        Assert.Equal(DriftGuidanceState.LowSpeed, DriftAngle.GetMovementRestriction(state with
        {
            GroundSpeedMetersPerSecond = 10,
            LocalVelocityXMetersPerSecond = .01f,
            LocalVelocityYMetersPerSecond = 10,
            LocalVelocityZMetersPerSecond = .01f
        }));
    }
}
