using System.Text.Json.Serialization;
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
    public double GForceWidthScale { get; set; } = 1;
    public double GForceHeightScale { get; set; } = 1;
    public bool InvertLateralG { get; set; } = true;
    public bool InvertLongitudinalG { get; set; }
    public bool BoostGaugeEnabled { get; set; } = true;
    public bool BoostGaugeAttached { get; set; } = true;
    public bool BoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeStockColors { get; set; }
    public BoostPressureUnit BoostPressureUnit { get; set; } = Wisp.App.BoostPressureUnit.Psi;
    public double BoostGaugeScale { get; set; } = 1;
    public bool TireTemperatureGaugeEnabled { get; set; } = true;
    public bool TireTemperatureGaugeAttached { get; set; } = true;
    public bool TireTemperatureReactiveColors { get; set; } = true;
    public TireTemperatureUnit TireTemperatureUnit { get; set; } = Wisp.App.TireTemperatureUnit.Fahrenheit;
    public double TireTemperatureGaugeScale { get; set; } = 1;
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

    [JsonIgnore]
    public string Summary => LayoutMode switch
    {
        HudLayoutMode.Native => NativeGaugeMode == Wisp.App.NativeGaugeMode.Analogue
            ? "Native analogue HUD"
            : "Native digital HUD",
        HudLayoutMode.Combined => "Combined HUD",
        HudLayoutMode.SeparateBoxes => "Two boxes HUD",
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
            GForceWidthScale = settings.GForceWidthScale,
            GForceHeightScale = settings.GForceHeightScale,
            InvertLateralG = settings.InvertLateralG,
            InvertLongitudinalG = settings.InvertLongitudinalG,
            BoostGaugeEnabled = settings.BoostGaugeEnabled,
            BoostGaugeAttached = settings.BoostGaugeAttached,
            BoostGaugeColorNumber = settings.BoostGaugeColorNumber,
            DigitalBoostGaugeColorNumber = settings.DigitalBoostGaugeColorNumber,
            DigitalBoostGaugeStockColors = settings.DigitalBoostGaugeStockColors,
            BoostPressureUnit = settings.BoostPressureUnit,
            BoostGaugeScale = settings.BoostGaugeScale,
            TireTemperatureGaugeEnabled = settings.TireTemperatureGaugeEnabled,
            TireTemperatureGaugeAttached = settings.TireTemperatureGaugeAttached,
            TireTemperatureReactiveColors = settings.TireTemperatureReactiveColors,
            TireTemperatureUnit = settings.TireTemperatureUnit,
            TireTemperatureGaugeScale = settings.TireTemperatureGaugeScale,
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
            CustomBoostHighColor = settings.CustomBoostHighColor
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
        settings.GForceWidthScale = GForceWidthScale;
        settings.GForceHeightScale = GForceHeightScale;
        settings.InvertLateralG = InvertLateralG;
        settings.InvertLongitudinalG = InvertLongitudinalG;
        settings.BoostGaugeEnabled = BoostGaugeEnabled;
        settings.BoostGaugeAttached = BoostGaugeAttached;
        settings.BoostGaugeColorNumber = BoostGaugeColorNumber;
        settings.DigitalBoostGaugeColorNumber = DigitalBoostGaugeColorNumber;
        settings.DigitalBoostGaugeStockColors = DigitalBoostGaugeStockColors;
        settings.BoostPressureUnit = BoostPressureUnit;
        settings.BoostGaugeScale = BoostGaugeScale;
        settings.TireTemperatureGaugeEnabled = TireTemperatureGaugeEnabled;
        settings.TireTemperatureGaugeAttached = TireTemperatureGaugeAttached;
        settings.TireTemperatureReactiveColors = TireTemperatureReactiveColors;
        settings.TireTemperatureUnit = TireTemperatureUnit;
        settings.TireTemperatureGaugeScale = TireTemperatureGaugeScale;
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
        if (!Enum.IsDefined(SpeedUnit)) SpeedUnit = Wisp.Core.SpeedUnit.MilesPerHour;
        if (!Enum.IsDefined(TorqueUnit)) TorqueUnit = Wisp.App.TorqueUnit.NewtonMeters;
        if (!Enum.IsDefined(LayoutMode)) LayoutMode = HudLayoutMode.Minimal;
        if (!Enum.IsDefined(NativeGaugeMode)) NativeGaugeMode = Wisp.App.NativeGaugeMode.Digital;
        if (!Enum.IsDefined(GearDisplayMode)) GearDisplayMode = Wisp.App.GearDisplayMode.Manual;
        if (!Enum.IsDefined(BoostPressureUnit)) BoostPressureUnit = Wisp.App.BoostPressureUnit.Psi;
        if (!Enum.IsDefined(TireTemperatureUnit)) TireTemperatureUnit = Wisp.App.TireTemperatureUnit.Fahrenheit;
        OverlayWidthScale = NormalizeScale(OverlayWidthScale);
        OverlayHeightScale = NormalizeScale(OverlayHeightScale);
        GForceWidthScale = NormalizeScale(GForceWidthScale);
        GForceHeightScale = NormalizeScale(GForceHeightScale);
        BoostGaugeScale = NormalizeScale(BoostGaugeScale);
        TireTemperatureGaugeScale = NormalizeScale(TireTemperatureGaugeScale);
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
}
