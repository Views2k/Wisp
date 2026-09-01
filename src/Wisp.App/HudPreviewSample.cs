using Wisp.Core;

namespace Wisp.App;

internal static class HudPreviewSample
{
    public const string Caption = "Sample preview · illustrative values, not live FH6 data";
    private const double SpeedMetersPerSecond = 30;

    // This illustrative frame belongs only to the settings preview, never the live overlay.
    private static readonly NativeGaugeFrame Frame = new(
        true,
        67,
        4_200,
        8_000,
        TransmissionGear.Fourth,
        SpeedUnit.MilesPerHour,
        new ExactRedlineResult(
            ExactRedlineStatus.Exact,
            7_000 * 2 * Math.PI / 60,
            7_000,
            "Illustrative preview only"),
        NativeAssistSnapshot.Unavailable(),
        NativeNeedleAngleDegrees: NativeGaugeGeometry.AnalogNeedleAngle(4_200, 8_000),
        NativeNeedleBlurAmount: 0);

    public static NativeGaugeFrame Create(SpeedUnit unit, GearDisplayMode gearDisplayMode)
    {
        var multiplier = unit == SpeedUnit.MilesPerHour
            ? SpeedModel.MetersPerSecondToMilesPerHour
            : SpeedModel.MetersPerSecondToKilometersPerHour;
        return Frame with
        {
            Speed = (int)Math.Floor(SpeedMetersPerSecond * multiplier),
            Unit = unit,
            GearDisplayMode = gearDisplayMode
        };
    }
}
