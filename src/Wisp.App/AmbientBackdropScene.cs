namespace Wisp.App;

internal enum AmbientParticleLayer : byte
{
    Far,
    Middle,
    Near
}

internal readonly record struct AmbientPoint(double X, double Y);
internal readonly record struct AmbientParticle(
    AmbientPoint Position,
    double Radius,
    int Shade,
    AmbientParticleLayer Layer);

internal sealed class AmbientBackdropScene
{
    internal const int FarParticleCount = 40;
    internal const int MiddleParticleCount = 72;
    internal const int NearParticleCount = 128;
    internal const int ParticleCount = FarParticleCount + MiddleParticleCount + NearParticleCount;
    internal const int PaletteSize = 32;

    private const double RibbonBaseY = 0.69;
    private const double RibbonSlope = -0.30;
    private const double PrimaryAmplitude = 0.085;
    private const double PrimarySpatialFrequency = 0.83;
    private const double PrimaryPhase = 0.08;
    private const double PrimaryPeriodSeconds = 181;
    private const double SecondaryAmplitude = 0.028;
    private const double SecondarySpatialFrequency = 2.15;
    private const double SecondaryPhase = 0.31;
    private const double SecondaryPeriodSeconds = 137;
    private const double PointerRadius = 0.22;

    private static readonly double[] GroupCenters = [0.08, 0.27, 0.49, 0.72, 0.93];
    private static readonly double[] GroupHalfWidths = [0.085, 0.065, 0.085, 0.070, 0.080];

    private readonly ParticleSeed[] _seeds = new ParticleSeed[ParticleCount];
    private readonly AmbientParticle[] _particles = new AmbientParticle[ParticleCount];
    private int _particleCount;

    internal AmbientBackdropScene(uint seed = 0x57495350)
    {
        var state = seed;
        for (var index = 0; index < ParticleCount; index++)
        {
            var layer = Layer(index);
            var layerIndex = LayerIndex(index, layer);
            var connector = (layerIndex + (int)layer * 2) % 7 == 0;
            var group = (layerIndex * 3 + (int)layer * 2) % GroupCenters.Length;
            var along = connector
                ? 0.03 + Fraction((layerIndex + 0.5) * 0.61803398875 + Next(ref state) * 0.07) * 0.94
                : GroupCenters[group] + Bell3(ref state) * GroupHalfWidths[group];
            var across = Bell3(ref state) * RibbonHalfWidth(layer) * (connector ? 0.72 : 1.0);
            var radius = layer switch
            {
                AmbientParticleLayer.Far => 3.0 + Next(ref state) * 7.5,
                AmbientParticleLayer.Middle => 1.4 + Next(ref state) * 2.8,
                _ => 0.55 + Next(ref state) * 1.65
            };
            var light = layer switch
            {
                AmbientParticleLayer.Far => 0.12 + Next(ref state) * 0.33,
                AmbientParticleLayer.Middle => 0.25 + Next(ref state) * 0.45,
                _ => 0.42 + Next(ref state) * 0.58
            };
            _seeds[index] = new ParticleSeed(
                Math.Clamp(along, 0.015, 0.985),
                across,
                radius,
                light,
                layer,
                Next(ref state) * Math.Tau,
                CreateMotion(ref state));
        }
    }

    internal ReadOnlySpan<AmbientParticle> Particles => _particles.AsSpan(0, _particleCount);

    internal void Update(double width, double height, double seconds) =>
        Update(width, height, seconds, default, 0);

    internal void Update(
        double width,
        double height,
        double seconds,
        AmbientPoint pointer,
        double pointerActivity)
    {
        _particleCount = 0;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }

        seconds = NormalizeTime(seconds);
        var minimumDimension = Math.Min(width, height);
        var scale = Math.Clamp(minimumDimension / 720, 0.72, 1.35);
        pointerActivity = ValidPointer(pointer)
            ? Math.Clamp(double.IsFinite(pointerActivity) ? pointerActivity : 0, 0, 1)
            : 0;

