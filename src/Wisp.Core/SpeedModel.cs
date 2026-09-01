namespace Wisp.Core;

public enum SpeedUnit
{
    MilesPerHour,
    KilometersPerHour
}

public enum SpeedSourceMode
{
    WheelIndicated = 0,
    Fh6VehicleSpeed = 1
}

public readonly record struct IndicatedSpeed(
    double MetersPerSecond,
    double DisplayValue,
    bool IsAvailable,
    bool UsesEstimatedRadius,
    string SelectedWheels);

public readonly record struct RollingRadii(double FrontMeters, double RearMeters)
{
    public static RollingRadii Uniform(double radiusMeters) => new(radiusMeters, radiusMeters);

    public bool IsPlausible =>
        RollingRadiusEstimator.IsPlausibleRadius(FrontMeters) &&
        RollingRadiusEstimator.IsPlausibleRadius(RearMeters);

    public double Representative(DrivetrainType drivetrain) => drivetrain switch
    {
        DrivetrainType.FrontWheelDrive => FrontMeters,
        DrivetrainType.RearWheelDrive => RearMeters,
        DrivetrainType.AllWheelDrive => (FrontMeters + RearMeters) * 0.5,
        _ => double.NaN
    };
}

public static class DrivenWheelSpeed
{
    public static double MetersPerSecond(VehicleState state, RollingRadii radii)
    {
        var wheels = state.WheelRotationRadiansPerSecond;
        var front = ((double)wheels.FrontLeft + wheels.FrontRight) * 0.5 * radii.FrontMeters;
        var rear = ((double)wheels.RearLeft + wheels.RearRight) * 0.5 * radii.RearMeters;
        return Math.Abs(state.Drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => front,
            DrivetrainType.RearWheelDrive => rear,
            DrivetrainType.AllWheelDrive => (front + rear) * 0.5,
            _ => double.NaN
        });
    }
}

public sealed class SpeedModel
{
    public const double MetersPerSecondToMilesPerHour = 2.2369362920544;
    public const double MetersPerSecondToKilometersPerHour = 3.6;
    public const double MaximumIndicatedMetersPerSecond = 250.0;
    public const double MaximumSmoothingDeviationMetersPerSecond =
        1.5 / MetersPerSecondToMilesPerHour;

    private readonly IDrivenWheelSelector _selector;
    private double? _filteredMetersPerSecond;
    private int _lastCarOrdinal;

    public SpeedModel(IDrivenWheelSelector? selector = null)
    {
        _selector = selector ?? new DrivenWheelSelector();
    }

    public IndicatedSpeed Calculate(
        VehicleState state,
        double? rollingRadiusMeters,
        SpeedUnit unit,
        WheelAggregationMode aggregationMode,
        double smoothing,
        TimeSpan elapsed)
    {
        var radii = rollingRadiusMeters is { } radius
            ? RollingRadii.Uniform(radius)
            : (RollingRadii?)null;
        return CalculateWithRadii(state, radii, unit, aggregationMode, smoothing, elapsed);
    }

    public IndicatedSpeed CalculateWithRadii(
        VehicleState state,
        RollingRadii? rollingRadii,
        SpeedUnit unit,
        WheelAggregationMode aggregationMode,
        double smoothing,
        TimeSpan elapsed)
    {
        var selection = _selector.Select(state, aggregationMode);
        if (rollingRadii is not { } radii || !radii.IsPlausible)
        {
            _filteredMetersPerSecond = null;
            _lastCarOrdinal = state.CarOrdinal;
            return new IndicatedSpeed(0, 0, false, false, selection.Description);
        }

        var multiplier = unit == SpeedUnit.MilesPerHour
            ? MetersPerSecondToMilesPerHour
            : MetersPerSecondToKilometersPerHour;
        var raw = DrivenWheelSpeed.MetersPerSecond(state, radii);
        if (!double.IsFinite(raw) || raw < 0 || raw > MaximumIndicatedMetersPerSecond)
        {
            if (_filteredMetersPerSecond is { } lastValid && _lastCarOrdinal == state.CarOrdinal)
            {
                return new IndicatedSpeed(
                    lastValid,
                    lastValid * multiplier,
                    true,
                    false,
                    selection.Description);
            }

            _filteredMetersPerSecond = null;
            _lastCarOrdinal = state.CarOrdinal;
            return new IndicatedSpeed(0, 0, false, false, selection.Description);
        }

        if (_filteredMetersPerSecond is null || _lastCarOrdinal != state.CarOrdinal || elapsed > TimeSpan.FromMilliseconds(500))
        {
            _filteredMetersPerSecond = raw;
        }
        else
        {
            var normalizedSmoothing = Math.Clamp(smoothing, 0, 1);
            var timeConstantSeconds = normalizedSmoothing * 0.25;
            var alpha = timeConstantSeconds <= 0
                ? 1.0
                : 1.0 - Math.Exp(-Math.Clamp(elapsed.TotalSeconds, 0, 0.5) / timeConstantSeconds);
            _filteredMetersPerSecond += (raw - _filteredMetersPerSecond.Value) * alpha;
            _filteredMetersPerSecond = Math.Clamp(
                _filteredMetersPerSecond.Value,
                Math.Max(0, raw - MaximumSmoothingDeviationMetersPerSecond),
                raw + MaximumSmoothingDeviationMetersPerSecond);
        }

        _lastCarOrdinal = state.CarOrdinal;

        return new IndicatedSpeed(
            _filteredMetersPerSecond.Value,
            _filteredMetersPerSecond.Value * multiplier,
            true,
            false,
            selection.Description);
    }

    public IndicatedSpeed CalculateVehicleSpeed(VehicleState state, SpeedUnit unit)
    {
        // FH6's Speed field is already metres per second. Do not run it through
        // the wheel-radius filter or smoothing: this mode is intended to match
        // the game's own ground-speed readout from the same telemetry frame.
        var raw = Math.Abs((double)state.GroundSpeedMetersPerSecond);
        if (!double.IsFinite(raw) || raw > MaximumIndicatedMetersPerSecond)
        {
            _filteredMetersPerSecond = null;
            _lastCarOrdinal = state.CarOrdinal;
            return new IndicatedSpeed(0, 0, false, false, "FH6 vehicle speed");
        }

        _filteredMetersPerSecond = null;
        _lastCarOrdinal = state.CarOrdinal;
        var multiplier = unit == SpeedUnit.MilesPerHour
            ? MetersPerSecondToMilesPerHour
            : MetersPerSecondToKilometersPerHour;
        return new IndicatedSpeed(
            raw,
            raw * multiplier,
            true,
            false,
            "FH6 vehicle speed");
    }

    public void Reset()
    {
        _filteredMetersPerSecond = null;
        _lastCarOrdinal = 0;
    }
}
