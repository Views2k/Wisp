using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Wisp.Core;

namespace Wisp.App;

public enum HudLayoutMode
{
    Minimal = 0,
    Combined = 1,
    SeparateBoxes = 2,
    Native = 3
}

public enum NativeGaugeMode
{
    Digital = 0,
    Analogue = 1
}

public enum GearDisplayMode
{
    Manual = 0,
    Automatic = 1
}

public enum TireTemperatureUnit
{
    Fahrenheit = 0,
    Celsius = 1
}

public enum BoostPressureUnit
{
    Psi = 0,
    Bar = 1
}

public sealed class AppSettings
{
    private const int CurrentSettingsRevision = 9;

    public int SettingsRevision { get; set; }
    public int UdpPort { get; set; } = 5500;
    public SpeedUnit SpeedUnit { get; set; } = SpeedUnit.MilesPerHour;
    public TorqueUnit TorqueUnit { get; set; } = global::Wisp.App.TorqueUnit.NewtonMeters;
    public SpeedSourceMode SpeedSource { get; set; } = SpeedSourceMode.WheelIndicated;
    public WheelAggregationMode AggregationMode { get; set; } = WheelAggregationMode.RawDrivenWheels;
    public double OverlayWidthScale { get; set; } = 1.0;
    public double OverlayHeightScale { get; set; } = 1.0;
    public double OverlayOpacity { get; set; } = 1.0;
    public double Smoothing { get; set; }
    public bool OverlayLocked { get; set; } = true;
    public bool GForceEnabled { get; set; } = true;
    public bool GForceAttached { get; set; } = true;
    public bool BoostGaugeEnabled { get; set; } = true;
    public bool BoostGaugeAttached { get; set; } = true;
    public bool BoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeColorNumber { get; set; }
    public bool DigitalBoostGaugeStockColors { get; set; }
    public BoostPressureUnit BoostPressureUnit { get; set; } = BoostPressureUnit.Psi;
    public double BoostGaugeScale { get; set; } = 1.0;
    public bool TireTemperatureGaugeEnabled { get; set; } = true;
    public bool TireTemperatureGaugeAttached { get; set; } = true;
    public bool TireTemperatureReactiveColors { get; set; } = true;
    public TireTemperatureUnit TireTemperatureUnit { get; set; } = TireTemperatureUnit.Fahrenheit;
    public double TireTemperatureGaugeScale { get; set; } = 1.0;
    public double GForceWidthScale { get; set; } = 1.0;
    public double GForceHeightScale { get; set; } = 1.0;
    public HudLayoutMode LayoutMode { get; set; } = HudLayoutMode.Minimal;
    public NativeGaugeMode NativeGaugeMode { get; set; } = NativeGaugeMode.Digital;
    public GearDisplayMode GearDisplayMode { get; set; } = GearDisplayMode.Manual;
    public bool InvertLateralG { get; set; } = true;
    public bool InvertLongitudinalG { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool StartWithForza { get; set; }
    public bool StartMinimizedWithForza { get; set; }
    public bool AnimatedBackground { get; set; } = true;
    public bool AutomaticApplicationUpdateChecks { get; set; } = true;
    public DateTimeOffset? LastApplicationUpdateCheckUtc { get; set; }
    public bool DebugLoggingEnabled { get; set; }
    public DateTimeOffset? DebugLoggingExpiresAtUtc { get; set; }
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
    public List<HudPreset> HudPresets { get; set; } = new();
    public bool SidebarCollapsed { get; set; }
    public bool GameAwareVisibility { get; set; } = true;
    public bool OverlayHotkeyEnabled { get; set; }
    public OverlayHotkeyModifiers OverlayHotkeyModifiers { get; set; } =
        OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
    public Key OverlayHotkeyKey { get; set; } = Key.H;
    public bool AutoMinimizeOnTelemetry { get; set; } = true;
    public bool TractionCueEnabled { get; set; } = true;
    public bool HasCompletedSetup { get; set; }
    public SetupCompletionRecord? SetupCompletion { get; set; }

    [JsonIgnore]
    internal bool InstallerSetupRequired { get; private set; }

    [JsonIgnore]
    public bool RequiresSetup => InstallerSetupRequired || SetupCompletion?.IsValid != true;

    public Dictionary<string, OverlayPlacement> Placements { get; set; } = new();
    public Dictionary<string, OverlayPlacement> GForcePlacements { get; set; } = new();
    public Dictionary<string, OverlayPlacement> BoostGaugePlacements { get; set; } = new();
    public Dictionary<string, OverlayPlacement> TireTemperatureGaugePlacements { get; set; } = new();
    public string? LastOverlayPlacementKey { get; set; }
    public string? LastGForcePlacementKey { get; set; }
    public string? LastBoostGaugePlacementKey { get; set; }
    public string? LastTireTemperatureGaugePlacementKey { get; set; }
    public List<CalibrationSnapshot> Calibrations { get; set; } = new();

    [JsonPropertyName("OverlayScale")]
    public double? LegacyOverlayScale { get; set; }

    [JsonPropertyName("GForceScale")]
    public double? LegacyGForceScale { get; set; }

    public void MigrateLegacySizing()
    {
        Placements ??= new Dictionary<string, OverlayPlacement>();
        GForcePlacements ??= new Dictionary<string, OverlayPlacement>();
        BoostGaugePlacements ??= new Dictionary<string, OverlayPlacement>();
        TireTemperatureGaugePlacements ??= new Dictionary<string, OverlayPlacement>();

        if (LegacyOverlayScale is { } overlayScale)
        {
            OverlayWidthScale = overlayScale;
            OverlayHeightScale = overlayScale;
            LegacyOverlayScale = null;
        }

        if (LegacyGForceScale is { } gForceScale)
        {
            GForceWidthScale = gForceScale;
            GForceHeightScale = gForceScale;
            LegacyGForceScale = null;
        }

        foreach (var placement in Placements.Values.Concat(GForcePlacements.Values).Concat(BoostGaugePlacements.Values)
                     .Concat(TireTemperatureGaugePlacements.Values)
                     .Where(value => value is not null))
        {
            placement.MigrateLegacySizing();
        }
    }

    public void MigrateSettings()
    {
        MigrateLegacySizing();
        if (SettingsRevision < 2)
        {
            // Earlier releases silently selected an outlier-limited value that
            // was not the literal mechanical driven-wheel average.
            AggregationMode = WheelAggregationMode.RawDrivenWheels;
            if (Math.Abs(Smoothing - 0.18) < 0.0001)
            {
                Smoothing = 0.08;
            }
        }

        if (SettingsRevision < 3 && Math.Abs(Smoothing - 0.08) < 0.0001)
        {
            // The former default added avoidable latency to the physical
            // driven-wheel reading. Preserve other explicit user values.
            Smoothing = 0;
        }

        if (SettingsRevision < 4)
        {
            // Calibration records written by earlier physics models do not
            // identify their schema or, in some releases, their drivetrain.
            // Relearn them rather than silently trusting incompatible data.
            Calibrations = new List<CalibrationSnapshot>();
            // Restore the literal per-frame reading for this accuracy release.
            // Smoothing remains available as an explicit post-migration choice.
            Smoothing = 0;
        }

        if (SettingsRevision < 7 && LayoutMode == HudLayoutMode.Native)
        {
            // Native assets now retain their source material alpha correctly.
            // Reset the former whole-window attenuation once so an upgraded
            // Native HUD starts at FH6's authored opacity. The slider remains
            // fully user-adjustable after migration.
            OverlayOpacity = 1.0;
        }

        SettingsRevision = CurrentSettingsRevision;
        Normalize();
    }

    private void Normalize()
    {
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

        if (UdpPort is < 1024 or > 65535 or >= 5200 and <= 5300)
        {
            UdpPort = 5500;
        }

        if (!Enum.IsDefined(SpeedUnit))
        {
            SpeedUnit = SpeedUnit.MilesPerHour;
        }

        if (!Enum.IsDefined(TorqueUnit))
        {
            TorqueUnit = global::Wisp.App.TorqueUnit.NewtonMeters;
        }

        if (!Enum.IsDefined(SpeedSource))
        {
            SpeedSource = SpeedSourceMode.WheelIndicated;
        }

        if (!Enum.IsDefined(AggregationMode))
        {
            AggregationMode = WheelAggregationMode.RawDrivenWheels;
        }

        if (!Enum.IsDefined(LayoutMode))
        {
            LayoutMode = HudLayoutMode.Minimal;
        }

        if (!Enum.IsDefined(NativeGaugeMode))
        {
            NativeGaugeMode = NativeGaugeMode.Digital;
        }

        if (!Enum.IsDefined(GearDisplayMode))
        {
            GearDisplayMode = GearDisplayMode.Manual;
        }

        if (!OverlayHotkeyChord.TryCreate(
                OverlayHotkeyModifiers,
                OverlayHotkeyKey,
                out _,
                out _))
        {
            OverlayHotkeyEnabled = false;
            OverlayHotkeyModifiers = OverlayHotkeyChord.Default.Modifiers;
            OverlayHotkeyKey = OverlayHotkeyChord.Default.Key;
        }

        var debugLoggingNowUtc = DateTimeOffset.UtcNow;
        if (!DebugLoggingEnabled ||
            DebugLoggingExpiresAtUtc is not { } debugLoggingExpiry ||
            debugLoggingExpiry <= debugLoggingNowUtc ||
            debugLoggingExpiry > debugLoggingNowUtc + TimeSpan.FromHours(24))
        {
            DebugLoggingEnabled = false;
            DebugLoggingExpiresAtUtc = null;
        }

        OverlayWidthScale = NormalizeScale(OverlayWidthScale);
        OverlayHeightScale = NormalizeScale(OverlayHeightScale);
        GForceWidthScale = NormalizeScale(GForceWidthScale);
        GForceHeightScale = NormalizeScale(GForceHeightScale);
        BoostGaugeScale = NormalizeScale(BoostGaugeScale);
        TireTemperatureGaugeScale = NormalizeScale(TireTemperatureGaugeScale);
        OverlayOpacity = double.IsFinite(OverlayOpacity)
            ? Math.Clamp(OverlayOpacity, 0.35, 1.0)
            : 1.0;
        Smoothing = double.IsFinite(Smoothing)
            ? Math.Clamp(Smoothing, 0, 1)
            : 0;
        Placements ??= new Dictionary<string, OverlayPlacement>();
        GForcePlacements ??= new Dictionary<string, OverlayPlacement>();
        BoostGaugePlacements ??= new Dictionary<string, OverlayPlacement>();
        TireTemperatureGaugePlacements ??= new Dictionary<string, OverlayPlacement>();
        Calibrations ??= new List<CalibrationSnapshot>();
        HudPresets ??= new List<HudPreset>();
        HudPreset.NormalizeList(HudPresets);
        if (!Enum.IsDefined(TireTemperatureUnit))
        {
            TireTemperatureUnit = TireTemperatureUnit.Fahrenheit;
        }
        if (!Enum.IsDefined(BoostPressureUnit))
        {
            BoostPressureUnit = BoostPressureUnit.Psi;
        }
        // Older releases set this flag on the first packet. Only an explicit,
        // versioned wizard completion can now satisfy the startup gate.
        HasCompletedSetup = !RequiresSetup;
        Calibrations = Calibrations
            .Where(snapshot =>
                snapshot is not null &&
                snapshot.CarOrdinal > 0 &&
                snapshot.CalibrationRevision == RollingRadiusEstimator.CurrentCalibrationRevision &&
                snapshot.Drivetrain is { } drivetrain &&
                Enum.IsDefined(drivetrain) &&
                snapshot.SampleCount >= CalibrationOptions.DefaultMinimumSamples &&
                RollingRadiusEstimator.TrySnapshotRadii(snapshot, drivetrain, out _))
            .GroupBy(snapshot => snapshot.CarOrdinal)
            .Select(group => group.Last())
            .ToList();
        NormalizePlacements(Placements);
        NormalizePlacements(GForcePlacements);
        NormalizePlacements(BoostGaugePlacements);
        NormalizePlacements(TireTemperatureGaugePlacements);

        if (LastOverlayPlacementKey is not null && !Placements.ContainsKey(LastOverlayPlacementKey))
        {
            LastOverlayPlacementKey = null;
        }

        if (LastGForcePlacementKey is not null && !GForcePlacements.ContainsKey(LastGForcePlacementKey))
        {
            LastGForcePlacementKey = null;
        }

        if (LastBoostGaugePlacementKey is not null && !BoostGaugePlacements.ContainsKey(LastBoostGaugePlacementKey))
        {
            LastBoostGaugePlacementKey = null;
        }

        if (LastTireTemperatureGaugePlacementKey is not null &&
            !TireTemperatureGaugePlacements.ContainsKey(LastTireTemperatureGaugePlacementKey))
        {
            LastTireTemperatureGaugePlacementKey = null;
        }
    }

    private static void NormalizePlacements(Dictionary<string, OverlayPlacement> placements)
    {
        foreach (var key in placements
                     .Where(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || !pair.Value.Normalize())
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            placements.Remove(key);
        }
    }

    private static double NormalizeScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.5, 2.0) : 1.0;

