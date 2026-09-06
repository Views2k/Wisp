namespace Wisp.App;

internal readonly record struct AmbientPoint(double X, double Y);
internal readonly record struct AmbientParticle(
    AmbientPoint Position,
    double Radius,
    double Opacity,
    double Softness,
    double Red,
    double Green,
    double Blue);

internal sealed class AmbientBackdropScene
{
    internal const int ParticleCount = 2048;
    private const double BoundX = 4.35;
    private const double BoundY = 4.05;
    private const double BoundZ = 1.65;
    private const double HorizontalExtent = BoundX * 0.93;
    private const double VerticalExtent = BoundY * 0.86;
    private const double DepthExtent = BoundZ * 0.84;
    private const double PointerRadius = 0.22;
    private static readonly double[] StreamCenters = [-0.72, -0.48, -0.2, 0.08, 0.35, 0.6, 0.79];

    private readonly StreamSeed[] _streams = new StreamSeed[StreamCenters.Length];
    private readonly ParticleSeed[] _seeds = new ParticleSeed[ParticleCount];
    private readonly AmbientParticle[] _particles = new AmbientParticle[ParticleCount];
    private int _particleCount;

    internal AmbientBackdropScene(uint seed = 0x57495350)
    {
        var streamState = seed ^ 0xd1b54a35;
        var commonPhase = Next(ref streamState) * Math.Tau;
        for (var index = 0; index < _streams.Length; index++)
        {
            _streams[index] = new StreamSeed(
                StreamCenters[index] + (Next(ref streamState) - 0.5) * 0.13,
                0.1 + Next(ref streamState) * 0.13,
                0.72 + Next(ref streamState) * 0.48,
                commonPhase + Next(ref streamState) * 1.7 + index * 0.61,
                0.085 + Next(ref streamState) * 0.055,
                0.12 + Next(ref streamState) * 0.08,
                0.61 + Next(ref streamState) * 0.52,
                commonPhase * 0.73 + Next(ref streamState) * 2.1 + index * 0.47);
        }

        var shaderState = seed;
        var shaderX = Next(ref shaderState) * 97;
        var shaderY = Next(ref shaderState) * 97;
        var particleState = seed ^ 0xa53c9e17;
        for (var index = 0; index < ParticleCount; index++)
        {
            var progress = Math.Pow(Next(ref particleState), 1.78);
            var streamIndex = (int)(Next(ref particleState) * _streams.Length);
            var verticalNoise = Centered(ref particleState) * _streams[streamIndex].Spread;
            var depthNoise = Centered(ref particleState) * 1.02;
            var velocityVariation = Next(ref particleState);
            var life = Math.Clamp(1 - progress * (0.93 + Next(ref particleState) * 0.2), 0.025, 0.995);
            _seeds[index] = new ParticleSeed(
                streamIndex, progress, verticalNoise, depthNoise, life,
                Mix(0.012, 0.026, velocityVariation),
                Hash21(index % 64 + shaderX, index / 64 + shaderY));
            _ = Next(ref particleState);
            _ = Next(ref particleState);
        }
    }

    internal ReadOnlySpan<AmbientParticle> Particles => _particles.AsSpan(0, _particleCount);

    internal void Update(double width, double height, double seconds) =>
        Update(width, height, seconds, default, 0);

    internal void Update(double width, double height, double seconds, AmbientPoint pointer, double pointerActivity)
    {
        _particleCount = 0;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;

        seconds = NormalizeTime(seconds);
        pointerActivity = ValidPointer(pointer) && double.IsFinite(pointerActivity)
            ? Math.Clamp(pointerActivity, 0, 1)
            : 0;

        for (var index = 0; index < ParticleCount; index++)
        {
            var seed = _seeds[index];
            var stream = _streams[seed.StreamIndex];
            var lifetimeProgress = 1 - seed.Life + seconds * seed.LifeRate;
            var age = Fraction(lifetimeProgress);
            var life = 1 - age;
            // The website's mature stream distribution is present immediately.
            // Analytic paths keep CPU work bounded; respawns occur only at the faded life boundary.
            var progress = lifetimeProgress < 1
                ? seed.Progress + seconds * seed.LifeRate
                : age;
            var streamAngle = progress * Math.Tau * stream.Frequency + stream.Phase;
            var depthAngle = progress * Math.Tau * stream.DepthFrequency + stream.DepthPhase;
            var x = -HorizontalExtent + progress * HorizontalExtent * 2;
            var y = Math.Clamp(stream.Center + Math.Sin(streamAngle) * stream.Amplitude + seed.VerticalNoise,
                -0.98, 0.98) * VerticalExtent;
            var z = Math.Clamp(-0.02 + Math.Sin(depthAngle) * stream.DepthAmplitude + seed.DepthNoise,
                -0.98, 0.98) * DepthExtent;
            var particle = Project(x, y, z, life, seed.Variation, width, height);
            var position = particle.Position;
            ApplyPointer(ref position, width, height, pointer, pointerActivity, SmoothStep(-BoundZ, BoundZ, z));
            _particles[index] = particle with { Position = position };
        }
        _particleCount = ParticleCount;
    }

