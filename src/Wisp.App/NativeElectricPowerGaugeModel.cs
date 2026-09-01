namespace Wisp.App;

internal sealed class NativeElectricPowerGaugeModel
{
    private const double NativeRegenIdleFill = 0.04;

    public NativeElectricPowerGaugeDisplay Update(
        double nativeRegenFillAmount = double.NaN,
        double nativePowerFillAmount = double.NaN,
        double nativeRegenPowerRatio = double.NaN)
    {
        if (TryNativeDisplay(
                nativeRegenFillAmount,
                nativePowerFillAmount,
                nativeRegenPowerRatio,
                out var native))
        {
            return native;
        }

        // Missing or partial native state is not replaced with a plausible bar.
        // The renderer hides this display until the complete FH6 triplet returns.
        return new NativeElectricPowerGaugeDisplay(
            false,
            0,
            0,
            0);
    }

    internal static bool TryNativeDisplay(
        double regenFillAmount,
        double powerFillAmount,
        double regenPowerRatio,
        out NativeElectricPowerGaugeDisplay display)
    {
        if (!IsNativeRatio(regenFillAmount) ||
            !IsNativeRatio(powerFillAmount) ||
            !IsNativeRatio(regenPowerRatio))
        {
            display = default;
            return false;
        }

        // The original XAML adds its authored 0.04 marker at presentation time.
        display = new NativeElectricPowerGaugeDisplay(
            true,
            regenPowerRatio,
            Math.Clamp(regenFillAmount + NativeRegenIdleFill, 0, 1),
            powerFillAmount);
        return true;
    }

    private static bool IsNativeRatio(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;
}

internal readonly record struct NativeElectricPowerGaugeDisplay(
    bool Available,
    double RegenRatio,
    double RegenFill,
    double PowerFill);
