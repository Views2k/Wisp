namespace Wisp.App;

public sealed partial class AppController
{
    public void StartShiftCalibration() => ViewModel.StartShiftCalibration();

    public void CancelShiftCalibration() => ViewModel.CancelShiftCalibration();

    private void UpdateShiftCueObservation()
    {
        var enabled = Settings.AccelerationShiftCueEnabled && !Settings.RequiresSetup &&
            !_runtimeSuspended && !_disposed;
        _receiver.ValidatedStateObserver = enabled ? ViewModel.ObserveShiftCueTelemetry : null;
        if (!enabled) ViewModel.ResetShiftCueObservations();
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
