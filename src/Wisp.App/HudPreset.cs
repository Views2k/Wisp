using System.Text.Json.Serialization;
using System.Windows.Input;
using Wisp.Core;

namespace Wisp.App;

public sealed class HudPreset
{
    public const int MaximumCount = 24;
    public const int MaximumNameLength = 40;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public SpeedUnit SpeedUnit { get; set; } = Wisp.Core.SpeedUnit.MilesPerHour;
    public TorqueUnit TorqueUnit { get; set; } = Wisp.App.TorqueUnit.NewtonMeters;
    public HudLayoutMode LayoutMode { get; set; } = HudLayoutMode.Minimal;
    public NativeGaugeMode NativeGaugeMode { get; set; } = Wisp.App.NativeGaugeMode.Digital;
    public GearDisplayMode GearDisplayMode { get; set; } = Wisp.App.GearDisplayMode.Manual;
    public double OverlayWidthScale { get; set; } = 1;
    public double OverlayHeightScale { get; set; } = 1;
    public double OverlayOpacity { get; set; } = 1;
    public bool GForceEnabled { get; set; } = true;
    public bool GForceAttached { get; set; } = true;
    public double GForceGaugeScale { get; set; } = 1;
    public double GForceWidthScale { get; set; } = 1;
    public double GForceHeightScale { get; set; } = 1;
    public bool InvertLateralG { get; set; } = true;
    public bool InvertLongitudinalG { get; set; }
    public bool BoostGaugeEnabled { get; set; } = true;
    public bool BoostGaugeAttached { get; set; } = true;
    public bool BoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeStockColors { get; set; }
    public bool ShowBoostVacuum { get; set; }
    public BoostPressureUnit BoostPressureUnit { get; set; } = Wisp.App.BoostPressureUnit.Psi;
    public double BoostGaugeScale { get; set; } = 1;
    public bool TireTemperatureGaugeEnabled { get; set; } = true;
    public bool TireTemperatureGaugeAttached { get; set; } = true;
    public bool TireTemperatureReactiveColors { get; set; } = true;
    public TireTemperatureUnit TireTemperatureUnit { get; set; } = Wisp.App.TireTemperatureUnit.Fahrenheit;
    public double TireTemperatureGaugeScale { get; set; } = 1;
    public bool LapMapEnabled { get; set; }
    public double LapMapScale { get; set; } = 1;
    public string? LapDeltaAheadColor { get; set; }
    public string? LapDeltaBehindColor { get; set; }
    public string? LapMapTrackColor { get; set; }
    public string? LapMapCarColor { get; set; }
    public string? LapMapBackgroundColor { get; set; }
    public LapTimingMode LapTimingMode { get; set; }
    public bool LapDeltaEnabled { get; set; }
    public LapDeltaReference LapDeltaReference { get; set; }
    public bool LapDeltaShowBar { get; set; } = true;
    public double LapDeltaScale { get; set; } = 1;
    public bool PowerGaugeEnabled { get; set; }
    public bool TorqueGaugeEnabled { get; set; }
    public bool PowerGaugeAttached { get; set; } = true;
    public bool TorqueGaugeAttached { get; set; } = true;
    public double PowerTorqueSmoothingMilliseconds { get; set; } = 250;
    public bool PowerTorqueShowNegative { get; set; }
    public bool AccelerationShiftCueEnabled { get; set; }
    public string? ShiftCueGreenColor { get; set; }
    public string? ShiftCueYellowColor { get; set; }
    public string? ShiftCueRedColor { get; set; }
    public bool PowerTorqueDriftMode { get; set; }
    public double PowerTorqueDriftFlashFrequencyHz { get; set; } = 1.25;
    public string? PowerTorqueDriftFlashColor { get; set; }
    public bool PowerGaugeColorNumber { get; set; }
    public bool TorqueGaugeColorNumber { get; set; }
    public string? CustomPowerLowColor { get; set; }
    public string? CustomPowerMidColor { get; set; }
    public string? CustomPowerHighColor { get; set; }
    public string? CustomTorqueLowColor { get; set; }
    public string? CustomTorqueMidColor { get; set; }
    public string? CustomTorqueHighColor { get; set; }
    // Retain the shared v2.1 value as the fallback for settings and profiles
    // written before each gauge had its own size. Explicit sizes always win.
    public double PowerTorqueGaugeScale { get; set; } = 1;
    private double? _powerGaugeScale;
    private double? _torqueGaugeScale;
    public double PowerGaugeScale
    {
        get => _powerGaugeScale ?? PowerTorqueGaugeScale;
        set => _powerGaugeScale = value;
    }
    public double TorqueGaugeScale
    {
        get => _torqueGaugeScale ?? PowerTorqueGaugeScale;
        set => _torqueGaugeScale = value;
    }
    public double PowerGaugeMaximum { get; set; } = 1000;
    public double TorqueGaugeMaximumNm { get; set; } = 1200;
    public bool TractionCueEnabled { get; set; } = true;
    public string ColorTheme { get; set; } = AppColorThemes.DefaultName;
    public string BackgroundTheme { get; set; } = AppBackgroundThemes.DefaultName;
    public string HudBorderTheme { get; set; } = AppColorThemes.DefaultName;
    public string BoostGaugeTheme { get; set; } = BoostGaugeThemes.DefaultName;
    public string? CustomAccentColor { get; set; }
    public string? CustomBackgroundColor { get; set; }
    public string? CustomHudBorderColor { get; set; }
    public string? CustomBoostLowColor { get; set; }
    public string? CustomBoostMidColor { get; set; }
    public string? CustomBoostHighColor { get; set; }
    public string? CustomTractionCueColor { get; set; }
    public string? CustomGForceColor { get; set; }
    public string? CustomGForceTrailColor { get; set; }
    // Saved from 2.5.1, with lap delta settings saved reliably. Applying a profile saved earlier
    // leaves these and the lap delta settings as they are: profiles from before 2.5 have none, and
    // would otherwise turn lap delta off and switch Time Attack to race timing.
    public int Revision { get; set; }
    public string? CustomParticleColor { get; set; }
    public string? AppBorderColor { get; set; }
    public string? AppTextColor { get; set; }
    public string? AppMutedTextColor { get; set; }
    public bool DriftGaugeEnabled { get; set; }
    public double DriftGaugeScale { get; set; } = 1;
    public bool DriftGaugeDarkMode { get; set; }
    public bool DriftGaugeBackgroundEnabled { get; set; }
    public double DriftGaugeBackgroundOpacity { get; set; } = .5;
    // Revision 2 includes driving controls and independent copies of every saved HUD arrangement.
    // Earlier profiles leave the newly added settings and placements unchanged.
    public SpeedSourceMode SpeedSource { get; set; } = SpeedSourceMode.WheelIndicated;
    public double Smoothing { get; set; } = 0;
    public bool OverlayLocked { get; set; } = true;
    public bool GameAwareVisibility { get; set; } = true;
    public DriftGaugeGuidanceMode DriftGaugeGuidanceMode { get; set; } = global::Wisp.Core.DriftGaugeGuidanceMode.DriftZoneAngleBonus;
    public double DriftTargetDegrees { get; set; } = 40;
    public double DriftToleranceDegrees { get; set; } = 10;
    public bool OverlayHotkeyEnabled { get; set; } = false;
    public OverlayHotkeyModifiers OverlayHotkeyModifiers { get; set; } = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
    public Key OverlayHotkeyKey { get; set; } = Key.H;
    public bool RecordingShortcutEnabled { get; set; } = false;
    public OverlayHotkeyModifiers RecordingShortcutModifiers { get; set; } = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
    public Key RecordingShortcutKey { get; set; } = Key.R;
    public int RecordingCountdownSeconds { get; set; } = 0;
    public int RecordingStopAfterSeconds { get; set; } = 0;
    public bool MarkerShortcutEnabled { get; set; } = false;
    public OverlayHotkeyModifiers MarkerShortcutModifiers { get; set; } = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
    public Key MarkerShortcutKey { get; set; } = Key.M;
    public Wisp.Core.Runs.RunPurpose RunPurpose { get; set; } = Wisp.Core.Runs.RunPurpose.General;
    public bool LapReviewRecordingEnabled { get; set; } = false;
    public Dictionary<string, OverlayPlacement>? Placements { get; set; }
    public Dictionary<string, OverlayPlacement>? GForcePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? BoostGaugePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? TireTemperatureGaugePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? DriftGaugePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? PowerGaugePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? TorqueGaugePlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? LapDeltaPlacements { get; set; }
    public Dictionary<string, OverlayPlacement>? LapMapPlacements { get; set; }
    public Dictionary<int, PowerTorqueGaugeRange>? PowerTorqueGaugeRanges { get; set; }
    internal const int CurrentRevision = 2;

