using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class PowerTorqueGaugeSettingsUiTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true
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
        var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        try
        {
            var control = new PowerTorqueGaugeSettingsControl { DataContext = controller.ViewModel };
            control.Initialize(controller);
            control.Measure(new Size(500, 1800));
            control.Arrange(new Rect(0, 0, 500, 1800));
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var smoothing = Assert.IsType<Slider>(control.FindName("SmoothingSlider"));
            Assert.Equal(500, smoothing.Value);
            Assert.NotNull(smoothing.FocusVisualStyle);
            smoothing.SetCurrentValue(RangeBase.ValueProperty, 950d);
            Assert.Equal(950, settings.PowerTorqueSmoothingMilliseconds);
            var powerAttached = Assert.IsType<CheckBox>(control.FindName("PowerAttachedToggle"));
            powerAttached.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Assert.False(settings.PowerGaugeAttached);
            Assert.True(settings.TorqueGaugeAttached);
            Assert.IsType<CheckBox>(control.FindName("NegativeToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(settings.PowerTorqueShowNegative);
            Assert.IsType<CheckBox>(control.FindName("PowerColorNumberToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(settings.PowerGaugeColorNumber);
            Assert.False(settings.TorqueGaugeColorNumber);

            var editor = Assert.IsType<ColorWheelEditor>(control.FindName("GaugeColorEditor"));
            editor.SetCurrentValue(ColorWheelEditor.SelectedColorProperty, Color.FromRgb(0x10, 0x20, 0x30));
            Assert.Equal("#FF102030", settings.CustomPowerLowColor);
            Assert.Null(settings.CustomTorqueLowColor);
            var torqueEnd = Descendants(control).OfType<RadioButton>().Single(radio => Equals(radio.Tag, "5"));
            torqueEnd.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            editor.SetCurrentValue(ColorWheelEditor.SelectedColorProperty, Color.FromRgb(0x40, 0x50, 0x60));
            Assert.Equal("#FF405060", settings.CustomTorqueHighColor);
            Assert.Equal("#FF102030", settings.CustomPowerLowColor);

            Assert.IsType<CheckBox>(control.FindName("PowerToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.False(settings.PowerGaugeEnabled);
            Assert.False(powerAttached.IsEnabled);
            Assert.True(settings.TorqueGaugeEnabled);
        }
        finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
