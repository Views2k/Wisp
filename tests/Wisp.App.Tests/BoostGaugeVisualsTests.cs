using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

internal static class BoostGaugeVisualsTests
{
    // Keep shared shader resources on the existing resource-only STA fixture.
    internal static void AssertOnCurrentDispatcher()
    {
        NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -20, 2);
        NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -200, 3);
        NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Bar, -20, 2);
        NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Psi, -0.1, 2, showsMinus: false);
        NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(BoostPressureUnit.Bar, -0.1, 2, showsMinus: false);
        foreach (var unit in new[] { BoostPressureUnit.Psi, BoostPressureUnit.Bar })
            foreach (var stock in new[] { false, true })
                DigitalRailPlacesVacuumBelowTheZeroReference(unit, stock);
    }

    private static void NativeReadoutDrawsAMinusAndFitsSignedTelemetryInsideTheRing(
        BoostPressureUnit unit, double pressure, int digitCount, bool showsMinus = true)
    {
        var model = new BoostDisplayModel();
        model.Calculate(42, false, 24);
        var gauge = new AnalogBoostGaugeView
        {
            PressureUnit = unit,
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
        Assert.True(drawing.Bounds.Width <= 44.01);
    }

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
