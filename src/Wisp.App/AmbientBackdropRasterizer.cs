namespace Wisp.App;

// The website's dark background and point-sprite material, composited before
// conversion to 8-bit output so faint overlapping particles do not form bands.
internal sealed class AmbientBackdropRasterizer
{
    internal const int MaximumPixels = 1_600_000;
    private readonly float[] _background;
    private readonly float[] _composite;
    private readonly byte[] _roundingNoise;

    internal AmbientBackdropRasterizer(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        var count = width * height;
        _background = new float[count * 3];
        _composite = new float[count * 3];
        _roundingNoise = new byte[count];
        Pixels = new byte[count * 4];
        var aspect = width / (double)height;
        for (var y = 0; y < height; y++)
        {
            var dy = (1 - (y + 0.5) / height - 0.42) * 0.8;
            for (var x = 0; x < width; x++)
            {
                var dx = ((x + 0.5) / width - 1.2) * aspect * 0.58;
                var light = (float)(Math.Exp(-(dx * dx + dy * dy) * 1.24) * 0.135);
                var index = y * width + x;
                _background[index * 3] = 9 + 10 * light;
                _background[index * 3 + 1] = 13 + 30 * light;
                _background[index * 3 + 2] = 18 + 26 * light;
                _roundingNoise[index] = Noise(x, y);
                Pixels[index * 4 + 3] = 255;
            }
        }
    }

    internal int Width { get; }
    internal int Height { get; }
    internal byte[] Pixels { get; }

    internal void Render(ReadOnlySpan<AmbientParticle> particles, double intensity)
    {
        Array.Copy(_background, _composite, _background.Length);
        intensity = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : 0;
        if (intensity > 0)
            foreach (var particle in particles)
                Composite(particle, intensity);

        for (var index = 0; index < _roundingNoise.Length; index++)
        {
            // Stable stochastic rounding removes contour steps without adding
            // a visible animated grain layer or biasing the average color.
            var rounding = (_roundingNoise[index] + 0.5f) / 256;
            var source = index * 3;
            var target = index * 4;
            Pixels[target] = Quantize(_composite[source + 2], rounding);
            Pixels[target + 1] = Quantize(_composite[source + 1], rounding);
            Pixels[target + 2] = Quantize(_composite[source], rounding);
        }
    }

    private void Composite(AmbientParticle particle, double intensity)
    {
        if (!double.IsFinite(particle.Position.X) || !double.IsFinite(particle.Position.Y) ||
            !double.IsFinite(particle.Radius) || particle.Radius <= 0 || particle.Radius > 21 ||
            !double.IsFinite(particle.Opacity) || !double.IsFinite(particle.Softness) ||
            !double.IsFinite(particle.Red) || !double.IsFinite(particle.Green) || !double.IsFinite(particle.Blue))
            return;

        var peakAlpha = Math.Clamp(particle.Opacity, 0, 1) * intensity;
        if (peakAlpha < 0.001)
            return;
        var left = Math.Max(0, Math.Ceiling(particle.Position.X - particle.Radius - 0.5));
        var right = Math.Min(Width - 1, Math.Floor(particle.Position.X + particle.Radius - 0.5));
        var top = Math.Max(0, Math.Ceiling(particle.Position.Y - particle.Radius - 0.5));
        var bottom = Math.Min(Height - 1, Math.Floor(particle.Position.Y + particle.Radius - 0.5));
        if (left > right || top > bottom)
            return;
        var exponent = 0.72 + (2.6 - 0.72) * Math.Clamp(particle.Softness, 0, 1);
        var red = (float)(Math.Clamp(particle.Red, 0, 1) * 255);
        var green = (float)(Math.Clamp(particle.Green, 0, 1) * 255);
        var blue = (float)(Math.Clamp(particle.Blue, 0, 1) * 255);
        for (var y = (int)top; y <= (int)bottom; y++)
        {
            var py = (y + 0.5 - particle.Position.Y) / particle.Radius;
            for (var x = (int)left; x <= (int)right; x++)
            {
                var px = (x + 0.5 - particle.Position.X) / particle.Radius;
                var radius = Math.Sqrt(px * px + py * py);
                if (radius >= 1)
                    continue;
                var derivative = radius > 0.000001
                    ? (Math.Abs(px) + Math.Abs(py)) / (radius * particle.Radius)
                    : 1 / particle.Radius;
                var antialias = Math.Max(derivative * 1.35, 0.006);
                var mask = 1 - SmoothStep(1 - antialias, 1 + antialias, radius);
                var alpha = (float)(mask * Math.Pow(1 - radius, exponent) * peakAlpha);
                if (alpha < 0.001f)
                    continue;
                var index = (y * Width + x) * 3;
                _composite[index] += (red - _composite[index]) * alpha;
                _composite[index + 1] += (green - _composite[index + 1]) * alpha;
                _composite[index + 2] += (blue - _composite[index + 2]) * alpha;
            }
        }
    }

    private static double SmoothStep(double lower, double upper, double value)
    {
        var amount = Math.Clamp((value - lower) / (upper - lower), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static byte Quantize(float value, float rounding) => (byte)Math.Clamp((int)(value + rounding), 0, 255);

    private static byte Noise(int x, int y)
    {
        unchecked
        {
            var value = (uint)x * 0x1f123bb5u ^ (uint)y * 0x5f356495u ^ 0x57495350u;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return (byte)(value >> 24);
        }
    }
}
