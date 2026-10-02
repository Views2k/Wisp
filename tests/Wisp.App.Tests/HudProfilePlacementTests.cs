using System.Windows;
using System.Windows.Interop;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class HudProfilePlacementTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            AutomaticApplicationUpdateChecks = false,
            GForceAttached = false,
            BoostGaugeAttached = false,
            TireTemperatureGaugeAttached = false,
            PowerGaugeAttached = false,
            TorqueGaugeAttached = false,
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true
        };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        }, new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var previousShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var windows = new List<Window>();
        try
        {
            controller.Overlay = new OverlayWindow(controller);
            controller.GForceOverlay = new GForceWindow(controller);
            controller.BoostGaugeOverlay = new BoostGaugeWindow(controller);
            controller.TireTemperatureGaugeOverlay = new TireTemperatureGaugeWindow(controller);
            controller.InitializeDriftGaugeWindow();
            controller.InitializePowerTorqueGaugeWindows();
            controller.InitializeLapDeltaWindow();
            windows.AddRange([controller.Overlay, controller.GForceOverlay, controller.BoostGaugeOverlay,
                controller.TireTemperatureGaugeOverlay, controller.DriftGaugeOverlay!, controller.PowerGaugeOverlay!,
                controller.TorqueGaugeOverlay!, controller.LapDeltaOverlay!, controller.LapMapOverlay!]);
            var area = SystemParameters.WorkArea;
            for (var index = 0; index < windows.Count; index++)
            {
                var window = windows[index];
                window.Left = area.Left + 60 + index * 4;
                window.Top = area.Top + 70 + index * 4;
                _ = new WindowInteropHelper(window).EnsureHandle();
            }
            Assert.True(controller.TryCreateHudPreset("Arrangement", out var profile, out var error), error);
            var expected = windows.Select(window => new Point(window.Left, window.Top)).ToArray();
            foreach (var window in windows)
            {
                window.Left += 100;
                window.Top += 80;
            }
            controller.ViewModel.OverlayWidthScale = 1.5;
            controller.ViewModel.BoostGaugeScale = 1.4;
            controller.ViewModel.TireTemperatureGaugeScale = 1.3;
            controller.ApplyViewOptions();
            Assert.True(controller.TryApplyHudPreset(profile!.Id, out error), error);
            for (var index = 0; index < windows.Count; index++)
            {
                Assert.InRange(Math.Abs(expected[index].X - windows[index].Left), 0, 1);
                Assert.InRange(Math.Abs(expected[index].Y - windows[index].Top), 0, 1);
            }
            Assert.Equal(profile.OverlayWidthScale, settings.OverlayWidthScale);
            Assert.Equal(profile.BoostGaugeScale, settings.BoostGaugeScale);
            Assert.Equal(profile.TireTemperatureGaugeScale, settings.TireTemperatureGaugeScale);
            Assert.Equal(profile.OverlayWidthScale, settings.Placements[settings.LastOverlayPlacementKey!].WidthScale);
            Assert.Equal(profile.BoostGaugeScale, settings.BoostGaugePlacements[settings.LastBoostGaugePlacementKey!].WidthScale);
        }
        finally
        {
            foreach (var window in windows) window.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Application.Current.ShutdownMode = previousShutdown;
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
