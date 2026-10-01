using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Xunit;

namespace Wisp.App.Tests;

internal static class OverlayMonitorPlacementTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var monitors = new List<Rectangle>();
        Assert.True(EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref Rectangle bounds, IntPtr data) =>
            {
                monitors.Add(bounds);
                return true;
            }, IntPtr.Zero));
        Assert.NotEmpty(monitors);
        var foreground = GetForegroundWindow();
        var reference = new Window { Width = 160, Height = 120, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var referenceHandle = new WindowInteropHelper(reference).EnsureHandle();
            foreach (var monitor in monitors)
            {
                Assert.True(SetWindowPos(referenceHandle, IntPtr.Zero,
                    monitor.Left + 40, monitor.Top + 40, 160, 120, 0x0014));
                foreach (var layout in Enum.GetValues<HudLayoutMode>())
                {
                    AssertPlacement(referenceHandle, monitor, layout, NativeGaugeMode.Digital);
                    if (layout == HudLayoutMode.Native)
                        AssertPlacement(referenceHandle, monitor, layout, NativeGaugeMode.Analogue);
                }
                AssertGForcePlacement(referenceHandle, preserveSaved: false);
                AssertGForcePlacement(referenceHandle, preserveSaved: true);
            }
            Assert.Equal(foreground, GetForegroundWindow());
        }
        finally { reference.Close(); }
    }

    private static void AssertGForcePlacement(IntPtr reference, bool preserveSaved)
    {
        var settings = new AppSettings
        {
            LayoutMode = HudLayoutMode.SeparateBoxes,
            OverlayLocked = true,
            GForceEnabled = true,
            GForceAttached = false,
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false
        };
        // An unrelated monitor's history must not block the current default.
        settings.GForcePlacements["Other-1280x720-GForceV2"] = new(60, 70, 1, 1);
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var overlay = new OverlayWindow(controller);
        var gForce = new GForceWindow(controller);
        controller.Overlay = overlay;
        controller.GForceOverlay = gForce;
        try
        {
            controller.RestoreOverlayPlacement();
            gForce.RestorePosition(80, 90);
            if (preserveSaved) controller.SaveGForcePlacement();
            var before = new Point(gForce.Left, gForce.Top);
            controller.CompleteInitialOverlayPlacement(reference);
            var expected = preserveSaved ? before : OverlayPlacementGeometry.PlaceAdjacentHorizontally(
                overlay.CurrentMonitorPlacementArea(), overlay.GetPlacementBounds(), new Size(gForce.Width, gForce.Height));
            Assert.Equal(expected.X, gForce.Left, 5);
            Assert.Equal(expected.Y, gForce.Top, 5);
            Assert.True(settings.GForcePlacements.ContainsKey("Other-1280x720-GForceV2"));
        }
        finally
        {
            gForce.Close();
            overlay.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void AssertPlacement(IntPtr reference, Rectangle monitor, HudLayoutMode layout, NativeGaugeMode mode)
    {
        var settings = new AppSettings
        {
            LayoutMode = layout,
            NativeGaugeMode = mode,
            OverlayLocked = true,
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false
        };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var overlay = new OverlayWindow(controller);
        controller.Overlay = overlay;
        try
        {
            controller.RestoreOverlayPlacement();
            controller.CompleteInitialOverlayPlacement(IntPtr.Zero);
            controller.CompleteInitialOverlayPlacement(new IntPtr(-1));
            Assert.Empty(settings.Placements);
            Assert.Null(settings.LastOverlayPlacementKey);
            var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Assert.Empty(reloaded.Placements);
            Assert.Null(reloaded.LastOverlayPlacementKey);

            // Editing must not jump under the pointer when Forza becomes known.
            settings.OverlayLocked = false;
            var editing = overlay.GetPlacementBounds();
            controller.CompleteInitialOverlayPlacement(reference);
            Assert.Equal(editing, overlay.GetPlacementBounds());
            Assert.Empty(settings.Placements);

            settings.OverlayLocked = true;
            // A locked profile can be applied while initial placement is still
            // pending. Its layout must survive until profile positions restore.
            settings.OverlayWidthScale = 1.25;
            settings.OverlayHeightScale = .8;
            overlay.ApplyLayout(layout, mode, 1.25, .8, settings.OverlayOpacity);
            var profileBounds = overlay.GetPlacementBounds();
            var applyingProfile = typeof(AppController).GetField("_applyingHudPreset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            applyingProfile.SetValue(controller, true);
            controller.CompleteInitialOverlayPlacement(reference);
            Assert.Equal(profileBounds, overlay.GetPlacementBounds());
            Assert.Equal(1.25, settings.OverlayWidthScale);
            Assert.Equal(.8, settings.OverlayHeightScale);
            Assert.Empty(settings.Placements);
            Assert.Null(settings.LastOverlayPlacementKey);
            applyingProfile.SetValue(controller, false);

            // The deferred default still completes after profile application.
            controller.CompleteInitialOverlayPlacement(reference);
            var handle = new WindowInteropHelper(overlay).Handle;
            Assert.Equal(MonitorFromWindow(reference, 2), MonitorFromWindow(handle, 2));
            Assert.True(GetWindowRect(handle, out var placed));
            Assert.InRange(placed.Left, monitor.Left - 1, monitor.Right);
            Assert.InRange(placed.Top, monitor.Top - 1, monitor.Bottom);
            Assert.InRange(placed.Right, monitor.Left, monitor.Right + 1);
            Assert.InRange(placed.Bottom, monitor.Top, monitor.Bottom + 1);
            Assert.Single(settings.Placements);
            var key = settings.LastOverlayPlacementKey!;
            Assert.Equal(overlay.GetDisplayKey(), key);

            // Saving a manual position cancels automatic placement, including
            // after another restore; scale and position must survive unchanged.
            overlay.RestorePosition(80, 90);
            controller.SaveOverlayPlacement();
            var saved = settings.Placements[settings.LastOverlayPlacementKey!];
            var savedBounds = overlay.GetPlacementBounds();
            controller.RestoreOverlayPlacement();
            controller.CompleteInitialOverlayPlacement(reference);
            var restored = overlay.GetPlacementBounds();
            Assert.Equal(savedBounds.TopLeft, restored.TopLeft);
            // WPF rounds HWND sizes to physical pixels when restoring layout.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(overlay);
            Assert.InRange(Math.Abs(savedBounds.Width - restored.Width), 0, 1 / dpi.DpiScaleX);
            Assert.InRange(Math.Abs(savedBounds.Height - restored.Height), 0, 1 / dpi.DpiScaleY);
            Assert.Equal(saved.WidthScale, settings.OverlayWidthScale);
            Assert.Equal(saved.HeightScale, settings.OverlayHeightScale);

            // The existing reset action is the recovery path for old installs
            // whose wrong-screen default was already saved by an earlier build.
            typeof(AppController).GetField("_lastConfirmedForzaWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, reference);
            controller.ResetOverlayPosition();
            Assert.Equal(MonitorFromWindow(reference, 2), MonitorFromWindow(handle, 2));
            Assert.NotEqual(savedBounds, overlay.GetPlacementBounds());
            Assert.Equal(overlay.GetPlacementBounds().Left, settings.Placements[settings.LastOverlayPlacementKey!].Left);
            Assert.Equal(overlay.GetPlacementBounds().Top, settings.Placements[settings.LastOverlayPlacementKey!].Top);
        }
        finally
        {
            overlay.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // A manual drag before the first game detection must also win.
        var manualController = new AppController(new AppSettings { OverlayLocked = true }, _ => { }, new NoStartupRegistration());
        var manualOverlay = new OverlayWindow(manualController);
        manualController.Overlay = manualOverlay;
        try
        {
            manualController.RestoreOverlayPlacement();
            manualOverlay.RestorePosition(80, 90);
            manualController.SaveOverlayPlacement();
            var bounds = manualOverlay.GetPlacementBounds();
            manualController.CompleteInitialOverlayPlacement(reference);
            Assert.Equal(bounds, manualOverlay.GetPlacementBounds());

            var previousKey = manualController.Settings.LastOverlayPlacementKey!;
            var previousPlacement = manualController.Settings.Placements[previousKey];
            var previousValue = (previousPlacement.Left, previousPlacement.Top, previousPlacement.WidthScale, previousPlacement.HeightScale);
            var nextLayout = manualController.Settings.LayoutMode == HudLayoutMode.Minimal
                ? HudLayoutMode.Combined : HudLayoutMode.Minimal;
            manualController.Settings.LayoutMode = nextLayout;
            manualOverlay.ApplyLayout(nextLayout, NativeGaugeMode.Digital, 1, 1, 1);
            manualController.RestoreOverlayPlacement();
            // ApplyViewOptions updates placement scales after restoring a new style.
            // The pending style must not keep the previous style's save target.
            typeof(AppController).GetMethod("UpdateCurrentPlacementScales", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manualController, null);
            Assert.Null(manualController.Settings.LastOverlayPlacementKey);
            Assert.Equal(previousValue, (previousPlacement.Left, previousPlacement.Top, previousPlacement.WidthScale, previousPlacement.HeightScale));
        }
        finally
        {
            manualOverlay.Close();
            manualController.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rectangle bounds, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rectangle bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
