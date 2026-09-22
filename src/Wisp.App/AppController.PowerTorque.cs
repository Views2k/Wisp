namespace Wisp.App;

public sealed partial class AppController
{
    public PowerTorqueGaugeWindow? PowerGaugeOverlay { get; private set; }
    public PowerTorqueGaugeWindow? TorqueGaugeOverlay { get; private set; }
    private bool PowerTorqueGaugesCanAttach => Settings.LayoutMode == HudLayoutMode.Native &&
        Settings.NativeGaugeMode == NativeGaugeMode.Analogue;
    public bool IsDetachedPowerGaugeEnabled => Settings.PowerGaugeEnabled &&
        (!PowerTorqueGaugesCanAttach || !Settings.PowerGaugeAttached) && ViewModel.PowerTorqueDisplay.Available;
    public bool IsDetachedTorqueGaugeEnabled => Settings.TorqueGaugeEnabled &&
        (!PowerTorqueGaugesCanAttach || !Settings.TorqueGaugeAttached) && ViewModel.PowerTorqueDisplay.Available;
    private bool _powerGaugeDetached;
    private bool _torqueGaugeDetached;

    internal System.Windows.Size DetachedSupplementaryGaugeCellSize
    {
        get
        {
            const double analogSize = PowerTorqueGaugeLayout.GaugeDiameter + 8;
            var power = analogSize * Math.Max(Settings.PowerGaugeScale, Settings.TorqueGaugeScale);
            var boost = analogSize * Settings.BoostGaugeScale;
            var tireWidth = (Settings.NativeGaugeMode == NativeGaugeMode.Digital ? 310 : analogSize) * Settings.TireTemperatureGaugeScale;
            var tireHeight = (Settings.NativeGaugeMode == NativeGaugeMode.Digital ? 92 : analogSize) * Settings.TireTemperatureGaugeScale;
            return new(Math.Max(power, Math.Max(boost, tireWidth)), Math.Max(power, Math.Max(boost, tireHeight)));
        }
    }

    internal System.Windows.Rect DefaultSupplementaryGaugeAnchor(System.Windows.Rect speedBounds, System.Windows.Rect workArea)
    {
        if (!IsStandaloneGForceWindowEnabled || GForceOverlay is not { } meter ||
            !double.IsFinite(meter.Left) || !double.IsFinite(meter.Top) ||
            !double.IsFinite(meter.Width) || !double.IsFinite(meter.Height))
            return speedBounds;

        var meterBounds = new System.Windows.Rect(meter.Left, meter.Top, meter.Width, meter.Height);
        meterBounds.Intersect(workArea);
        return meterBounds.IsEmpty ? speedBounds : System.Windows.Rect.Union(speedBounds, meterBounds);
    }

    internal void InitializePowerTorqueGaugeWindows()
    {
        if (_disposed || Settings.RequiresSetup) return;
        if (PowerGaugeOverlay is null)
        {
            var window = new PowerTorqueGaugeWindow(this, false);
            PowerGaugeOverlay = window;
            window.Closed += (_, _) => { if (ReferenceEquals(PowerGaugeOverlay, window)) PowerGaugeOverlay = null; };
            RestorePowerGaugePlacement();
        }
        if (TorqueGaugeOverlay is null)
        {
            var window = new PowerTorqueGaugeWindow(this, true);
            TorqueGaugeOverlay = window;
            window.Closed += (_, _) => { if (ReferenceEquals(TorqueGaugeOverlay, window)) TorqueGaugeOverlay = null; };
            RestoreTorqueGaugePlacement();
        }
        ApplyPowerTorqueGaugeWindowSettings();
    }

