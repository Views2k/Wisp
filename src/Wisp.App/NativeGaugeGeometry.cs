using Wisp.Core;

namespace Wisp.App;

public static class NativeGaugeGeometry
{
    public const double AnalogStartAngleDegrees = 120;
    public const double ElectricAnalogStartAngleDegrees = 150;
    public const double AnalogSweepAngleDegrees = 240;
    public const double AnalogLargeDashRpm = 1000;
    public const double AnalogSmallDashRpm = 333.333343505859375;
    public const double AnalogNeedleShutterMilliseconds = 4;
    public const double AnalogMaximumNeedleBlurRadians = 0.65;
    public const int MaximumTextureSpeed = 999;

    public static int ScaleMaximumThousands(double engineMaximumRpm)
    {
        if (!double.IsFinite(engineMaximumRpm) || engineMaximumRpm <= 0)
        {
            return 0;
        }

        // FH6's native gauge state already carries a ceiling rounded to the
        // next 1,000 RPM. Ceiling also preserves that rule for fallback data.
        return Math.Clamp((int)Math.Ceiling(engineMaximumRpm / 1000d), 1, 30);
    }

    public static double ScaleMaximumRpm(double engineMaximumRpm) =>
        ScaleMaximumThousands(engineMaximumRpm) * 1000d;

    public static double NormalizedRpm(double engineRpm, double engineMaximumRpm)
    {
        var scaleMaximumRpm = ScaleMaximumRpm(engineMaximumRpm);
        if (!double.IsFinite(engineRpm) || scaleMaximumRpm <= 0)
        {
            return 0;
        }

        return Math.Clamp(engineRpm / scaleMaximumRpm, 0, 1);
    }

    public static bool HasExactTachometerState(
        ExactRedlineResult exactRedline,
        double tachometerMaximumRpm) =>
        exactRedline.IsExact &&
        double.IsFinite(tachometerMaximumRpm) &&
        tachometerMaximumRpm > 0 &&
        exactRedline.Rpm <= tachometerMaximumRpm;

    public static double AnalogNeedleAngle(double engineRpm, double engineMaximumRpm) =>
        AnalogStartAngleDegrees + (NormalizedRpm(engineRpm, engineMaximumRpm) * AnalogSweepAngleDegrees);

    public static double ElectricAnalogNeedleAngle(double speed, double maximumSpeed)
    {
        var normalized = double.IsFinite(speed) &&
                         double.IsFinite(maximumSpeed) &&
                         maximumSpeed > 0
            ? Math.Clamp(speed / maximumSpeed, 0, 1)
            : 0;
        return ElectricAnalogStartAngleDegrees + (normalized * AnalogSweepAngleDegrees);
    }

    // FH6's native needle material registers a 4 ms shutter and clamps the
    // signed angular blur to 0.65 radians.
    public static double AnalogNeedleBlurRadians(double angleDeltaDegrees, double elapsedSeconds)
    {
        if (!double.IsFinite(angleDeltaDegrees) ||
            !double.IsFinite(elapsedSeconds) ||
            elapsedSeconds <= 0 ||
            elapsedSeconds > 0.25)
        {
            return 0;
        }

        var angularVelocityDegreesPerSecond = angleDeltaDegrees / elapsedSeconds;
        var shutterSeconds = AnalogNeedleShutterMilliseconds / 1000d;
        return Math.Clamp(
            -angularVelocityDegreesPerSecond * Math.PI / 180d * shutterSeconds,
            -AnalogMaximumNeedleBlurRadians,
            AnalogMaximumNeedleBlurRadians);
    }

    public static double RedlineStartNormalized(
        ExactRedlineResult exactRedline,
        double engineMaximumRpm)
    {
        if (!HasExactTachometerState(exactRedline, engineMaximumRpm))
        {
            return 1;
        }

        return Math.Clamp(exactRedline.Rpm / ScaleMaximumRpm(engineMaximumRpm), 0, 1);
    }

    public static bool IsRedlineValue(double valueThousands, ExactRedlineResult exactRedline) =>
        exactRedline.IsExact && valueThousands * 1000 >= exactRedline.Rpm;

    // HUDSpeedometerControl exposes IsLit per RPM label but not its view-model
    // formula. This is the source-consistent threshold used by the native gauge:
    // a label becomes lit once live RPM reaches that label's 1000-RPM position.
    public static bool IsAnalogRpmNumberLit(int valueThousands, double engineRpm) =>
        valueThousands >= 0 &&
        double.IsFinite(engineRpm) &&
        engineRpm >= valueThousands * 1000d;

    public static bool IsShiftLightActive(double engineRpm, ExactRedlineResult exactRedline)
    {
        if (!double.IsFinite(engineRpm) || !exactRedline.IsExact)
        {
            return false;
        }

        return engineRpm >= exactRedline.Rpm;
    }

    public static int ClampSpeed(int speed) => Math.Clamp(speed, 0, MaximumTextureSpeed);

    public static (int Hundreds, int Tens, int Ones) SpeedDigits(int speed)
    {
        speed = ClampSpeed(speed);
        return (speed / 100, (speed / 10) % 10, speed % 10);
    }

    public static string? GearToken(
        TransmissionGear gear,
        GearDisplayMode displayMode = GearDisplayMode.Manual) => gear switch
        {
            TransmissionGear.Reverse => "R",
            TransmissionGear.Neutral => "N",
            >= TransmissionGear.First and <= TransmissionGear.Tenth =>
                displayMode == GearDisplayMode.Automatic ? "Drive" : ((int)gear).ToString(),
            _ => null
        };
}