    [JsonIgnore]
    public string Summary => LayoutMode switch
    {
        HudLayoutMode.Native => NativeGaugeMode == Wisp.App.NativeGaugeMode.Analogue
            ? "Native analogue HUD"
            : "Native digital HUD",
        HudLayoutMode.Combined => "Combined HUD",
        HudLayoutMode.SeparateBoxes => "Box HUD",
        _ => "Minimal HUD"
    };

    public static HudPreset Capture(AppSettings settings, string name, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryNormalizeName(name, out var normalizedName, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }

        return new HudPreset
        {
            Id = id is { } existing && existing != Guid.Empty ? existing : Guid.NewGuid(),
            Name = normalizedName,
            SpeedSource = settings.SpeedSource,
            Smoothing = settings.Smoothing,
            OverlayLocked = settings.OverlayLocked,
            GameAwareVisibility = settings.GameAwareVisibility,
            DriftGaugeGuidanceMode = settings.DriftGaugeGuidanceMode,
            DriftTargetDegrees = settings.DriftTargetDegrees,
            DriftToleranceDegrees = settings.DriftToleranceDegrees,
            OverlayHotkeyEnabled = settings.OverlayHotkeyEnabled,
            OverlayHotkeyModifiers = settings.OverlayHotkeyModifiers,
            OverlayHotkeyKey = settings.OverlayHotkeyKey,
            RecordingShortcutEnabled = settings.RecordingShortcutEnabled,
            RecordingShortcutModifiers = settings.RecordingShortcutModifiers,
            RecordingShortcutKey = settings.RecordingShortcutKey,
            RecordingCountdownSeconds = settings.RecordingCountdownSeconds,
            RecordingStopAfterSeconds = settings.RecordingStopAfterSeconds,
            MarkerShortcutEnabled = settings.MarkerShortcutEnabled,
            MarkerShortcutModifiers = settings.MarkerShortcutModifiers,
            MarkerShortcutKey = settings.MarkerShortcutKey,
            RunPurpose = settings.RunPurpose,
            LapReviewRecordingEnabled = settings.LapReviewRecordingEnabled,
            Placements = CopyPlacements(settings.Placements),
            GForcePlacements = CopyPlacements(settings.GForcePlacements),
            BoostGaugePlacements = CopyPlacements(settings.BoostGaugePlacements),
            TireTemperatureGaugePlacements = CopyPlacements(settings.TireTemperatureGaugePlacements),
            DriftGaugePlacements = CopyPlacements(settings.DriftGaugePlacements),
            PowerGaugePlacements = CopyPlacements(settings.PowerGaugePlacements),
            TorqueGaugePlacements = CopyPlacements(settings.TorqueGaugePlacements),
            LapDeltaPlacements = CopyPlacements(settings.LapDeltaPlacements),
            LapMapPlacements = CopyPlacements(settings.LapMapPlacements),
            PowerTorqueGaugeRanges = new(settings.PowerTorqueGaugeRanges),
            SpeedUnit = settings.SpeedUnit,
            TorqueUnit = settings.TorqueUnit,
            LayoutMode = settings.LayoutMode,
            NativeGaugeMode = settings.NativeGaugeMode,
            GearDisplayMode = settings.GearDisplayMode,
            OverlayWidthScale = settings.OverlayWidthScale,
            OverlayHeightScale = settings.OverlayHeightScale,
            OverlayOpacity = settings.OverlayOpacity,
            GForceEnabled = settings.GForceEnabled,
            GForceAttached = settings.GForceAttached,
            GForceGaugeScale = settings.GForceGaugeScale,
            GForceWidthScale = settings.GForceWidthScale,
            GForceHeightScale = settings.GForceHeightScale,
            InvertLateralG = settings.InvertLateralG,
            InvertLongitudinalG = settings.InvertLongitudinalG,
            BoostGaugeEnabled = settings.BoostGaugeEnabled,
            BoostGaugeAttached = settings.BoostGaugeAttached,
            BoostGaugeColorNumber = settings.BoostGaugeColorNumber,
            DigitalBoostGaugeColorNumber = settings.DigitalBoostGaugeColorNumber,
            DigitalBoostGaugeStockColors = settings.DigitalBoostGaugeStockColors,
            ShowBoostVacuum = settings.ShowBoostVacuum,
            BoostPressureUnit = settings.BoostPressureUnit,
            BoostGaugeScale = settings.BoostGaugeScale,
            TireTemperatureGaugeEnabled = settings.TireTemperatureGaugeEnabled,
            TireTemperatureGaugeAttached = settings.TireTemperatureGaugeAttached,
            TireTemperatureReactiveColors = settings.TireTemperatureReactiveColors,
            TireTemperatureUnit = settings.TireTemperatureUnit,
            TireTemperatureGaugeScale = settings.TireTemperatureGaugeScale,
            LapMapEnabled = settings.LapMapEnabled,
            LapMapScale = settings.LapMapScale,
            LapDeltaAheadColor = settings.LapDeltaAheadColor,
            LapDeltaBehindColor = settings.LapDeltaBehindColor,
            LapMapTrackColor = settings.LapMapTrackColor,
            LapMapCarColor = settings.LapMapCarColor,
            LapMapBackgroundColor = settings.LapMapBackgroundColor,
            LapTimingMode = settings.LapTimingMode,
            LapDeltaEnabled = settings.LapDeltaEnabled,
            LapDeltaReference = settings.LapDeltaReference,
            LapDeltaShowBar = settings.LapDeltaShowBar,
            LapDeltaScale = settings.LapDeltaScale,
            PowerGaugeEnabled = settings.PowerGaugeEnabled,
            TorqueGaugeEnabled = settings.TorqueGaugeEnabled,
            PowerGaugeAttached = settings.PowerGaugeAttached,
            TorqueGaugeAttached = settings.TorqueGaugeAttached,
            PowerTorqueSmoothingMilliseconds = settings.PowerTorqueSmoothingMilliseconds,
            PowerTorqueShowNegative = settings.PowerTorqueShowNegative,
            AccelerationShiftCueEnabled = settings.AccelerationShiftCueEnabled,
            ShiftCueGreenColor = settings.ShiftCueGreenColor,
            ShiftCueYellowColor = settings.ShiftCueYellowColor,
            ShiftCueRedColor = settings.ShiftCueRedColor,
            PowerTorqueDriftMode = settings.PowerTorqueDriftMode,
            PowerTorqueDriftFlashFrequencyHz = settings.PowerTorqueDriftFlashFrequencyHz,
            PowerTorqueDriftFlashColor = settings.PowerTorqueDriftFlashColor,
            PowerGaugeColorNumber = settings.PowerGaugeColorNumber,
            TorqueGaugeColorNumber = settings.TorqueGaugeColorNumber,
            CustomPowerLowColor = settings.CustomPowerLowColor,
            CustomPowerMidColor = settings.CustomPowerMidColor,
            CustomPowerHighColor = settings.CustomPowerHighColor,
            CustomTorqueLowColor = settings.CustomTorqueLowColor,
            CustomTorqueMidColor = settings.CustomTorqueMidColor,
            CustomTorqueHighColor = settings.CustomTorqueHighColor,
            PowerTorqueGaugeScale = settings.PowerTorqueGaugeScale,
            PowerGaugeScale = settings.PowerGaugeScale,
            TorqueGaugeScale = settings.TorqueGaugeScale,
            PowerGaugeMaximum = settings.PowerGaugeMaximum,
            TorqueGaugeMaximumNm = settings.TorqueGaugeMaximumNm,
            TractionCueEnabled = settings.TractionCueEnabled,
            ColorTheme = settings.ColorTheme,
            BackgroundTheme = settings.BackgroundTheme,
            HudBorderTheme = settings.HudBorderTheme,
            BoostGaugeTheme = settings.BoostGaugeTheme,
            CustomAccentColor = settings.CustomAccentColor,
            CustomBackgroundColor = settings.CustomBackgroundColor,
            CustomHudBorderColor = settings.CustomHudBorderColor,
            CustomBoostLowColor = settings.CustomBoostLowColor,
            CustomBoostMidColor = settings.CustomBoostMidColor,
            CustomBoostHighColor = settings.CustomBoostHighColor,
            CustomTractionCueColor = settings.CustomTractionCueColor,
            CustomGForceColor = settings.CustomGForceColor,
            CustomGForceTrailColor = settings.CustomGForceTrailColor,
            Revision = CurrentRevision,
            CustomParticleColor = settings.CustomParticleColor,
            AppBorderColor = settings.ApplicationStyle?.BorderColor,
            AppTextColor = settings.ApplicationStyle?.TextColor,
            AppMutedTextColor = settings.ApplicationStyle?.MutedTextColor,
            DriftGaugeEnabled = settings.DriftGaugeEnabled,
            DriftGaugeScale = settings.DriftGaugeScale,
            DriftGaugeDarkMode = settings.DriftGaugeDarkMode,
            DriftGaugeBackgroundEnabled = settings.DriftGaugeBackgroundEnabled,
            DriftGaugeBackgroundOpacity = settings.DriftGaugeBackgroundOpacity
        };
    }

    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize();
        settings.SpeedUnit = SpeedUnit;
        settings.TorqueUnit = TorqueUnit;
        settings.LayoutMode = LayoutMode;
        settings.NativeGaugeMode = NativeGaugeMode;
        settings.GearDisplayMode = GearDisplayMode;
        settings.OverlayWidthScale = OverlayWidthScale;
        settings.OverlayHeightScale = OverlayHeightScale;
        settings.OverlayOpacity = OverlayOpacity;
        settings.GForceEnabled = GForceEnabled;
        settings.GForceAttached = GForceAttached;
        settings.GForceGaugeScale = GForceGaugeScale;
        settings.GForceWidthScale = GForceWidthScale;
        settings.GForceHeightScale = GForceHeightScale;
        settings.InvertLateralG = InvertLateralG;
        settings.InvertLongitudinalG = InvertLongitudinalG;
        settings.BoostGaugeEnabled = BoostGaugeEnabled;
        settings.BoostGaugeAttached = BoostGaugeAttached;
        settings.BoostGaugeColorNumber = BoostGaugeColorNumber;
        settings.DigitalBoostGaugeColorNumber = DigitalBoostGaugeColorNumber;
        settings.DigitalBoostGaugeStockColors = DigitalBoostGaugeStockColors;
        settings.ShowBoostVacuum = ShowBoostVacuum;
        settings.BoostPressureUnit = BoostPressureUnit;
        settings.BoostGaugeScale = BoostGaugeScale;
        settings.TireTemperatureGaugeEnabled = TireTemperatureGaugeEnabled;
        settings.TireTemperatureGaugeAttached = TireTemperatureGaugeAttached;
        settings.TireTemperatureReactiveColors = TireTemperatureReactiveColors;
        settings.TireTemperatureUnit = TireTemperatureUnit;
        settings.TireTemperatureGaugeScale = TireTemperatureGaugeScale;
        settings.PowerGaugeEnabled = PowerGaugeEnabled;
        settings.TorqueGaugeEnabled = TorqueGaugeEnabled;
        settings.PowerGaugeAttached = PowerGaugeAttached;
        settings.TorqueGaugeAttached = TorqueGaugeAttached;
        settings.PowerTorqueSmoothingMilliseconds = PowerTorqueSmoothingMilliseconds;
        settings.PowerTorqueShowNegative = PowerTorqueShowNegative;
        settings.AccelerationShiftCueEnabled = AccelerationShiftCueEnabled;
        settings.ShiftCueGreenColor = ShiftCueGreenColor;
        settings.ShiftCueYellowColor = ShiftCueYellowColor;
        settings.ShiftCueRedColor = ShiftCueRedColor;
        settings.PowerTorqueDriftMode = PowerTorqueDriftMode;
        settings.PowerTorqueDriftFlashFrequencyHz = PowerTorqueDriftFlashFrequencyHz;
        settings.PowerTorqueDriftFlashColor = PowerTorqueDriftFlashColor;
        settings.PowerGaugeColorNumber = PowerGaugeColorNumber;
        settings.TorqueGaugeColorNumber = TorqueGaugeColorNumber;
        settings.CustomPowerLowColor = CustomPowerLowColor;
        settings.CustomPowerMidColor = CustomPowerMidColor;
        settings.CustomPowerHighColor = CustomPowerHighColor;
        settings.CustomTorqueLowColor = CustomTorqueLowColor;
        settings.CustomTorqueMidColor = CustomTorqueMidColor;
        settings.CustomTorqueHighColor = CustomTorqueHighColor;
        settings.PowerTorqueGaugeScale = PowerTorqueGaugeScale;
        settings.PowerGaugeScale = PowerGaugeScale;
        settings.TorqueGaugeScale = TorqueGaugeScale;
        settings.PowerGaugeMaximum = PowerGaugeMaximum;
        settings.TorqueGaugeMaximumNm = TorqueGaugeMaximumNm;
        settings.TractionCueEnabled = TractionCueEnabled;
        settings.ColorTheme = ColorTheme;
        settings.BackgroundTheme = BackgroundTheme;
        settings.HudBorderTheme = HudBorderTheme;
        settings.BoostGaugeTheme = BoostGaugeTheme;
        settings.CustomAccentColor = CustomAccentColor;
        settings.CustomBackgroundColor = CustomBackgroundColor;
        settings.CustomHudBorderColor = CustomHudBorderColor;
        settings.CustomBoostLowColor = CustomBoostLowColor;
        settings.CustomBoostMidColor = CustomBoostMidColor;
        settings.CustomBoostHighColor = CustomBoostHighColor;
        settings.CustomTractionCueColor = CustomTractionCueColor;
        settings.CustomGForceColor = CustomGForceColor;
        settings.CustomGForceTrailColor = CustomGForceTrailColor;
        if (Revision < 1) return;
        settings.LapMapEnabled = LapMapEnabled;
        settings.LapMapScale = LapMapScale;
        settings.LapDeltaAheadColor = LapDeltaAheadColor;
        settings.LapDeltaBehindColor = LapDeltaBehindColor;
        settings.LapMapTrackColor = LapMapTrackColor;
        settings.LapMapCarColor = LapMapCarColor;
        settings.LapMapBackgroundColor = LapMapBackgroundColor;
        settings.LapTimingMode = LapTimingMode;
        settings.LapDeltaEnabled = LapDeltaEnabled;
        settings.LapDeltaReference = LapDeltaReference;
        settings.LapDeltaShowBar = LapDeltaShowBar;
        settings.LapDeltaScale = LapDeltaScale;
        settings.CustomParticleColor = CustomParticleColor;
        var style = (settings.ApplicationStyle ?? new AppStyleSettings()).Clone();
        style.BorderColor = AppBorderColor;
        style.TextColor = AppTextColor;
        style.MutedTextColor = AppMutedTextColor;
        style.Normalize();
        settings.ApplicationStyle = style;
        settings.DriftGaugeEnabled = DriftGaugeEnabled;
        settings.DriftGaugeScale = DriftGaugeScale;
        settings.DriftGaugeDarkMode = DriftGaugeDarkMode;
        settings.DriftGaugeBackgroundEnabled = DriftGaugeBackgroundEnabled;
        settings.DriftGaugeBackgroundOpacity = DriftGaugeBackgroundOpacity;
        if (Revision < 2) return;
        settings.SpeedSource = SpeedSource;
        settings.Smoothing = Smoothing;
        settings.OverlayLocked = OverlayLocked;
        settings.GameAwareVisibility = GameAwareVisibility;
        settings.DriftGaugeGuidanceMode = DriftGaugeGuidanceMode;
        settings.DriftTargetDegrees = DriftTargetDegrees;
        settings.DriftToleranceDegrees = DriftToleranceDegrees;
        settings.OverlayHotkeyEnabled = OverlayHotkeyEnabled;
        settings.OverlayHotkeyModifiers = OverlayHotkeyModifiers;
        settings.OverlayHotkeyKey = OverlayHotkeyKey;
        settings.RecordingShortcutEnabled = RecordingShortcutEnabled;
        settings.RecordingShortcutModifiers = RecordingShortcutModifiers;
        settings.RecordingShortcutKey = RecordingShortcutKey;
        settings.RecordingCountdownSeconds = RecordingCountdownSeconds;
        settings.RecordingStopAfterSeconds = RecordingStopAfterSeconds;
        settings.MarkerShortcutEnabled = MarkerShortcutEnabled;
        settings.MarkerShortcutModifiers = MarkerShortcutModifiers;
        settings.MarkerShortcutKey = MarkerShortcutKey;
        settings.RunPurpose = RunPurpose;
        settings.LapReviewRecordingEnabled = LapReviewRecordingEnabled;
        if (Placements is not null) settings.Placements = CopyPlacements(Placements);
        if (GForcePlacements is not null) settings.GForcePlacements = CopyPlacements(GForcePlacements);
        if (BoostGaugePlacements is not null) settings.BoostGaugePlacements = CopyPlacements(BoostGaugePlacements);
        if (TireTemperatureGaugePlacements is not null) settings.TireTemperatureGaugePlacements = CopyPlacements(TireTemperatureGaugePlacements);
        if (DriftGaugePlacements is not null) settings.DriftGaugePlacements = CopyPlacements(DriftGaugePlacements);
        if (PowerGaugePlacements is not null) settings.PowerGaugePlacements = CopyPlacements(PowerGaugePlacements);
        if (TorqueGaugePlacements is not null) settings.TorqueGaugePlacements = CopyPlacements(TorqueGaugePlacements);
        if (LapDeltaPlacements is not null) settings.LapDeltaPlacements = CopyPlacements(LapDeltaPlacements);
        if (LapMapPlacements is not null) settings.LapMapPlacements = CopyPlacements(LapMapPlacements);
        if (PowerTorqueGaugeRanges is not null) settings.PowerTorqueGaugeRanges = new(PowerTorqueGaugeRanges);
    }

    public bool Normalize()
    {
        if (!TryNormalizeName(Name, out var normalizedName, out _))
        {
            return false;
        }

        Name = normalizedName;
        if (Id == Guid.Empty)
        {
            Id = Guid.NewGuid();
        }
        if (!Enum.IsDefined(SpeedSource)) SpeedSource = SpeedSourceMode.WheelIndicated;
        if (!Enum.IsDefined(DriftGaugeGuidanceMode)) DriftGaugeGuidanceMode = global::Wisp.Core.DriftGaugeGuidanceMode.DriftZoneAngleBonus;
        if (!Enum.IsDefined(RunPurpose)) RunPurpose = Wisp.Core.Runs.RunPurpose.General;
        Smoothing = double.IsFinite(Smoothing) ? Math.Clamp(Smoothing, 0, 1) : 0;
        DriftTargetDegrees = double.IsFinite(DriftTargetDegrees) ? Math.Clamp(DriftTargetDegrees, 10, 75) : 40;
        DriftToleranceDegrees = double.IsFinite(DriftToleranceDegrees) ? Math.Clamp(DriftToleranceDegrees, 2, 15) : 10;
        if (RecordingCountdownSeconds is not (0 or 3 or 5 or 10)) RecordingCountdownSeconds = 0;
        if (RecordingStopAfterSeconds is not (0 or 30 or 60 or 120 or 300 or 600)) RecordingStopAfterSeconds = 0;
        if (!OverlayHotkeyChord.TryCreate(OverlayHotkeyModifiers, OverlayHotkeyKey, out _, out _))
        {
            OverlayHotkeyEnabled = false;
            OverlayHotkeyModifiers = OverlayHotkeyChord.Default.Modifiers;
            OverlayHotkeyKey = OverlayHotkeyChord.Default.Key;
        }
        if (!OverlayHotkeyChord.TryCreate(RecordingShortcutModifiers, RecordingShortcutKey, out _, out _))
        {
            RecordingShortcutEnabled = false;
            RecordingShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
            RecordingShortcutKey = Key.R;
        }
        if (!OverlayHotkeyChord.TryCreate(MarkerShortcutModifiers, MarkerShortcutKey, out _, out _))
        {
            MarkerShortcutEnabled = false;
            MarkerShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
            MarkerShortcutKey = Key.M;
        }
        if (Placements is not null) Placements = CopyPlacements(Placements);
        if (GForcePlacements is not null) GForcePlacements = CopyPlacements(GForcePlacements);
        if (BoostGaugePlacements is not null) BoostGaugePlacements = CopyPlacements(BoostGaugePlacements);
        if (TireTemperatureGaugePlacements is not null) TireTemperatureGaugePlacements = CopyPlacements(TireTemperatureGaugePlacements);
        if (DriftGaugePlacements is not null) DriftGaugePlacements = CopyPlacements(DriftGaugePlacements);
        if (PowerGaugePlacements is not null) PowerGaugePlacements = CopyPlacements(PowerGaugePlacements);
        if (TorqueGaugePlacements is not null) TorqueGaugePlacements = CopyPlacements(TorqueGaugePlacements);
        if (LapDeltaPlacements is not null) LapDeltaPlacements = CopyPlacements(LapDeltaPlacements);
        if (LapMapPlacements is not null) LapMapPlacements = CopyPlacements(LapMapPlacements);
        if (PowerTorqueGaugeRanges is not null)
            PowerTorqueGaugeRanges = PowerTorqueGaugeRanges.Where(pair => pair.Key > 0 && pair.Value is not null)
                .TakeLast(512).ToDictionary(pair => pair.Key, pair => new PowerTorqueGaugeRange(
                    AppSettings.NormalizePowerGaugeMaximum(pair.Value.PowerMaximum),
                    AppSettings.NormalizeTorqueGaugeMaximum(pair.Value.TorqueMaximumNm)));
        if (!Enum.IsDefined(SpeedUnit)) SpeedUnit = Wisp.Core.SpeedUnit.MilesPerHour;
        if (!Enum.IsDefined(TorqueUnit)) TorqueUnit = Wisp.App.TorqueUnit.NewtonMeters;
        if (!Enum.IsDefined(LayoutMode)) LayoutMode = HudLayoutMode.Minimal;
        if (!Enum.IsDefined(NativeGaugeMode)) NativeGaugeMode = Wisp.App.NativeGaugeMode.Digital;
        if (!Enum.IsDefined(GearDisplayMode)) GearDisplayMode = Wisp.App.GearDisplayMode.Manual;
        if (!Enum.IsDefined(BoostPressureUnit)) BoostPressureUnit = Wisp.App.BoostPressureUnit.Psi;
        if (!Enum.IsDefined(TireTemperatureUnit)) TireTemperatureUnit = Wisp.App.TireTemperatureUnit.Fahrenheit;
        OverlayWidthScale = NormalizeScale(OverlayWidthScale);
        OverlayHeightScale = NormalizeScale(OverlayHeightScale);
        GForceGaugeScale = NormalizeScale(GForceGaugeScale);
        GForceWidthScale = NormalizeScale(GForceWidthScale);
        GForceHeightScale = NormalizeScale(GForceHeightScale);
        BoostGaugeScale = NormalizeScale(BoostGaugeScale);
        TireTemperatureGaugeScale = NormalizeScale(TireTemperatureGaugeScale);
        LapMapScale = double.IsFinite(LapMapScale) ? Math.Clamp(LapMapScale, .5, 3) : 1;
        LapDeltaAheadColor = ColorCustomization.NormalizeGauge(LapDeltaAheadColor);
        LapDeltaBehindColor = ColorCustomization.NormalizeGauge(LapDeltaBehindColor);
        LapMapTrackColor = ColorCustomization.NormalizeGauge(LapMapTrackColor);
        LapMapCarColor = ColorCustomization.NormalizeGauge(LapMapCarColor);
        LapMapBackgroundColor = ColorCustomization.NormalizeParticle(LapMapBackgroundColor);
        LapDeltaScale = NormalizeScale(LapDeltaScale);
        CustomParticleColor = ColorCustomization.NormalizeParticle(CustomParticleColor);
        DriftGaugeScale = NormalizeScale(DriftGaugeScale);
        DriftGaugeBackgroundOpacity = double.IsFinite(DriftGaugeBackgroundOpacity) ? Math.Clamp(DriftGaugeBackgroundOpacity, 0, 1) : .5;
        if (!Enum.IsDefined(LapTimingMode)) LapTimingMode = global::Wisp.Core.LapTimingMode.GameLaps;
        if (!Enum.IsDefined(LapDeltaReference)) LapDeltaReference = global::Wisp.Core.LapDeltaReference.SessionBest;
        PowerTorqueGaugeScale = NormalizeScale(PowerTorqueGaugeScale);
        PowerGaugeScale = NormalizeScale(PowerGaugeScale);
        TorqueGaugeScale = NormalizeScale(TorqueGaugeScale);
        PowerTorqueSmoothingMilliseconds = AppSettings.NormalizePowerTorqueSmoothing(PowerTorqueSmoothingMilliseconds);
        PowerTorqueDriftFlashFrequencyHz = AppSettings.NormalizePowerTorqueDriftFlashFrequency(PowerTorqueDriftFlashFrequencyHz);
        PowerTorqueDriftFlashColor = ColorCustomization.NormalizePowerTorqueDriftFlash(PowerTorqueDriftFlashColor);
        CustomPowerLowColor = ColorCustomization.NormalizeGauge(CustomPowerLowColor);
        CustomPowerMidColor = ColorCustomization.NormalizeGauge(CustomPowerMidColor);
        CustomPowerHighColor = ColorCustomization.NormalizeGauge(CustomPowerHighColor);
        CustomTorqueLowColor = ColorCustomization.NormalizeGauge(CustomTorqueLowColor);
        CustomTorqueMidColor = ColorCustomization.NormalizeGauge(CustomTorqueMidColor);
        CustomTorqueHighColor = ColorCustomization.NormalizeGauge(CustomTorqueHighColor);
        PowerGaugeMaximum = AppSettings.NormalizePowerGaugeMaximum(PowerGaugeMaximum);
        TorqueGaugeMaximumNm = AppSettings.NormalizeTorqueGaugeMaximum(TorqueGaugeMaximumNm);
        OverlayOpacity = double.IsFinite(OverlayOpacity) ? Math.Clamp(OverlayOpacity, 0.35, 1) : 1;
        ColorTheme = AppColorThemes.NormalizeName(ColorTheme);
        BackgroundTheme = AppBackgroundThemes.NormalizeName(BackgroundTheme);
        HudBorderTheme = AppColorThemes.NormalizeName(HudBorderTheme);
        BoostGaugeTheme = BoostGaugeThemes.NormalizeName(BoostGaugeTheme);
        CustomAccentColor = ColorCustomization.NormalizeAccent(CustomAccentColor);
        CustomBackgroundColor = ColorCustomization.NormalizeBackground(CustomBackgroundColor);
        CustomHudBorderColor = ColorCustomization.NormalizeHudBorder(CustomHudBorderColor);
        CustomBoostLowColor = ColorCustomization.NormalizeGauge(CustomBoostLowColor);
        CustomBoostMidColor = ColorCustomization.NormalizeGauge(CustomBoostMidColor);
        CustomBoostHighColor = ColorCustomization.NormalizeGauge(CustomBoostHighColor);
        CustomTractionCueColor = ColorCustomization.NormalizeTractionCue(CustomTractionCueColor);
        CustomGForceColor = ColorCustomization.NormalizeGauge(CustomGForceColor);
        CustomGForceTrailColor = ColorCustomization.NormalizeGauge(CustomGForceTrailColor);
        return true;
    }

    public static void NormalizeList(List<HudPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identifiers = new HashSet<Guid>();
        var normalized = new List<HudPreset>(Math.Min(presets.Count, MaximumCount));
        foreach (var preset in presets.Where(preset => preset is not null))
        {
            if (!preset.Normalize() || !names.Add(preset.Name))
            {
                continue;
            }
            while (!identifiers.Add(preset.Id))
            {
                preset.Id = Guid.NewGuid();
            }
            normalized.Add(preset);
            if (normalized.Count == MaximumCount)
            {
                break;
            }
        }

        presets.Clear();
        presets.AddRange(normalized);
    }

    public static bool TryNormalizeName(string? value, out string normalized, out string error)
    {
        normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            error = "Enter a profile name.";
            return false;
        }
        if (normalized.Length > MaximumNameLength)
        {
            error = $"Profile names can contain up to {MaximumNameLength} characters.";
            return false;
        }
        if (normalized.Any(char.IsControl))
        {
            error = "Profile names cannot contain control characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static double NormalizeScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.5, 2) : 1;

    internal static Dictionary<string, OverlayPlacement> CopyPlacements(
        IReadOnlyDictionary<string, OverlayPlacement>? placements)
    {
        var copy = new Dictionary<string, OverlayPlacement>(StringComparer.Ordinal);
        if (placements is null) return copy;
        foreach (var (key, value) in placements.TakeLast(32))
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || value is null) continue;
            var placement = new OverlayPlacement(value.Left, value.Top, value.WidthScale, value.HeightScale);
            if (placement.Normalize()) copy[key] = placement;
        }
        return copy;
    }
}
