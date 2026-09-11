using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class OverlayGForcePlacementTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var previousShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (var mode in new[] { NativeGaugeMode.Digital, NativeGaugeMode.Analogue })
                foreach (var electric in new[] { false, true }) AssertPlacement(mode, electric);
        }
        finally { Application.Current.ShutdownMode = previousShutdown; }
    }

    private static void AssertPlacement(NativeGaugeMode mode, bool electric)
    {
        const double scale = 0.75;
        var settings = new AppSettings
        {
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = mode,
            OverlayWidthScale = scale,
            OverlayHeightScale = scale,
            OverlayOpacity = 1,
            GForceEnabled = false,
            GForceAttached = true,
            BoostGaugeEnabled = false,
            TireTemperatureGaugeEnabled = false,
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false
        };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var window = new OverlayWindow(controller) { Left = 100, Top = 100 };
        controller.Overlay = window;
        try
        {
            window.Show();
            window.SetElectricPowertrain(electric);
            window.SetEditMode(false);
            window.ResetPosition();
            Flush(window);
            var area = window.CurrentMonitorPlacementArea();
            var logicalBounds = window.GetPlacementBounds();
            var expected = OverlayPlacementGeometry.PlaceNativeBottomRight(area, logicalBounds.Size,
                OverlayPlacementGeometry.NativeContentAnchorBounds(mode, electric, scale, scale),
                window.CurrentNativeReferenceScale(), VisualTreeHelper.GetDpi(window).DpiScaleX,
                VisualTreeHelper.GetDpi(window).DpiScaleY);
            AssertLogicalPoint(window, expected, logicalBounds.TopLeft);

            // Restoring a legacy top-edge position clamps the full fixed surface once;
            // subsequent toggles must not move it or hide any part of the enabled meter.
            window.RestorePlacementPosition(area.Left, area.Top);
            Flush(window);
            AssertLogicalPoint(window, area.TopLeft, new Point(window.Left, window.Top));
            AssertTogglesStayPut();
            window.RestorePosition(area.Right, area.Bottom);
            Flush(window);
            Assert.InRange(window.Left + window.Width, area.Left, area.Right + 1);
            Assert.InRange(window.Top + window.Height, area.Top, area.Bottom + 1);
            AssertTogglesStayPut();

            foreach (var enabled in new[] { false, true })
            {
                SetEnabled(enabled);
                window.RestorePosition(area.Left + 40, area.Top + 80);
                Flush(window);
                var before = WindowBounds();
                controller.SaveOverlayPlacement();
                var stored = settings.Placements[settings.LastOverlayPlacementKey!];
                AssertLogicalPoint(window, new Point(before.Left, before.Top + (enabled ? 0 : 72 * scale)),
                    new Point(stored.Left, stored.Top));
                window.RestorePosition(area.Left + 120, area.Top + 120);
                controller.RestoreOverlayPlacement();
                Flush(window);
                AssertLogicalBounds(window, before, WindowBounds());
            }

            SetEnabled(false);
            var hiddenPreset = HudPreset.Capture(settings, "Without G-force");
            SetEnabled(true);
            var shownPreset = HudPreset.Capture(settings, "With G-force");
            settings.HudPresets.AddRange([hiddenPreset, shownPreset]);
            Flush(window);
            var profileBounds = WindowPixelBounds(window);
            foreach (var preset in new[] { hiddenPreset, shownPreset, hiddenPreset })
            {
                Assert.True(controller.TryApplyHudPreset(preset.Id, out var error), error);
                Assert.Equal(profileBounds, WindowPixelBounds(window));
                Flush(window);
                Assert.Equal(profileBounds, WindowPixelBounds(window));
            }

            window.SetTelemetryVisible(true, 1);
            Flush(window);
            var root = Assert.IsType<Grid>(window.FindName("RootPanel"));
            Assert.True(root.IsVisible);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.Width), 64, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).All(index => pixels[index * 4 + 3] == 0),
                "The reserved area must remain transparent while the attached meter is disabled.");
            var style = GetWindowLong(new WindowInteropHelper(window).Handle, -20);
            Assert.NotEqual(0, style & OverlayActivationPolicy.TransparentExtendedStyle);
            Assert.NotEqual(0, style & OverlayActivationPolicy.NoActivateExtendedStyle);

            void AssertTogglesStayPut()
            {
                // WPF may briefly report the requested fractional DIP size before
                // reflecting HWND rounding. Actual pixels must never move or resize.
                var bounds = WindowPixelBounds(window);
                for (var cycle = 0; cycle < 2; cycle++)
                    foreach (var enabled in new[] { true, false })
                    {
                        SetEnabled(enabled);
                        Assert.Equal(bounds, WindowPixelBounds(window));
                        Flush(window);
                        Assert.Equal(bounds, WindowPixelBounds(window));
                        var meter = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("AttachedNativeGForce"));
                        Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, meter.Visibility);
                    }
            }

            void SetEnabled(bool enabled)
            {
                controller.ViewModel.GForceEnabled = enabled;
                settings.GForceEnabled = enabled;
                window.ApplyLayout(HudLayoutMode.Native, mode, scale, scale, 1);
            }

            Rect WindowBounds() => new(window.Left, window.Top, window.Width, window.Height);
        }
        finally
        {
            window.Close();
            controller.Overlay = null;
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private static void AssertLogicalPoint(Window window, Point expected, Point actual)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1 / dpi.DpiScaleX);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1 / dpi.DpiScaleY);
    }

    private static void AssertLogicalBounds(Window window, Rect expected, Rect actual)
    {
        // Saved placement uses DIPs, allowing at most one physical pixel of rounding.
        // Toggle stability is checked separately against exact Win32 rectangles.
        AssertLogicalPoint(window, expected.TopLeft, actual.TopLeft);
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.InRange(Math.Abs(expected.Width - actual.Width), 0, 1 / dpi.DpiScaleX);
        Assert.InRange(Math.Abs(expected.Height - actual.Height), 0, 1 / dpi.DpiScaleY);
    }

    private static (int Left, int Top, int Right, int Bottom) WindowPixelBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(GetWindowRect(handle, out var bounds), "Win32 must provide the live HWND rectangle.");
        return (bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);
}
