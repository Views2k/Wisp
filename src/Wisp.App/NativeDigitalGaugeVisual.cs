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
