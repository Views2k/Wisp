namespace Wisp.Core;

public enum WheelAggregationMode
{
    Robust,
    RawDrivenWheels
}

public readonly record struct WheelSelection(double AngularSpeedRadiansPerSecond, string Description);

public interface IDrivenWheelSelector
{
    WheelSelection Select(VehicleState state, WheelAggregationMode mode);
}

public sealed class DrivenWheelSelector : IDrivenWheelSelector
{
    public WheelSelection Select(VehicleState state, WheelAggregationMode mode)
    {
        var wheels = state.WheelRotationRadiansPerSecond;

        return state.Drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => new WheelSelection(
                AggregatePair(wheels.FrontLeft, wheels.FrontRight, mode), "Front (FL + FR)"),
            DrivetrainType.RearWheelDrive => new WheelSelection(
                AggregatePair(wheels.RearLeft, wheels.RearRight, mode), "Rear (RL + RR)"),
            DrivetrainType.AllWheelDrive => new WheelSelection(
                AggregateFour(wheels, mode), "All four"),
            _ => throw new ArgumentOutOfRangeException(nameof(state), "Unknown drivetrain value.")
        };
    }

    private static double AggregatePair(float leftValue, float rightValue, WheelAggregationMode mode)
    {
        _ = mode;
        return Math.Abs(((double)leftValue + rightValue) * 0.5);
    }

    private static double AggregateFour(WheelValues values, WheelAggregationMode mode)
    {
        Span<double> speeds = stackalloc double[4]
        {
            values.FrontLeft,
            values.FrontRight,
            values.RearLeft,
            values.RearRight
        };

        _ = mode;
        return Math.Abs((speeds[0] + speeds[1] + speeds[2] + speeds[3]) * 0.25);
    }
}
