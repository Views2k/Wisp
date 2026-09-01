using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public sealed class NativeAnalogMaterialVisual : FrameworkElement
{
    private readonly AnalogGaugeShaderEffect _effect = new();

    public NativeAnalogMaterialVisual()
    {
        Effect = _effect;
        IsHitTestVisible = false;
    }

    public void UpdateFrame(NativeGaugeFrame frame)
    {
        var hasExactTachometer = NativeGaugeGeometry.HasExactTachometerState(
            frame.ExactRedline,
            frame.TachometerMaximumRpm);
        Visibility = hasExactTachometer ? Visibility.Visible : Visibility.Collapsed;
        if (!hasExactTachometer)
        {
            return;
        }

        var parameters = GaugeParametersFor(frame);
        if (_effect.GaugeParameters != parameters)
        {
            _effect.GaugeParameters = parameters;
        }
    }

    internal static Point GaugeParametersFor(NativeGaugeFrame frame)
    {
        var scaleMaximumRpm = NativeGaugeGeometry.ScaleMaximumRpm(frame.TachometerMaximumRpm);
        return new Point(
            NativeGaugeGeometry.RedlineStartNormalized(frame.ExactRedline, frame.TachometerMaximumRpm),
            NativeGaugeGeometry.AnalogLargeDashRpm / scaleMaximumRpm);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
    }
}
