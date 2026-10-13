using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private const string ActiveShiftCalibrationCueStatus = "Calibrating — follow the calibration status below.";
    private ShiftCalibrationManager? _shiftCalibration;
    private string _shiftCalibrationStatus = "Enable shift guidance, then return to driving to calibrate.";
    private bool _canStartShiftCalibration;
    private bool _canCancelShiftCalibration;
    private bool _shiftCalibrationFeatureAvailable = true;
    private string _shiftCalibrationFeatureReason = "Shift calibration is temporarily paused.";

    public string ShiftCalibrationStatus { get => _shiftCalibrationStatus; private set => Set(ref _shiftCalibrationStatus, value); }
    public bool CanStartShiftCalibration { get => _canStartShiftCalibration; private set => Set(ref _canStartShiftCalibration, value); }
    public bool CanCancelShiftCalibration { get => _canCancelShiftCalibration; private set => Set(ref _canCancelShiftCalibration, value); }

    internal void InitializeShiftCalibration(ShiftCalibrationManager manager)
    {
        _shiftCalibration = manager;
        ResetShiftCue();
        PublishShiftCalibrationStatus();
    }

    internal void RefreshShiftCalibration(VehicleState? state, NativeHudSnapshot native, string build, long now)
    {
        _shiftCalibration?.Update(state, native, build, AccelerationShiftCueEnabled, now);
        PublishShiftCalibrationStatus();
    }

    internal void StartShiftCalibration()
    {
        if (_shiftCalibrationFeatureAvailable && _shiftCalibration?.Start() == true)
        {
            ResetShiftCue();
            NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
        }
        PublishShiftCalibrationStatus();
    }

    internal Task SetShiftCalibrationFeatureAvailable(bool available, string? reason)
    {
        _shiftCalibrationFeatureAvailable = available;
        _shiftCalibrationFeatureReason = reason ?? "Shift calibration is temporarily paused.";
        var pending = _shiftCalibration?.SetFeatureAvailable(available) ?? Task.CompletedTask;
        PublishShiftCalibrationStatus();
        return pending;
    }

    internal void CancelShiftCalibration()
    {
        _shiftCalibration?.Cancel();
        ResetShiftCue();
        NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
        PublishShiftCalibrationStatus();
    }

    internal Task SuspendShiftCalibration()
    {
        var pending = _shiftCalibration?.Suspend() ?? Task.CompletedTask;
        ResetShiftCue();
        NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
        PublishShiftCalibrationStatus();
        return pending;
    }

    private void PublishShiftCalibrationStatus()
    {
        if (_shiftCalibration is null) return;
        ShiftCalibrationStatus = _shiftCalibrationFeatureAvailable ? _shiftCalibration.Status : _shiftCalibrationFeatureReason;
        CanStartShiftCalibration = _shiftCalibrationFeatureAvailable && AccelerationShiftCueEnabled && _shiftCalibration.CanStart;
        CanCancelShiftCalibration = _shiftCalibration.Active;
        if (AccelerationShiftCueEnabled && CanCancelShiftCalibration)
            ShiftCueStatus = ActiveShiftCalibrationCueStatus;
        else if (ShiftCueStatus == ActiveShiftCalibrationCueStatus)
            ShiftCueStatus = AccelerationShiftCueEnabled ? "Waiting for driving data" : "Off";
    }

    private ShiftCuePerformance? CalibratedShiftPerformance(ShiftCuePerformance? native) =>
        _shiftCalibration is null ? native : _shiftCalibration.Apply(native);
}
