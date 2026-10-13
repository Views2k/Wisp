namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private bool _backgroundAnimationFeatureAvailable = true;
    public bool EffectiveBackgroundAnimation => _backgroundAnimationFeatureAvailable && AnimatedBackground;
    internal void SetBackgroundAnimationFeatureAvailable(bool available)
    {
        _backgroundAnimationFeatureAvailable = available;
        OnPropertyChanged(nameof(EffectiveBackgroundAnimation));
    }

    private bool _shiftGuidanceFeatureAvailable = true;
    private string _shiftGuidanceFeatureReason = "Shift guidance is temporarily paused.";
    internal void SetShiftGuidanceFeatureAvailable(bool available, string? reason)
    {
        _shiftGuidanceFeatureReason = reason ?? "Shift guidance is temporarily paused.";
        if (_shiftGuidanceFeatureAvailable == available)
        {
            if (!available) ShiftCueStatus = _shiftGuidanceFeatureReason;
            return;
        }
        _shiftGuidanceFeatureAvailable = available;
        ResetShiftCue();
        NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
        ShiftCueStatus = available ? AccelerationShiftCueEnabled ? "Waiting for driving data" : "Off" : _shiftGuidanceFeatureReason;
    }

    private bool _tireCalibrationFeatureAvailable = true;
    private string _tireCalibrationFeatureReason = "Tire calibration is temporarily paused.";
    public string TireCalibrationActionHint => _tireCalibrationFeatureAvailable
        ? "Use after changing wheel or tire diameter on this car." : _tireCalibrationFeatureReason;

    internal void SetTireCalibrationFeatureAvailable(bool available, string? reason)
    {
        _tireCalibrationFeatureAvailable = available;
        _tireCalibrationFeatureReason = reason ?? "Tire calibration is temporarily paused.";
        OnPropertyChanged(nameof(CanRelearnCurrentTires));
        OnPropertyChanged(nameof(TireCalibrationActionHint));
    }

    private bool _applicationUpdatesFeatureAvailable = true;
    private string _applicationUpdatesFeatureReason = "Application updates are temporarily paused.";

    internal void SetApplicationUpdateFeatureAvailable(bool available, string? reason)
    {
        _applicationUpdatesFeatureAvailable = available;
        _applicationUpdatesFeatureReason = reason ?? "Application updates are temporarily paused.";
        OnPropertyChanged(nameof(ApplicationUpdateStatus));
        OnPropertyChanged(nameof(ApplicationUpdateAction));
        OnPropertyChanged(nameof(CanCheckApplicationUpdate));
        OnPropertyChanged(nameof(IsApplicationUpdateAvailable));
    }
}