        for (var index = 0; index < ParticleCount; index++)
        {
            var seed = _seeds[index];
            var parallax = LayerCoupling(seed.Layer);
            var alongNoise = Noise(
                seconds / seed.Motion.XSeconds + seed.Motion.Offset,
                seed.Motion.Key);
            var alongOscillation = Math.Sin(
                seconds / seed.Motion.WaveSeconds * Math.Tau + seed.Phase);
            var along = Math.Clamp(
                seed.Along + parallax * (alongNoise * 0.010 + alongOscillation * 0.004),
                0.012,
                0.988);
            var center = RibbonCenter(along, seconds);
            var derivative = RibbonDerivative(along, seconds);
            var tangentX = width;
            var tangentY = derivative * height;
            var tangentLength = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
            var normalX = -tangentY / tangentLength;
            var normalY = tangentX / tangentLength;
            var acrossNoise = Noise(
                seconds / seed.Motion.YSeconds + seed.Motion.Offset,
                seed.Motion.Key ^ 0x9E3779B9);
            var acrossWave = Math.Sin(
                seconds / (seed.Motion.WaveSeconds * 1.23) * Math.Tau + seed.Phase * 0.63);
            var across = seed.Across + parallax * (acrossNoise * 0.008 + acrossWave * 0.003);
            var offset = across * minimumDimension;
            var x = width * along + normalX * offset;
            var y = height * center + normalY * offset;
            ApplyPointer(ref x, ref y, width, height, pointer, pointerActivity, seed.Layer);
            var shimmer = 0.88 + 0.12 * Noise(
                seconds / 29 + seed.Motion.Offset,
                seed.Motion.Key ^ 0x68E31DA4);
            _particles[index] = new AmbientParticle(
                new AmbientPoint(x, y),
                seed.Radius * scale,
                Shade(seed.Light * shimmer),
                seed.Layer);
        }
        _particleCount = ParticleCount;
    }

    internal static double RibbonCenter(double along, double seconds)
    {
        along = Math.Clamp(double.IsFinite(along) ? along : 0.5, 0, 1);
        seconds = NormalizeTime(seconds);
        var primary = Math.Tau *
            (along * PrimarySpatialFrequency + PrimaryPhase + seconds / PrimaryPeriodSeconds);
        var secondary = Math.Tau *
            (along * SecondarySpatialFrequency + SecondaryPhase - seconds / SecondaryPeriodSeconds);
        return RibbonBaseY + RibbonSlope * along +
            PrimaryAmplitude * Math.Sin(primary) +
            SecondaryAmplitude * Math.Sin(secondary);
    }

    // Independent long-period paths avoid a short synchronized scene reset.
    internal static double NormalizeTime(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 ? seconds : 0;

    private static double RibbonDerivative(double along, double seconds)
    {
        var primary = Math.Tau *
            (along * PrimarySpatialFrequency + PrimaryPhase + seconds / PrimaryPeriodSeconds);
        var secondary = Math.Tau *
            (along * SecondarySpatialFrequency + SecondaryPhase - seconds / SecondaryPeriodSeconds);
        return RibbonSlope +
            PrimaryAmplitude * Math.Tau * PrimarySpatialFrequency * Math.Cos(primary) +
            SecondaryAmplitude * Math.Tau * SecondarySpatialFrequency * Math.Cos(secondary);
    }

    private static void ApplyPointer(
        ref double x,
        ref double y,
        double width,
        double height,
        AmbientPoint pointer,
        double activity,
        AmbientParticleLayer layer)
    {
        if (activity <= 0)
            return;
        var minimumDimension = Math.Min(width, height);
        var deltaX = x - pointer.X * width;
        var deltaY = y - pointer.Y * height;
        var radius = minimumDimension * PointerRadius;
        var distanceSquared = deltaX * deltaX + deltaY * deltaY;
        if (distanceSquared <= 0.0001 || distanceSquared >= radius * radius)
            return;
        var distance = Math.Sqrt(distanceSquared);
        var influence = 1 - distance / radius;
        influence = influence * influence * (3 - 2 * influence) * activity * LayerCoupling(layer);
        var directionX = deltaX / distance;
        var directionY = deltaY / distance;
        var tangentX = -directionY;
        var tangentY = directionX;
        x += (directionX * 0.88 + tangentX * 0.12) * minimumDimension * 0.010 * influence;
        y += (directionY * 0.88 + tangentY * 0.12) * minimumDimension * 0.007 * influence;
    }

    private static bool ValidPointer(AmbientPoint pointer) =>
        double.IsFinite(pointer.X) && double.IsFinite(pointer.Y) &&
        pointer.X >= 0 && pointer.X <= 1 && pointer.Y >= 0 && pointer.Y <= 1;

    private static double LayerCoupling(AmbientParticleLayer layer) => layer switch
    {
        AmbientParticleLayer.Far => 0.35,
        AmbientParticleLayer.Middle => 0.65,
        _ => 1.0
    };

    private static double RibbonHalfWidth(AmbientParticleLayer layer) => layer switch
    {
        AmbientParticleLayer.Far => 0.18,
        AmbientParticleLayer.Middle => 0.105,
        _ => 0.055
    };

    private static AmbientParticleLayer Layer(int index) => index switch
    {
        < FarParticleCount => AmbientParticleLayer.Far,
        < FarParticleCount + MiddleParticleCount => AmbientParticleLayer.Middle,
        _ => AmbientParticleLayer.Near
    };

    private static int LayerIndex(int index, AmbientParticleLayer layer) => layer switch
    {
        AmbientParticleLayer.Far => index,
        AmbientParticleLayer.Middle => index - FarParticleCount,
        _ => index - FarParticleCount - MiddleParticleCount
    };

    private static MotionSeed CreateMotion(ref uint state) => new(
        Hash(state),
        48 + Next(ref state) * 68,
        56 + Next(ref state) * 82,
        62 + Next(ref state) * 96,
        Next(ref state) * 80);

    private static double Noise(double coordinate, uint key)
    {
        var cell = Math.Floor(coordinate);
        var blend = coordinate - cell;
        blend = blend * blend * blend * (blend * (blend * 6 - 15) + 10);
        var a = NoiseSample(cell, key);
        return a + (NoiseSample(cell + 1, key) - a) * blend;
    }

    private static double NoiseSample(double cell, uint key)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(cell));
        return Hash(key ^ (uint)bits ^ (uint)(bits >> 32)) / (double)uint.MaxValue * 2 - 1;
    }

    private static uint Hash(uint value)
    {
        value = unchecked((value ^ (value >> 16)) * 0x7FEB352D);
        value = unchecked((value ^ (value >> 15)) * 0x846CA68B);
        return value ^ (value >> 16);
    }

    private static double Next(ref uint state)
    {
        state = unchecked(state * 1664525 + 1013904223);
        return state / (double)uint.MaxValue;
    }

    private static double Bell3(ref uint state) =>
        ((Next(ref state) + Next(ref state) + Next(ref state)) / 3 - 0.5) * 2;

    private static double Fraction(double value) => value - Math.Floor(value);
    private static int Shade(double light) =>
        (int)Math.Round(Math.Clamp(light, 0, 1) * (PaletteSize - 1));

    private readonly record struct MotionSeed(
        uint Key,
        double XSeconds,
        double YSeconds,
        double WaveSeconds,
        double Offset);

    private readonly record struct ParticleSeed(
        double Along,
        double Across,
        double Radius,
        double Light,
        AmbientParticleLayer Layer,
        double Phase,
        MotionSeed Motion);
}
