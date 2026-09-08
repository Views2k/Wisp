using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class BoostVacuumUiTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            LayoutMode = HudLayoutMode.Native,
            NativeGaugeMode = NativeGaugeMode.Analogue,
            BoostGaugeEnabled = true,
            BoostGaugeAttached = true
        };
        var now = DateTimeOffset.UtcNow;
        var preferences = SetupPreferences.FromSettings(settings) with
        {
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        SetupCompletion.Save(settings, preferences,
            new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        string? persisted = null;
        var controller = new AppController(settings, value => persisted = JsonSerializer.Serialize(value), new NoStartupRegistration());
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        MainWindow? window = null;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow(controller);
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            var toggle = Assert.Single(Descendants(window).OfType<CheckBox>(), check =>
                BindingOperations.GetBinding(check, ToggleButton.IsCheckedProperty)?.Path.Path == nameof(DiagnosticsViewModel.ShowBoostVacuum));
            Assert.False(toggle.IsChecked);
            // Exercise the authored Checked/Unchecked handlers without showing a window or starting the App.
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            foreach (var enabled in new[] { true, false })
            {
                persisted = null;
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, enabled);
                Assert.NotNull(toggle.GetBindingExpression(ToggleButton.IsCheckedProperty));
                Assert.Equal(enabled, controller.ViewModel.ShowBoostVacuum);
                Assert.Equal(enabled, settings.ShowBoostVacuum);
                Assert.True(controller.TrySavePendingSettings());
                Assert.Equal(enabled, JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(persisted))!.ShowBoostVacuum);
            }
            AssertStationaryCombustionGaugeVisibility(controller, window, toggle);
        }
        finally
        {
            try
            {
                try { window?.Close(); }
                finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally
            {
                application.ShutdownMode = shutdownMode;
            }
        }
    }

    private static void AssertStationaryCombustionGaugeVisibility(AppController controller, MainWindow window, CheckBox vacuum)
    {
        var enabled = Assert.Single(Descendants(window).OfType<CheckBox>(), check =>
            BindingOperations.GetBinding(check, ToggleButton.IsCheckedProperty)?.Path.Path == nameof(DiagnosticsViewModel.BoostGaugeEnabled));
        var overlay = new OverlayWindow(controller);
        var detached = new BoostGaugeWindow(controller);
        try
        {
            var analog = Assert.IsType<AnalogBoostGaugeView>(overlay.FindName("AttachedAnalogBoost"));
            var digital = Assert.IsType<DigitalBoostRailView>(overlay.FindName("AttachedDigitalBoost"));
            var detachedGauge = Assert.Single(Descendants(detached).OfType<AnalogBoostGaugeView>());
            foreach (var unit in new[] { BoostPressureUnit.Psi, BoostPressureUnit.Bar })
            {
                controller.ViewModel.UseBarBoostPressure = unit == BoostPressureUnit.Bar;
                foreach (var mode in new[] { NativeGaugeMode.Analogue, NativeGaugeMode.Digital })
                {
                    controller.ViewModel.NativeGaugeSelectionIndex = (int)mode;
                    controller.Settings.NativeGaugeMode = mode;
                    controller.ViewModel.BoostGaugeAttached = true;
                    controller.Settings.BoostGaugeAttached = true;
                    overlay.SetElectricPowertrain(false);
                    overlay.ApplyLayout(HudLayoutMode.Native, mode, 1, 1, 1);
                    enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    foreach (var pressure in new[] { 0f, -0.2f, -10f, 0f })
                    {
                        UpdateBoostTelemetry(controller.ViewModel, isElectric: false, pressure);
                        // The real Checked/Unchecked handlers must not expose
                        // negative-only NA telemetry when changing this option.
                        foreach (var showVacuum in new[] { false, true })
                        {
                            vacuum.SetCurrentValue(ToggleButton.IsCheckedProperty, showVacuum);
                            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                            Assert.True(controller.ViewModel.BoostDisplay.IsAvailable);
                            Assert.Equal(0, controller.ViewModel.BoostDisplay.PressurePsi);
                            Assert.Equal(0, controller.ViewModel.BoostDisplay.LearnedPeakPsi);
                            Assert.Equal(mode == NativeGaugeMode.Analogue ? Visibility.Visible : Visibility.Collapsed, analog.Visibility);
                            Assert.Equal(mode == NativeGaugeMode.Digital ? Visibility.Visible : Visibility.Collapsed, digital.Visibility);
                            Assert.Equal(0, analog.Display.PressurePsi);
                            Assert.Equal(0, digital.Display.PressurePsi);
                            Assert.Equal(unit, analog.PressureUnit);
                            Assert.Equal(unit, digital.PressureUnit);
                        }
                    }
                    enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                    Assert.False(controller.Settings.BoostGaugeEnabled);
                    Assert.Equal(Visibility.Collapsed, analog.Visibility);
                    Assert.Equal(Visibility.Collapsed, digital.Visibility);
                    Assert.False(controller.IsDetachedBoostGaugeEnabled);
                    enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    controller.ViewModel.BoostGaugeAttached = false;
                    controller.Settings.BoostGaugeAttached = false;
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                    Assert.Equal(mode == NativeGaugeMode.Analogue, controller.IsDetachedBoostGaugeEnabled);
                    Assert.True(detachedGauge.Display.IsAvailable);
                    Assert.Equal(0, detachedGauge.Display.PressurePsi);
                    Assert.Equal(unit, detachedGauge.PressureUnit);
                    enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                    Assert.False(controller.IsDetachedBoostGaugeEnabled);
                    enabled.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                    UpdateBoostTelemetry(controller.ViewModel, isElectric: true, pressure: -10);
                    overlay.SetElectricPowertrain(true);
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                    Assert.True(controller.Settings.BoostGaugeEnabled);
                    Assert.False(controller.ViewModel.BoostDisplay.IsAvailable);
                    Assert.False(detachedGauge.Display.IsAvailable);
                    Assert.False(controller.IsDetachedBoostGaugeEnabled);
                    Assert.Equal(Visibility.Collapsed, analog.Visibility);
                    Assert.Equal(Visibility.Collapsed, digital.Visibility);
                }
            }
            // These exercise real control bindings and layout/controller gates;
            // no test window, live listener or game session is started.
            Assert.False(overlay.IsVisible);
            Assert.False(detached.IsVisible);
        }
        finally
        {
            detached.Close();
            overlay.Close();
        }
    }

    private static void UpdateBoostTelemetry(DiagnosticsViewModel viewModel, bool isElectric, float pressure)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = 1,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            CarOrdinal = isElectric ? 102 : 101,
            NumCylinders = isElectric ? 0 : 12,
            BoostPressurePsi = pressure,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 0,
            WheelRotationRadiansPerSecond = default,
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = default,
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            EngineRpm = isElectric ? 0 : 900,
            EngineMaximumRpm = 8000,
            Gear = TransmissionGear.Neutral,
            Steering = 0,
            Accelerator = 0,
            Brake = 0
        };
        viewModel.Update(state, new IndicatedSpeed(0, 0, true, false, "Fixture"),
            new CalibrationResult(null, 0.3, 0.2, 0, true, string.Empty, false), default,
            TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
