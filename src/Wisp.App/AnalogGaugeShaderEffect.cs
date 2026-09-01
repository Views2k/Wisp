using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Wisp.App;

internal sealed class AnalogGaugeShaderEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("/Wisp;component/Shaders/AnalogGauge.ps", UriKind.Relative)
    };

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input),
        typeof(AnalogGaugeShaderEffect),
        0);

    public static readonly DependencyProperty GaugeParametersProperty = DependencyProperty.Register(
        nameof(GaugeParameters),
        typeof(Point),
        typeof(AnalogGaugeShaderEffect),
        new UIPropertyMetadata(new Point(1, 1d / 24d), PixelShaderConstantCallback(0)));

    public AnalogGaugeShaderEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(GaugeParametersProperty);
    }

    public Brush? Input
    {
        get => (Brush?)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point GaugeParameters
    {
        get => (Point)GetValue(GaugeParametersProperty);
        set => SetValue(GaugeParametersProperty, value);
    }
}
