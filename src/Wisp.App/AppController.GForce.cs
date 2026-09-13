namespace Wisp.App;

public sealed partial class AppController
{
    public void SetCustomGForceColor(string? value)
    {
        if (_disposed) return;
        var normalized = ColorCustomization.NormalizeGauge(value);
        if (Settings.CustomGForceColor == normalized) return;
        Settings.CustomGForceColor = normalized;
        ViewModel.UpdateGForceColors(Settings);
        ScheduleSettingsSave();
    }

    public void SetCustomGForceTrailColor(string? value)
    {
        if (_disposed) return;
        var normalized = ColorCustomization.NormalizeGauge(value);
        if (Settings.CustomGForceTrailColor == normalized) return;
        Settings.CustomGForceTrailColor = normalized;
        ViewModel.UpdateGForceColors(Settings);
        ScheduleSettingsSave();
    }
}
