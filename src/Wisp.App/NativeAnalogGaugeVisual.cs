using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public sealed class NativeAnalogGaugeVisual : FrameworkElement
{
    private const double NativeOverlayCenterOffsetY = -1.5;
    private static readonly Color NumberTint = Color.FromArgb(102, 255, 255, 255);
    private static readonly Color RedlineNumberTint = Color.FromArgb(255, 255, 0, 136);
    private static readonly Color LitRedlineNumberTint = Color.FromArgb(205, 255, 0, 136);
    private readonly Dictionary<(int Value, Color Tint), ImageSource> _numberImages = new();
    private NativeGaugeFrame _frame;
    private (bool HasExactTachometer, int ScaleMaximum, int FirstRedlineNumber,
        int HighestLitRedlineNumber) _renderState;
    private bool _hasRenderState;

    public void UpdateFrame(NativeGaugeFrame frame)
    {
        _frame = frame;
        var renderState = NumberLayerStateFor(frame);
        if (_hasRenderState && renderState == _renderState)
        {
            return;
        }

        _renderState = renderState;
        _hasRenderState = true;
        InvalidateVisual();
    }

    internal static (bool HasExactTachometer, int ScaleMaximum, int FirstRedlineNumber,
        int HighestLitRedlineNumber) NumberLayerStateFor(NativeGaugeFrame frame)
    {
        var hasExactTachometer = NativeGaugeGeometry.HasExactTachometerState(
            frame.ExactRedline,
            frame.TachometerMaximumRpm);
        if (!hasExactTachometer)
        {
            return (false, 0, 0, 0);
        }

        var scaleMaximum = NativeGaugeGeometry.ScaleMaximumThousands(
            frame.TachometerMaximumRpm);
        var firstRedlineNumber = Math.Clamp(
            (int)Math.Ceiling(frame.ExactRedline.Rpm / 1000d),
            0,
            scaleMaximum + 1);
        var highestLitRedlineNumber = double.IsFinite(frame.EngineRpm)
            ? Math.Clamp(
                (int)Math.Floor(frame.EngineRpm / 1000d),
                firstRedlineNumber - 1,
                scaleMaximum)
            : firstRedlineNumber - 1;
        return (true, scaleMaximum, firstRedlineNumber, highestLitRedlineNumber);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!NativeGaugeGeometry.HasExactTachometerState(
                _frame.ExactRedline,
                _frame.TachometerMaximumRpm))
        {
            return;
        }

        var scaleMaximum = NativeGaugeGeometry.ScaleMaximumThousands(_frame.TachometerMaximumRpm);
        var center = new Point(144, 144);
        const double numberRadius = 126;
        var step = NativeGaugeGeometry.AnalogSweepAngleDegrees / scaleMaximum;

        for (var value = 0; value <= scaleMaximum; value++)
        {
            var angle = NativeGaugeGeometry.AnalogStartAngleDegrees + (value * step);
            var numberCenter = PointAt(center, numberRadius, angle);
            numberCenter.Offset(0, NativeOverlayCenterOffsetY);
            DrawTintedNumber(
                drawingContext,
                value,
                new Rect(numberCenter.X - 9, numberCenter.Y - 9, 18, 18),
                NumberTintFor(value, _frame));
        }
    }

    internal static Color NumberTintFor(int valueThousands, NativeGaugeFrame frame)
    {
        if (!NativeGaugeGeometry.IsRedlineValue(valueThousands, frame.ExactRedline))
        {
            return NumberTint;
        }

        return NativeGaugeGeometry.IsAnalogRpmNumberLit(valueThousands, frame.EngineRpm)
            ? LitRedlineNumberTint
            : RedlineNumberTint;
    }

    private void DrawTintedNumber(DrawingContext context, int value, Rect bounds, Color tint)
    {
        var key = (value, tint);
        if (!_numberImages.TryGetValue(key, out var image))
        {
            image = NativeAssetCache.GetTinted(
                NativeGaugeMode.Analogue,
                $"HUD_Dial_RevNumbers_{value}.png",
                tint);
            _numberImages.Add(key, image);
        }

        context.DrawImage(image, bounds);
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(
            center.X + (Math.Cos(radians) * radius),
            center.Y + (Math.Sin(radians) * radius));
    }

}