    internal void MarkInstallerSetupRequired()
    {
        InstallerSetupRequired = true;
        HasCompletedSetup = false;
    }

    internal void ClearInstallerSetupRequirement() => InstallerSetupRequired = false;

}

public sealed record SetupCompletionRecord
{
    public const int CurrentVersion = 1;
    public const int MinimumPackets = 12;
    public const int MinimumMovingPackets = 3;
    public const int MinimumElapsedMilliseconds = 500;
    public const int TestTimeoutSeconds = 45;

    public int Version { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int ValidatedUdpPort { get; init; }
    public int ValidatedPackets { get; init; }
    public int MovingPackets { get; init; }
    public double ValidatedElapsedMilliseconds { get; init; }
    public bool DataOutConfirmed { get; init; }
    public bool DisplayModeConfirmed { get; init; }
    public bool StockHudConfirmed { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        Version == CurrentVersion &&
        CompletedAtUtc > DateTimeOffset.UnixEpoch &&
        ValidatedUdpPort is >= 1024 and <= 65535 and not (>= 5200 and <= 5300) &&
        ValidatedPackets >= MinimumPackets &&
        MovingPackets >= MinimumMovingPackets && MovingPackets <= ValidatedPackets &&
        double.IsFinite(ValidatedElapsedMilliseconds) &&
        ValidatedElapsedMilliseconds >= MinimumElapsedMilliseconds &&
        ValidatedElapsedMilliseconds <= TestTimeoutSeconds * 1000 &&
        DataOutConfirmed && DisplayModeConfirmed && StockHudConfirmed;
}

public sealed class OverlayPlacement
{
    public OverlayPlacement()
    {
    }

    public OverlayPlacement(double left, double top, double widthScale, double heightScale)
    {
        Left = left;
        Top = top;
        WidthScale = widthScale;
        HeightScale = heightScale;
    }

    public double Left { get; set; }
    public double Top { get; set; }
    public double WidthScale { get; set; } = 1.0;
    public double HeightScale { get; set; } = 1.0;

    [JsonPropertyName("Scale")]
    public double? LegacyScale { get; set; }

    public void MigrateLegacySizing()
    {
        if (LegacyScale is not { } scale)
        {
            return;
        }

        WidthScale = scale;
        HeightScale = scale;
        LegacyScale = null;
    }

    public bool Normalize()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return false;
        }

        WidthScale = double.IsFinite(WidthScale) ? Math.Clamp(WidthScale, 0.5, 2.0) : 1.0;
        HeightScale = double.IsFinite(HeightScale) ? Math.Clamp(HeightScale, 0.5, 2.0) : 1.0;
        return true;
    }
}

