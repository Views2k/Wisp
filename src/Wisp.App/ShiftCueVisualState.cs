namespace Wisp.App;

public readonly record struct ShiftCueVisualState(
    bool Enabled,
    int Stage,
    uint ColorArgb,
    bool FlashOn,
    double TargetRpm)
{
    public bool IsVisible => Enabled && Stage is >= 1 and <= 3 &&
        double.IsFinite(TargetRpm) && TargetRpm > 0 &&
        (ColorArgb >> 24) != 0 && (Stage < 3 || FlashOn);

    private bool HasTarget => Enabled && Stage is >= 0 and <= 3 &&
        double.IsFinite(TargetRpm) && TargetRpm > 0;

    internal (bool HasTarget, bool Visible, uint ColorArgb) Appearance =>
        IsVisible ? (HasTarget, true, ColorArgb) : (HasTarget, false, 0);

    internal bool ReplacesRedlineCue(NativeGaugeFrame frame) => HasTarget && !frame.IsElectric &&
        frame.Gear is >= Wisp.Core.TransmissionGear.First and <= Wisp.Core.TransmissionGear.Tenth;

    internal bool CanRender(NativeGaugeFrame frame) => IsVisible && !frame.IsElectric &&
        frame.Gear is >= Wisp.Core.TransmissionGear.First and <= Wisp.Core.TransmissionGear.Tenth;
}
