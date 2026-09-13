using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

public sealed class OrbitSurfaceTests
{
    [Theory]
    [InlineData(OrbitSurfaceShape.Card)]
    [InlineData(OrbitSurfaceShape.Capsule)]
    [InlineData(OrbitSurfaceShape.Swept)]
    [InlineData(OrbitSurfaceShape.Rounded)]
    public void ShapesKeepTheirCornersClearAndTheirLowerCenterContinuous(OrbitSurfaceShape shape) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Shape = shape;
        var capture = Capture(surface);

        Assert.True(capture.Drawing.IsFrozen);
        Assert.Equal(0, PixelAlpha(capture.Pixels, 2, 2));
        Assert.True(PixelAlpha(capture.Pixels, 150, 60) > 0);
        Assert.True(PixelAlpha(capture.Pixels, 2, 60) > 0);
        for (var x = 135; x <= 165; x++)
            Assert.True(PixelAlpha(capture.Pixels, x, 118) > 0,
                $"The {shape} lower center must remain filled at x={x}.");
        if (shape == OrbitSurfaceShape.Swept)
        {
            var lowerEdge = Enumerable.Range(100, 101)
                .Select(x => Enumerable.Range(60, 60).Last(y => PixelAlpha(capture.Pixels, x, y) > 0))
                .ToArray();
            for (var i = 1; i <= 50; i++)
                Assert.True(lowerEdge[i] >= lowerEdge[i - 1] - 1, "The lower curve must descend smoothly toward the center.");
            for (var i = 51; i < lowerEdge.Length; i++)
                Assert.True(lowerEdge[i] <= lowerEdge[i - 1] + 1, "The lower curve must rise smoothly away from the center.");
        }
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(89, 1)]
    [InlineData(209, 0.5)]
    [InlineData(255, 1)]
    public void LayeredLightingPreservesBackgroundAlphaWithoutMutatingThemeBrushes(int alpha, double brushOpacity) => OnSta(() =>
    {
        var surface = CreateSurface();
        var background = new SolidColorBrush(Color.FromArgb((byte)alpha, 20, 27, 37)) { Opacity = brushOpacity };
        var accent = Assert.IsType<SolidColorBrush>(surface.AccentBrush);
        var originalAccent = accent.Color;
        surface.Background = background;
        surface.BorderBrush = Brushes.Transparent;
        var capture = Capture(surface);
        var expectedAlpha = (byte)Math.Round(alpha * brushOpacity);
        var paintedAlpha = capture.Pixels.Where((_, index) => index % 4 == 3).ToArray();

        Assert.InRange(PixelAlpha(capture.Pixels, 150, 60), Math.Max(0, expectedAlpha - 1), Math.Min(255, expectedAlpha + 1));
        Assert.True(paintedAlpha.Max() <= expectedAlpha + 1);
        if (alpha == 0)
            Assert.All(capture.Pixels, value => Assert.Equal(0, value));
        Assert.Equal((byte)alpha, background.Color.A);
        Assert.Equal(brushOpacity, background.Opacity);
        Assert.Equal(originalAccent, accent.Color);
        Assert.False(background.IsFrozen);
        Assert.False(accent.IsFrozen);
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.35)]
    public void FocusBorderRetainsItsOwnColorAndOpacityOverATransparentFill(double opacity) => OnSta(() =>
    {
        var surface = CreateSurface();
        var border = new SolidColorBrush(Colors.White) { Opacity = opacity };
        surface.Background = Brushes.Transparent;
        surface.BorderBrush = border;
        surface.BorderThickness = new Thickness(3);
        var capture = Capture(surface);
        var expectedAlpha = (int)Math.Round(255 * opacity);
        Assert.Equal(0, PixelAlpha(capture.Pixels, 150, 60));
        for (var channel = 0; channel < 4; channel++)
            Assert.InRange(capture.Pixels[(1 * 300 + 150) * 4 + channel],
                Math.Max(0, expectedAlpha - 1), Math.Min(255, expectedAlpha + 1));
        Assert.False(border.IsFrozen);
        Assert.Equal(Colors.White, border.Color);

        border.Color = Colors.Magenta;
        var recolored = Capture(surface);
        Assert.NotSame(capture.Drawing, recolored.Drawing);
        Assert.Equal(0, recolored.Pixels[(1 * 300 + 150) * 4 + 1]);
        Assert.Equal(0, PixelAlpha(recolored.Pixels, 150, 60));
        Assert.False(border.IsFrozen);
    });

    [Theory]
    [InlineData(89)]
    [InlineData(209)]
    public void TranslucentBorderDoesNotStackOpacityWithTheGlassFill(int alpha) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Background = new SolidColorBrush(Color.FromArgb((byte)alpha, 20, 27, 37));
        surface.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)alpha, 255, 255, 255));
        surface.BorderThickness = new Thickness(3);
        var capture = Capture(surface);
        Assert.InRange(PixelAlpha(capture.Pixels, 150, 60), alpha - 1, alpha + 1);
        Assert.InRange(PixelAlpha(capture.Pixels, 150, 1), alpha - 1, alpha + 1);
        Assert.True(capture.Pixels.Where((_, index) => index % 4 == 3).Max() <= alpha + 1);
        Assert.True(capture.Drawing.IsFrozen);
    });

    [Fact]
    public void RepeatedAndIdleRenderingReusesTheFrozenDrawingUntilThemeOrGeometryChanges() => OnSta(() =>
    {
        var surface = CreateSurface();
        AppThemeResources.Apply(surface.Resources, AppColorThemes.Resolve("Aqua"));
        surface.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        surface.SetResourceReference(OrbitSurface.AccentBrushProperty, "AccentBrush");
        var original = Capture(surface);
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            var repeated = Capture(surface);
            Assert.Same(original.Drawing, repeated.Drawing);
            Assert.Equal(original.Pixels, repeated.Pixels);
        }

        AppThemeResources.Apply(surface.Resources, AppColorThemes.Resolve("Pink"), AppBackgroundThemes.Resolve("Navy"));
        var themed = Capture(surface);
        Assert.NotSame(original.Drawing, themed.Drawing);
        Assert.False(original.Pixels.SequenceEqual(themed.Pixels));
        Assert.True(themed.Drawing.IsFrozen);

        surface.CornerRadius = new CornerRadius(8);
        var rounded = Capture(surface);
        Assert.NotSame(themed.Drawing, rounded.Drawing);
        Assert.False(themed.Pixels.SequenceEqual(rounded.Pixels));

        surface.Shape = OrbitSurfaceShape.Swept;
        var swept = Capture(surface);
        Assert.NotSame(rounded.Drawing, swept.Drawing);
        Assert.True(PixelAlpha(swept.Pixels, 150, 118) > 0);

        var resized = Capture(surface, 320, 140);
        Assert.NotSame(swept.Drawing, resized.Drawing);
        Assert.Same(resized.Drawing, Capture(surface, 320, 140).Drawing);
        Assert.True(resized.Drawing.IsFrozen);
    });

    [Fact]
    public void QuietSurfacesUseOneFlatFillAndReuseItAcrossAccentAndGlowChanges() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Treatment = OrbitSurfaceTreatment.Quiet;
        surface.BorderThickness = new Thickness(0);
        var quiet = Capture(surface);
        var fill = Assert.IsType<DrawingGroup>(Assert.Single(quiet.Drawing.Children));
        Assert.Single(fill.Children);
        var center = (60 * 300 + 150) * 4;
        Assert.Equal(new byte[] { 37, 27, 20, 255 }, quiet.Pixels.Skip(center).Take(4));

        surface.AccentBrush = Brushes.Magenta;
        surface.GlowOpacity = 0;
        var unchanged = Capture(surface);
        Assert.Same(quiet.Drawing, unchanged.Drawing);
        Assert.Equal(quiet.Pixels, unchanged.Pixels);

        surface.Treatment = OrbitSurfaceTreatment.Instrument;
        surface.GlowOpacity = 1;
        var instrument = Capture(surface);
        Assert.NotSame(quiet.Drawing, instrument.Drawing);
        var instrumentFill = Assert.IsType<DrawingGroup>(Assert.Single(instrument.Drawing.Children));
        var clippedLighting = Assert.IsType<DrawingGroup>(Assert.Single(instrumentFill.Children));
        Assert.NotNull(clippedLighting.ClipGeometry);
        Assert.Equal(5, clippedLighting.Children.Count);
        Assert.IsType<LinearGradientBrush>(Assert.IsType<GeometryDrawing>(clippedLighting.Children[0]).Brush);
        Assert.All(clippedLighting.Children.Skip(1), child =>
            Assert.IsType<RadialGradientBrush>(Assert.IsType<GeometryDrawing>(child).Brush));
        Assert.False(quiet.Pixels.SequenceEqual(instrument.Pixels));
        Assert.True(instrument.Drawing.IsFrozen);
    });

    [Fact]
    public void QuietSurfacesPreserveCustomBrushAlphaAndDoNotFreezeTheCallersBrush() => OnSta(() =>
    {
        var background = new SolidColorBrush(Color.FromArgb(160, 20, 27, 37)) { Opacity = 0.5 };
        var surface = CreateSurface();
        surface.Treatment = OrbitSurfaceTreatment.Quiet;
        surface.Background = background;
        surface.SurfaceOpacity = 0.5;
        surface.BorderThickness = new Thickness(0);
        var original = Capture(surface);
        Assert.InRange(PixelAlpha(original.Pixels, 150, 60), 39, 41);
        Assert.False(background.IsFrozen);
        background.Color = Color.FromArgb(160, 40, 80, 120);
        var changed = Capture(surface);
        Assert.NotSame(original.Drawing, changed.Drawing);
        Assert.False(original.Pixels.SequenceEqual(changed.Pixels));
        Assert.Equal(PixelAlpha(original.Pixels, 150, 60), PixelAlpha(changed.Pixels, 150, 60));
        Assert.False(background.IsFrozen);
    });

    [Fact]
    public void SurfaceKeepsBorderPaddingAndChildLayoutSemantics() => OnSta(() =>
    {
        var padding = new Thickness(12, 8, 16, 10);
        var thickness = new Thickness(1, 2, 3, 4);
        var expectedChild = new Border { Width = 80, Height = 40 };
        var actualChild = new Border { Width = 80, Height = 40 };
        var expected = new Border { Padding = padding, BorderThickness = thickness, Child = expectedChild };
        var actual = new OrbitSurface { Padding = padding, BorderThickness = thickness, Child = actualChild };
        foreach (var border in new Border[] { expected, actual })
        {
            border.Measure(new Size(300, 120));
            border.Arrange(new Rect(0, 0, 300, 120));
            border.UpdateLayout();
        }
        Assert.Equal(expected.DesiredSize, actual.DesiredSize);
        Assert.Equal(expectedChild.RenderSize, actualChild.RenderSize);
        Assert.Equal(VisualTreeHelper.GetOffset(expectedChild), VisualTreeHelper.GetOffset(actualChild));
    });

    [Fact]
    public void GlowSettingChangesOnlyTheCachedSurfaceLightingAndKeepsItsAlpha() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Resources["OrbitGlowOpacity"] = 1d;
        var lit = Capture(surface);
        surface.Resources["OrbitGlowOpacity"] = 0d;
        var unlit = Capture(surface);
        Assert.Equal(0, surface.GlowOpacity);
        Assert.NotSame(lit.Drawing, unlit.Drawing);
        Assert.False(lit.Pixels.SequenceEqual(unlit.Pixels));
        Assert.Equal(lit.Pixels.Where((_, index) => index % 4 == 3),
            unlit.Pixels.Where((_, index) => index % 4 == 3));
        Assert.Same(unlit.Drawing, Capture(surface).Drawing);
        surface.Resources["OrbitGlowOpacity"] = 1d;
        Assert.Equal(lit.Pixels, Capture(surface).Pixels);
    });

    [Fact]
    public void SurfaceOpacityDoesNotFadeTheBorderOrChildContent() => OnSta(() =>
    {
        var child = new Border { Width = 20, Height = 20, Background = Brushes.White };
        var surface = CreateSurface();
        surface.Child = child;
        surface.BorderBrush = Brushes.White;
        surface.BorderThickness = new Thickness(3);
        surface.Resources["OrbitSurfaceOpacity"] = 0d;
        var transparent = Capture(surface);
        Assert.Equal(0, PixelAlpha(transparent.Pixels, 150, 60));
        Assert.Equal(255, PixelAlpha(transparent.Pixels, 150, 1));
        Assert.Equal(1, child.Opacity);
        Assert.Equal(1, surface.Opacity);

        var bitmap = new RenderTargetBitmap(300, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[300 * 120 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);
        Assert.Equal(255, PixelAlpha(pixels, 150, 60));
        Assert.Equal(0, PixelAlpha(pixels, 100, 60));
        surface.Resources["OrbitSurfaceOpacity"] = 0.5d;
        Assert.InRange(PixelAlpha(Capture(surface).Pixels, 100, 60), 127, 128);
    });

    [Theory]
    [InlineData(OrbitSurfaceShape.Card, 96)]
    [InlineData(OrbitSurfaceShape.Card, 144)]
    [InlineData(OrbitSurfaceShape.Capsule, 120)]
    [InlineData(OrbitSurfaceShape.Rounded, 144)]
    [InlineData(OrbitSurfaceShape.Swept, 144)]
    public void ThinCurvedBorderRemainsVisibleThroughARealClippedVisual(OrbitSurfaceShape shape, int dpi) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Shape = shape;
        surface.Background = Brushes.Transparent;
        surface.BorderBrush = Brushes.White;
        surface.BorderThickness = new Thickness(1);
        var drawing = Capture(surface).Drawing;
        var outer = Assert.IsAssignableFrom<GeometryDrawing>(drawing.Children[1]).Geometry;
        var host = new Border { Width = 300, Height = 120, ClipToBounds = true, Child = surface };
        host.Measure(new Size(300, 120));
        host.Arrange(new Rect(0, 0, 300, 120));
        host.UpdateLayout();
        var scale = dpi / 96d;
        var width = (int)Math.Ceiling(300 * scale);
        var height = (int)Math.Ceiling(120 * scale);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var contour = outer.GetFlattenedPathGeometry();
        foreach (var fraction in Enumerable.Range(0, 80).Select(index => index / 80d))
        {
            contour.GetPointAtFractionLength(fraction, out var point, out _);
            var x = (int)Math.Floor(point.X * scale);
            var y = (int)Math.Floor(point.Y * scale);
            var visible = false;
            for (var row = Math.Max(0, y - 1); row <= Math.Min(height - 1, y + 1); row++)
                for (var column = Math.Max(0, x - 1); column <= Math.Min(width - 1, x + 1); column++)
                    visible |= pixels[(row * width + column) * 4 + 3] > 8;
            Assert.True(visible, $"The {shape} border disappeared near {point} at {dpi} DPI.");
        }
    });

    private static OrbitSurface CreateSurface() => new()
    {
        Treatment = OrbitSurfaceTreatment.Instrument,
        Background = new SolidColorBrush(Color.FromRgb(20, 27, 37)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(41, 54, 70)),
        AccentBrush = new SolidColorBrush(Color.FromRgb(99, 216, 212)),
        BorderThickness = new Thickness(1.4),
        CornerRadius = new CornerRadius(32)
    };

    private static (DrawingGroup Drawing, byte[] Pixels) Capture(OrbitSurface surface, int width = 300, int height = 120)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            typeof(OrbitSurface).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(surface, [context]);
        }
        var drawing = Assert.IsType<DrawingGroup>(typeof(OrbitSurface)
            .GetField("_surfaceDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (drawing, pixels);
    }

    private static int PixelAlpha(byte[] pixels, int x, int y) => pixels[(y * 300 + x) * 4 + 3];

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Surface rendering STA check timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