    // Depth, perspective, point size, life envelope, and material match the production website shader.
    internal static AmbientParticle Project(
        double x, double y, double z, double life, double variation, double width, double height)
    {
        var viewDepth = Math.Max(1, 5 - z);
        var depth = SmoothStep(-BoundZ, BoundZ, z);
        var focusDistance = Math.Abs(depth - 0.56);
        var pointSize = (Mix(1.15, 4.4, depth) + Math.Pow(focusDistance, 1.38) * 31) * height / 900;
        var lifeEnvelope = SmoothStep(0, 0.016, 1 - life) * SmoothStep(0, 0.024, life);
        var focusOpacity = Mix(0.115, 0.34, 1 - SmoothStep(0.08, 0.48, focusDistance));
        var areaCompensation = 1 / Math.Sqrt(Math.Max(1, pointSize * 0.20));
        var opacity = lifeEnvelope * focusOpacity * areaCompensation * Mix(0.78, 1.15, variation);
        var softness = Mix(0.82, 0.18, 1 - SmoothStep(0.04, 0.46, focusDistance));
        var colorFocus = 1 - focusDistance;
        return new AmbientParticle(
            new AmbientPoint((x * 1.7320508 / viewDepth + 1) * width / 2,
                (1 - y * 1.7320508 / viewDepth) * height / 2),
            Math.Clamp(pointSize, 1, 42) / 2,
            opacity, softness,
            Mix(0.25, 0.54, colorFocus),
            Mix(0.40, 0.60, colorFocus),
            Mix(0.39, 0.60, colorFocus));
    }

    internal static double NormalizeTime(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 ? seconds : 0;

    private static void ApplyPointer(
        ref AmbientPoint position, double width, double height,
        AmbientPoint pointer, double activity, double depth)
    {
        if (activity <= 0)
            return;
        var minimumDimension = Math.Min(width, height);
        var deltaX = position.X - pointer.X * width;
        var deltaY = position.Y - pointer.Y * height;
        var radius = minimumDimension * PointerRadius;
        var distanceSquared = deltaX * deltaX + deltaY * deltaY;
        if (distanceSquared <= 0.0001 || distanceSquared >= radius * radius)
            return;
        var distance = Math.Sqrt(distanceSquared);
        var influence = SmoothStep(0, 1, 1 - distance / radius) * activity * Mix(0.38, 1, depth);
        var directionX = deltaX / distance;
        var directionY = deltaY / distance;
        position = new AmbientPoint(
            position.X + (directionX * 0.88 - directionY * 0.12) * minimumDimension * 0.010 * influence,
            position.Y + (directionY * 0.88 + directionX * 0.12) * minimumDimension * 0.007 * influence);
    }

    private static bool ValidPointer(AmbientPoint pointer) =>
        double.IsFinite(pointer.X) && double.IsFinite(pointer.Y) &&
        pointer.X >= 0 && pointer.X <= 1 && pointer.Y >= 0 && pointer.Y <= 1;

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Mix(double a, double b, double weight) => a + (b - a) * weight;
    private static double Fraction(double value) => value - Math.Floor(value);

    private static double Next(ref uint state)
    {
        state = unchecked(state + 0x6d2b79f5);
        var value = unchecked((state ^ (state >> 15)) * (state | 1));
        value ^= unchecked(value + ((value ^ (value >> 7)) * (value | 61)));
        return (value ^ (value >> 14)) / 4294967296.0;
    }

    private static double Centered(ref uint state) =>
        Next(ref state) + Next(ref state) + Next(ref state) + Next(ref state) - 2;

    private static double Hash21(double x, double y)
    {
        x = Fraction(x * 123.34);
        y = Fraction(y * 456.21);
        var dot = x * (x + 45.32) + y * (y + 45.32);
        return Fraction((x + dot) * (y + dot));
    }

    private readonly record struct StreamSeed(
        double Center, double Amplitude, double Frequency, double Phase, double Spread,
        double DepthAmplitude, double DepthFrequency, double DepthPhase);

    private readonly record struct ParticleSeed(
        int StreamIndex, double Progress, double VerticalNoise, double DepthNoise,
        double Life, double LifeRate, double Variation);
}
