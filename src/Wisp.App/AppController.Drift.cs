using Wisp.App.Drift;
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
    internal DriftZoneScoringProfile? CurrentDriftZoneProfile => DriftZoneProfileCatalog.ForBuild(_nativeHudProcessService.AttachedCompatibilityPack);
    private bool IsDriftGaugeWindowEnabled => Settings.DriftGaugeEnabled && DriftGaugeOverlay is { RendererFailed: false };

    internal void InitializeDriftGaugeWindow()
    {
        if (_disposed || Settings.RequiresSetup || DriftGaugeOverlay is not null) return;
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
        if (Settings.DriftGaugePlacements.TryGetValue(key, out var placement)) window.RestorePosition(placement.Left, placement.Top);
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
