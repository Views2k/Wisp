using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

public sealed class GForceCustomizationTests
{
    [Fact]
    public void IndependentColorsAreFrozenAndOnlyReplacedWhenThatColorChanges()
    {
        var settings = new AppSettings();
        var model = new DiagnosticsViewModel(settings);
        Assert.Null(model.GForceDotBrush); Assert.Null(model.GForceTrailBrush);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        settings.CustomGForceColor = "aa4400";
        model.UpdateGForceColors(settings);
        var dot = Assert.IsType<SolidColorBrush>(model.GForceDotBrush);
        Assert.Equal(Color.FromRgb(170, 68, 0), dot.Color);
        Assert.True(dot.IsFrozen);
        Assert.Null(model.GForceTrailBrush);
        Assert.Equal(128, Assert.IsType<SolidColorBrush>(model.GForceDotStrokeBrush).Color.A);
        settings.CustomGForceTrailColor = "#804466CC";
        model.UpdateGForceColors(settings);
        var trail = Assert.IsType<SolidColorBrush>(model.GForceTrailBrush);
        Assert.Equal(Color.FromArgb(128, 68, 102, 204), trail.Color);
        Assert.True(trail.IsFrozen);
        Assert.Same(dot, model.GForceDotBrush);
        var previousCount = changed.Count;
        model.UpdateGForceColors(settings);
        Assert.Equal(previousCount, changed.Count);
        Assert.Same(trail, model.GForceTrailBrush);
        settings.CustomGForceColor = null;
        model.UpdateGForceColors(settings);
        Assert.Null(model.GForceDotBrush); Assert.Null(model.GForceDotStrokeBrush); Assert.Null(model.GForceGlowColor);
        Assert.Same(trail, model.GForceTrailBrush);
    }

    [Fact]
    public void ColorsNormalizeRoundTripAndOldProfilesRestoreAuthoredDefaults()
    {
        var settings = new AppSettings { SettingsRevision = 9, CustomGForceColor = "00112233", CustomGForceTrailColor = "invalid" };
        settings.MigrateSettings();
        Assert.Equal("#40112233", settings.CustomGForceColor);
        Assert.Null(settings.CustomGForceTrailColor);
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.MigrateSettings();
        Assert.Equal(settings.CustomGForceColor, restored.CustomGForceColor);
        var profile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Old native profile","GForceEnabled":false}""")!;
        profile.ApplyTo(restored);
        Assert.Null(restored.CustomGForceColor); Assert.Null(restored.CustomGForceTrailColor);
        Assert.False(restored.GForceEnabled);
    }

    internal static void AssertOnCurrentDispatcher(AppController controller)
    {
        var model = controller.ViewModel;
        var originalEnabled = model.GForceEnabled;
        var originalDot = controller.Settings.CustomGForceColor;
        var originalTrail = controller.Settings.CustomGForceTrailColor;
        OverlayWindow? overlay = null;
        try
        {
            var settings = new AppSettings { CustomGForceColor = "#FFEF8040", CustomGForceTrailColor = "#AA44BBDD" };
            model.UpdateGForceColors(settings);
            foreach (var native in new[] { false, true })
            {
                UserControl meter = native ? new NativeGForceMeterView() : new GForceMeterView();
                meter.DataContext = model;
                meter.Measure(new Size(180, 140)); meter.Arrange(new Rect(0, 0, 180, 140)); meter.UpdateLayout();
                meter.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                var dot = Descendants(meter).OfType<Ellipse>().Single(ellipse => ellipse.Width == 14);
                Assert.Same(model.GForceDotBrush, dot.Fill);
                Assert.Same(model.GForceTrailBrush, Assert.Single(Descendants(meter).OfType<GForceTrailView>()).TrailBrush);
                settings.CustomGForceColor = null; settings.CustomGForceTrailColor = null;
                model.UpdateGForceColors(settings);
                meter.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.NotNull(dot.Fill);
                if (native) Assert.Equal(Color.FromArgb(204, 255, 255, 255), Assert.IsType<SolidColorBrush>(dot.Fill).Color);
                settings.CustomGForceColor = "#FFEF8040"; settings.CustomGForceTrailColor = "#AA44BBDD";
                model.UpdateGForceColors(settings);
            }
            overlay = new OverlayWindow(controller);
            foreach (var enabled in new[] { true, false, true, false })
            {
                model.GForceEnabled = enabled;
                overlay.ApplyLayout(HudLayoutMode.Combined, NativeGaugeMode.Digital, 1, 1, 1);
                Assert.Equal(enabled ? 390 : 190, overlay.Width);
                Assert.Equal(enabled ? 166 : 150, overlay.Height);
                Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, Assert.IsType<Grid>(overlay.FindName("CombinedPanel")).Visibility);
                Assert.Equal(enabled ? Visibility.Collapsed : Visibility.Visible, Assert.IsType<Grid>(overlay.FindName("BoxedSpeedPanel")).Visibility);
                Assert.False(overlay.IsVisible);
            }
        }
        finally
        {
            overlay?.Close();
            model.GForceEnabled = originalEnabled;
            model.UpdateGForceColors(new AppSettings { CustomGForceColor = originalDot, CustomGForceTrailColor = originalTrail });
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
