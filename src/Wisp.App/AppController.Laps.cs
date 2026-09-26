using Wisp.App.Laps;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class AppController
{
    private readonly LapDeltaService _lapDelta = new(LapReferenceStore.ForCurrentUser());
    internal LapDeltaService LapDelta => _lapDelta;
    internal LapDeltaWindow? LapDeltaOverlay { get; private set; }
    internal LapDeltaWindow? LapMapOverlay { get; private set; }
    private string? _lapGameDisplayKey;
    private DateTimeOffset _nextLapMonitorCheckUtc;

    internal void InitializeLapDeltaWindow()
    {
        if (_disposed || Settings.RequiresSetup || LapDeltaOverlay is not null) return;
        LapDeltaOverlay = new LapDeltaWindow(this);
        LapMapOverlay = new LapDeltaWindow(this, map: true);
        RestoreLapPlacement(false);
        RestoreLapPlacement(true);
        ApplyLapDeltaSettings();
    }

    internal void ApplyLapDeltaSettings()
    {
        Settings.NormalizeLapDeltaSettings();
        ConfigureLapWindow(LapDeltaOverlay, Settings.LapDeltaEnabled, Settings.LapDeltaScale);
        ConfigureLapWindow(LapMapOverlay, Settings.LapMapEnabled, Settings.LapMapScale);
        UpdateShiftCueObservation();
    }
    private void ConfigureLapWindow(LapDeltaWindow? window, bool enabled, double scale)
    {
        window?.Configure(Settings.LapDeltaReference, Settings.LapDeltaShowBar, Settings);
        window?.ApplyAppearance(scale, Settings.OverlayOpacity);
        window?.SetEnabled(enabled);
        window?.SetEditMode(!Settings.OverlayLocked);
    }

    public void SetLapTimingMode(LapTimingMode mode)
    {
        if (_disposed || Settings.RequiresSetup) return;
        Settings.LapTimingMode = mode;
        ApplyLapDeltaSettings();
        ScheduleSettingsSave();
    }

    public void SetLapDeltaSettings(bool enabled, LapDeltaReference reference, bool bar, double scale)
    {
        if (_disposed || Settings.RequiresSetup) return;
        var wasEnabled = Settings.LapDeltaEnabled;
        Settings.LapDeltaEnabled = enabled;
        Settings.LapDeltaReference = reference;
        Settings.LapDeltaShowBar = bar;
        Settings.LapDeltaScale = scale;
        ApplyLapDeltaSettings();
        if (enabled && !wasEnabled && WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow)) RestoreLapPlacement(false, _lastConfirmedForzaWindow);
        SaveLapDeltaPlacement();
        UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
        ScheduleSettingsSave();
    }

    public void SetLapMapSettings(bool enabled, double scale)
    {
        if (_disposed || Settings.RequiresSetup) return;
        var wasEnabled = Settings.LapMapEnabled;
        Settings.LapMapEnabled = enabled;
        Settings.LapMapScale = scale;
        ApplyLapDeltaSettings();
        if (enabled && !wasEnabled && WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow)) RestoreLapPlacement(true, _lastConfirmedForzaWindow);
        SaveLapMapPlacement();
        UpdateOverlayVisibility(DateTimeOffset.UtcNow, force: true);
        ScheduleSettingsSave();
    }

    public void SetLapColors(string? ahead, string? behind, string? track, string? car, string? background)
    {
        if (_disposed || Settings.RequiresSetup) return;
        Settings.LapDeltaAheadColor = ahead;
        Settings.LapDeltaBehindColor = behind;
        Settings.LapMapTrackColor = track;
        Settings.LapMapCarColor = car;
        Settings.LapMapBackgroundColor = background;
        ApplyLapDeltaSettings();
        ScheduleSettingsSave();
    }

    public void ResetLapDeltaSession() => _lapDelta.Reset();
    public void SaveLapDeltaPlacement() => SaveLapPlacement(false);
    public void SaveLapMapPlacement() => SaveLapPlacement(true);
    private void SaveLapPlacement(bool map)
    {
        if (_disposed || Settings.RequiresSetup || (map ? LapMapOverlay : LapDeltaOverlay) is not { } window) return;
        window.ClampToMonitor();
        var key = window.GetDisplayKey();
        var scale = map ? Settings.LapMapScale : Settings.LapDeltaScale;
        if (map) Settings.LastLapMapPlacementKey = key;
        else Settings.LastLapDeltaPlacementKey = key;
        (map ? Settings.LapMapPlacements : Settings.LapDeltaPlacements)[key] = new(window.Left, window.Top, scale, scale);
        Settings.NormalizeLapDeltaSettings();
        ScheduleSettingsSave();
    }

    private void RestoreLapPlacement(bool map, IntPtr gameWindow = default)
    {
        if ((map ? LapMapOverlay : LapDeltaOverlay) is not { } window) return;
        var placements = map ? Settings.LapMapPlacements : Settings.LapDeltaPlacements;
        var last = map ? Settings.LastLapMapPlacementKey : Settings.LastLapDeltaPlacementKey;
        var key = window.GetDisplayKey(gameWindow);
        if (gameWindow == IntPtr.Zero && last is { } previous && placements.ContainsKey(previous)) key = previous;
        if (placements.TryGetValue(key, out var placement)) window.RestorePosition(placement.Left, placement.Top);
        else window.ResetPosition(gameWindow);
    }

    private void RefreshLapDeltaDisplay(IntPtr gameWindow, DateTimeOffset now)
    {
        if (LapDeltaOverlay is not { } window || gameWindow == IntPtr.Zero || now < _nextLapMonitorCheckUtc) return;
        _nextLapMonitorCheckUtc = now + TimeSpan.FromSeconds(1);
        var key = window.GetDisplayKey(gameWindow);
        if (_lapGameDisplayKey == key) return;
        _lapGameDisplayKey = key;
        RestoreLapPlacement(false, gameWindow);
        RestoreLapPlacement(true, gameWindow);
    }

    public void ResetLapDeltaPlacement() => ResetLapPlacement(false);
    public void ResetLapMapPlacement() => ResetLapPlacement(true);
    private void ResetLapPlacement(bool map)
    {
        if (_disposed || Settings.RequiresSetup || (map ? LapMapOverlay : LapDeltaOverlay) is not { } window) return;
        (map ? Settings.LapMapPlacements : Settings.LapDeltaPlacements).Remove(window.GetDisplayKey());
        window.ResetPosition(WindowZOrder.IsWindowAvailable(_lastConfirmedForzaWindow) ? _lastConfirmedForzaWindow : IntPtr.Zero);
        SaveLapPlacement(map);
    }
}
