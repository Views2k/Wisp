using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class GForceGaugeSizingUiTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        foreach (var legacy in new[] { false, true })
        {
            var settings = new AppSettings
            {
                StartWithWindows = false,
                StartWithForza = false,
                AutomaticApplicationUpdateChecks = false,
                LayoutMode = HudLayoutMode.Native,
                NativeGaugeMode = NativeGaugeMode.Analogue,
                GForceEnabled = true,
                GForceAttached = true,
                GForceWidthScale = 1.35,
                GForceHeightScale = 0.85
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
            var previousShutdownMode = Application.Current.ShutdownMode;
            ControlPanelWindow? window = null;
            try
            {
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
                tabs.SelectedItem = Assert.Single(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "Appearance"));
                if (!legacy)
                    Assert.IsType<RadioButton>(window.FindName("AppearanceGaugesCategory")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                surface.Measure(new Size(1464, 994));
                surface.Arrange(new Rect(0, 0, 1464, 994));
                surface.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var slider = Assert.IsType<Slider>(window.FindName("GForceGaugeScaleSlider"));
                Assert.Equal(BindingStatus.Active, slider.GetBindingExpression(RangeBase.ValueProperty)!.Status);
                Assert.Equal("G-force meter size", AutomationProperties.GetName(slider));
                Assert.NotNull(slider.Template);
                Assert.NotNull(slider.FocusVisualStyle);
                Assert.True(slider.Focusable);
                Assert.True(slider.IsEnabled);
                Assert.Equal(0.5, slider.Minimum);
                Assert.Equal(2, slider.Maximum);
                foreach (var scale in new[] { 0.5, 1.0, 2.0, 1.25 })
                {
                    slider.SetCurrentValue(RangeBase.ValueProperty, scale);
                    Assert.Equal(scale, controller.ViewModel.GForceGaugeScale);
                    Assert.Equal(scale, settings.GForceGaugeScale);
                    Assert.Equal(1.35, settings.GForceWidthScale);
                    Assert.Equal(0.85, settings.GForceHeightScale);
                    Assert.True(controller.TrySavePendingSettings());
                    Assert.Equal(scale, JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(persisted))!.GForceGaugeScale);
                }
                Slider.IncreaseSmall.Execute(null, slider);
                Assert.Equal(1.3, settings.GForceGaugeScale, 5);
                controller.ViewModel.GForceEnabled = false;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.False(slider.IsEnabled);
                Assert.Equal(0.42, Assert.IsType<Grid>(slider.Template.FindName("SliderRoot", slider)).Opacity, 5);
            }
            finally
            {
                try
                {
                    try { window?.Close(); }
                    finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                }
                finally { Application.Current.ShutdownMode = previousShutdownMode; }
            }
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
