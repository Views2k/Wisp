using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wisp.App;

public sealed partial class AppController
{
    private bool _applyingHudPreset;

    private void CaptureCurrentHudPlacements()
    {
        SaveOverlayPlacement();
        SaveGForcePlacement();
        SaveBoostGaugePlacement();
        SaveTireTemperatureGaugePlacement();
        SaveDriftGaugePlacement();
        SavePowerGaugePlacement();
        SaveTorqueGaugePlacement();
        SaveLapDeltaPlacement();
        SaveLapMapPlacement();
    }

    private void RestoreHudProfilePlacements(HudPreset preset)
    {
        RestoreProfilePlacement(Overlay, preset.Placements, () => Overlay!.GetDisplayKey(), (x, y) => Overlay!.RestorePlacementPosition(x, y),
            () => Overlay!.GetPlacementBounds());
        RestoreProfilePlacement(GForceOverlay, preset.GForcePlacements, () => GForceOverlay!.GetDisplayKey(), (x, y) => GForceOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(BoostGaugeOverlay, preset.BoostGaugePlacements, () => BoostGaugeOverlay!.GetDisplayKey(), (x, y) => BoostGaugeOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(TireTemperatureGaugeOverlay, preset.TireTemperatureGaugePlacements, () => TireTemperatureGaugeOverlay!.GetDisplayKey(), (x, y) => TireTemperatureGaugeOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(DriftGaugeOverlay, preset.DriftGaugePlacements, () => DriftGaugeOverlay!.GetDisplayKey(), (x, y) => DriftGaugeOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(PowerGaugeOverlay, preset.PowerGaugePlacements, () => PowerGaugeOverlay!.GetDisplayKey(), (x, y) => PowerGaugeOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(TorqueGaugeOverlay, preset.TorqueGaugePlacements, () => TorqueGaugeOverlay!.GetDisplayKey(), (x, y) => TorqueGaugeOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(LapDeltaOverlay, preset.LapDeltaPlacements, () => LapDeltaOverlay!.GetDisplayKey(), (x, y) => LapDeltaOverlay!.RestorePosition(x, y));
        RestoreProfilePlacement(LapMapOverlay, preset.LapMapPlacements, () => LapMapOverlay!.GetDisplayKey(), (x, y) => LapMapOverlay!.RestorePosition(x, y));
        // Persist the actual clamped/current-screen positions and profile sizes together.
        // This does not mutate the profile's independent arrangement snapshot.
        CaptureCurrentHudPlacements();
    }

    private static void RestoreProfilePlacement(Window? window,
        IReadOnlyDictionary<string, OverlayPlacement>? placements, Func<string> getKey, Action<double, double> restore,
        Func<Rect>? getPlacementBounds = null)
    {
        if (window is null) return;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        OverlayPlacement? placement = null;
        placements?.TryGetValue(getKey(), out placement);
        var current = getPlacementBounds?.Invoke().TopLeft ?? new Point(window.Left, window.Top);
        // The main native HUD saves a logical top below its transparent G-force
        // reserve. Every supplementary window saves its actual HWND origin.
        var savedOriginOffset = current - new Point(window.Left, window.Top);
        placement ??= new OverlayPlacement(current.X, current.Y, 1, 1);
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top)) return;
        var monitor = new ProfileMonitorInfo { Size = Marshal.SizeOf<ProfileMonitorInfo>() };
        if (!GetProfileMonitorInfo(ProfileMonitorFromWindow(handle, 2), ref monitor)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var area = new Rect(monitor.Bounds.Left / dpi.DpiScaleX, monitor.Bounds.Top / dpi.DpiScaleY,
            (monitor.Bounds.Right - monitor.Bounds.Left) / dpi.DpiScaleX,
            (monitor.Bounds.Bottom - monitor.Bounds.Top) / dpi.DpiScaleY);
        var point = ClampProfilePosition(placement, area, new Size(window.Width, window.Height), savedOriginOffset);
        restore(point.X, point.Y);
    }

    internal static Point ClampProfilePosition(OverlayPlacement placement, Rect currentMonitor, Size windowSize,
        Vector savedOriginOffset = default)
    {
        // MonitorFromWindow uses the largest intersection, not the top-left point.
        // Keep the complete HWND on this monitor before normal RestorePosition
        // re-detects it, then return to the saved-position coordinate convention.
        var requested = new Point(placement.Left, placement.Top) - savedOriginOffset;
        return OverlayPlacementGeometry.ClampNativeInside(currentMonitor, windowSize, requested) + savedOriginOffset;
    }

    private bool TryRegisterProfileShortcuts(HudPreset preset, out string error)
    {
        error = string.Empty;
        if (_runtimeSuspended) return true;
        var requests = new[]
        {
            new ProfileShortcut("HUD visibility", Settings.OverlayHotkeyEnabled,
                new(Settings.OverlayHotkeyModifiers, Settings.OverlayHotkeyKey), preset.OverlayHotkeyEnabled,
                new(preset.OverlayHotkeyModifiers, preset.OverlayHotkeyKey), _overlayHotkeyRegistration),
            new ProfileShortcut("Recording", Settings.RecordingShortcutEnabled,
                new(Settings.RecordingShortcutModifiers, Settings.RecordingShortcutKey), preset.RecordingShortcutEnabled,
                new(preset.RecordingShortcutModifiers, preset.RecordingShortcutKey), _recordingHotkeyRegistration),
            new ProfileShortcut("Marker", Settings.MarkerShortcutEnabled,
                new(Settings.MarkerShortcutModifiers, Settings.MarkerShortcutKey), preset.MarkerShortcutEnabled,
                new(preset.MarkerShortcutModifiers, preset.MarkerShortcutKey), _markerHotkeyRegistration)
        };
        if (requests.All(request => request.PreviousEnabled == request.Enabled && request.PreviousChord == request.Chord))
            return true;
        if (requests.Where(request => request.Enabled).GroupBy(request => request.Chord).Any(group => group.Count() > 1))
        {
            error = "This profile assigns the same shortcut to multiple actions. Change its shortcuts before applying it.";
            return false;
        }
        if (requests.Any(request => request.Register is null && (request.Enabled || request.PreviousEnabled)))
        {
            error = "The shortcut service is unavailable. The profile was not applied.";
            return false;
        }
        // Release all three first so a valid profile can swap their existing chords.
        foreach (var request in requests) request.Register?.Invoke(false, request.PreviousChord);
        foreach (var request in requests)
        {
            var result = request.Register?.Invoke(request.Enabled, request.Chord) ?? OverlayHotkeyRegistrationResult.Success;
            if (result.Succeeded) continue;
            foreach (var rollback in requests) rollback.Register?.Invoke(false, rollback.Chord);
            var restored = true;
            foreach (var rollback in requests)
                restored &= rollback.Register?.Invoke(rollback.PreviousEnabled, rollback.PreviousChord).Succeeded ?? true;
            error = $"{request.Label} shortcut could not be registered: {result.Error}. The profile was not applied." +
                    (restored ? "" : " A previous shortcut also could not be restored; check its shortcut control.");
            return false;
        }
        return true;
    }

    private sealed record ProfileShortcut(string Label, bool PreviousEnabled, OverlayHotkeyChord PreviousChord,
        bool Enabled, OverlayHotkeyChord Chord,
        Func<bool, OverlayHotkeyChord, OverlayHotkeyRegistrationResult>? Register);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProfileRectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProfileMonitorInfo
    {
        public int Size;
        public ProfileRectangle Bounds;
        public ProfileRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern IntPtr ProfileMonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProfileMonitorInfo(IntPtr monitor, ref ProfileMonitorInfo info);
}
