using Wisp.Core;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private ShiftCalibrationManager? _shiftCalibration;
    private string _shiftCalibrationStatus = "Enable shift guidance, then return to driving to calibrate.";
    private bool _canStartShiftCalibration;
    private bool _canCancelShiftCalibration;

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
        if (_shiftCalibration?.Start() == true)
        {
            ResetShiftCue();
            NativeGaugeFrame = NativeGaugeFrame with { ShiftCue = default };
            ShiftCueStatus = "Calibrating — shift guidance resumes when the checks pass.";
        }
        PublishShiftCalibrationStatus();
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
        ShiftCalibrationStatus = _shiftCalibration.Status;
        CanStartShiftCalibration = AccelerationShiftCueEnabled && _shiftCalibration.CanStart;
        CanCancelShiftCalibration = _shiftCalibration.Active;
    }

    private ShiftCuePerformance? CalibratedShiftPerformance(ShiftCuePerformance? native) =>
        _shiftCalibration is null ? native : _shiftCalibration.Apply(native);
}
