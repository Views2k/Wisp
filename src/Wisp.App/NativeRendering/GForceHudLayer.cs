using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Wisp.App.NativeRendering;

internal sealed class GForceHudMotion
{
    private readonly NativeTachometerInterpolator _x = new(allowNegativeValues: true);
    private readonly NativeTachometerInterpolator _y = new(allowNegativeValues: true);
    private readonly NativeTachometerInterpolator _scale = new();
    private readonly NativeTachometerInterpolator _time = new();
    private readonly Queue<PendingSample> _pending = new();
    private long _origin;
    private NativeGForceInput _input;
    private long _received;
    private bool _hasInput;
    internal GForceTrailHistory Trail { get; } = new();
    internal bool Active => _hasInput;

    internal void Observe(NativeGForceInput input, long timestamp)
    {
        if (!input.Active || !double.IsFinite(input.XG) || !double.IsFinite(input.YG) ||
            !double.IsFinite(input.FullScaleG) || input.FullScaleG <= 0)
        {
            Reset();
            return;
        }
        if (_hasInput && input.CarOrdinal != _input.CarOrdinal) Reset();
        var received = input.ReceivedTimestamp > 0 ? input.ReceivedTimestamp : timestamp;
        if (_hasInput && (input == _input || input.ReceivedTimestamp > 0 && received <= _received)) return;
        if (!_hasInput) _origin = received;
        _input = input;
        _received = received;
        _hasInput = true;
        var now = Math.Max(timestamp, received);
        _x.Observe(input.CarOrdinal, input.GameTimestampMilliseconds, input.XG, now, received);
        _y.Observe(input.CarOrdinal, input.GameTimestampMilliseconds, input.YG, now, received);
        _scale.Observe(input.CarOrdinal, input.GameTimestampMilliseconds, input.FullScaleG, now, received);
        var sampleTime = (received - _origin) / (double)System.Diagnostics.Stopwatch.Frequency;
        _time.Observe(input.CarOrdinal, input.GameTimestampMilliseconds, sampleTime, now, received);
        _pending.Enqueue(new(new(input.XG, input.YG), input.FullScaleG, sampleTime));
        AdvanceTrail(now);
    }

    internal (Point Offset, double FullScaleG) Sample(long timestamp)
    {
        if (!_hasInput) return (default, 1);
        var scale = Math.Max(double.Epsilon, _scale.Sample(timestamp));
        AdvanceTrail(timestamp);
        return (GForceTrailHistory.Project(new(_x.Sample(timestamp), _y.Sample(timestamp)), scale), scale);
    }

    internal void Reset()
    {
        _x.Reset(); _y.Reset(); _scale.Reset(); _time.Reset(); Trail.Clear(); _pending.Clear();
        _input = default; _received = 0; _hasInput = false;
    }

    private void AdvanceTrail(long timestamp)
    {
        var played = _time.Sample(timestamp);
        while (_pending.TryPeek(out var sample) && sample.Time <= played + 1d / System.Diagnostics.Stopwatch.Frequency)
        {
            _pending.Dequeue();
            Trail.TryAddUnscaled(sample.Point, sample.FullScaleG);
        }
    }

    private readonly record struct PendingSample(Point Point, double FullScaleG, double Time);
}

internal static class GForceHudLayer
{
    private const uint FirstId = 60_000;
    private const double DotSize = 46, StrokeSize = 8;
    private const int GlyphWidth = 16, GlyphHeight = 24, GlyphPadding = 2;
    private const string Glyphs = "+-0123456789. Gg—";
    private static readonly ConditionalWeakTable<FrameworkElement, Cache> Caches = new();

