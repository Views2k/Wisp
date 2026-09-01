using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Wisp.App;

internal sealed class AnalogNeedleShaderEffect : ShaderEffect
{
    private static readonly PixelShader CombustionShader = new()
    {
        UriSource = new Uri("/Wisp;component/Shaders/AnalogNeedle.ps", UriKind.Relative)
    };

    private static readonly PixelShader ElectricShader = new()
    {
        UriSource = new Uri("/Wisp;component/Shaders/ElectricAnalogNeedle.ps", UriKind.Relative)
    };

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        nameof(Input),
        typeof(AnalogNeedleShaderEffect),
        0);

    public static readonly DependencyProperty BlurAmountProperty = DependencyProperty.Register(
        nameof(BlurAmount),
        typeof(double),
        typeof(AnalogNeedleShaderEffect),
        new UIPropertyMetadata(0d, PixelShaderConstantCallback(0)));

    public AnalogNeedleShaderEffect()
    {
        PixelShader = CombustionShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BlurAmountProperty);
    }

    public Brush? Input
    {
        get => (Brush?)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double BlurAmount
    {
        get => (double)GetValue(BlurAmountProperty);
        set => SetValue(BlurAmountProperty, value);
    }

    public void UseElectricMaterial(bool useElectricMaterial) =>
        PixelShader = useElectricMaterial ? ElectricShader : CombustionShader;
}
