namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private AppSettings? _powerTorqueSettings;
    private bool _powerGaugeEnabled;
    private bool _torqueGaugeEnabled;
    private bool _powerGaugeAttached = true;
    private bool _torqueGaugeAttached = true;
    private double _powerTorqueSmoothingMilliseconds = 500;
    private bool _powerTorqueShowNegative;
    private bool _powerGaugeColorNumber;
    private bool _torqueGaugeColorNumber;
    private string? _customPowerLowColor;
    private string? _customPowerMidColor;
    private string? _customPowerHighColor;
    private string? _customTorqueLowColor;
    private string? _customTorqueMidColor;
    private string? _customTorqueHighColor;
    private double _powerTorqueGaugeScale = 1;
    private double _powerGaugeMaximum = 1000;
    private double _torqueGaugeMaximumNm = 1200;

    public bool PowerGaugeEnabled { get => _powerGaugeEnabled; set => Set(ref _powerGaugeEnabled, value); }
    public bool TorqueGaugeEnabled { get => _torqueGaugeEnabled; set => Set(ref _torqueGaugeEnabled, value); }
    public bool PowerGaugeAttached { get => _powerGaugeAttached; set => Set(ref _powerGaugeAttached, value); }
    public bool TorqueGaugeAttached { get => _torqueGaugeAttached; set => Set(ref _torqueGaugeAttached, value); }
    public bool PowerTorqueShowNegative { get => _powerTorqueShowNegative; set => Set(ref _powerTorqueShowNegative, value); }
    public bool PowerGaugeColorNumber { get => _powerGaugeColorNumber; set => Set(ref _powerGaugeColorNumber, value); }
    public bool TorqueGaugeColorNumber { get => _torqueGaugeColorNumber; set => Set(ref _torqueGaugeColorNumber, value); }
    public double PowerTorqueSmoothingMilliseconds
    {
        get => _powerTorqueSmoothingMilliseconds;
        set => Set(ref _powerTorqueSmoothingMilliseconds, AppSettings.NormalizePowerTorqueSmoothing(value));
    }
    public string? CustomPowerLowColor
    {
        get => _customPowerLowColor;
        set { if (Set(ref _customPowerLowColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(PowerGaugeLowBrush)); }
    }
    public string? CustomPowerMidColor
    {
        get => _customPowerMidColor;
        set { if (Set(ref _customPowerMidColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(PowerGaugeMidBrush)); }
    }
    public string? CustomPowerHighColor
    {
        get => _customPowerHighColor;
        set { if (Set(ref _customPowerHighColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(PowerGaugeHighBrush)); }
    }
    public string? CustomTorqueLowColor
    {
        get => _customTorqueLowColor;
        set { if (Set(ref _customTorqueLowColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(TorqueGaugeLowBrush)); }
    }
    public string? CustomTorqueMidColor
    {
        get => _customTorqueMidColor;
        set { if (Set(ref _customTorqueMidColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(TorqueGaugeMidBrush)); }
    }
    public string? CustomTorqueHighColor
    {
        get => _customTorqueHighColor;
        set { if (Set(ref _customTorqueHighColor, ColorCustomization.NormalizeGauge(value))) OnPropertyChanged(nameof(TorqueGaugeHighBrush)); }
    }
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
        PowerGaugeAttached = settings.PowerGaugeAttached;
        TorqueGaugeAttached = settings.TorqueGaugeAttached;
        PowerTorqueSmoothingMilliseconds = settings.PowerTorqueSmoothingMilliseconds;
        PowerTorqueShowNegative = settings.PowerTorqueShowNegative;
        PowerGaugeColorNumber = settings.PowerGaugeColorNumber;
        TorqueGaugeColorNumber = settings.TorqueGaugeColorNumber;
        CustomPowerLowColor = settings.CustomPowerLowColor;
        CustomPowerMidColor = settings.CustomPowerMidColor;
        CustomPowerHighColor = settings.CustomPowerHighColor;
        CustomTorqueLowColor = settings.CustomTorqueLowColor;
        CustomTorqueMidColor = settings.CustomTorqueMidColor;
        CustomTorqueHighColor = settings.CustomTorqueHighColor;
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
