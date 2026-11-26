using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ParticleVisibilitySettingsTests
{
    [Fact]
    public void ExistingSettingsKeepParticlesVisibleAndPreservePausedAnimationAndColors()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"AnimatedBackground":false,"CustomBackgroundColor":"#FF15151D","CustomParticleColor":"#8055CCAA"}""")!;
        settings.MigrateSettings();
        var viewModel = new DiagnosticsViewModel(settings);

        Assert.True(new AppSettings().IsWinterModeEnabled);
        Assert.True(settings.IsWinterModeEnabled);
        Assert.True(viewModel.IsWinterModeEnabled);
        Assert.True(settings.BackgroundParticlesEnabled);
        Assert.True(viewModel.BackgroundParticlesEnabled);
        Assert.False(viewModel.AnimatedBackground);
        Assert.Equal("#FF15151D", settings.CustomBackgroundColor);
        Assert.Equal("#8055CCAA", settings.CustomParticleColor);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void BackgroundPreferencesRoundTripWithoutChangingColors(bool visible, bool animated, bool winter)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(System.IO.Path.Combine(directory, "settings.json"));
            service.Save(new AppSettings
            {
                BackgroundParticlesEnabled = visible,
                AnimatedBackground = animated,
                IsWinterModeEnabled = winter,
                CustomBackgroundColor = "#FF15151D",
                CustomParticleColor = "#8055CCAA"
            });
            var restored = service.Load();
            var viewModel = new DiagnosticsViewModel(restored);

            Assert.Equal(visible, restored.BackgroundParticlesEnabled);
            Assert.Equal(visible, viewModel.BackgroundParticlesEnabled);
            Assert.Equal(animated, restored.AnimatedBackground);
            Assert.Equal(animated, viewModel.AnimatedBackground);
            Assert.Equal(winter, restored.IsWinterModeEnabled);
            Assert.Equal(winter, viewModel.IsWinterModeEnabled);
            Assert.Equal("#FF15151D", restored.CustomBackgroundColor);
            Assert.Equal("#8055CCAA", restored.CustomParticleColor);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    internal static void Verify(Window window, AppController controller)
    {
        var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
        var previousTab = tabs.SelectedIndex;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Appearance"));
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        content.Measure(new Size(1280, 900));
        content.Arrange(new Rect(0, 0, 1280, 900));
        content.UpdateLayout();
        FlushBindings(window);
        var show = Assert.IsType<CheckBox>(window.FindName("ShowBackgroundParticlesToggle"));
        var animate = Assert.IsType<CheckBox>(window.FindName("AnimateParticlesToggle"));
        var particles = Assert.IsType<AmbientBackdrop>(window.FindName("AppParticles"));
        var surfaceBrush = window.FindResource("WindowBrush");
        var originalVisible = controller.ViewModel.BackgroundParticlesEnabled;
        var originalAnimated = controller.ViewModel.AnimatedBackground;
        var originalSavedVisible = controller.Settings.BackgroundParticlesEnabled;
        var originalSavedAnimated = controller.Settings.AnimatedBackground;
        var originalBackground = controller.Settings.CustomBackgroundColor;
        var originalParticleColor = controller.Settings.CustomParticleColor;

        try
        {
            foreach (var toggle in new[] { show, animate })
            {
                Assert.Same(window.FindResource("ToggleSwitchStyle"), toggle.Style);
                Assert.NotNull(toggle.GetBindingExpression(ToggleButton.IsCheckedProperty));
                toggle.ApplyTemplate();
                Assert.IsType<Border>(toggle.Template.FindName("ToggleTrack", toggle));
                Assert.IsType<Ellipse>(toggle.Template.FindName("ToggleKnob", toggle));
            }
            Assert.Equal("Show background particles", AutomationProperties.GetName(show));
            Assert.Equal("Animate background particles", AutomationProperties.GetName(animate));

            controller.ViewModel.AnimatedBackground = true;
            controller.ViewModel.BackgroundParticlesEnabled = false;
            FlushBindings(window);
            Assert.False(show.IsChecked);
            Assert.True(animate.IsChecked);
            Assert.False(animate.IsEnabled);
            Assert.Equal(Visibility.Collapsed, particles.Visibility);
            Assert.False(particles.HasAnimationTickSubscription);
            Assert.Same(surfaceBrush, window.FindResource("WindowBrush"));

            controller.ViewModel.AnimatedBackground = false;
            controller.ViewModel.BackgroundParticlesEnabled = true;
            FlushBindings(window);
            Assert.True(show.IsChecked);
            Assert.False(animate.IsChecked);
            Assert.True(animate.IsEnabled);
            Assert.Equal(Visibility.Visible, particles.Visibility);
            Assert.False(particles.IsAnimationEnabled);
            Assert.False(particles.HasAnimationTickSubscription);
            Assert.Same(surfaceBrush, window.FindResource("WindowBrush"));
            Assert.Equal(originalBackground, controller.Settings.CustomBackgroundColor);
            Assert.Equal(originalParticleColor, controller.Settings.CustomParticleColor);

        }
        finally
        {
            controller.ViewModel.BackgroundParticlesEnabled = originalVisible;
            controller.ViewModel.AnimatedBackground = originalAnimated;
            FlushBindings(window);
            tabs.SelectedIndex = previousTab;
            controller.Settings.BackgroundParticlesEnabled = originalSavedVisible;
            controller.Settings.AnimatedBackground = originalSavedAnimated;
        }
        VerifyLoadedWinterPreference(window is LegacyMainWindow);
    }

    private static void VerifyLoadedWinterPreference(bool legacy)
    {
        var directory = Directory.CreateTempSubdirectory("Wisp-winter-settings-");
        AppController? controller = null;
        ControlPanelWindow? window = null;
        DispatcherTimer? timeout = null;
        try
        {
            var store = new SettingsService(Path.Combine(directory.FullName, "settings.json"));
            var seed = new AppSettings
            {
                StartWithWindows = false,
                AutomaticApplicationUpdateChecks = false,
                CompletedFeatureTourId = FeatureTourSession.CurrentTourId,
                AnimatedBackground = false,
                CustomParticleColor = "#FF55BBCC"
            };
            var now = DateTimeOffset.UtcNow;
            var setup = SetupPreferences.FromSettings(seed) with
            {
                DataOutConfirmed = true,
                DisplayModeConfirmed = true,
                StockHudConfirmed = true
            };
            SetupCompletion.Save(seed, setup, new SetupTelemetryEvidence(seed.UdpPort, 12, 12,
                TimeSpan.FromMilliseconds(550), now), store.Save, now);
            DispatcherFrame? pendingSave = null;
            var saves = 0;
            controller = new AppController(store.Load(), value =>
            {
                store.Save(value);
                saves++;
                if (pendingSave is not null) pendingSave.Continue = false;
            }, new NoStartupRegistration(),
                runsDirectory: Path.Combine(directory.FullName, "Runs"),
                shiftCalibrationDirectory: Path.Combine(directory.FullName, "ShiftCalibration"),
                clipLibraryDirectory: Path.Combine(directory.FullName, "Clips"),
                tuneLibraryDirectory: Path.Combine(directory.FullName, "Tunes"));
            window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Opacity = 0;
            var tabs = Assert.IsType<TabControl>(window.FindName("RootTabs"));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Extras"));
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var car = RunTestData.State();
            controller.ViewModel.Update(car, default, default, default, TimeSpan.Zero, seed.SpeedUnit, 60,
                refreshDiagnostics: false, updateGForce: false);
            Assert.DoesNotContain(car.CarOrdinal, controller.Settings.PowerTorqueGaugeRanges.Keys);
            var extras = tabs.SelectedItem;
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Appearance"));
            FlushBindings(window);
            var show = Assert.IsType<CheckBox>(window.FindName("ShowBackgroundParticlesToggle"));
            var animate = Assert.IsType<CheckBox>(window.FindName("AnimateParticlesToggle"));
            var rim = legacy ? null : Assert.IsType<DashboardRimEffect>(window.FindName("DashboardRim"));
            foreach (var flag in new[] { false, true })
            {
                show.SetCurrentValue(ToggleButton.IsCheckedProperty, flag);
                FlushBindings(window);
                Assert.Equal(flag, controller.Settings.BackgroundParticlesEnabled);
                if (rim is not null) Assert.Equal(flag, rim.ParticlesEnabled);
            }
            foreach (var flag in new[] { true, false })
            {
                animate.SetCurrentValue(ToggleButton.IsCheckedProperty, flag);
                FlushBindings(window);
                Assert.Equal(flag, controller.Settings.AnimatedBackground);
            }
            Assert.DoesNotContain(car.CarOrdinal, controller.Settings.PowerTorqueGaugeRanges.Keys);
            tabs.SelectedItem = extras;
            FlushBindings(window);
            var winter = Assert.IsType<CheckBox>(window.FindName("WinterModeToggle"));
            Assert.True(window.IsLoaded && winter.IsLoaded && winter.Focusable);
            Assert.Same(window.FindResource("ToggleSwitchStyle"), winter.Style);
            var peer = new CheckBoxAutomationPeer(winter);
            Assert.Equal("Winter mode", peer.GetName());
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            var particles = Assert.IsType<AmbientBackdrop>(window.FindName("AppParticles"));
            Assert.True(winter.IsChecked);
            timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (_, _) => { if (pendingSave is not null) pendingSave.Continue = false; };
            foreach (var expected in new[] { false, true })
            {
                var previousSaves = saves;
                pendingSave = new DispatcherFrame();
                timeout.Start();
                toggle.Toggle();
                FlushBindings(window);
                Assert.Equal(expected, controller.Settings.IsWinterModeEnabled);
                Assert.Equal(expected, particles.IsWinterModeEnabled);
                if (rim is not null)
                {
                    Assert.Equal(expected, rim.IsWinterModeEnabled);
                    Assert.Equal(particles.WinterParticleColor, rim.WinterParticleColor);
                }
                Dispatcher.PushFrame(pendingSave);
                timeout.Stop();
                pendingSave = null;
                Assert.True(saves > previousSaves, "The window did not write its changed winter preference.");
                var restored = store.Load();
                Assert.Equal(expected, new DiagnosticsViewModel(restored).IsWinterModeEnabled);
                Assert.DoesNotContain(car.CarOrdinal, restored.PowerTorqueGaugeRanges.Keys);
                Assert.False(restored.AnimatedBackground);
                Assert.Equal("#FF55BBCC", restored.CustomParticleColor);
            }
            tabs.SelectedItem = window.FindName("DashboardTab");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var readout = legacy
                ? Assert.IsType<Grid>(window.FindName("LegacyDashboardHero")).Children.OfType<Border>().First()
                : Assert.IsType<OrbitSurface>(window.FindName("OrbitInstrument"));
            var snow = ReadoutPixels(readout);
            controller.SetWinterMode(false);
            FlushBindings(window);
            var original = ReadoutPixels(readout);
            var glassPixels = 0;
            var solidForegroundPixels = 0;
            for (var pixel = 0; pixel < snow.Length; pixel++)
            {
                uint before = original[pixel], after = snow[pixel];
                if ((before >> 24) == 255 && (after >> 24) is > 0 and < 240) glassPixels++;
                if (before == after && (after >> 24) == 255 && (after & 0xff) > 190)
                    solidForegroundPixels++;
            }
            Assert.True(glassPixels > 100, "The dashboard surface should show the snowfall behind its readouts.");
            Assert.True(solidForegroundPixels > 10, "Bright dashboard details should remain solid above the snow.");
            controller.SetWinterMode(true);
            controller.ViewModel.BackgroundParticlesEnabled = false;
            FlushBindings(window);
            Assert.Same(window.FindResource("CardBrush"), readout.Background);
        }
        finally
        {
            timeout?.Stop();
            window?.Close();
            controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            directory.Delete(recursive: true);
        }
    }

    private static uint[] ReadoutPixels(Border readout)
    {
        const int width = 640, height = 200;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(readout) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) =>
            throw new InvalidOperationException("Changing winter mode must not register a startup task.");
    }

    private static void FlushBindings(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
    }
}
