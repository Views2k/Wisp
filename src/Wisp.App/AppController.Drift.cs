using System.IO;
using Wisp.App.Drift;
using Wisp.App.Supplementary;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class AppController
{
    private string? _driftGameDisplayKey;
    private DateTimeOffset _nextDriftMonitorCheckUtc;
    public DriftGaugeWindow? DriftGaugeOverlay { get; private set; }
    public event EventHandler? DriftGaugeStatusChanged;
    public string DriftGaugeStatus { get; private set; } = "Off";
    internal VehicleState? LatestDriftTelemetry => _receiver.Latest;
    private DriftZoneCurveCheck? _driftZoneCheck;

    // Read by the drift renderer on every frame; DriftZoneCurveCheck.Resolve never waits.
    internal DriftZoneScoringProfile? CurrentDriftZoneProfile
    {
        get
        {
            var pack = _nativeHudProcessService.AttachedCompatibilityPack;
            var listed = DriftZoneProfileCatalog.ForBuild(pack);
            return _driftZoneCheck is { } check
                ? check.Resolve(pack is null ? null : DriftZoneBuildKey.For(pack), listed,
                    _receiver.Latest is { IsRaceOn: true }, FeatureAllowed("drift-zone-check"))
                : listed;
        }
    }

    private static DriftZoneCurveCheck CreateDriftZoneCheck()
    {
        var factory = new NativeHudProcessMemoryFactory();
        return new DriftZoneCurveCheck(
            DriftZoneProfileCatalog.KnownCurve,
            cancellation =>
            {
                if (!factory.TryOpen(cancellation, out var opened, out _) || opened is null)
                {
                    return null;
                }

                if (opened is not NativeHudProcessMemory game)
                {
                    opened.Dispose();
                    return null;
                }

                return new DriftZoneScanSession(DriftZoneBuildKey.For(game.CompatibilityPack), new DriftZoneGameMemory(game), game);
            },
            new DriftZoneCheckStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "drift-zone-checks.json")),
            static (outcome, duration) => SupplementaryObservations.Record("feature", "drift", outcome, "check", duration));
    }
    private bool IsDriftGaugeWindowEnabled => HudFeatureAllowed("drift-hud") && Settings.DriftGaugeEnabled && DriftGaugeOverlay is { RendererFailed: false };

    internal void InitializeDriftGaugeWindow()
    {
        if (_disposed || Settings.RequiresSetup || DriftGaugeOverlay is not null) return;
        _driftZoneCheck ??= CreateDriftZoneCheck();
        DriftGaugeOverlay = new DriftGaugeWindow(this);
        RestoreDriftGaugePlacement();
        DriftGaugeOverlay.SetEditMode(!Settings.OverlayLocked);
        DriftGaugeOverlay.SetEnabled(Settings.DriftGaugeEnabled);
        SetDriftGaugeStatus(Settings.DriftGaugeEnabled ? "Waiting for driving telemetry" : "Off");
    }

    public void SetDriftGaugeSettings(bool enabled, double targetDegrees, double toleranceDegrees, double scale, bool? darkMode = null,
        DriftGaugeGuidanceMode? guidanceMode = null, bool? backgroundEnabled = null, double? backgroundOpacity = null)
    {
        if (_disposed || Settings.RequiresSetup) return;
        var wasEnabled = Settings.DriftGaugeEnabled;
        Settings.DriftGaugeEnabled = enabled;
        Settings.DriftTargetDegrees = targetDegrees;
        Settings.DriftToleranceDegrees = toleranceDegrees;
        Settings.DriftGaugeScale = scale;
        if (darkMode is { } useDarkMode) Settings.DriftGaugeDarkMode = useDarkMode;
        if (guidanceMode is { } mode) Settings.DriftGaugeGuidanceMode = mode;
        if (backgroundEnabled is { } useBackground) Settings.DriftGaugeBackgroundEnabled = useBackground;
        if (backgroundOpacity is { } backingOpacity) Settings.DriftGaugeBackgroundOpacity = backingOpacity;
        Settings.NormalizeDriftGaugeSettings();
        DriftGaugeOverlay?.ApplyAppearance(Settings.DriftGaugeScale, Settings.OverlayOpacity);
        DriftGaugeOverlay?.SetEnabled(enabled);
        DriftGaugeOverlay?.SetEditMode(!Settings.OverlayLocked);
        if (enabled && !wasEnabled && WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow)) RestoreDriftGaugePlacement(_lastConfirmedForzaWindow);
        if (!enabled) SetDriftGaugeStatus("Off");
        else if (DriftGaugeStatus == "Off") SetDriftGaugeStatus("Waiting for driving telemetry");
        SaveDriftGaugePlacement();
        UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
        ScheduleSettingsSave();
    }

    public void SaveDriftGaugePlacement()
    {
        if (_disposed || Settings.RequiresSetup || DriftGaugeOverlay is not { } window) return;
        window.ClampToMonitor();
        var key = window.GetDisplayKey();
        Settings.LastDriftGaugePlacementKey = key;
        Settings.DriftGaugePlacements[key] = new(window.Left, window.Top, Settings.DriftGaugeScale, Settings.DriftGaugeScale);
        Settings.NormalizeDriftGaugeSettings();
        ScheduleSettingsSave();
    }

    private void RestoreDriftGaugePlacement(IntPtr gameWindow = default)
    {
        if (DriftGaugeOverlay is not { } window) return;
        var key = window.GetDisplayKey(gameWindow);
        if (gameWindow == IntPtr.Zero && Settings.LastDriftGaugePlacementKey is { } previous && Settings.DriftGaugePlacements.ContainsKey(previous)) key = previous;
        if (Settings.DriftGaugePlacements.TryGetValue(key, out var placement))
        {
            MonitorPlacement.TryMoveToMonitor(window, key);
            window.RestorePosition(placement.Left, placement.Top);
        }
        else window.ResetPosition(gameWindow);
    }

    private void RefreshDriftGaugeDisplay(IntPtr gameWindow, DateTimeOffset now)
    {
        if (DriftGaugeOverlay is not { } window || gameWindow == IntPtr.Zero || now < _nextDriftMonitorCheckUtc) return;
        _nextDriftMonitorCheckUtc = now + TimeSpan.FromSeconds(1);
        var key = window.GetDisplayKey(gameWindow);
        if (_driftGameDisplayKey == key) return;
        _driftGameDisplayKey = key;
        RestoreDriftGaugePlacement(gameWindow);
    }

    public void ResetDriftGaugePlacement()
    {
        if (_disposed || Settings.RequiresSetup || DriftGaugeOverlay is not { } window) return;
        Settings.DriftGaugePlacements.Remove(window.GetDisplayKey());
        window.ResetPosition(WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow) ? _lastConfirmedForzaWindow : IntPtr.Zero);
        SaveDriftGaugePlacement();
    }

    internal void UpdateDriftGaugeStatus(bool ready, int error)
    {
        if (_disposed || !Settings.DriftGaugeEnabled) return;
        SetDriftGaugeStatus(ready ? "Ready" : $"Renderer unavailable (0x{error:X8}). Turn the drift gauge off and on to retry.");
    }

    private void SetDriftGaugeStatus(string status)
    {
        if (DriftGaugeStatus == status) return;
        DriftGaugeStatus = status;
        DriftGaugeStatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
