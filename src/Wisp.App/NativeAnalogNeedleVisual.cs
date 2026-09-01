using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

public sealed class NativeAnalogNeedleVisual : FrameworkElement
{
    private readonly AnalogNeedleShaderEffect _effect = new();

    public static readonly DependencyProperty IsElectricMaterialProperty = DependencyProperty.Register(
        nameof(IsElectricMaterial),
        typeof(bool),
        typeof(NativeAnalogNeedleVisual),
        new FrameworkPropertyMetadata(false, OnIsElectricMaterialChanged));

    public NativeAnalogNeedleVisual()
    {
        Effect = _effect;
        IsHitTestVisible = false;
    }

    public double BlurAmount
    {
        get => _effect.BlurAmount;
        set
        {
            if (_effect.BlurAmount != value)
            {
                _effect.BlurAmount = value;
            }
        }
    }

    public bool IsElectricMaterial
    {
        get => (bool)GetValue(IsElectricMaterialProperty);
        set => SetValue(IsElectricMaterialProperty, value);
    }

    private static void OnIsElectricMaterialChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs) =>
        ((NativeAnalogNeedleVisual)dependencyObject)._effect.UseElectricMaterial((bool)eventArgs.NewValue);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
    }
}
