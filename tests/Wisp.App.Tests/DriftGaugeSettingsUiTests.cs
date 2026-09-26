using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class DriftGaugeSettingsUiTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (var legacy in new[] { false, true }) AssertInterface(legacy);
        }
        finally
        {
            application.ShutdownMode = shutdownMode;
        }
    }

    private static void AssertInterface(bool legacy)
    {
        var settings = new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            UseLegacyInterface = legacy,
            DriftGaugeEnabled = true,
            DriftGaugeDarkMode = true,
            DriftGaugeBackgroundEnabled = true,
            DriftGaugeBackgroundOpacity = .65,
            DriftTargetDegrees = 52,
            DriftToleranceDegrees = 7,
            DriftGaugeScale = 1.25,
            DriftGaugePlacements = new() { ["fixture-display"] = new(12, 34, 1.25, 1.25) },
            LastDriftGaugePlacementKey = "fixture-display"
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
        var jsonOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string? persisted = null;
        var controller = new AppController(settings, value => persisted = JsonSerializer.Serialize(value, jsonOptions), new NoStartupRegistration());
        ControlPanelWindow? window = null;
        try
        {
            window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
            var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
            tabs.SelectedIndex = 2;
            if (!legacy) Assert.IsType<RadioButton>(window.FindName("AppearanceGaugesCategory")).IsChecked = true;
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            surface.Measure(new Size(1280, 900));
            surface.Arrange(new Rect(0, 0, 1280, 900));
            surface.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            var control = Assert.IsType<DriftGaugeSettingsControl>(window.FindName("DriftGaugeSettings"));
            var selector = Assert.IsType<ComboBox>(control.FindName("GuidanceSelector"));
            var customSettings = Assert.IsType<StackPanel>(control.FindName("CustomTargetSettings"));
            var zoneDescription = Assert.IsType<StackPanel>(control.FindName("ZoneGuidanceDescription"));
            var target = Assert.IsType<Slider>(control.FindName("TargetSlider"));
            var tolerance = Assert.IsType<Slider>(control.FindName("ToleranceSlider"));
            var background = Assert.IsType<CheckBox>(control.FindName("BackgroundToggle"));
            var opacity = Assert.IsType<Slider>(control.FindName("BackgroundOpacitySlider"));
            var moreOptions = Assert.IsType<Expander>(control.FindName("MoreOptions"));
            Assert.False(moreOptions.IsExpanded);
            foreach (var name in new[] { "DarkModeToggle", "BackgroundToggle", "BackgroundOpacitySlider", "ScaleSlider" })
            {
                var parent = LogicalTreeHelper.GetParent(Assert.IsAssignableFrom<FrameworkElement>(control.FindName(name)));
                while (parent is not null && parent != moreOptions) parent = LogicalTreeHelper.GetParent(parent);
                Assert.Same(moreOptions, parent);
            }
            moreOptions.IsExpanded = true;
            surface.UpdateLayout();
            Assert.True(Assert.IsType<StackPanel>(moreOptions.Content).ActualHeight > 100);
            Assert.True(background.IsChecked);
            Assert.Equal(.65, opacity.Value);
            selector.ApplyTemplate();
            Assert.Same(control.Resources["DriftGuidanceComboStyle"], selector.Style);
            Assert.Same(control.Resources["DriftGuidanceItemStyle"], selector.ItemContainerStyle);
            var comboSurface = Assert.IsType<Border>(selector.Template.FindName("ComboSurface", selector));
            var focus = Assert.IsType<Border>(selector.Template.FindName("ComboFocus", selector));
            var popup = Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
            var popupSurface = Assert.IsType<Border>(popup.Child);
            Assert.Same(selector.Background, comboSurface.Background);
            Assert.Same(selector.FindResource("PanelBrush"), popupSurface.Background);
            Assert.Same(selector.FindResource("AccentBrush"), focus.BorderBrush);
            Assert.Equal(new Thickness(2), focus.BorderThickness);
            Assert.True(popupSurface.CornerRadius.TopLeft > 0);
            var originalForeground = selector.Foreground;
            selector.IsEnabled = false;
            surface.UpdateLayout();
            Assert.Equal(.45, Assert.IsType<Grid>(selector.Template.FindName("ComboRoot", selector)).Opacity);
            Assert.Same(originalForeground, selector.Foreground);
            selector.IsEnabled = true;
            surface.UpdateLayout();
            Assert.Equal(1, Assert.IsType<Grid>(selector.Template.FindName("ComboRoot", selector)).Opacity);
            background.IsChecked = false;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.False(opacity.IsEnabled);
            Assert.False(settings.DriftGaugeBackgroundEnabled);
            Assert.Equal(.65, settings.DriftGaugeBackgroundOpacity);
            background.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            opacity.SetCurrentValue(RangeBase.ValueProperty, .8d);
            Assert.True(opacity.IsEnabled);
            Assert.Equal(DriftGaugeGuidanceMode.DriftZoneAngleBonus, selector.SelectedValue);
            Assert.Equal(Visibility.Collapsed, customSettings.Visibility);
            Assert.Equal(Visibility.Visible, zoneDescription.Visibility);
            Assert.Equal(52, target.Value);
            Assert.Equal(7, tolerance.Value);

            foreach (var mode in new[]
            {
                DriftGaugeGuidanceMode.CustomTarget, DriftGaugeGuidanceMode.DriftZoneAngleBonus,
                DriftGaugeGuidanceMode.CustomTarget, DriftGaugeGuidanceMode.DriftZoneAngleBonus
            })
            {
                persisted = null;
                selector.SetCurrentValue(Selector.SelectedValueProperty, mode);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var custom = mode == DriftGaugeGuidanceMode.CustomTarget;
                Assert.Equal(custom ? Visibility.Visible : Visibility.Collapsed, customSettings.Visibility);
                Assert.Equal(custom ? Visibility.Collapsed : Visibility.Visible, zoneDescription.Visibility);
                Assert.Equal(mode, settings.DriftGaugeGuidanceMode);
                Assert.Equal(Visibility.Visible, moreOptions.Visibility);
                if (custom)
                {
                    target.SetCurrentValue(RangeBase.ValueProperty, 57d);
                    tolerance.SetCurrentValue(RangeBase.ValueProperty, 6d);
                }
                Assert.Equal(57, target.Value);
                Assert.Equal(6, tolerance.Value);
                Assert.True(controller.TrySavePendingSettings());
                var restored = JsonSerializer.Deserialize<AppSettings>(Assert.IsType<string>(persisted), jsonOptions)!;
                Assert.Equal(mode, restored.DriftGaugeGuidanceMode);
                Assert.Equal(57, restored.DriftTargetDegrees);
                Assert.Equal(6, restored.DriftToleranceDegrees);
                Assert.True(restored.DriftGaugeEnabled);
                Assert.True(restored.DriftGaugeDarkMode);
                Assert.True(restored.DriftGaugeBackgroundEnabled);
                Assert.Equal(.8, restored.DriftGaugeBackgroundOpacity);
                Assert.Equal(1.25, restored.DriftGaugeScale);
                Assert.Equal("fixture-display", restored.LastDriftGaugePlacementKey);
                Assert.Equal(12, restored.DriftGaugePlacements["fixture-display"].Left);
                Assert.Equal(34, restored.DriftGaugePlacements["fixture-display"].Top);
                Assert.Null(controller.DriftGaugeOverlay);
                Assert.False(window.IsVisible);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
            }
        }
        finally
        {
            try { window?.Close(); }
            finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
