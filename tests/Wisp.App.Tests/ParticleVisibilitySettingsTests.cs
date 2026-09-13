using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
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

        Assert.True(settings.BackgroundParticlesEnabled);
        Assert.True(viewModel.BackgroundParticlesEnabled);
        Assert.False(viewModel.AnimatedBackground);
        Assert.Equal("#FF15151D", settings.CustomBackgroundColor);
        Assert.Equal("#8055CCAA", settings.CustomParticleColor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VisibilityAndAnimationPersistIndependentlyWithoutChangingColors(bool visible, bool animated)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(System.IO.Path.Combine(directory, "settings.json"));
            service.Save(new AppSettings
            {
                BackgroundParticlesEnabled = visible,
                AnimatedBackground = animated,
                CustomBackgroundColor = "#FF15151D",
                CustomParticleColor = "#8055CCAA"
            });
            var restored = service.Load();
            var viewModel = new DiagnosticsViewModel(restored);

            Assert.Equal(visible, restored.BackgroundParticlesEnabled);
            Assert.Equal(visible, viewModel.BackgroundParticlesEnabled);
            Assert.Equal(animated, restored.AnimatedBackground);
            Assert.Equal(animated, viewModel.AnimatedBackground);
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
        tabs.SelectedIndex = 2;
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
    }

    private static void FlushBindings(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
    }
}
