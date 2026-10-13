namespace Wisp.App;

public sealed partial class AppController
{
    internal bool ShiftCueRuntimeEnabled => FeatureAllowed("shift-guidance") && (Settings.AccelerationShiftCueEnabled || ShiftCaptureActive);
    public void StartShiftCalibration() => ViewModel.StartShiftCalibration();

    public void CancelShiftCalibration() => ViewModel.CancelShiftCalibration();

    private void UpdateShiftCueObservation()
    {
        var enabled = FeatureAllowed("shift-guidance") && Settings.AccelerationShiftCueEnabled && !Settings.RequiresSetup &&
            !_runtimeSuspended && !_disposed;
        var deltaEnabled = HudFeatureAllowed("lap-delta") && Settings.LapDeltaEnabled;
        var mapEnabled = HudFeatureAllowed("lap-map") && Settings.LapMapEnabled;
        var recordingEnabled = FeatureAllowed("lap-recording") && FeatureAllowed("run-library") && Settings.LapReviewRecordingEnabled;
        var lapEnabled = (deltaEnabled || mapEnabled || recordingEnabled) &&
            !Settings.RequiresSetup && !_runtimeSuspended && !_disposed;
        _lapDelta.Configure(lapEnabled, Settings.LapDeltaReference, mapEnabled, Settings.LapTimingMode,
            recordingEnabled);
        _receiver.ValidatedStateObserver = enabled && lapEnabled ? ObserveLapAndShiftTelemetry :
            enabled ? ViewModel.ObserveShiftCueTelemetry : lapEnabled ? _lapDelta.Observe : null;
        if (!enabled) ViewModel.ResetShiftCueObservations();
    }

    private void ObserveLapAndShiftTelemetry(Wisp.Core.VehicleState? state)
    {
        _lapDelta.Observe(state);
        ViewModel.ObserveShiftCueTelemetry(state);
    }

    public void SetShiftCueColor(int stage, string? value)
    {
        var color = ColorCustomization.NormalizePowerTorqueDriftFlash(value);
        switch (stage)
        {
            case 1: Settings.ShiftCueGreenColor = color; break;
            case 2: Settings.ShiftCueYellowColor = color; break;
            case 3: Settings.ShiftCueRedColor = color; break;
            default: return;
        }
        ScheduleSettingsSave();
    }
}
