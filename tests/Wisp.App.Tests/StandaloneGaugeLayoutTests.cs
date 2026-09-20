using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class StandaloneGaugeLayoutTests
{
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    internal static void AssertOnCurrentDispatcher()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            BoostGaugeEnabled = true,
            TireTemperatureGaugeEnabled = true,
            BoostGaugeAttached = true,
            TireTemperatureGaugeAttached = true
        };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var overlay = new OverlayWindow(controller);
        var boost = new BoostGaugeWindow(controller);
        var tire = new TireTemperatureGaugeWindow(controller);
        try
        {
            UpdateTelemetry(controller.ViewModel, false);
            foreach (var mode in new[] { NativeGaugeMode.Analogue, NativeGaugeMode.Digital })
                foreach (var attached in new[] { true, false })
                    foreach (var layout in new[] { HudLayoutMode.Native, HudLayoutMode.Minimal,
                        HudLayoutMode.Combined, HudLayoutMode.SeparateBoxes, HudLayoutMode.Native })
                    {
                        var model = controller.ViewModel;
                        settings.LayoutMode = layout;
                        settings.NativeGaugeMode = mode;
                        settings.BoostGaugeAttached = model.BoostGaugeAttached = attached;
                        settings.TireTemperatureGaugeAttached = model.TireTemperatureGaugeAttached = attached;
                        model.LayoutSelectionIndex = (int)layout;
                        model.NativeGaugeSelectionIndex = (int)mode;
                        overlay.ApplyLayout(layout, mode, 1, 1, 1);
                        var native = layout == HudLayoutMode.Native;
                        Assert.Equal(!native || !attached && mode == NativeGaugeMode.Analogue,
                            controller.IsDetachedBoostGaugeEnabled);
                        Assert.Equal(!native || !attached, controller.IsDetachedTireTemperatureGaugeEnabled);
                        Assert.Equal(native && mode == NativeGaugeMode.Analogue, model.CanAttachAnalogueBoostGauge);
                        Assert.Equal(native && mode == NativeGaugeMode.Digital, Visible("AttachedDigitalBoost"));
                        Assert.Equal(native && mode == NativeGaugeMode.Analogue && attached, Visible("AttachedAnalogBoost"));
                        Assert.Equal(native && mode == NativeGaugeMode.Digital && attached, Visible("AttachedDigitalTireTemperature"));
                        Assert.Equal(native && mode == NativeGaugeMode.Analogue && attached, Visible("AttachedAnalogTireTemperature"));
                        if (!native)
                        {
                            var size = new Size(overlay.Width, overlay.Height);
                            model.BoostGaugeEnabled = model.TireTemperatureGaugeEnabled = false;
                            overlay.ApplyLayout(layout, mode, 1, 1, 1);
                            Assert.Equal(size, new Size(overlay.Width, overlay.Height));
                            model.BoostGaugeEnabled = model.TireTemperatureGaugeEnabled = true;
                        }
                        Assert.Equal(attached, settings.BoostGaugeAttached);
                        Assert.Equal(attached, settings.TireTemperatureGaugeAttached);
                        foreach (var scale in new[] { .5, 1, 2 })
                        {
                            boost.ApplyAppearance(scale, 1);
                            tire.ApplyGaugeMode(mode);
                            tire.ApplyAppearance(scale, 1);
                            AssertArtworkFits(boost);
                            AssertArtworkFits(tire);
                            var workArea = new Rect(0, 0, 1280, 720);
                            var anchor = new Rect(1020, 580, 260, 140);
                            settings.BoostGaugeScale = settings.TireTemperatureGaugeScale = scale;
                            boost.ResetPosition(anchor, workArea);
                            tire.ResetPosition(anchor, workArea);
                            var boostBounds = new Rect(boost.Left, boost.Top, boost.Width, boost.Height);
                            var tireBounds = new Rect(tire.Left, tire.Top, tire.Width, tire.Height);
                            Assert.True(workArea.Contains(boostBounds));
                            Assert.True(workArea.Contains(tireBounds));
                            Assert.False(boostBounds.IntersectsWith(tireBounds));
                        }
                        settings.BoostGaugeEnabled = false;
                        Assert.False(controller.IsDetachedBoostGaugeEnabled);
                        settings.BoostGaugeEnabled = true;
                        settings.TireTemperatureGaugeEnabled = false;
                        Assert.False(controller.IsDetachedTireTemperatureGaugeEnabled);
                        settings.TireTemperatureGaugeEnabled = true;
                    }
            foreach (var layout in Enum.GetValues<HudLayoutMode>())
            {
                settings.LayoutMode = layout;
                settings.TireTemperatureGaugeAttached = false;
                UpdateTelemetry(controller.ViewModel, true);
                Assert.False(controller.IsDetachedBoostGaugeEnabled);
                Assert.True(controller.IsDetachedTireTemperatureGaugeEnabled);
            }
            AssertAttachmentControls(controller, legacy: false);
            AssertAttachmentControls(controller, legacy: true);
            Assert.False(overlay.IsVisible);
            Assert.False(boost.IsVisible);
            Assert.False(tire.IsVisible);
        }
        finally
        {
            try
            {
                tire.Close();
                boost.Close();
                overlay.Close();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally { application.ShutdownMode = shutdownMode; }
        }

        bool Visible(string name) => Assert.IsAssignableFrom<FrameworkElement>(overlay.FindName(name)).Visibility == Visibility.Visible;
    }

    private static void AssertArtworkFits(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Opacity = 1;
        var size = new Size(window.Width, window.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        // Needle shader quads include transparent corners beyond the dial. Use
        // authored dial/text drawings here; native needle pixels have separate renderer tests.
        var gauges = Descendants(root).OfType<FrameworkElement>().Where(gauge =>
            gauge.Visibility == Visibility.Visible &&
            gauge is AnalogBoostGaugeView or AnalogTireTemperatureGaugeView or DigitalTireTemperatureGaugeView).ToArray();
        Assert.Single(gauges);
        foreach (var gauge in gauges)
        {
            var drawing = VisualTreeHelper.GetDrawing(gauge);
            Assert.NotNull(drawing);
            var art = gauge.TransformToAncestor(root).TransformBounds(drawing.Bounds);
            Assert.False(art.IsEmpty);
            Assert.True(art.Left >= -.1 && art.Top >= -.1 &&
                art.Right <= size.Width + .1 && art.Bottom <= size.Height + .1, $"{window.GetType().Name}: {art} outside {size}");
        }
    }

    private static void AssertAttachmentControls(AppController controller, bool legacy)
    {
        Window window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
        try
        {
            var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
            tabs.SelectedItem = Assert.Single(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "Appearance"));
            if (window.FindName("AppearanceGaugesCategory") is RadioButton category)
                category.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            root.Measure(new Size(1464, 994));
            root.Arrange(new Rect(0, 0, 1464, 994));
            root.UpdateLayout();
            var checks = Descendants(root).OfType<CheckBox>().ToArray();
            var boost = Assert.Single(checks, c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == nameof(DiagnosticsViewModel.BoostGaugeAttached));
            var tire = Assert.Single(checks, c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == nameof(DiagnosticsViewModel.TireTemperatureGaugeAttached));
            foreach (var layout in Enum.GetValues<HudLayoutMode>())
                foreach (var mode in Enum.GetValues<NativeGaugeMode>())
                {
                    controller.ViewModel.LayoutSelectionIndex = (int)layout;
                    controller.ViewModel.NativeGaugeSelectionIndex = (int)mode;
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    Assert.Equal(layout == HudLayoutMode.Native && mode == NativeGaugeMode.Analogue, boost.IsEnabled);
                    Assert.Equal(layout == HudLayoutMode.Native, tire.IsEnabled);
                    Assert.NotNull(boost.Template);
                    Assert.NotNull(tire.Template);
                }
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void UpdateTelemetry(DiagnosticsViewModel model, bool electric)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            CarOrdinal = electric ? 102 : 101,
            NumCylinders = electric ? 0 : 8,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 20,
            WheelRotationRadiansPerSecond = default,
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = default,
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            GameTimestampMilliseconds = electric ? 2u : 1u,
            EngineRpm = electric ? 0 : 4500,
            EngineMaximumRpm = 8000,
            Gear = TransmissionGear.Third,
            Steering = 0,
            Accelerator = 180,
            Brake = 0,
            BoostPressurePsi = 24,
            TireTemperatureFahrenheit = new(176, 178, 180, 182)
        };
        model.Update(state, new IndicatedSpeed(20, 45, true, false, "Fixture"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false), default,
            TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false);
    }
}
