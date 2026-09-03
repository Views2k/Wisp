using Wisp.Core;

namespace Wisp.App;

public readonly record struct NativeGaugeFrame(
    bool SpeedAvailable,
    int Speed,
    double EngineRpm,
    double TachometerMaximumRpm,
    TransmissionGear Gear,
    SpeedUnit Unit,
    ExactRedlineResult ExactRedline,
    NativeAssistSnapshot? Assists = null,
    GearDisplayMode GearDisplayMode = GearDisplayMode.Manual,
    bool IsElectric = false,
    double PowerWatts = 0,
    double TorqueNm = 0,
    int CarOrdinal = 0,
    uint GameTimestampMilliseconds = 0,
    byte Accelerator = 0,
    byte Brake = 0,
    long? ReceivedTimestamp = null,
    double NativeNeedleAngleDegrees = double.NaN,
    double NativeNeedleBlurAmount = double.NaN,
    double NativeRegenFillAmount = double.NaN,
    double NativePowerFillAmount = double.NaN,
    double NativeRegenPowerRatio = double.NaN,
    double NativeElectricMaximumSpeed = double.NaN,
    long NativeGaugeObservedTimestamp = 0L,
    bool NativeGaugeSourceInvalidated = false,
    NativeElectricGearState ElectricGearState = default,
    NativeDisplayedSpeedState DisplayedSpeedState = default,
    SpeedSourceMode SpeedSource = SpeedSourceMode.WheelIndicated)
{
    public NativeAssistSnapshot NativeAssists => Assists ?? NativeAssistSnapshot.Unavailable();

    public NativeGaugeFrame PreserveStableTachometerState(NativeGaugeFrame previous)
    {
        if (NativeGaugeGeometry.HasExactTachometerState(ExactRedline, TachometerMaximumRpm) ||
            NativeGaugeSourceInvalidated || CarOrdinal <= 0 || previous.CarOrdinal != CarOrdinal ||
            !NativeGaugeGeometry.HasExactTachometerState(
                previous.ExactRedline,
                previous.TachometerMaximumRpm))
        {
            return this;
        }

        // RPM changes much faster than the independent process-memory sample.
        // A torn comparison during a shift can reject one refresh even though
        // the car and its static tachometer scale have not changed. Keep the
        // last verified scale for that same car until the source is explicitly
        // invalidated, rather than blanking the native material for one frame.
        return this with
        {
            ExactRedline = previous.ExactRedline,
            TachometerMaximumRpm = previous.TachometerMaximumRpm
        };
    }

    public static NativeGaugeFrame Empty(SpeedUnit unit) =>
        new(
            false,
            0,
            0,
            0,
            TransmissionGear.Neutral,
            unit,
            ExactRedlineResult.Unavailable(),
            NativeAssistSnapshot.Unavailable(),
            GearDisplayMode.Manual);
}
