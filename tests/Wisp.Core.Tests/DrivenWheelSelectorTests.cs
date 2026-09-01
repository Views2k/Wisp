using Xunit;

namespace Wisp.Core.Tests;

public sealed class DrivenWheelSelectorTests
{
    private readonly DrivenWheelSelector _selector = new();

    [Fact]
    public void RwdSelectsRearWheels()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(10, 12, 80, 84));

        var result = _selector.Select(state, WheelAggregationMode.RawDrivenWheels);

        Assert.Equal(82, result.AngularSpeedRadiansPerSecond);
        Assert.Contains("Rear", result.Description);
    }

    [Fact]
    public void FwdSelectsFrontWheels()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.FrontWheelDrive,
            wheelSpeed: new WheelValues(60, 64, 10, 12));

        var result = _selector.Select(state, WheelAggregationMode.RawDrivenWheels);

        Assert.Equal(62, result.AngularSpeedRadiansPerSecond);
        Assert.Contains("Front", result.Description);
    }

    [Fact]
    public void AwdSelectionUsesLiteralMeanOfAllDrivenWheels()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            wheelSpeed: new WheelValues(2, 100, 102, 900));

        var result = _selector.Select(state, WheelAggregationMode.Robust);

        Assert.Equal(276, result.AngularSpeedRadiansPerSecond);
    }

    [Fact]
    public void TwoWheelDriveSelectionPreservesDifferentialMean()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(80, 80, 100, 1_000));

        var result = _selector.Select(state, WheelAggregationMode.Robust);

        Assert.Equal(550, result.AngularSpeedRadiansPerSecond);
    }

    [Fact]
    public void LegacyRobustAndRawModesHaveIdenticalMechanicalSemantics()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(80, 80, 0, 200));

        var legacy = _selector.Select(state, WheelAggregationMode.Robust);
        var raw = _selector.Select(state, WheelAggregationMode.RawDrivenWheels);

        Assert.Equal(100, legacy.AngularSpeedRadiansPerSecond);
        Assert.Equal(raw.AngularSpeedRadiansPerSecond, legacy.AngularSpeedRadiansPerSecond);
    }

    [Fact]
    public void ReverseUsesTheMagnitudeOfTheAlgebraicAxleMean()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(-10, -10, -80, -84));

        var result = _selector.Select(state, WheelAggregationMode.RawDrivenWheels);

        Assert.Equal(82, result.AngularSpeedRadiansPerSecond);
    }

    [Fact]
    public void OppositeWheelDirectionsCancelAtTheDifferentialCarrier()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(10, 10, -100, 100));

        var result = _selector.Select(state, WheelAggregationMode.RawDrivenWheels);

        Assert.Equal(0, result.AngularSpeedRadiansPerSecond);
    }
}
