using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class OverlayElectricGaugePlacementTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings
        {
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            OverlayWidthScale = 1,
            OverlayHeightScale = 1,
            GForceEnabled = false,
            BoostGaugeEnabled = true,
            BoostGaugeAttached = true,
            TireTemperatureGaugeAttached = true,
            PowerGaugeAttached = true,
            TorqueGaugeAttached = true,
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false
        };
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var window = new OverlayWindow(controller) { Left = 100, Top = 100 };
        var preview = new SupplementaryAnalogGaugePreview { DataContext = controller.ViewModel };
        var previewWindow = new Window
        {
            Content = preview,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            Left = 100,
            Top = 100
        };
        try
        {
            var model = controller.ViewModel;
            var root = Assert.IsType<Grid>(window.FindName("RootPanel"));
            var boost = Gauge("AttachedAnalogBoost");
            var tire = Gauge("AttachedAnalogTireTemperature");
            var power = Gauge("AttachedPowerGauge");
            var torque = Gauge("AttachedTorqueGauge");
            var gauges = new[] { boost, tire, power, torque };
            var previewGauges = new FrameworkElement[]
            {
                Assert.Single(preview.Children.OfType<AnalogBoostGaugeView>()),
                Assert.Single(preview.Children.OfType<AnalogTireTemperatureGaugeView>()),
                Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => !gauge.IsTorque),
                Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => gauge.IsTorque)
            };
            uint timestamp = 0;
            foreach (var mask in new[] { 1, 2, 4, 7 })
                foreach (var scales in new[] { (.5, .5, .5), (.75, .75, .75), (1d, 1d, 1d), (2d, 2d, 2d), (2d, .5, 1.25) })
                {
                    settings.TireTemperatureGaugeEnabled = (mask & 1) != 0;
                    settings.PowerGaugeEnabled = (mask & 2) != 0;
                    settings.TorqueGaugeEnabled = (mask & 4) != 0;
                    settings.TireTemperatureGaugeScale = scales.Item1;
                    settings.PowerGaugeScale = scales.Item2;
                    settings.TorqueGaugeScale = scales.Item3;
                    model.TireTemperatureGaugeEnabled = settings.TireTemperatureGaugeEnabled;
                    model.PowerGaugeEnabled = settings.PowerGaugeEnabled;
                    model.TorqueGaugeEnabled = settings.TorqueGaugeEnabled;
                    model.TireTemperatureGaugeScale = settings.TireTemperatureGaugeScale;
                    model.PowerGaugeScale = settings.PowerGaugeScale;
                    model.TorqueGaugeScale = settings.TorqueGaugeScale;
                    UpdateFrame(electric: false);
                    window.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue, 1, 1, 1);
                    Arrange();
                    AssertPreviewParity();
                    var combustionBounds = gauges.Select(Bounds).ToArray();
                    var combustionSize = root.RenderSize;
                    if (scales == (1d, 1d, 1d))
                    {
                        Assert.Equal(276, boost.Margin.Left);
                        if (mask == 7)
                        {
                            Assert.Equal(276, tire.Margin.Left);
                            Assert.Equal(410, power.Margin.Left);
                            Assert.Equal(410, torque.Margin.Left);
                        }
                    }

                    UpdateFrame(electric: true);
                    window.SetElectricPowertrain(true);
                    Arrange();
                    AssertPreviewParity();
                    Assert.Equal(Visibility.Collapsed, boost.Visibility);
                    var electricPanel = Assert.IsType<Grid>(window.FindName("NativeElectricAnalogPanel"));
                    var speedometer = Assert.IsType<NativeElectricAnalogSpeedometer>(Assert.Single(electricPanel.Children.Cast<UIElement>()));
                    var dial = Assert.IsType<Image>(speedometer.FindName("DialImage"));
                    var dialBounds = dial.TransformToAncestor(root).TransformBounds(new Rect(dial.RenderSize));
                    Assert.Equal(345, dial.Width);
                    var speedometerBounds = speedometer.TransformToAncestor(root).TransformBounds(new Rect(speedometer.RenderSize));
                    Assert.True(dialBounds.Right > speedometerBounds.Right);
                    var dialCenter = speedometer.TransformToAncestor(root).Transform(new Point(182.5, 200.5));
                    var visible = new[] { tire, power, torque }.Where(gauge => gauge.Visibility == Visibility.Visible).ToArray();
                    Assert.Equal((mask == 7 ? 3 : 1), visible.Length);
                    for (var index = 0; index < visible.Length; index++)
                    {
                        var bounds = Bounds(visible[index]);
                        var dpi = VisualTreeHelper.GetDpi(speedometer);
                        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                        var radius = bounds.Width * .43 + bounds.Width / 136 * 1.1;
                        var dialRadius = Math.Ceiling(145.5 * dpi.DpiScaleX) / dpi.DpiScaleX;
                        var clearance = (center - dialCenter).Length - dialRadius - radius;
                        var geometry = $"mask={mask}, scales={scales}, gauge={visible[index].Name}, " +
                            $"dpi=({dpi.DpiScaleX:R},{dpi.DpiScaleY:R}), dial={dialBounds}, speedometer={speedometerBounds}, " +
                            $"bounds={bounds}, circleClearance={clearance:R}, margin={visible[index].Margin}, root={root.RenderSize}";
                        Assert.True(clearance >= 4,
                            "Attached EV gauges must clear the visible speedometer rim. " + geometry);
                        Assert.True(new Rect(root.RenderSize).Contains(bounds), "The HUD surface must contain the attached gauge. " + geometry);
                        for (var other = index + 1; other < visible.Length; other++)
                        {
                            var otherBounds = Bounds(visible[other]);
                            var otherCenter = new Point(otherBounds.Left + otherBounds.Width / 2,
                                otherBounds.Top + otherBounds.Height / 2);
                            var otherRadius = otherBounds.Width * .43 + otherBounds.Width / 136 * 1.1;
                            Assert.True((center - otherCenter).Length >= radius + otherRadius + 4, geometry);
                        }
                    }

                    Assert.Equal(scales.Item1, settings.TireTemperatureGaugeScale);
                    Assert.Equal(scales.Item2, settings.PowerGaugeScale);
                    Assert.Equal(scales.Item3, settings.TorqueGaugeScale);
                    if (scales == (1d, 1d, 1d))
                    {
                        var expectedCenters = new[] { new Point(342.5, 138.5), new Point(373.5, 231.5), new Point(378.5, 327.5) };
                        foreach (var (gauge, index) in new[] { tire, power, torque }.Select((gauge, index) => (gauge, index)))
                        {
                            if (gauge.Visibility != Visibility.Visible) continue;
                            var bounds = Bounds(gauge);
                            Assert.InRange(Math.Abs(bounds.Width - 102), 0, 1);
                            Assert.InRange(Math.Abs(bounds.Left + bounds.Width / 2 - expectedCenters[index].X), 0, 1);
                            Assert.InRange(Math.Abs(bounds.Top + bounds.Height / 2 - expectedCenters[index].Y), 0, 1);
                        }
                    }

                    UpdateFrame(electric: false);
                    window.SetElectricPowertrain(false);
                    Arrange();
                    AssertPreviewParity();
                    Assert.Equal(combustionSize, root.RenderSize);
                    Assert.True(combustionBounds.SequenceEqual(gauges.Select(Bounds)),
                        $"ICE roundtrip mask={mask}, scales={scales}, VM=({model.TireTemperatureGaugeScale},{model.PowerGaugeScale},{model.TorqueGaugeScale}); " +
                        string.Join("; ", gauges.Select((gauge, index) =>
                            $"{gauge.Name}: before={combustionBounds[index]}, after={Bounds(gauge)}, width={gauge.Width}, render={gauge.RenderSize}, margin={gauge.Margin}, transform={gauge.LayoutTransform}, valid=({gauge.IsMeasureValid},{gauge.IsArrangeValid})")));
                }

            UpdateFrame(electric: true);
            window.SetElectricPowertrain(true);
            settings.GForceGaugeScale = model.GForceGaugeScale = 2;
            Arrange();
            AssertPreviewParity();
            var attachedElectricPreview = previewGauges.Select(PreviewBounds).ToArray();
            settings.TireTemperatureGaugeAttached = model.TireTemperatureGaugeAttached = false;
            settings.PowerGaugeAttached = model.PowerGaugeAttached = false;
            settings.TorqueGaugeAttached = model.TorqueGaugeAttached = false;
            Arrange();
            Assert.All(new[] { tire, power, torque }, gauge => Assert.Equal(Visibility.Collapsed, gauge.Visibility));
            Assert.All(previewGauges.Skip(1), gauge => Assert.Equal(Visibility.Visible, gauge.Visibility));
            Assert.Equal(attachedElectricPreview, previewGauges.Select(PreviewBounds).ToArray());
            settings.TireTemperatureGaugeAttached = model.TireTemperatureGaugeAttached = true;
            settings.PowerGaugeAttached = model.PowerGaugeAttached = true;
            settings.TorqueGaugeAttached = model.TorqueGaugeAttached = true;
            Arrange();
            AssertPreviewParity();
            UpdateFrame(electric: false);
            window.SetElectricPowertrain(false);
            Arrange();
            AssertPreviewParity();
            var attachedCombustionPreview = previewGauges.Select(PreviewBounds).ToArray();
            settings.BoostGaugeAttached = model.BoostGaugeAttached = false;
            Arrange();
            Assert.Equal(Visibility.Collapsed, boost.Visibility);
            Assert.Equal(Visibility.Visible, previewGauges[0].Visibility);
            Assert.Equal(attachedCombustionPreview, previewGauges.Select(PreviewBounds).ToArray());
            Assert.False(settings.BoostGaugeAttached);
            Assert.False(model.BoostGaugeAttached);

            void AssertPreviewParity()
            {
                for (var index = 0; index < gauges.Length; index++)
                {
                    Assert.Equal(gauges[index].Visibility, previewGauges[index].Visibility);
                    if (gauges[index].Visibility == Visibility.Collapsed) continue;
                    var live = Bounds(gauges[index]);
                    var shown = PreviewBounds(previewGauges[index]);
                    Assert.Equal(live.X, shown.X, 6);
                    Assert.Equal(live.Y, shown.Y, 6);
                    Assert.Equal(live.Width, shown.Width, 6);
                    Assert.Equal(live.Height, shown.Height, 6);
                    Assert.True(new Rect(preview.RenderSize).Contains(shown));
                }
            }

            Rect PreviewBounds(FrameworkElement gauge) => gauge.Visibility == Visibility.Collapsed
                ? Rect.Empty
                : gauge.TransformToAncestor(preview).TransformBounds(new Rect(gauge.RenderSize));

            FrameworkElement Gauge(string name) => Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
            Rect Bounds(FrameworkElement gauge) => gauge.Visibility == Visibility.Collapsed
                ? Rect.Empty
                : gauge.TransformToAncestor(root).TransformBounds(new Rect(gauge.RenderSize));
            void Arrange()
            {
                // Settle the full Window/Viewbox layout before reading child bounds.
                // Root-only arrangement can retain a previous child's layout transform.
                window.Show();
                previewWindow.Show();
                window.UpdateLayout();
                previewWindow.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();
                previewWindow.UpdateLayout();
            }
            void UpdateFrame(bool electric)
            {
                var state = new VehicleState
                {
                    IsRaceOn = true,
                    CarOrdinal = electric ? 2 : 1,
                    GameTimestampMilliseconds = timestamp += 16,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                    NumCylinders = electric ? 0 : 4,
                    Drivetrain = DrivetrainType.AllWheelDrive,
                    PowerWatts = 100_000,
                    TorqueNm = 400,
                    BoostPressurePsi = 12,
                    TireTemperatureFahrenheit = new WheelValues(200, 210, 220, 230),
                    GroundSpeedMetersPerSecond = 20,
                    WheelRotationRadiansPerSecond = default,
                    TireSlipRatio = default,
                    TireSlipAngle = default,
                    NormalizedSuspensionTravel = default,
                    LateralAccelerationMetersPerSecondSquared = 0,
                    LongitudinalAccelerationMetersPerSecondSquared = 0,
                    EngineRpm = 2_000,
                    EngineMaximumRpm = 8_000,
                    Gear = TransmissionGear.Second,
                    Steering = 0,
                    Accelerator = 0,
                    Brake = 0
                };
                model.Update(state, new IndicatedSpeed(20, 45, true, false, "All"),
                    new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
                    NativeHudSnapshot.Unavailable(carOrdinal: state.CarOrdinal), default,
                    TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false);
                Assert.Equal(settings.TireTemperatureGaugeScale, model.TireTemperatureGaugeScale);
                Assert.Equal(settings.PowerGaugeScale, model.PowerGaugeScale);
                Assert.Equal(settings.TorqueGaugeScale, model.TorqueGaugeScale);
            }
        }
        finally
        {
            previewWindow.Close();
            window.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.ShutdownMode = shutdownMode;
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
