namespace Wisp.Core.Tunes;

// Forza's completed benchmark results, retained in their original SI units.
// Null is a completed calculation without a numeric result; it is never zero.
public sealed record TunePerformance(
    float? Braking60MphMetres, float? Braking100MphMetres,
    float? LateralG60Mph, float? LateralG120Mph,
    float? Acceleration60MphSeconds, float? Acceleration100MphSeconds,
    float? TopSpeedMetresPerSecond, float? MechanicalBalance,
    float? AeroBalance, float? AeroEfficiency)
{
    public bool IsValid =>
        Valid(Braking60MphMetres, 0, 100000) && Valid(Braking100MphMetres, 0, 100000) &&
        Valid(LateralG60Mph, 0, 100) && Valid(LateralG120Mph, 0, 100) &&
        Valid(Acceleration60MphSeconds, 0, 10000) && Valid(Acceleration100MphSeconds, 0, 10000) &&
        Valid(TopSpeedMetresPerSecond, 0, 1000) && Valid(MechanicalBalance, 0, 1) &&
        Valid(AeroBalance, 0, 1) && Valid(AeroEfficiency, 0, 100);

    private static bool Valid(float? value, float minimum, float maximum) =>
        value is null || float.IsFinite(value.Value) && value >= minimum && value <= maximum;
}
