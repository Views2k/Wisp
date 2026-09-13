namespace Wisp.Core;

public sealed record DriftZoneScoringProfile(
    double MinimumAngleDegrees,
    double MaximumAngleDegrees,
    double SaturationAngleDegrees,
    double MinimumAngleMultiplier,
    double MaximumAngleMultiplier)
{
    public bool IsValid =>
        double.IsFinite(MinimumAngleDegrees) && double.IsFinite(MaximumAngleDegrees) && double.IsFinite(SaturationAngleDegrees) &&
        MinimumAngleDegrees >= 0 && MinimumAngleDegrees < SaturationAngleDegrees &&
        SaturationAngleDegrees <= MaximumAngleDegrees && MaximumAngleDegrees <= 180 &&
        double.IsFinite(MinimumAngleMultiplier) && double.IsFinite(MaximumAngleMultiplier) &&
        MinimumAngleMultiplier >= 0 && MaximumAngleMultiplier > 0 && MinimumAngleMultiplier <= MaximumAngleMultiplier;
}

public static class DriftZoneScoring
{
    // The game's angle calculation requires velocity length squared greater than 0.0001.
    private const double MinimumVelocitySquared = 0.0001;

    public static DriftAngleReading Measure(VehicleState? state, DriftZoneScoringProfile? profile)
    {
        if (profile is { IsValid: false } ||
            state is not
            {
                IsRaceOn: true, LocalVelocityXMetersPerSecond: { } x,
                LocalVelocityYMetersPerSecond: { } y, LocalVelocityZMetersPerSecond: { } z
            } ||
            !FiniteVelocity(x) || !FiniteVelocity(y) || !FiniteVelocity(z) || !FiniteVelocity(state.GroundSpeedMetersPerSecond))
            return Unavailable(DriftGuidanceState.Unavailable);

        var speedSquared = (double)x * x + (double)y * y + (double)z * z;
        if (speedSquared <= MinimumVelocitySquared) return Unavailable(DriftGuidanceState.LowSpeed);
        var speed = Math.Sqrt(speedSquared);
        if (speed > Math.Abs(state.GroundSpeedMetersPerSecond) + Math.Max(1, speed * .1))
            return Unavailable(DriftGuidanceState.Unavailable);

        // This is the 3-D angle from the car's forward axis, not planar sideslip.
        var magnitude = Math.Acos(Math.Clamp(z / speed, -1, 1)) * 180 / Math.PI;
        double? signed = x == 0 ? magnitude == 0 ? 0 : null : Math.CopySign(magnitude, x);
        var guidance = profile is null ? DriftGuidanceState.ProfileUnavailable :
            magnitude < profile.MinimumAngleDegrees ? DriftGuidanceState.BelowScoringAngle :
            magnitude > profile.MaximumAngleDegrees ? DriftGuidanceState.AboveScoringAngle :
            magnitude >= profile.SaturationAngleDegrees ? DriftGuidanceState.MaximumAngleBonus : DriftGuidanceState.AngleBonusIncreasing;
        return new(signed, guidance) { IsZoneGuidance = true, MagnitudeDegrees = magnitude };
    }

    // An angle multiplier alone does not establish that a zone is active or points are being awarded.
    public static double? EvaluateAngleMultiplier(double angle, DriftZoneScoringProfile? profile)
    {
        if (profile is not { IsValid: true } || !double.IsFinite(angle) || angle is < 0 or > 180) return null;
        var fraction = Math.Clamp((angle - profile.MinimumAngleDegrees) /
            (profile.SaturationAngleDegrees - profile.MinimumAngleDegrees), 0, 1);
        return profile.MinimumAngleMultiplier + fraction * (profile.MaximumAngleMultiplier - profile.MinimumAngleMultiplier);
    }

    private static bool FiniteVelocity(float value) => float.IsFinite(value) && Math.Abs(value) <= 500;
    private static DriftAngleReading Unavailable(DriftGuidanceState state) => new(null, state) { IsZoneGuidance = true };
}
