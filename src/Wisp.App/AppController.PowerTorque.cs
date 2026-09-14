namespace Wisp.App;

public sealed partial class AppController
{
    public void SetPowerTorqueScalesFromCurrentRun()
    {
        if (_disposed || !ViewModel.SetPowerTorqueScalesFromCurrentRun()) return;
        ScheduleSettingsSave();
    }
}
