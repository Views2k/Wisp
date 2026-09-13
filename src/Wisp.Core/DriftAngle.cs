namespace Wisp.Core;

public enum DriftGaugeGuidanceMode { CustomTarget = 0, DriftZoneAngleBonus = 1 }

public enum DriftGuidanceState
{
    Unavailable, LowSpeed, Reversing, BelowTarget, OnTarget, AboveTarget,
    ProfileUnavailable, BelowScoringAngle, AngleBonusIncreasing, MaximumAngleBonus, AboveScoringAngle
}

public readonly record struct DriftAngleReading(double? SignedDegrees, DriftGuidanceState State)
{
    public bool IsZoneGuidance { get; init; }
    public double? MagnitudeDegrees { get; init; }
}

public static class DriftAngle
{
    // Five mph, expressed at the same precision as Data Out's velocity fields.
    public const float MinimumPlanarSpeed = 2.2352f;

    // Data Out's common Sled prefix: local X points right and local Z points forward.
    // Body sideslip is the angle of velocity relative to the car, not a tire slip value.
    public static DriftAngleReading Measure(VehicleState? state, double targetDegrees, double toleranceDegrees)
    {
        if (GetMovementRestriction(state) is { } restriction)
            return new(null, restriction);
        var movingState = state!;
        if (movingState.Gear == TransmissionGear.Reverse)
            return new(null, DriftGuidanceState.Reversing);
        var degrees = Math.Atan2(movingState.LocalVelocityXMetersPerSecond!.Value, movingState.LocalVelocityZMetersPerSecond!.Value) * 180 / Math.PI;
        var target = double.IsFinite(targetDegrees) ? Math.Clamp(targetDegrees, 10, 75) : 40;
        var tolerance = double.IsFinite(toleranceDegrees) ? Math.Clamp(toleranceDegrees, 2, 15) : 10;
        var low = Math.Max(0, target - tolerance);
        var high = Math.Min(90, target + tolerance);
        var magnitude = Math.Abs(degrees);
        return new(degrees, magnitude < low ? DriftGuidanceState.BelowTarget :
            magnitude <= high ? DriftGuidanceState.OnTarget : DriftGuidanceState.AboveTarget);
    }

    // This display gate also applies to zone guidance; it does not alter the game's scoring formula.
    public static DriftGuidanceState? GetMovementRestriction(VehicleState? state)
    {
        if (state is not { IsRaceOn: true, LocalVelocityXMetersPerSecond: { } x, LocalVelocityZMetersPerSecond: { } z } ||
            !float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(state.GroundSpeedMetersPerSecond) ||
            Math.Abs(x) > 500 || Math.Abs(z) > 500 || Math.Abs(state.GroundSpeedMetersPerSecond) > 500)
            return DriftGuidanceState.Unavailable;
        var speed = Math.Sqrt((double)x * x + (double)z * z);
        if (speed < MinimumPlanarSpeed || Math.Abs(state.GroundSpeedMetersPerSecond) < MinimumPlanarSpeed)
            return DriftGuidanceState.LowSpeed;
        // Planar velocity cannot substantially exceed the reported 3-D speed.
        if (speed > Math.Abs(state.GroundSpeedMetersPerSecond) + Math.Max(1, speed * .1))
            return DriftGuidanceState.Unavailable;
        return null;
    }
}