public sealed class SettingsService
{
    internal const string SetupRequiredMarkerFileName = "setup-required";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;
    private readonly string _setupRequiredMarkerPath;

    public SettingsService(string? settingsPath = null)
    {
        if (settingsPath is not null)
        {
            _settingsPath = Path.GetFullPath(settingsPath);
        }
        else
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wisp");
            _settingsPath = Path.Combine(directory, "settings.json");
        }

        _setupRequiredMarkerPath = Path.Combine(
            Path.GetDirectoryName(_settingsPath)!,
            SetupRequiredMarkerFileName);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return PrepareForLoad(new AppSettings());
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                           ?? new AppSettings();
            return PrepareForLoad(settings);
        }
        catch (JsonException)
        {
            return PrepareForLoad(new AppSettings());
        }
        catch (IOException)
        {
            return PrepareForLoad(new AppSettings());
        }
        catch (UnauthorizedAccessException)
        {
            return PrepareForLoad(new AppSettings());
        }
        catch (SecurityException)
        {
            return PrepareForLoad(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        settings.ColorTheme = AppColorThemes.NormalizeName(settings.ColorTheme);
        settings.BackgroundTheme = AppBackgroundThemes.NormalizeName(settings.BackgroundTheme);
        settings.HudBorderTheme = AppColorThemes.NormalizeName(settings.HudBorderTheme);
        settings.BoostGaugeTheme = BoostGaugeThemes.NormalizeName(settings.BoostGaugeTheme);
        settings.CustomAccentColor = ColorCustomization.NormalizeAccent(settings.CustomAccentColor);
        settings.CustomBackgroundColor = ColorCustomization.NormalizeBackground(settings.CustomBackgroundColor);
        settings.CustomHudBorderColor = ColorCustomization.NormalizeHudBorder(settings.CustomHudBorderColor);
        settings.CustomBoostLowColor = ColorCustomization.NormalizeGauge(settings.CustomBoostLowColor);
        settings.CustomBoostMidColor = ColorCustomization.NormalizeGauge(settings.CustomBoostMidColor);
        settings.CustomBoostHighColor = ColorCustomization.NormalizeGauge(settings.CustomBoostHighColor);
        settings.CustomTractionCueColor = ColorCustomization.NormalizeTractionCue(settings.CustomTractionCueColor);
        settings.HudPresets ??= new List<HudPreset>();
        HudPreset.NormalizeList(settings.HudPresets);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Staging files are never loaded; a later save uses a unique name.
            }
        }
    }

    internal void SaveCompletedSetup(AppSettings settings)
    {
        if (settings.SetupCompletion?.IsValid != true || !settings.HasCompletedSetup)
        {
            throw new InvalidOperationException("Setup completion must be valid before it can be persisted.");
        }

        // The marker is the install transaction's startup gate. It is removed
        // only after the completed wizard state has reached settings.json.
        Save(settings);
        File.Delete(_setupRequiredMarkerPath);
        settings.ClearInstallerSetupRequirement();
    }

    private AppSettings PrepareForLoad(AppSettings settings)
    {
        settings.MigrateSettings();
        if (File.Exists(_setupRequiredMarkerPath))
        {
            settings.MarkInstallerSetupRequired();
        }

        return settings;
    }
}
