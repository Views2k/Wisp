using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public sealed class NativeDigitalGaugeVisual : FrameworkElement
{
    private readonly DigitalGaugeShaderEffect _effect = new();

    public NativeDigitalGaugeVisual()
    {
        Effect = _effect;
        IsHitTestVisible = false;
    }

    public void UpdateFrame(NativeGaugeFrame frame)
    {
        var parameters = GaugeParametersFor(frame);
        UpdateGaugeParameters(parameters.X, parameters.Y);
    }

    internal void UpdateGaugeParameters(double currentFraction, double redlineFraction)
    {
        var parameters = new Point(
            Math.Clamp(currentFraction, 0, 1),
            Math.Clamp(redlineFraction, 0, 1));
        if (_effect.GaugeParameters != parameters)
        {
            _effect.GaugeParameters = parameters;
        }
    }

    internal static Point GaugeParametersFor(NativeGaugeFrame frame) =>
        new(
            NativeGaugeGeometry.NormalizedRpm(frame.EngineRpm, frame.TachometerMaximumRpm),
            NativeGaugeGeometry.RedlineStartNormalized(
                frame.ExactRedline,
                frame.TachometerMaximumRpm));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
    }
}
