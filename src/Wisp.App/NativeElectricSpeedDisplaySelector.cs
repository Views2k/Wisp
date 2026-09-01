using System.Diagnostics;

namespace Wisp.App;

internal readonly record struct NativeElectricSpeedDisplay(
    int Hundreds,
    int Tens,
    int Ones,
    bool SpeedLessOrEqualOne,
    bool SpeedLessTen,
    bool SpeedLessHundred,
    bool UsesNativeState);

internal static class NativeElectricSpeedDisplaySelector
{
    private static readonly long FreshnessTicks =
        (long)Math.Round(
            Stopwatch.Frequency *
            NativeNeedlePlayback.NativeSampleFreshnessMilliseconds /
            1_000d);

    public static NativeElectricSpeedDisplay Resolve(NativeGaugeFrame frame, long nowTimestamp)
    {
        var native = frame.DisplayedSpeedState;
        var age = nowTimestamp - frame.NativeGaugeObservedTimestamp;
        var useNative = frame.IsElectric &&
                        frame.SpeedAvailable &&
                        frame.SpeedSource == Wisp.Core.SpeedSourceMode.Fh6VehicleSpeed &&
                        !frame.NativeGaugeSourceInvalidated &&
                        native.IsUsable &&
                        native.Unit == frame.Unit &&
                        frame.NativeGaugeObservedTimestamp > 0 &&
                        age >= 0 &&
                        age <= FreshnessTicks;
        if (useNative)
        {
            return new NativeElectricSpeedDisplay(
                native.Hundreds,
                native.Tens,
                native.Ones,
                native.SpeedLessOrEqualOne,
                native.SpeedLessTen,
                native.SpeedLessHundred,
                true);
        }

        var digits = NativeGaugeGeometry.SpeedDigits(frame.Speed);
        return new NativeElectricSpeedDisplay(
            digits.Hundreds,
            digits.Tens,
            digits.Ones,
            frame.Speed <= 1,
            frame.Speed < 10,
            frame.Speed < 100,
            false);
    }
}
