using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

internal static class PowerTorqueGaugeVisualTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var gauge = new PowerTorqueGaugeView { Width = 140, Height = 140, Maximum = 1_000 };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        foreach (var (value, angle) in new[] { (-100d, 135d), (0d, 135d), (500d, 270d), (1_000d, 405d), (1_500d, 405d) })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, value, 1_500, 1_500);
            Assert.True(gauge.HasVisibleNeedle);
            Assert.Equal(value, gauge.DisplayedValue);
            Assert.Equal(angle, gauge.CurrentNeedleAngle, 8);
        }
        gauge.Display = PowerTorqueDisplay.Unavailable;
        Assert.False(gauge.HasVisibleNeedle);
        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        gauge.IsTorque = true;
        gauge.Maximum = 1_200;
        Assert.Equal(507, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 507d / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.TorqueUnit = TorqueUnit.PoundFeet;
        Assert.Equal(373.94400968357366, gauge.DisplayedValue, 8);
        Assert.Equal(135 + 270 * gauge.DisplayedValue / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.IsTorque = false;
        gauge.Maximum = 1_000;
        Assert.Equal(427, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 427d / 1_000, gauge.CurrentNeedleAngle, 8);

        foreach (var value in new[] { -999d, 0d, 427d, 1_500d, 12_000d })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, 0, 0, 0);
            var drawing = RenderMethod(gauge, "DrawValue");
            var leaves = Flatten(drawing).ToArray();
            var digitCount = Math.Abs(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture).Length;
            Assert.Equal(digitCount, leaves.OfType<ImageDrawing>().Count());
            Assert.Equal(value < 0 ? 1 : 0, leaves.OfType<GeometryDrawing>().Count(item => item.Geometry is LineGeometry));
            Assert.True(drawing.Bounds.Width <= 48.01);
        }

        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        RenderMethod(gauge, "OnRender");
        var field = typeof(PowerTorqueGaugeView).GetField("_dial", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalDial = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(originalDial.IsFrozen);
        gauge.Display = gauge.Display with { PowerBhp = 500, PeakPowerBhp = 650 };
        RenderMethod(gauge, "OnRender");
        Assert.Same(originalDial, field.GetValue(gauge));
        gauge.Maximum = 2_000;
        RenderMethod(gauge, "OnRender");
        Assert.NotSame(originalDial, field.GetValue(gauge));
        Assert.Equal(135 + 270 * 500d / 2_000, gauge.CurrentNeedleAngle, 8);

        foreach (var maximum in new[] { 0, -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => gauge.Maximum = maximum);
        gauge.Display = new PowerTorqueDisplay(true, double.NaN, 0, 0, 0);
        Assert.False(gauge.HasVisibleNeedle);
        Assert.Equal(135, gauge.CurrentNeedleAngle);

        ReadoutAndNeedleUseTheirSeparateSamples();
        ReadoutDrawingIsCachedBetweenNumericPublications();
        CustomPaletteHonorsAllStopsAndTheirOpacity();
        ColoredArcCacheIsFrozenReusedAndReplacedWhenItsPaletteChanges();
        ActiveArcClampsNegativeAndOverrangeOutput();
        ChangingNumberColorsDoesNotGrowTheGlobalTintedTextureCache();
        SupplementaryRimsHaveMatchingRenderedDiameterAndCenter();
        SharedPreviewUsesActualGaugeBindingsAndIndependentSizes();
        DetachedPairPreviewShowsEveryEnabledGauge();
    }

    private static void DetachedPairPreviewShowsEveryEnabledGauge()
    {
        var model = new DiagnosticsViewModel(new AppSettings { PowerGaugeAttached = false, TorqueGaugeAttached = false });
        var preview = new PowerTorqueGaugePairPreview { DataContext = model };
        var panel = Assert.IsType<System.Windows.Controls.StackPanel>(preview.Content);
        var power = Assert.Single(panel.Children.OfType<PowerTorqueGaugeView>(), gauge => !gauge.IsTorque);
        var torque = Assert.Single(panel.Children.OfType<PowerTorqueGaugeView>(), gauge => gauge.IsTorque);
        foreach (var mask in new[] { 3, 2, 1, 0 })
        {
            model.PowerGaugeEnabled = (mask & 1) != 0;
            model.TorqueGaugeEnabled = (mask & 2) != 0;
            preview.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(mask == 0 ? Visibility.Collapsed : Visibility.Visible, preview.Visibility);
            Assert.Equal((mask & 1) == 0 ? Visibility.Collapsed : Visibility.Visible, power.Visibility);
            Assert.Equal((mask & 2) == 0 ? Visibility.Collapsed : Visibility.Visible, torque.Visibility);
            Assert.Equal((mask & 1) == 0 ? 0 : 2, torque.Margin.Top);
            Assert.False(model.PowerGaugeAttached);
            Assert.False(model.TorqueGaugeAttached);
        }
        preview.DataContext = null;
    }

    private static void SharedPreviewUsesActualGaugeBindingsAndIndependentSizes()
    {
        var settings = new AppSettings
        {
            BoostGaugeEnabled = true,
            BoostGaugeAttached = false,
            TireTemperatureGaugeEnabled = true,
            TireTemperatureGaugeAttached = false,
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true,
            PowerGaugeAttached = false,
            TorqueGaugeAttached = false,
            BoostGaugeScale = .5,
            TireTemperatureGaugeScale = 1.25,
            PowerGaugeScale = 2,
            TorqueGaugeScale = 1
        };
        var preview = new SupplementaryAnalogGaugePreview { DataContext = new DiagnosticsViewModel(settings) };
        var boost = Assert.Single(preview.Children.OfType<AnalogBoostGaugeView>());
        var tire = Assert.Single(preview.Children.OfType<AnalogTireTemperatureGaugeView>());
        var power = Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => !gauge.IsTorque);
        var torque = Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => gauge.IsTorque);
        Assert.All(new FrameworkElement[] { boost, tire, power, torque },
            gauge => Assert.Equal(Visibility.Visible, gauge.Visibility));
        Assert.False(settings.BoostGaugeAttached);
        Assert.False(settings.TireTemperatureGaugeAttached);
        Assert.False(settings.PowerGaugeAttached);
        Assert.False(settings.TorqueGaugeAttached);
        Assert.Equal("PreviewBoostDisplay", BindingOperations.GetBinding(boost, BoostVisualBase.DisplayProperty)!.Path.Path);
        Assert.Equal("SelectedBoostPressureUnit", BindingOperations.GetBinding(boost, BoostVisualBase.PressureUnitProperty)!.Path.Path);
        Assert.Equal("PreviewTireTemperatureDisplay", BindingOperations.GetBinding(tire, TireTemperatureVisualBase.DisplayProperty)!.Path.Path);
        Assert.Equal("SelectedTireTemperatureUnit", BindingOperations.GetBinding(tire, TireTemperatureVisualBase.TemperatureUnitProperty)!.Path.Path);
        Assert.Equal("PreviewPowerTorqueDisplay", BindingOperations.GetBinding(power, PowerTorqueGaugeView.DisplayProperty)!.Path.Path);
        Assert.Equal("PreviewPowerTorqueDisplay", BindingOperations.GetBinding(torque, PowerTorqueGaugeView.DisplayProperty)!.Path.Path);
        Assert.Equal(.5, Assert.IsType<ScaleTransform>(boost.LayoutTransform).ScaleX);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(tire.LayoutTransform).ScaleX);
        Assert.Equal(2, Assert.IsType<ScaleTransform>(power.LayoutTransform).ScaleX);
        Assert.Equal(1, Assert.IsType<ScaleTransform>(torque.LayoutTransform).ScaleX);
        Assert.Equal(boost.Margin.Top + 34, power.Margin.Top + 136);
        Assert.Equal(tire.Margin.Top + 85, torque.Margin.Top + 68);
        preview.DataContext = null;
    }

    private static void SupplementaryRimsHaveMatchingRenderedDiameterAndCenter()
    {
        var boost = new AnalogBoostGaugeView { Display = new BoostDisplay(true, 0, 0, 0, 70) };
        var tire = new AnalogTireTemperatureGaugeView
        {
            Display = new TireTemperatureDisplay(true, 176, 177, .42, .423)
        };
        FrameworkElement[] gauges = [boost, tire, new PowerTorqueGaugeView(), new PowerTorqueGaugeView { IsTorque = true }];
        foreach (var gauge in gauges)
        {
            gauge.Width = gauge.Height = 136;
            gauge.Measure(new Size(136, 136));
            gauge.Arrange(new Rect(0, 0, 136, 136));
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
                gauge.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gauge, [dc]);
            var rim = Assert.Single(RimBounds(drawing, Matrix.Identity));
            // These arcs include both horizontal extrema and their top. Compare
            // actual authored drawing geometry, not just equal control widths.
            // WPF approximates these arcs with Beziers; their computed bounds
            // differ from the circle by about .02 DIP. Keep a subpixel tolerance.
            Assert.InRange(rim.Width, 116.91, 117.01);
            Assert.InRange(rim.Left + rim.Width / 2, 67.95, 68.05);
            Assert.InRange(rim.Top + rim.Width / 2, 67.95, 68.05);
        }
    }

    private static IEnumerable<Rect> RimBounds(Drawing drawing, Matrix transform)
    {
        if (drawing is DrawingGroup group)
        {
            var local = group.Transform?.Value ?? Matrix.Identity;
            local.Append(transform);
            foreach (var child in group.Children)
                foreach (var bounds in RimBounds(child, local)) yield return bounds;
        }
        else if (drawing is GeometryDrawing { Geometry: StreamGeometry, Pen.Brush: SolidColorBrush brush } geometry &&
                 brush.Color == Color.FromArgb(145, 142, 147, 156))
            yield return new MatrixTransform(transform).TransformBounds(geometry.Geometry.Bounds);
    }

    private static void ReadoutAndNeedleUseTheirSeparateSamples()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Maximum = 1_000,
            Display = new PowerTorqueDisplay(true, 800, 1_000, 900, 1_100)
            {
                ReadoutPowerBhp = 123,
                ReadoutTorqueNm = 200
            }
        };
        Assert.Equal(800, gauge.DisplayedValue);
        Assert.Equal(123, gauge.DisplayedReadout);
        Assert.Equal(351, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "123");
        gauge.Display = gauge.Display with { PowerBhp = 900 };
        Assert.Equal(378, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "123");

        gauge.IsTorque = true;
        gauge.TorqueUnit = TorqueUnit.PoundFeet;
        Assert.Equal(737.5621492772656, gauge.DisplayedValue, 8);
        Assert.Equal(147.51242985545312, gauge.DisplayedReadout, 8);
        Assert.Equal(135 + 270 * 737.5621492772656 / 1_000, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "148");
        gauge.Display = new PowerTorqueDisplay(true, 345, 678, 900, 1_100);
        gauge.IsTorque = false;
        Assert.Equal(345, gauge.DisplayedReadout);
        AssertDrawnDigits(gauge, "345");
    }

    private static void CustomPaletteHonorsAllStopsAndTheirOpacity()
    {
        var low = Color.FromArgb(64, 20, 40, 60);
        var mid = Color.FromArgb(128, 100, 120, 140);
        var high = Color.FromArgb(192, 180, 200, 220);
        var gauge = new PowerTorqueGaugeView
        {
            LowBrush = new SolidColorBrush(low),
            MidBrush = new SolidColorBrush(mid),
            HighBrush = new SolidColorBrush(high)
        };
        Assert.Equal(low, gauge.PaletteColor(0));
        Assert.Equal(mid, gauge.PaletteColor(.56));
        Assert.Equal(high, gauge.PaletteColor(1));
        Assert.Equal(low, gauge.PaletteColor(-.5));
        Assert.Equal(high, gauge.PaletteColor(1.5));
        Assert.Equal(Color.FromArgb(96, 60, 80, 100), gauge.PaletteColor(.28));
        Assert.Equal(Color.FromArgb(160, 140, 160, 180), gauge.PaletteColor(.78));

        gauge.ColorNumber = true;
        gauge.Display = new PowerTorqueDisplay(true, 900, 0, 900, 0) { ReadoutPowerBhp = 560 };
        var drawing = RenderMethod(gauge, "DrawValue");
        var rectangles = Flatten(drawing).OfType<GeometryDrawing>().ToArray();
        Assert.Equal(3, rectangles.Length);
        Assert.All(rectangles, rectangle => Assert.Equal(mid, Assert.IsType<SolidColorBrush>(rectangle.Brush).Color));
        Assert.All(Groups(drawing).Where(group => group.OpacityMask is not null),
            group => Assert.True(Assert.IsType<ImageBrush>(group.OpacityMask).IsFrozen));
        Assert.Equal(3, Groups(drawing).Count(group => group.OpacityMask is not null));
    }

    private static void ReadoutDrawingIsCachedBetweenNumericPublications()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Width = 140,
            Height = 140,
            Display = new PowerTorqueDisplay(true, 500, 0, 600, 0) { ReadoutPowerBhp = 123 }
        };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        RenderMethod(gauge, "OnRender");
        var field = typeof(PowerTorqueGaugeView).GetField("_readoutDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(first.IsFrozen);
        gauge.Display = gauge.Display with { PowerBhp = 750 };
        RenderMethod(gauge, "OnRender");
        Assert.Same(first, field.GetValue(gauge));
        gauge.Display = gauge.Display with { ReadoutPowerBhp = 124 };
        RenderMethod(gauge, "OnRender");
        var next = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(next.IsFrozen);
        Assert.NotSame(first, next);
        gauge.ColorNumber = true;
        RenderMethod(gauge, "OnRender");
        var tinted = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(tinted.IsFrozen);
        Assert.NotSame(next, tinted);
        gauge.LowBrush = Brushes.Orange;
        RenderMethod(gauge, "OnRender");
        Assert.NotSame(tinted, field.GetValue(gauge));
    }

    private static void ColoredArcCacheIsFrozenReusedAndReplacedWhenItsPaletteChanges()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Display = new PowerTorqueDisplay(true, 500, 0, 600, 0)
        };
        RenderMethod(gauge, "DrawActiveArc");
        var field = typeof(PowerTorqueGaugeView).GetField("_coloredArc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(first.IsFrozen);
        Assert.Equal(180, Flatten(first).Count());
        gauge.Display = gauge.Display with { PowerBhp = 800 };
        RenderMethod(gauge, "DrawActiveArc");
        Assert.Same(first, field.GetValue(gauge));
        gauge.Maximum = 2_000;
        RenderMethod(gauge, "DrawActiveArc");
        Assert.Same(first, field.GetValue(gauge));

        var low = new SolidColorBrush(Color.FromArgb(128, 0, 255, 0));
        gauge.LowBrush = low;
        RenderMethod(gauge, "DrawActiveArc");
        var second = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(second.IsFrozen);
        Assert.NotSame(first, second);
        low.Color = Color.FromArgb(96, 255, 0, 0);
        RenderMethod(gauge, "DrawActiveArc");
        var third = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(third.IsFrozen);
        Assert.NotSame(second, third);
        Assert.Equal(180, Flatten(third).Count());
        var segments = Flatten(third).OfType<GeometryDrawing>().ToArray();
        var core = Assert.IsType<SolidColorBrush>(Assert.IsType<Pen>(segments[1].Pen).Brush).Color;
        var soft = Assert.IsType<SolidColorBrush>(Assert.IsType<Pen>(segments[0].Pen).Brush).Color;
        Assert.Equal(gauge.PaletteColor(1d / 90), core);
        Assert.Equal((byte)Math.Round(core.A * 58d / 255), soft.A);
    }

    private static void ActiveArcClampsNegativeAndOverrangeOutput()
    {
        var gauge = new PowerTorqueGaugeView { Maximum = 1_000 };
        foreach (var value in new[] { -500d, 0 })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, 0, 0, 0);
            Assert.Empty(RenderMethod(gauge, "DrawActiveArc").Children);
        }
        gauge.Display = new PowerTorqueDisplay(false, 500, 0, 0, 0);
        Assert.Empty(RenderMethod(gauge, "DrawActiveArc").Children);
        gauge.Display = new PowerTorqueDisplay(true, 500, 0, 0, 0);
        var half = RenderMethod(gauge, "DrawActiveArc");
        var clipped = Assert.Single(Groups(half), group => group.ClipGeometry is not null);
        var clip = Assert.IsAssignableFrom<Geometry>(clipped.ClipGeometry);
        Assert.True(clip.FillContains(PointOnArc(.25)));
        Assert.False(clip.FillContains(PointOnArc(.75)));

        gauge.Display = gauge.Display with { PowerBhp = 1_000 };
        var full = RenderMethod(gauge, "DrawActiveArc");
        Assert.DoesNotContain(Groups(full), group => group.ClipGeometry is not null);
        gauge.Display = gauge.Display with { PowerBhp = 1_500 };
        var over = RenderMethod(gauge, "DrawActiveArc");
        Assert.DoesNotContain(Groups(over), group => group.ClipGeometry is not null);
        Assert.Equal(full.Bounds, over.Bounds);
        Assert.Equal(1_500, gauge.DisplayedReadout);
        Assert.Equal(405, gauge.CurrentNeedleAngle, 8);
    }

    private static void ChangingNumberColorsDoesNotGrowTheGlobalTintedTextureCache()
    {
        var field = typeof(NativeAssetCache).GetField("TintedImages", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(null));
        var count = cache.Count;
        var gauge = new PowerTorqueGaugeView { ColorNumber = true };
        for (var index = 1; index <= 64; index++)
        {
            gauge.Display = new PowerTorqueDisplay(true, index * 15, 0, 1_000, 0);
            RenderMethod(gauge, "DrawValue");
        }
        Assert.Equal(count, cache.Count);
    }

    private static void AssertDrawnDigits(PowerTorqueGaugeView gauge, string digits)
    {
        var images = Flatten(RenderMethod(gauge, "DrawValue")).OfType<ImageDrawing>().ToArray();
        Assert.Equal(digits.Length, images.Length);
        for (var index = 0; index < digits.Length; index++)
            Assert.Same(NativeAssetCache.Get(NativeGaugeMode.Analogue,
                $"HUD_Dial_Speed_Analogue_{digits[index]}.png"), images[index].ImageSource);
    }

    private static Point PointOnArc(double fraction)
    {
        var radians = (135 + 270 * fraction) * Math.PI / 180;
        return new Point(70 + 60.2 * Math.Cos(radians), 70 + 60.2 * Math.Sin(radians));
    }

    private static IEnumerable<DrawingGroup> Groups(Drawing drawing) => drawing is DrawingGroup group
        ? new[] { group }.Concat(group.Children.SelectMany(Groups)) : [];

    private static DrawingGroup RenderMethod(PowerTorqueGaugeView gauge, string name)
    {
        var drawing = new DrawingGroup();
        using var dc = drawing.Open();
        typeof(PowerTorqueGaugeView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gauge, [dc]);
        return drawing;
    }

    private static IEnumerable<Drawing> Flatten(Drawing drawing) => drawing is DrawingGroup group
        ? group.Children.SelectMany(Flatten) : [drawing];
}
