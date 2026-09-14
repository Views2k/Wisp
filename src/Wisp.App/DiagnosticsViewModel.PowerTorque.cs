namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private AppSettings? _powerTorqueSettings;
    private bool _powerGaugeEnabled;
    private bool _torqueGaugeEnabled;
    private double _powerTorqueGaugeScale = 1;
    private double _powerGaugeMaximum = 1000;
    private double _torqueGaugeMaximumNm = 1200;

    public bool PowerGaugeEnabled { get => _powerGaugeEnabled; set => Set(ref _powerGaugeEnabled, value); }
    public bool TorqueGaugeEnabled { get => _torqueGaugeEnabled; set => Set(ref _torqueGaugeEnabled, value); }
    public double PowerTorqueGaugeScale
    {
        get => _powerTorqueGaugeScale;
        set => Set(ref _powerTorqueGaugeScale, double.IsFinite(value) ? Math.Clamp(value, .5, 2) : 1);
    }
    public double PowerGaugeMaximum
    {
        get => _powerGaugeMaximum;
        set
        {
            if (Set(ref _powerGaugeMaximum, AppSettings.NormalizePowerGaugeMaximum(value)))
                OnPropertyChanged(nameof(PreviewPowerGaugeMaximum));
        }
    }
    public double TorqueGaugeMaximumNm
    {
        get => _torqueGaugeMaximumNm;
        set
        {
            if (Set(ref _torqueGaugeMaximumNm, AppSettings.NormalizeTorqueGaugeMaximum(value)))
            {
                OnPropertyChanged(nameof(TorqueGaugeMaximum));
                OnPropertyChanged(nameof(PreviewTorqueGaugeMaximum));
            }
        }
    }
    public double TorqueGaugeMaximum => SelectedTorqueUnit == TorqueUnit.PoundFeet
        ? TorqueGaugeMaximumNm * 0.7375621492772656
        : TorqueGaugeMaximumNm;

    internal void InitializePowerTorqueSettings(AppSettings settings)
    {
        _powerTorqueSettings = settings;
        PowerGaugeEnabled = settings.PowerGaugeEnabled;
        TorqueGaugeEnabled = settings.TorqueGaugeEnabled;
        PowerTorqueGaugeScale = settings.PowerTorqueGaugeScale;
        RestorePowerTorqueRange();
    }

    private void RestorePowerTorqueRange()
    {
        if (_powerTorqueSettings is not { } settings) return;
        var range = settings.PowerTorqueGaugeRanges.GetValueOrDefault(_powerTorqueCarOrdinal);
        PowerGaugeMaximum = range?.PowerMaximum ?? settings.PowerGaugeMaximum;
        TorqueGaugeMaximumNm = range?.TorqueMaximumNm ?? settings.TorqueGaugeMaximumNm;
    }

    internal void SavePowerTorqueRange()
    {
        if (_powerTorqueSettings is not { } settings) return;
        if (_powerTorqueCarOrdinal > 0)
            settings.PowerTorqueGaugeRanges[_powerTorqueCarOrdinal] = new(PowerGaugeMaximum, TorqueGaugeMaximumNm);
        else
        {
            settings.PowerGaugeMaximum = PowerGaugeMaximum;
            settings.TorqueGaugeMaximumNm = TorqueGaugeMaximumNm;
        }
    }

    internal bool SetPowerTorqueScalesFromCurrentRun()
    {
        if (!CanSetPowerTorqueScales) return false;
        PowerGaugeMaximum = PowerTorqueDisplay.PeakPowerBhp * 1.1;
        TorqueGaugeMaximumNm = PowerTorqueDisplay.PeakTorqueNm * 1.1;
        SavePowerTorqueRange();
        return true;
    }

    internal void CapturePowerTorquePresetRange(HudPreset preset)
    {
        preset.PowerGaugeMaximum = PowerGaugeMaximum;
        preset.TorqueGaugeMaximumNm = TorqueGaugeMaximumNm;
    }

    internal void ApplyPowerTorquePresetRange(HudPreset preset)
    {
        PowerGaugeMaximum = preset.PowerGaugeMaximum;
        TorqueGaugeMaximumNm = preset.TorqueGaugeMaximumNm;
        SavePowerTorqueRange();
    }
}
