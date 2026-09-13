using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App;

internal sealed class AmbientParticleSprites
{
    internal const int MaximumCachedSprites = 1024;
    internal const int MaximumCachedPixelBytes = 8 * 1024 * 1024;
    internal const int MaximumSpritePixels = 256;
    private const int SoftnessLevels = 8;
    private readonly Dictionary<SpriteKey, BitmapSource> _sprites = [];
    private readonly Queue<SpriteKey> _insertionOrder = new();
    private readonly Dictionary<MaterialKey, byte[]> _coverage = [];
    private uint? _color;

    internal int CachedSpriteCount => _sprites.Count;
    internal int CachedPixelBytes { get; private set; }
    internal int CachedCoverageBytes { get; private set; }
    internal int CreatedSpriteCount { get; private set; }

    internal int Draw(DrawingContext context, ReadOnlySpan<AmbientParticle> particles,
        Size viewport, Color color, double intensity, DpiScale dpi)
    {
        if (!double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0 || !double.IsFinite(intensity))
            return 0;
        intensity = Math.Clamp(intensity, 0, 1) * color.A / 255d;
        if (intensity <= 0)
            return 0;

        var rgb = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_color != rgb)
        {
            _sprites.Clear();
            _insertionOrder.Clear();
            CachedPixelBytes = 0;
            _color = rgb;
        }

        var drawn = 0;
        var scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        foreach (var particle in particles)
        {
            if (!double.IsFinite(particle.Position.X) || !double.IsFinite(particle.Position.Y) ||
                !double.IsFinite(particle.Radius) || particle.Radius <= 0 || particle.Radius > 21 ||
                !double.IsFinite(particle.Opacity) || !double.IsFinite(particle.Softness))
                continue;

            var alpha = (byte)Math.Clamp((int)Math.Round(Math.Clamp(particle.Opacity, 0, 1) * intensity * 255), 0, 255);
            var diameter = particle.Radius * 2;
            var bounds = new Rect(particle.Position.X - particle.Radius,
                particle.Position.Y - particle.Radius, diameter, diameter);
            if (alpha == 0 || bounds.Right <= 0 || bounds.Bottom <= 0 ||
                bounds.Left >= viewport.Width || bounds.Top >= viewport.Height)
                continue;

            var pixels = 16;
            var requiredPixels = diameter * scale + 2;
            while (pixels < requiredPixels && pixels < MaximumSpritePixels)
                pixels *= 2;
            var softness = (byte)Math.Round(Math.Clamp(particle.Softness, 0, 1) * (SoftnessLevels - 1));
            var key = new SpriteKey(pixels, softness, alpha);
            if (!_sprites.TryGetValue(key, out var sprite))
                sprite = CreateSprite(key, color);

            // The texture's transparent gutter must not reduce the scene radius,
            // or changing texture resolution would make a moving point change size.
            var paddedRadius = particle.Radius * pixels / (pixels - 2d);
            bounds = new Rect(particle.Position.X - paddedRadius,
                particle.Position.Y - paddedRadius, paddedRadius * 2, paddedRadius * 2);
            // Keep fractional DIP positions: snapping tiny dots to whole pixels makes
            // their slow travel jump even when every composition frame is rendered.
            context.DrawImage(sprite, bounds);
            drawn++;
        }
        return drawn;
    }

    private BitmapSource CreateSprite(SpriteKey key, Color color)
    {
        var material = new MaterialKey(key.Pixels, key.Softness);
        if (!_coverage.TryGetValue(material, out var coverage))
        {
            coverage = CreateCoverage(material);
            _coverage.Add(material, coverage);
            CachedCoverageBytes += coverage.Length;
        }

        var pixels = new byte[key.Pixels * key.Pixels * 4];
        for (var index = 0; index < coverage.Length; index++)
        {
            var alpha = (byte)((coverage[index] * key.Alpha + 127) / 255);
            pixels[index * 4] = Premultiply(color.B, alpha);
            pixels[index * 4 + 1] = Premultiply(color.G, alpha);
            pixels[index * 4 + 2] = Premultiply(color.R, alpha);
            pixels[index * 4 + 3] = alpha;
        }
        var sprite = BitmapSource.Create(key.Pixels, key.Pixels, 96, 96,
            PixelFormats.Pbgra32, null, pixels, key.Pixels * 4);
        sprite.Freeze();

        while (_sprites.Count >= MaximumCachedSprites || CachedPixelBytes + pixels.Length > MaximumCachedPixelBytes)
        {
            var removed = _insertionOrder.Dequeue();
            _sprites.Remove(removed);
            CachedPixelBytes -= removed.Pixels * removed.Pixels * 4;
        }
        _sprites.Add(key, sprite);
        _insertionOrder.Enqueue(key);
        CachedPixelBytes += pixels.Length;
        CreatedSpriteCount++;
        return sprite;
    }

    private static byte[] CreateCoverage(MaterialKey key)
    {
        var result = new byte[key.Pixels * key.Pixels];
        var exponent = 0.72 + (2.6 - 0.72) * key.Softness / (SoftnessLevels - 1);
        var radius = key.Pixels / 2d - 1;
        for (var y = 0; y < key.Pixels; y++)
            for (var x = 0; x < key.Pixels; x++)
            {
                var coverage = 0d;
                for (var sampleY = 0; sampleY < 2; sampleY++)
                    for (var sampleX = 0; sampleX < 2; sampleX++)
                    {
                        var dx = (x + 0.25 + sampleX * 0.5 - key.Pixels / 2d) / radius;
                        var dy = (y + 0.25 + sampleY * 0.5 - key.Pixels / 2d) / radius;
                        var distance = Math.Sqrt(dx * dx + dy * dy);
                        if (distance < 1)
                            coverage += Math.Pow(1 - distance, exponent);
                    }
                result[y * key.Pixels + x] = (byte)Math.Clamp((int)Math.Round(coverage * 255 / 4), 0, 255);
            }
        return result;
    }

    private static byte Premultiply(byte channel, byte alpha) => (byte)((channel * alpha + 127) / 255);

    private readonly record struct SpriteKey(int Pixels, byte Softness, byte Alpha);
    private readonly record struct MaterialKey(int Pixels, byte Softness);
}
