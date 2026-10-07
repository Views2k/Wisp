using System.Windows;
using System.Windows.Media;

namespace Wisp.App.Runs;

public static class LapReviewPalette
{
    private static readonly Color[] Stops = [Color.FromRgb(52, 120, 246), Color.FromRgb(24, 203, 220),
        Color.FromRgb(80, 224, 113), Color.FromRgb(246, 213, 69), Color.FromRgb(248, 79, 67)];
    private static readonly Brush[] Brushes = Enumerable.Range(0, 64).Select(i =>
    {
        var brush = new SolidColorBrush(GetColor(i / 63d));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();
    public static Brush LegendBrush { get; } = CreateLegend();
    public static Brush ContactBrush { get; } = Freeze(Color.FromRgb(255, 105, 116));
    public static Brush StartBrush { get; } = Freeze(Color.FromRgb(46, 220, 175));
    private static Geometry ContactSymbol { get; } = CreateContactSymbol();

    internal static void DrawContact(DrawingContext drawing, Point position, Brush outline, bool selected = false)
    {
        drawing.PushTransform(new TranslateTransform(position.X, position.Y));
        drawing.DrawGeometry(ContactBrush, new Pen(selected ? System.Windows.Media.Brushes.White : outline, 1.5), ContactSymbol);
        drawing.Pop();
    }

    private static Geometry CreateContactSymbol()
    {
        var shape = new StreamGeometry();
        using (var drawing = shape.Open())
        {
            drawing.BeginFigure(new Point(0, -7), true, true);
            drawing.PolyLineTo(new Point[] { new(2.3, -2.3), new(7, 0), new(2.3, 2.3),
                new(0, 7), new(-2.3, 2.3), new(-7, 0), new(-2.3, -2.3) }, true, false);
        }
        shape.Freeze();
        return shape;
    }

    internal static Color GetColor(double fraction)
    {
        var scaled = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1) * (Stops.Length - 1);
        var lower = Math.Min((int)scaled, Stops.Length - 2);
        var t = scaled - lower;
        var a = Stops[lower]; var b = Stops[lower + 1];
        return Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    internal static Brush GetBrush(double fraction) => Brushes[Math.Clamp((int)Math.Round(
        (double.IsFinite(fraction) ? fraction : 0) * 63), 0, 63)];

    private static Brush CreateLegend()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (var i = 0; i < Stops.Length; i++) brush.GradientStops.Add(new(Stops[i], i / (double)(Stops.Length - 1)));
        brush.Freeze();
        return brush;
    }
    private static Brush Freeze(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}

internal readonly record struct LapReviewColorRange(double Minimum, double Maximum, bool HasValues)
{
    internal double Fraction(double value) => Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) : .5;
    internal static LapReviewColorRange From(LapReviewPlotData data)
    {
        var minimum = double.PositiveInfinity; var maximum = double.NegativeInfinity;
        if (data.Lap is { } lap)
            for (var i = 0; i < lap.Points.Length; i++)
                if (LapReviewPlot.Value(lap.Points[i], data, i) is { } value && double.IsFinite(value))
                { minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
        return double.IsFinite(minimum) ? new(minimum, maximum, true) : new(0, 0, false);
    }
}
