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
            TorqueGaugeEnabled = true,
            CustomPowerLowColor = "#FF90A0B0",
            CustomTorqueHighColor = "#FFB0C0D0"
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
            Assert.Equal(250, smoothing.Value);
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

            Assert.Empty(Descendants(control).OfType<ColorWheelEditor>());
            var changes = new List<string?>();
            controller.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            controller.SetCustomGaugeColors("#FF102030", "#FF405060", "#FF708090");
            AssertSharedPalette(controller, settings);
            foreach (var property in new[]
            {
                nameof(DiagnosticsViewModel.PowerGaugeLowBrush), nameof(DiagnosticsViewModel.PowerGaugeMidBrush),
                nameof(DiagnosticsViewModel.PowerGaugeHighBrush), nameof(DiagnosticsViewModel.TorqueGaugeLowBrush),
                nameof(DiagnosticsViewModel.TorqueGaugeMidBrush), nameof(DiagnosticsViewModel.TorqueGaugeHighBrush)
            }) Assert.Contains(property, changes);
            Assert.Equal("#FF90A0B0", settings.CustomPowerLowColor);
            Assert.Equal("#FFB0C0D0", settings.CustomTorqueHighColor);

            var profile = HudPreset.Capture(settings, "Shared palette");
            settings.HudPresets.Add(profile);
            controller.SetCustomGaugeColors(null, null, null);
            controller.SetBoostGaugeTheme("Mint");
            AssertSharedPalette(controller, settings);
            Assert.True(controller.TryApplyHudPreset(profile.Id, out var profileError), profileError);
            Assert.Equal("#FF102030", settings.CustomBoostLowColor);
            AssertSharedPalette(controller, settings);

            Assert.IsType<CheckBox>(control.FindName("PowerToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            control.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.False(settings.PowerGaugeEnabled);
            Assert.False(powerAttached.IsEnabled);
            Assert.True(settings.TorqueGaugeEnabled);
        }
        finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void AssertSharedPalette(AppController controller, AppSettings settings)
    {
        var resources = new ResourceDictionary();
        BoostGaugeThemeResources.Apply(resources, settings.BoostGaugeTheme,
            settings.CustomBoostLowColor, settings.CustomBoostMidColor, settings.CustomBoostHighColor);
        var low = Assert.IsType<SolidColorBrush>(resources["BoostLowBrush"]).Color;
        var mid = Assert.IsType<SolidColorBrush>(resources["BoostMidBrush"]).Color;
        var high = Assert.IsType<SolidColorBrush>(resources["BoostHighBrush"]).Color;
        var model = controller.ViewModel;
        Assert.Equal(low, Assert.IsType<SolidColorBrush>(model.PowerGaugeLowBrush).Color);
        Assert.Equal(mid, Assert.IsType<SolidColorBrush>(model.PowerGaugeMidBrush).Color);
        Assert.Equal(high, Assert.IsType<SolidColorBrush>(model.PowerGaugeHighBrush).Color);
        Assert.Equal(low, Assert.IsType<SolidColorBrush>(model.TorqueGaugeLowBrush).Color);
        Assert.Equal(mid, Assert.IsType<SolidColorBrush>(model.TorqueGaugeMidBrush).Color);
        Assert.Equal(high, Assert.IsType<SolidColorBrush>(model.TorqueGaugeHighBrush).Color);
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
