using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Wisp.App;

internal sealed class DigitalGaugeShaderEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("/Wisp;component/Shaders/DigitalGauge.ps", UriKind.Relative)
    };

    public static readonly DependencyProperty GaugeParametersProperty = DependencyProperty.Register(
        nameof(GaugeParameters),
        typeof(Point),
        typeof(DigitalGaugeShaderEffect),
        new UIPropertyMetadata(new Point(0, 1), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(DigitalGaugeShaderEffect), 0);

    public DigitalGaugeShaderEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(GaugeParametersProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point GaugeParameters
    {
        get => (Point)GetValue(GaugeParametersProperty);
        set => SetValue(GaugeParametersProperty, value);
    }
}
