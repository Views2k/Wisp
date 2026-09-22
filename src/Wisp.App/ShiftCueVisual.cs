using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;

namespace Wisp.App;

// The same small, immutable mask is used by the WPF fallback and native HUD.
// Its color and flash phase arrive with the existing gauge frame.
public sealed class ShiftCueVisual : FrameworkElement
{
    private ShiftCueVisualState _state;
    private SolidColorBrush? _brush;

    public bool Digital { get; set; }

    internal void Update(ShiftCueVisualState state)
    {
        if (_state.Appearance == state.Appearance) return;
        _state = state;
        _brush = state.IsVisible ? new SolidColorBrush(ShiftCueArtwork.Color(state.ColorArgb)) : null;
        _brush?.Freeze();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!_state.IsVisible || _brush is null) return;
        drawingContext.PushOpacityMask(ShiftCueArtwork.Mask(Digital));
        drawingContext.DrawRectangle(_brush, null, new Rect(RenderSize));
        drawingContext.Pop();
    }
}

internal static class ShiftCueArtwork
{
    internal const uint AnalogTextureId = 90_000;
    internal const uint DigitalTextureId = 90_001;
    private const int Size = 256;
    private static readonly byte[] AnalogPixels = CreateRing(.36, .023);
    private static readonly byte[] DigitalPixels = CreateRing(.465, .032);
    private static readonly Lazy<ImageBrush> AnalogMask = new(() => CreateMask(AnalogPixels));
    private static readonly Lazy<ImageBrush> DigitalMask = new(() => CreateMask(DigitalPixels));

    internal static ImageBrush Mask(bool digital) => digital ? DigitalMask.Value : AnalogMask.Value;

    internal static AnalogHudTexture Texture(bool digital) => new(
        digital ? DigitalTextureId : AnalogTextureId, Size, Size, Size * 4,
        digital ? DigitalPixels : AnalogPixels);

    internal static Color Color(uint argb) => System.Windows.Media.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    internal static AnalogHudColor Tint(uint argb) => new(
        (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    private static ImageBrush CreateMask(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, pixels, Size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    private static byte[] CreateRing(double radius, double thickness)
    {
        var pixels = new byte[Size * Size * 4];
        var center = Size / 2d;
        var ringRadius = radius * Size;
        var halfThickness = thickness * Size / 2;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var dx = x + .5 - center;
                var dy = y + .5 - center;
                var edgeDistance = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - ringRadius) - halfThickness;
                var alpha = (byte)Math.Round(Math.Clamp(.5 - edgeDistance, 0, 1) * 255);
                var offset = (y * Size + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = alpha;
            }
        return pixels;
    }
}