    internal static HudLayerSnapshot Capture(FrameworkElement control, DiagnosticsViewModel? vm)
    {
        control.Dispatcher.VerifyAccess();
        if (control is not NativeGForceMeterView and not GForceMeterView)
            throw new ArgumentException("A G-force gauge is required.", nameof(control));
        var trail = Descendants(control).OfType<GForceTrailView>().Single();
        var dot = Descendants(control).OfType<Ellipse>().Single(element => element.RenderTransform is TranslateTransform);
        var shadow = dot.Effect as DropShadowEffect;
        var width = control.RenderSize.Width;
        var height = control.RenderSize.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Arrange the G-force gauge before capture.");
        var dpi = VisualTreeHelper.GetDpi(control);
        var scale = Math.Max(2, Math.Ceiling(Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
        if (Window.GetWindow(control) is { } window)
        {
            var transform = control.TransformToAncestor(window);
            var origin = transform.Transform(new Point());
            var x = transform.Transform(new Point(1, 0)) - origin;
            var y = transform.Transform(new Point(0, 1)) - origin;
            scale = Math.Max(scale, Math.Ceiling(Math.Max(
                Math.Sqrt(x.X * x.X * dpi.DpiScaleX * dpi.DpiScaleX + x.Y * x.Y * dpi.DpiScaleY * dpi.DpiScaleY),
                Math.Sqrt(y.X * y.X * dpi.DpiScaleX * dpi.DpiScaleX + y.Y * y.Y * dpi.DpiScaleY * dpi.DpiScaleY))));
        }
        var key = new ArtworkKey(control is NativeGForceMeterView, width, height, vm?.IsNativeLayout == true,
            BrushKey(dot.Fill), BrushKey(dot.Stroke), BrushKey(trail.TrailBrush), shadow?.Color ?? Colors.Transparent,
            shadow?.BlurRadius ?? 0, shadow?.Opacity ?? 0, dpi.PixelsPerDip, scale);
        var cache = Caches.GetValue(control, static _ => new Cache());
        if (cache.Artwork is null || cache.Key != key)
        {
            cache.Artwork = CaptureArtwork(control, vm, dot, trail, key);
            cache.Key = key;
        }
        var input = vm?.NativeGForceInput ?? default;
        var texts = cache.Artwork.Text.Select(text => ResolveText(text.Path, vm)).ToArray();
        var invertedX = vm?.InvertLateralG == true;
        var invertedY = vm?.InvertLongitudinalG == true;
        if (cache.Snapshot is { } previous && ReferenceEquals(previous.Artwork, cache.Artwork) &&
            previous.Input == input && previous.InvertedX == invertedX && previous.InvertedY == invertedY &&
            previous.Text.SequenceEqual(texts)) return previous;
        return cache.Snapshot = new(cache.Artwork, input, texts, invertedX, invertedY);
    }

    private static Artwork CaptureArtwork(FrameworkElement source, DiagnosticsViewModel? vm, Ellipse dot,
        GForceTrailView trail, ArtworkKey key)
    {
        UserControl clone = key.Native ? new NativeGForceMeterView() : new GForceMeterView();
        if (source.TryFindResource("AccentBrush") is Brush accent) clone.Resources["AccentBrush"] = accent;
        clone.DataContext = vm;
        clone.Width = key.Width; clone.Height = key.Height;
        clone.UseLayoutRounding = source.UseLayoutRounding;
        clone.SnapsToDevicePixels = source.SnapsToDevicePixels;
        clone.Measure(new Size(key.Width, key.Height));
        clone.Arrange(new Rect(0, 0, key.Width, key.Height));
        clone.UpdateLayout();
        var children = Descendants(clone).ToArray();
        var cloneTrail = children.OfType<GForceTrailView>().Single();
        var center = cloneTrail.TransformToAncestor(clone).Transform(new Point(cloneTrail.ActualWidth / 2, cloneTrail.ActualHeight / 2));
        var textures = new List<AnalogHudTexture>();
        var textLayouts = new List<TextLayout>();
        foreach (var text in children.OfType<TextBlock>())
        {
            var binding = BindingOperations.GetBinding(text, TextBlock.TextProperty);
            if (binding is null) continue;
            if (LocallyVisible(text, clone))
            {
                var position = text.TransformToAncestor(clone).Transform(new Point());
                var right = text.HorizontalAlignment == HorizontalAlignment.Right;
                var bottom = text.VerticalAlignment == VerticalAlignment.Bottom;
                var anchor = new AnalogHudPoint(position.X + (right ? text.ActualWidth : 0),
                    position.Y + (bottom ? text.ActualHeight : 0));
                var id = FirstId + 100 + (uint)textLayouts.Count;
                var layout = CaptureText(id, text, binding.Path.Path, anchor, right, bottom, key.RasterScale, key.PixelsPerDip);
                textures.Add(layout.Texture);
                textLayouts.Add(layout);
            }
            text.Visibility = Visibility.Hidden;
        }
        foreach (var child in children)
            if (child is GForceTrailView || child is Ellipse { RenderTransform: TranslateTransform }) child.Visibility = Visibility.Hidden;
        textures.Add(Texture(FirstId, RasterizeElement(clone, key.Width, key.Height, key.RasterScale)));
        var dotCanvas = new Grid { Width = DotSize, Height = DotSize };
        dotCanvas.Children.Add(new Ellipse
        {
            Width = dot.Width,
            Height = dot.Height,
            Fill = dot.Fill?.CloneCurrentValue(),
            Stroke = dot.Stroke?.CloneCurrentValue(),
            StrokeThickness = dot.StrokeThickness,
            Effect = dot.Effect?.CloneCurrentValue(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        textures.Add(Texture(FirstId + 1, RasterizeElement(dotCanvas, DotSize, DotSize, key.RasterScale)));
        textures.Add(Texture(FirstId + 2, Rasterize(StrokeSize, StrokeSize, key.RasterScale,
            dc => dc.DrawEllipse(Brushes.White, null, new(4, 4), 2, 2))));
        for (var index = 0; index < GForceTrailHistory.Capacity - 1; index++)
        {
            var thickness = .8 + (index + 1d) / (GForceTrailHistory.Capacity - 1) * 1.2;
            textures.Add(Texture(FirstId + 10 + (uint)index * 2,
                Rasterize(StrokeSize, StrokeSize, key.RasterScale, dc =>
                    dc.DrawRectangle(Brushes.White, null, new(0, 4 - thickness / 2, StrokeSize, thickness)))));
            textures.Add(Texture(FirstId + 11 + (uint)index * 2,
                Rasterize(StrokeSize, StrokeSize, key.RasterScale, dc =>
                    dc.DrawEllipse(Brushes.White, null, new(4, 4), thickness / 2, thickness / 2))));
        }
        return new(key.Width, key.Height, center, BrushColor(trail.TrailBrush), textures.AsReadOnly(), textLayouts.AsReadOnly());
    }

    private static TextLayout CaptureText(uint id, TextBlock source, string path, AnalogHudPoint anchor,
        bool right, bool bottom, double scale, double pixelsPerDip)
    {
        var typeface = new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch);
        FormattedText Text(string value) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, source.FontSize, Brushes.White, pixelsPerDip);
        var advances = new double[Glyphs.Length];
        var image = Rasterize(Glyphs.Length * GlyphWidth, GlyphHeight, scale, dc =>
        {
            for (var index = 0; index < Glyphs.Length; index++)
            {
                var text = Text(Glyphs[index].ToString());
                advances[index] = text.WidthIncludingTrailingWhitespace;
                dc.DrawText(text, new(index * GlyphWidth + GlyphPadding, GlyphPadding));
            }
        });
        var kerning = new double[Glyphs.Length * Glyphs.Length];
        for (var left = 0; left < Glyphs.Length; left++)
            for (var next = 0; next < Glyphs.Length; next++)
                kerning[left * Glyphs.Length + next] = Text(string.Concat(Glyphs[left], Glyphs[next])).WidthIncludingTrailingWhitespace -
                    advances[left] - advances[next];
        return new(path, anchor, right, bottom, Text("0").Height, BrushColor(source.Foreground),
            Texture(id, image), advances, kerning);
    }

    private static string ResolveText(string path, DiagnosticsViewModel? vm) => path switch
    {
        nameof(DiagnosticsViewModel.GForceScaleText) => vm?.GForceScaleText ?? "1.0 G",
        nameof(DiagnosticsViewModel.LateralGText) => vm?.LateralGText ?? "—",
        nameof(DiagnosticsViewModel.LongitudinalGText) => vm?.LongitudinalGText ?? "—",
        _ => string.Empty
    };

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static bool LocallyVisible(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(current, root)) break;
        }
        return true;
    }

    private static string BrushKey(Brush? brush) => brush is null ? "" :
        brush.ToString(CultureInfo.InvariantCulture) + "/" + brush.Opacity.ToString("R", CultureInfo.InvariantCulture);
    private static AnalogHudColor BrushColor(Brush? brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.Transparent;
        return new(color.R, color.G, color.B, (byte)Math.Round(color.A * (brush?.Opacity ?? 1)));
    }

    private static BitmapSource RasterizeElement(FrameworkElement element, double width, double height, double scale)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element); image.Freeze();
        return image;
    }

    private static BitmapSource Rasterize(double width, double height, double scale, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze();
        return image;
    }

    private static AnalogHudTexture Texture(uint id, BitmapSource source)
    {
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return new(id, source.PixelWidth, source.PixelHeight, stride, pixels);
    }

    private sealed class Cache
    {
        internal ArtworkKey Key;
        internal Artwork? Artwork;
        internal Snapshot? Snapshot;
    }
    private readonly record struct ArtworkKey(bool Native, double Width, double Height, bool NativeLayout,
        string DotFill, string DotStroke, string Trail, Color Glow, double GlowRadius, double GlowOpacity,
        double PixelsPerDip, double RasterScale);
    private sealed record TextLayout(string Path, AnalogHudPoint Anchor, bool Right, bool Bottom, double Height,
        AnalogHudColor Color, AnalogHudTexture Texture, double[] Advances, double[] Kerning);
    private sealed record Artwork(double Width, double Height, Point Center, AnalogHudColor TrailColor,
        IReadOnlyList<AnalogHudTexture> Textures, IReadOnlyList<TextLayout> Text);
    private sealed record Compatibility(Artwork Artwork, int Car, bool Active, bool InvertedX, bool InvertedY);
    private sealed class Snapshot(Artwork artwork, NativeGForceInput input, string[] text, bool invertedX, bool invertedY) : HudLayerSnapshot
    {
        internal Artwork Artwork { get; } = artwork;
        internal NativeGForceInput Input { get; } = input;
        internal string[] Text { get; } = text;
        internal bool InvertedX { get; } = invertedX;
        internal bool InvertedY { get; } = invertedY;
        internal override IReadOnlyList<AnalogHudTexture> Textures => Artwork.Textures;
        internal override object CompatibilityKey { get; } = new Compatibility(artwork, input.CarOrdinal, input.Active, invertedX, invertedY);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }

    private sealed class Playback : HudLayerPlayback
    {
        private readonly GForceHudMotion _motion = new();
        private Snapshot? _snapshot;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            var next = (Snapshot)snapshot;
            if (_snapshot is { } previous && (previous.InvertedX != next.InvertedX || previous.InvertedY != next.InvertedY))
                _motion.Reset();
            _motion.Observe(next.Input, timestamp);
            _snapshot = next;
        }

        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is not { } snapshot) return [];
            var art = snapshot.Artwork;
            var commands = new List<DirectCompositionDrawCommand>(180)
            {
                AnalogHudScene.Quad(FirstId, new(0, 0, art.Width, art.Height))
            };
            var sample = _motion.Sample(timestamp);
            if (_motion.Active)
            {
                var count = _motion.Trail.Count;
                for (var index = 1; index < count; index++)
                {
                    var from = GForceTrailHistory.Project(_motion.Trail[index - 1], sample.FullScaleG);
                    var to = GForceTrailHistory.Project(_motion.Trail[index], sample.FullScaleG);
                    var pen = GForceTrailHistory.Capacity - count + index - 1;
                    AddSegment(commands, art, from, to, pen);
                }
                for (var index = 0; index < count - 1; index++)
                {
                    var freshness = (index + 1d) / count;
                    var point = GForceTrailHistory.Project(_motion.Trail[index], sample.FullScaleG);
                    var radius = 1 + freshness * .65;
                    commands.Add(AnalogHudScene.Quad(FirstId + 2,
                        new(art.Center.X + point.X - radius * 2, art.Center.Y + point.Y - radius * 2, radius * 4, radius * 4),
                        opacity: .10 + freshness * .22, color: art.TrailColor));
                }
            }
            commands.Add(AnalogHudScene.Quad(FirstId + 1,
                new(art.Center.X + sample.Offset.X - DotSize / 2, art.Center.Y + sample.Offset.Y - DotSize / 2, DotSize, DotSize)));
            for (var index = 0; index < art.Text.Count; index++) AddText(commands, art.Text[index], snapshot.Text[index]);
            return commands.ToArray();
        }
    }

    private static void AddSegment(List<DirectCompositionDrawCommand> commands, Artwork art, Point from, Point to, int pen)
    {
        var delta = to - from;
        var length = delta.Length;
        if (length <= double.Epsilon) return;
        var angle = Math.Atan2(delta.Y, delta.X) * 180 / Math.PI;
        var opacity = .10 + (pen + 1d) / (GForceTrailHistory.Capacity - 1) * .42;
        var id = FirstId + 10 + (uint)pen * 2;
        Add(AnalogHudScene.Quad(id, new(0, -4, length, 8), angle, opacity: opacity, color: art.TrailColor));
        var start = AnalogHudScene.Quad(id + 1, new(-4, -4, 4, 8), angle, opacity: opacity, color: art.TrailColor);
        start.UvRight = .5f;
        Add(start);
        var end = AnalogHudScene.Quad(id + 1, new(length, -4, 4, 8), angle, opacity: opacity, color: art.TrailColor);
        end.UvLeft = .5f;
        Add(end);
        void Add(DirectCompositionDrawCommand command)
        {
            command.OriginX += (float)(art.Center.X + from.X);
            command.OriginY += (float)(art.Center.Y + from.Y);
            commands.Add(command);
        }
    }

    private static void AddText(List<DirectCompositionDrawCommand> commands, TextLayout layout, string text)
    {
        var width = 0d;
        var previous = -1;
        foreach (var character in text)
        {
            var index = Glyphs.IndexOf(character);
            if (index < 0) continue;
            if (previous >= 0) width += layout.Kerning[previous * Glyphs.Length + index];
            width += layout.Advances[index]; previous = index;
        }
        var x = layout.Anchor.X - (layout.Right ? width : 0);
        var y = layout.Anchor.Y - (layout.Bottom ? layout.Height : 0);
        previous = -1;
        foreach (var character in text)
        {
            var index = Glyphs.IndexOf(character);
            if (index < 0) continue;
            if (previous >= 0) x += layout.Kerning[previous * Glyphs.Length + index];
            if (character != ' ')
            {
                var command = AnalogHudScene.Quad(layout.Texture.Id,
                    new(x - GlyphPadding, y - GlyphPadding, GlyphWidth, GlyphHeight), color: layout.Color);
                command.UvLeft = index / (float)Glyphs.Length; command.UvRight = (index + 1) / (float)Glyphs.Length;
                commands.Add(command);
            }
            x += layout.Advances[index]; previous = index;
        }
    }
}
