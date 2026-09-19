using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        Window? host = null;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Assert.Equal(250, controller.ViewModel.PowerTorqueSmoothingMilliseconds);
            var control = new PowerTorqueGaugeSettingsControl { DataContext = controller.ViewModel };
            control.Initialize(controller);
            Assert.Single(Descendants(control).OfType<Expander>()).SetCurrentValue(Expander.IsExpandedProperty, true);
            host = new Window
            {
                Content = control,
                Width = 500,
                Height = 1000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0
            };
            host.Show();
            host.UpdateLayout();
            host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            host.UpdateLayout();
            Assert.True(control.IsLoaded);
            var smoothing = Assert.IsType<Slider>(control.FindName("SmoothingSlider"));
            Assert.Equal(BindingStatus.Active, smoothing.GetBindingExpression(RangeBase.ValueProperty)!.Status);
            Assert.Equal(250, smoothing.Value);
            Assert.NotNull(smoothing.FocusVisualStyle);
            smoothing.SetCurrentValue(RangeBase.ValueProperty, 950d);
            Assert.Equal(950, settings.PowerTorqueSmoothingMilliseconds);
            var powerScale = Assert.IsType<Slider>(control.FindName("PowerGaugeScaleSlider"));
            var torqueScale = Assert.IsType<Slider>(control.FindName("TorqueGaugeScaleSlider"));
            Assert.Equal(1, powerScale.Value);
            Assert.Equal(1, torqueScale.Value);
            Assert.NotNull(powerScale.FocusVisualStyle);
            Assert.NotNull(torqueScale.FocusVisualStyle);
            powerScale.SetCurrentValue(RangeBase.ValueProperty, 1.25d);
            Assert.Equal(1.25, settings.PowerGaugeScale);
            Assert.Equal(1, settings.TorqueGaugeScale);
            torqueScale.SetCurrentValue(RangeBase.ValueProperty, 1.6d);
            Assert.Equal(1.25, settings.PowerGaugeScale);
            Assert.Equal(1.6, settings.TorqueGaugeScale);
            var powerAttached = Assert.IsType<CheckBox>(control.FindName("PowerAttachedToggle"));
            powerAttached.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Assert.False(settings.PowerGaugeAttached);
            Assert.True(settings.TorqueGaugeAttached);
            Assert.IsType<CheckBox>(control.FindName("NegativeToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(settings.PowerTorqueShowNegative);
            Assert.IsType<CheckBox>(control.FindName("PowerColorNumberToggle")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(settings.PowerGaugeColorNumber);
            Assert.False(settings.TorqueGaugeColorNumber);
            var driftMode = Assert.IsType<CheckBox>(control.FindName("DriftModeToggle"));
            Assert.Equal(BindingStatus.Active, driftMode.GetBindingExpression(ToggleButton.IsCheckedProperty)!.Status);
            Assert.False(driftMode.IsChecked);
            Assert.False(settings.PowerTorqueDriftMode);
            var revisionBeforeToggle = controller.ViewModel.NativePowerTorqueInput.Revision;
            driftMode.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(settings.PowerTorqueDriftMode);
            Assert.True(controller.ViewModel.PowerTorqueDriftModeEnabled);
            Assert.True(controller.ViewModel.NativePowerTorqueInput.Revision > revisionBeforeToggle);
            var revisionWhileEnabled = controller.ViewModel.NativePowerTorqueInput.Revision;
            driftMode.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            Assert.False(settings.PowerTorqueDriftMode);
            Assert.False(controller.ViewModel.PowerTorqueDriftModeEnabled);
            Assert.True(controller.ViewModel.NativePowerTorqueInput.Revision > revisionWhileEnabled);
            Assert.Equal(950, settings.PowerTorqueSmoothingMilliseconds);
            Assert.True(settings.PowerTorqueShowNegative);
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
            AssertDriftFlashPicker(controller, legacy: false);
            AssertDriftFlashPicker(controller, legacy: true);
        }
        finally
        {
            host?.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.ShutdownMode = shutdownMode;
        }
    }

    private static void AssertDriftFlashPicker(AppController controller, bool legacy)
    {
        var original = controller.Settings.PowerTorqueDriftFlashColor;
        ControlPanelWindow window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        try
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var selector = Assert.IsType<ListBox>(window.FindName("ColorTargetSelector"));
            selector.SelectedItem = Assert.Single(selector.Items.OfType<ListBoxItem>(), item => Equals(item.Content, "Drift cut flash"));
            var editor = Assert.IsType<ColorWheelEditor>(window.FindName("ColorEditor"));
            var reset = Assert.IsType<Button>(window.FindName("ResetDriftFlashColorButton"));
            Assert.Equal("Drift cut flash", editor.Title);
            Assert.Equal(1, editor.MinimumOpacity);
            var resetContainer = Assert.IsType<StackPanel>(reset.Parent);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, resetContainer.Visibility);
            var input = controller.ViewModel.NativePowerTorqueInput;
            var palette = Assert.IsType<SolidColorBrush>(controller.ViewModel.PowerGaugeLowBrush).Color;
            var selected = Color.FromRgb(36, 104, 172);
            editor.SetCurrentValue(ColorWheelEditor.SelectedColorProperty, selected);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal("#FF2468AC", controller.Settings.PowerTorqueDriftFlashColor);
            var flash = Assert.IsType<SolidColorBrush>(controller.ViewModel.PowerTorqueDriftFlashBrush);
            Assert.Equal(selected, flash.Color);
            Assert.True(flash.IsFrozen);
            Assert.Equal(input, controller.ViewModel.NativePowerTorqueInput);
            Assert.Equal(palette, Assert.IsType<SolidColorBrush>(controller.ViewModel.PowerGaugeLowBrush).Color);
            var gauges = Descendants(window).OfType<PowerTorqueGaugeView>().ToArray();
            Assert.NotEmpty(gauges);
            Assert.All(gauges, gauge => Assert.Equal(selected, Assert.IsType<SolidColorBrush>(gauge.DriftFlashBrush).Color));
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(controller.Settings.PowerTorqueDriftFlashColor);
            Assert.Equal(Color.FromRgb(255, 0, 136), editor.SelectedColor);
            Assert.Equal(editor.SelectedColor, Assert.IsType<SolidColorBrush>(controller.ViewModel.PowerTorqueDriftFlashBrush).Color);
            Assert.Equal(input, controller.ViewModel.NativePowerTorqueInput);
            selector.SelectedIndex = 0;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Collapsed, resetContainer.Visibility);
        }
        finally
        {
            window.Close();
            controller.SetPowerTorqueDriftFlashColor(original);
        }
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
