using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wisp.App.Tests;

internal static class BoostGaugeVisualsTests
{
    // Keep shared shader resources on the existing resource-only STA fixture.
    internal static void AssertOnCurrentDispatcher()
    {
        foreach (var colorNumber in new[] { false, true })
        {
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -20, 2, colorNumber);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -200, 3, colorNumber);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Bar, -20, 2, colorNumber);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -0.1, 2, colorNumber, showsMinus: false);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Bar, -0.1, 2, colorNumber, showsMinus: false);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, 24, 2, colorNumber, showsMinus: false);
            NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Bar, 24, 2, colorNumber, showsMinus: false);
        }
        ChangingPressureAndPulseColorsReusesDigitsWithoutGrowingTheSharedTintCache();
        SeparateBoostViewsKeepTheirReadoutColorsIndependent();
        foreach (var unit in new[] { BoostPressureUnit.Psi, BoostPressureUnit.Bar })
            foreach (var stock in new[] { false, true })
                DigitalRailPlacesVacuumBelowTheZeroReference(unit, stock);
    }

    private static void NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(
        BoostPressureUnit unit, double pressure, int digitCount, bool colorNumber, bool showsMinus = true)
    {
        var model = new BoostDisplayModel();
        model.Calculate(42, false, 24);
        var gauge = new AnalogBoostGaugeView
        {
            PressureUnit = unit,
            ColorNumber = colorNumber,
            Display = model.Calculate(42, false, pressure, showVacuum: true)
        };
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            typeof(AnalogBoostGaugeView).GetMethod("DrawNativeBoostDigits",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(gauge, [context, new Point(144, 144)]);
        }
        var leaves = Flatten(drawing).ToArray();
        Assert.Equal(digitCount, leaves.OfType<ImageDrawing>().Count());
        Assert.Equal(showsMinus ? 1 : 0, leaves.OfType<GeometryDrawing>().Count(item => item.Geometry is LineGeometry));
        Assert.Equal(unit == BoostPressureUnit.Bar ? 1 : 0,
            leaves.OfType<GeometryDrawing>().Count(item => item.Geometry is EllipseGeometry));
        Assert.True(drawing.Bounds.Width <= 44.01);
    }

    private static void ChangingPressureAndPulseColorsReusesDigitsWithoutGrowingTheSharedTintCache()
    {
        var model = new BoostDisplayModel();
        model.Calculate(42, false, 70);
        var gauge = new AnalogBoostGaugeView();
        gauge.Measure(new Size(136, 136));
        gauge.Arrange(new Rect(0, 0, 136, 136));
        var retainedDigits = new Dictionary<char, BitmapSource>();
        var initialTintCount = SharedTintCount();
        for (var pass = 0; pass < 3; pass++)
            for (var sample = 0; sample < 32; sample++)
            {
                var pressure = sample < 20 ? sample : 62 + (sample - 20) * .55;
                gauge.ColorNumber = pass != 1;
                gauge.Display = model.Calculate(42, false, pressure);
                var digits = BoostPressureUnits.FormatValue(pressure, BoostPressureUnit.Psi, padPsi: true);
                var images = DrawGaugeDigits(gauge);
                Assert.Equal(digits.Length, images.Length);
                for (var index = 0; index < digits.Length; index++)
                {
                    var image = Assert.IsAssignableFrom<BitmapSource>(images[index].ImageSource);
                    if (retainedDigits.TryGetValue(digits[index], out var previous))
                        Assert.Same(previous, image);
                    else
                        retainedDigits.Add(digits[index], image);
                    if (pass == 1 && sample < 10)
                    {
                        var source = NativeAssetCache.Get(NativeGaugeMode.Analogue,
                            $"HUD_Dial_Speed_Analogue_{digits[index]}.png");
                        var expected = NativeTintedBitmapTests.Pixels(source);
                        NativeAssetCache.MultiplyStraightBgraByColor(expected, Colors.WhiteSmoke);
                        Assert.Equal(expected, NativeTintedBitmapTests.Pixels(image));
                    }
                }
            }
        Assert.Equal(10, retainedDigits.Count);
        Assert.Equal(10, retainedDigits.Values.Distinct().Count());
        Assert.Equal(initialTintCount, SharedTintCount());
    }

    private static void SeparateBoostViewsKeepTheirReadoutColorsIndependent()
    {
        var model = new BoostDisplayModel();
        model.Calculate(42, false, 70);
        var first = new AnalogBoostGaugeView { Display = model.Calculate(42, false, 32) };
        var second = new AnalogBoostGaugeView { Display = first.Display, ColorNumber = true };
        foreach (var gauge in new[] { first, second })
        {
            gauge.Measure(new Size(136, 136));
            gauge.Arrange(new Rect(0, 0, 136, 136));
        }
        var firstImages = DrawGaugeDigits(first).Select(item => (BitmapSource)item.ImageSource).ToArray();
        var firstPixels = firstImages.Select(NativeTintedBitmapTests.Pixels).ToArray();
        var secondImages = DrawGaugeDigits(second).Select(item => (BitmapSource)item.ImageSource).ToArray();
        var secondPixels = secondImages.Select(NativeTintedBitmapTests.Pixels).ToArray();
        Assert.Equal(firstImages.Length, secondImages.Length);
        for (var index = 0; index < firstImages.Length; index++)
        {
            Assert.NotSame(firstImages[index], secondImages[index]);
            Assert.Equal(firstPixels[index], NativeTintedBitmapTests.Pixels(firstImages[index]));
            Assert.False(firstPixels[index].SequenceEqual(secondPixels[index]));
        }
        first.ColorNumber = true;
        first.LowBrush = Brushes.Red;
        first.MidBrush = Brushes.Orange;
        first.HighBrush = Brushes.Yellow;
        _ = DrawGaugeDigits(first);
        for (var index = 0; index < secondImages.Length; index++)
            Assert.Equal(secondPixels[index], NativeTintedBitmapTests.Pixels(secondImages[index]));
    }

    private static ImageDrawing[] DrawGaugeDigits(AnalogBoostGaugeView gauge)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            typeof(AnalogBoostGaugeView).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(gauge, [context]);
        var ring = NativeAssetCache.Get(NativeGaugeMode.Analogue, "HUD_Dial_Analog_Gear_1.png");
        return Flatten(drawing).OfType<ImageDrawing>()
            .Where(item => !ReferenceEquals(item.ImageSource, ring)).ToArray();
    }

    private static int SharedTintCount() =>
        Assert.IsAssignableFrom<IDictionary>(typeof(NativeAssetCache)
            .GetField("TintedImages", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)).Count;

    private static void DigitalRailPlacesVacuumBelowTheZeroReference(BoostPressureUnit unit, bool stock)
    {
        var model = new BoostDisplayModel();
        model.Calculate(42, false, 24);
        var rail = new DigitalBoostRailView { PressureUnit = unit, UseStockColors = stock };
        rail.Measure(new Size(302, 96));
        rail.Arrange(new Rect(0, 0, 302, 96));
        var material = Assert.IsType<NativeDigitalGaugeVisual>(Assert.Single(rail.Children.Cast<UIElement>()));
        var effect = Assert.IsType<DigitalGaugeShaderEffect>(material.Effect);
        double previous = -1;
        foreach (var pressure in new[] { -10d, 0, 20 })
        {
            rail.Display = model.Calculate(42, false, pressure, showVacuum: true);
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                typeof(DigitalBoostRailView).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(rail, [context]);
            }
            var zero = BoostPressureUnits.GaugeFraction(0, unit, showVacuum: true);
            var fraction = effect.GaugeParameters.X;
            Assert.True(fraction > previous);
            Assert.Equal(Math.Sign(pressure), Math.Sign(fraction - zero));
            Assert.Equal(stock ? Visibility.Visible : Visibility.Collapsed, material.Visibility);
            Assert.Contains(Flatten(drawing).OfType<GeometryDrawing>(), item =>
                item.Geometry is LineGeometry line && line.StartPoint.Y == 55.75 &&
                Math.Abs(line.StartPoint.X - (9.05 + 283 * zero)) < 0.001);
            previous = fraction;
        }
    }

    private static IEnumerable<Drawing> Flatten(Drawing drawing) => drawing is DrawingGroup group
        ? group.Children.SelectMany(Flatten)
        : [drawing];
}