    internal void ApplyPowerTorqueGaugeWindowSettings(bool restorePlacement = false)
    {
        var powerDetached = Settings.PowerGaugeEnabled && (!PowerTorqueGaugesCanAttach || !Settings.PowerGaugeAttached);
        var torqueDetached = Settings.TorqueGaugeEnabled && (!PowerTorqueGaugesCanAttach || !Settings.TorqueGaugeAttached);
        PowerGaugeOverlay?.ApplyAppearance(Settings.PowerGaugeScale, Settings.OverlayOpacity);
        TorqueGaugeOverlay?.ApplyAppearance(Settings.TorqueGaugeScale, Settings.OverlayOpacity);
        PowerGaugeOverlay?.SetEditMode(!Settings.OverlayLocked);
        TorqueGaugeOverlay?.SetEditMode(!Settings.OverlayLocked);
        if (powerDetached && (restorePlacement || !_powerGaugeDetached)) RestorePowerGaugePlacement();
        if (torqueDetached && (restorePlacement || !_torqueGaugeDetached)) RestoreTorqueGaugePlacement();
        _powerGaugeDetached = powerDetached;
        _torqueGaugeDetached = torqueDetached;
        PowerGaugeOverlay?.SetEnabled(IsDetachedPowerGaugeEnabled);
        TorqueGaugeOverlay?.SetEnabled(IsDetachedTorqueGaugeEnabled);
    }

    public void SavePowerGaugePlacement() => SavePowerTorqueGaugePlacement(PowerGaugeOverlay);
    public void SaveTorqueGaugePlacement() => SavePowerTorqueGaugePlacement(TorqueGaugeOverlay);

    private void SavePowerTorqueGaugePlacement(PowerTorqueGaugeWindow? window)
    {
        if (_disposed || Settings.RequiresSetup || window is null) return;
        var key = window.GetDisplayKey();
        var placements = window.IsTorque ? Settings.TorqueGaugePlacements : Settings.PowerGaugePlacements;
        if (window.IsTorque) Settings.LastTorqueGaugePlacementKey = key;
        else Settings.LastPowerGaugePlacementKey = key;
        var scale = window.IsTorque ? Settings.TorqueGaugeScale : Settings.PowerGaugeScale;
        placements[key] = new OverlayPlacement(window.Left, window.Top, scale, scale);
        ScheduleSettingsSave();
    }

    internal void RestorePowerGaugePlacement() => RestorePowerTorqueGaugePlacement(PowerGaugeOverlay);
    internal void RestoreTorqueGaugePlacement() => RestorePowerTorqueGaugePlacement(TorqueGaugeOverlay);

    private void RestorePowerTorqueGaugePlacement(PowerTorqueGaugeWindow? window)
    {
        if (window is null) return;
        window.ApplyAppearance(window.IsTorque ? Settings.TorqueGaugeScale : Settings.PowerGaugeScale,
            Settings.OverlayOpacity);
        var placements = window.IsTorque ? Settings.TorqueGaugePlacements : Settings.PowerGaugePlacements;
        var lastKey = window.IsTorque ? Settings.LastTorqueGaugePlacementKey : Settings.LastPowerGaugePlacementKey;
        var placement = OverlayPlacementResolver.FindPreferredPlacement(placements, lastKey,
            window.GetDisplayKey(), window.PlacementSuffix, out var key);
        if (placement is not null)
        {
            if (window.IsTorque) Settings.LastTorqueGaugePlacementKey = key;
            else Settings.LastPowerGaugePlacementKey = key;
            window.RestorePosition(placement.Left, placement.Top);
        }
        else if (Overlay is not null)
            window.ResetPosition(Overlay.GetPlacementBounds(), Overlay.CurrentMonitorPlacementArea());
    }

    internal void ResetPowerTorqueGaugePositions()
    {
        if (_disposed || Settings.RequiresSetup || Overlay is null) return;
        if (PowerGaugeOverlay is { } power)
        {
            Settings.PowerGaugePlacements.Remove(power.GetDisplayKey());
            power.ResetPosition(Overlay.GetPlacementBounds(), Overlay.CurrentMonitorPlacementArea());
            SavePowerGaugePlacement();
        }
        if (TorqueGaugeOverlay is { } torque)
        {
            Settings.TorqueGaugePlacements.Remove(torque.GetDisplayKey());
            torque.ResetPosition(Overlay.GetPlacementBounds(), Overlay.CurrentMonitorPlacementArea());
            SaveTorqueGaugePlacement();
        }
    }

    public void SetPowerTorqueScalesFromCurrentRun()
    {
        if (_disposed || !ViewModel.SetPowerTorqueScalesFromCurrentRun()) return;
        ScheduleSettingsSave();
    }
}
