using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal static class TextHudLayer
{
    private const string Glyphs = "0123456789—- .?";
    private const int CellWidth = 160, CellHeight = 200, Padding = 24;
    private static readonly ConditionalWeakTable<FrameworkElement, Cache> Caches = new();
    private sealed class Cache { internal Artwork? Artwork; internal object? Key; }
    private sealed record Artwork(uint BaseId, IReadOnlyList<AnalogHudTexture> Textures,
        double[] Advances, double[] Kerning, double Height, double Width, double ControlHeight);
    private readonly record struct Placement(double X, double Y, double Width, double Height);

    internal static HudLayerSnapshot Capture(FrameworkElement control, DiagnosticsViewModel? vm)
    {
        var text = Descendants(control).OfType<TextBlock>().First(element => element.FontSize == 104);
        DependencyObject? parent = text;
        while (parent is not null && parent is not Viewbox) parent = VisualTreeHelper.GetParent(parent);
        var viewbox = parent as Viewbox ?? throw new InvalidOperationException("A speed readout requires its authored viewbox.");
        var dpi = VisualTreeHelper.GetDpi(control);
        var scale = 2d;
        if (Window.GetWindow(control) is { } window && window.IsAncestorOf(control))
        {
            var transform = control.TransformToAncestor(window);
            var zero = transform.Transform(new Point());
            var x = transform.Transform(new Point(1, 0)) - zero;
            var y = transform.Transform(new Point(0, 1)) - zero;
            scale = Math.Max(2, Math.Ceiling(Math.Max(x.Length * dpi.DpiScaleX, y.Length * dpi.DpiScaleY)));
        }
        var border = (control.TryFindResource("HudBorderBrush") as SolidColorBrush)?.Color ?? Colors.Transparent;
        var key = (control.RenderSize, scale, text.FontFamily.Source, text.FontStyle, text.FontWeight,
            text.FontStretch, border, control.Name == "MinimalPanel");
        var cache = Caches.GetValue(control, static _ => new Cache());
        if (cache.Artwork is null || !Equals(cache.Key, key))
        {
            cache.Artwork = CaptureArtwork(control, text, scale);
            cache.Key = key;
        }
        var bounds = viewbox.TransformToAncestor(control).TransformBounds(new Rect(viewbox.RenderSize));
        var color = (text.Foreground as SolidColorBrush)?.Color ?? Colors.White;
        var speed = vm?.HudSpeed ?? text.Text;
        return new Snapshot(cache.Artwork, speed, new(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new(color.R, color.G, color.B, color.A));
    }

    private static Artwork CaptureArtwork(FrameworkElement control, TextBlock text, double scale)
    {
        var baseId = control.Name switch { "MinimalPanel" => 70_000u, "BoxedSpeedPanel" => 71_000u, _ => 72_000u };
        var textures = new List<AnalogHudTexture>();
        var hidden = Descendants(control).OfType<UIElement>()
            .Where(element => ReferenceEquals(element, text) || element is GForceMeterView).ToArray();
        var opacity = hidden.Select(element => element.Opacity).ToArray();
        var mask = control.OpacityMask;
        try
        {
            for (var i = 0; i < hidden.Length; i++) hidden[i].SetCurrentValue(UIElement.OpacityProperty, 0d);
            control.SetCurrentValue(UIElement.OpacityMaskProperty, null);
            textures.Add(Rasterize(baseId, control.ActualWidth, control.ActualHeight, scale, drawing =>
            {
                var brush = new VisualBrush(control)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(control.RenderSize),
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                drawing.DrawRectangle(brush, null, new Rect(control.RenderSize));
            }));
        }
        finally
        {
            control.SetCurrentValue(UIElement.OpacityMaskProperty, mask);
            for (var i = 0; i < hidden.Length; i++) hidden[i].SetCurrentValue(UIElement.OpacityProperty, opacity[i]);
        }
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        FormattedText Format(string value) => new(value, CultureInfo.InvariantCulture, text.FlowDirection,
            typeface, text.FontSize, Brushes.White, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        var advances = Glyphs.Select(c => Format(c.ToString()).WidthIncludingTrailingWhitespace).ToArray();
        var kerning = new double[Glyphs.Length * Glyphs.Length];
        for (var left = 0; left < Glyphs.Length; left++)
            for (var right = 0; right < Glyphs.Length; right++)
                kerning[left * Glyphs.Length + right] = Format(string.Concat(Glyphs[left], Glyphs[right])).WidthIncludingTrailingWhitespace
                    - advances[left] - advances[right];
        // Each original glyph remains a fixed texture; changing speed only changes quads.
        for (var index = 0; index < Glyphs.Length; index++)
        {
            var glyph = Format(Glyphs[index].ToString());
            textures.Add(Rasterize(baseId + 1 + (uint)index, CellWidth, CellHeight, scale,
                drawing => drawing.DrawText(glyph, new Point(Padding, Padding)),
                control.Name == "MinimalPanel" ? text.Effect?.CloneCurrentValue() : null));
        }
        return new(baseId, textures.AsReadOnly(), advances, kerning, Format("0").Height,
            control.ActualWidth, control.ActualHeight);
    }

    private static AnalogHudTexture Rasterize(uint id, double width, double height, double scale,
        Action<DrawingContext> draw, Effect? effect = null)
    {
        var visual = new DrawingVisual { Effect = effect };
        using (var context = visual.RenderOpen()) draw(context);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)), scale * 96, scale * 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new(id, bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class Snapshot(Artwork artwork, string text, Placement placement, AnalogHudColor color) : HudLayerSnapshot
    {
        internal Artwork Artwork { get; } = artwork;
        internal string Text { get; } = text;
        internal Placement Placement { get; } = placement;
        internal AnalogHudColor Color { get; } = color;
        internal override IReadOnlyList<AnalogHudTexture> Textures => Artwork.Textures;
        internal override object CompatibilityKey => (Artwork, Placement, Color);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }
    private sealed class Playback : HudLayerPlayback
    {
        private Snapshot? _snapshot;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) => _snapshot = (Snapshot)snapshot;
        internal override void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp)
        {
            if (_snapshot is not { } s) return;
            var a = s.Artwork;
            var indices = s.Text.Select(c => Glyphs.IndexOf(c) is var index && index >= 0 ? index : Glyphs.Length - 1).ToArray();
            double width = 6;
            for (var i = 0; i < indices.Length; i++)
            {
                width += a.Advances[indices[i]];
                if (i > 0) width += a.Kerning[indices[i - 1] * Glyphs.Length + indices[i]];
            }
            var scale = Math.Min(s.Placement.Width / Math.Max(1, width), s.Placement.Height / a.Height);
            var x = s.Placement.X + (s.Placement.Width - width * scale) / 2 + 3 * scale;
            var y = s.Placement.Y + (s.Placement.Height - a.Height * scale) / 2;
            commands.Add(AnalogHudScene.Quad(a.BaseId, new(0, 0, a.Width, a.ControlHeight)));
            for (var i = 0; i < indices.Length; i++)
            {
                var index = indices[i];
                if (i > 0) x += a.Kerning[indices[i - 1] * Glyphs.Length + index] * scale;
                commands.Add(AnalogHudScene.Quad(a.BaseId + 1 + (uint)index,
                    new(x - Padding * scale, y - Padding * scale, CellWidth * scale, CellHeight * scale), color: s.Color));
                x += a.Advances[index] * scale;
            }
        }
    }
}
