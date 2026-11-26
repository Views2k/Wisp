using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal sealed class AmbientSnowfall
{
    private const int MaximumFlakes = 84;
    internal const int Shapes = 5;
    private const int Depths = 3;
    internal const int RotationSteps = 40;
    private static readonly Rect ArtBounds = new(-16, -16, 32, 32);
    private static readonly StreamGeometry[] Crystals = CreateCrystals();
    private static readonly RotateTransform[] Rotations = CreateRotations();
    private readonly Flake[] _flakes = new Flake[MaximumFlakes];
    private readonly DrawingImage?[,,] _images = new DrawingImage?[Shapes, Depths, RotationSteps];
    private readonly Pen?[] _pens = new Pen?[Depths];
    private Color _color;

    internal AmbientSnowfall(uint seed = 0x534e4f57)
    {
        var random = new Random(unchecked((int)seed));
        for (var i = 0; i < _flakes.Length; i++)
        {
            var depth = random.NextDouble() switch { < 0.40 => 0, < 0.77 => 1, _ => 2 };
            var depthSize = 4.5 + depth * 4 + random.NextDouble() * (4 + depth);
            var spin = (5 + random.NextDouble() * 18) * (random.Next(2) == 0 ? -1 : 1);
            _flakes[i] = new Flake((uint)random.Next(), (i % 4 + random.NextDouble()) / 4,
                0.55 + random.NextDouble() * 0.40, depthSize * 0.43, depth, random.Next(Shapes),
                12 + depthSize * 1.8 + random.NextDouble() * 15, 1 + random.NextDouble() * 12,
                5 + random.NextDouble() * 16, 0.32 + random.NextDouble() * 0.69,
                random.NextDouble() * Math.Tau, random.NextDouble() * 60, spin);
        }
    }

    internal int Draw(DrawingContext drawing, Size viewport, double seconds, Color color, double intensity,
        AmbientPoint pointer = default, double pointerActivity = 0)
    {
        if (!double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0 || !double.IsFinite(intensity) || intensity <= 0)
            return 0;

        color.A = (byte)Math.Round(color.A * Math.Min(intensity, 1));
        if (color.A == 0)
            return 0;
        if (_color != color)
        {
            Array.Clear(_images);
            Array.Clear(_pens);
            _color = color;
        }

        seconds = AmbientBackdropScene.NormalizeTime(seconds);
        pointerActivity = pointer is { X: >= 0 and <= 1, Y: >= 0 and <= 1 } && double.IsFinite(pointerActivity)
            ? Math.Clamp(pointerActivity, 0, 1) : 0;
        var count = (int)Math.Clamp(Math.Round(viewport.Width * viewport.Height / 17500d), 7, MaximumFlakes);
        var drawn = 0;
        for (var i = 0; i < count; i++)
        {
            var flake = _flakes[i];
            var diameter = 2 * flake.Radius;
            var width = viewport.Width + diameter;
            var period = viewport.Height * flake.CycleLength / flake.Fall;
            var cycles = flake.Offset + seconds / period;
            var cycle = Math.Floor(cycles);
            var emission = Emission(flake.Seed, (uint)cycle);
            var age = (cycles - cycle) / (0.64 + (emission >> 24) * (0.20 / 255));
            if (age <= 0 || age >= 1)
                continue;

            // Between emissions the crystal is invisible; each new path starts gently in the upper sky.
            var startY = ((emission >> 8) & 255) * (0.37 / 255);
            var distance = ((emission >> 16) & 255) / 255d;
            var endY = (emission >> 24) < 50 ? 1.04 + distance * 0.08 : startY + 0.22 + distance * 0.34;
            var fade = SmoothStep(Math.Min(age / 0.14, (1 - age) / (0.27 + distance * 0.09)));
            var wave = Math.Sin(flake.Phase + seconds * flake.SwayRate);
            var elapsed = (cycles - cycle) * period;
            var x = Wrap((emission & 255) * (width / 256) + elapsed * flake.Wind + wave * flake.Sway, width) - flake.Radius;
            var y = (startY + age * (endY - startY)) * viewport.Height;
            var position = new AmbientPoint(x, y);
            AmbientBackdropScene.ApplyPointer(ref position, viewport.Width, viewport.Height,
                pointer, pointerActivity, flake.Depth / 2d);
            var bounds = new Rect(position.X - flake.Radius, position.Y - flake.Radius, diameter, diameter);
            if (bounds.Right <= 0 || bounds.Bottom <= 0 || bounds.Left >= viewport.Width || bounds.Top >= viewport.Height)
                continue;

            var angle = Wrap(flake.Angle + seconds * flake.Spin + wave * 4, 60);
            var orientation = (int)Math.Round(angle * RotationSteps / 60) % RotationSteps;
            var image = _images[flake.Shape, flake.Depth, orientation] ??= CreateImage(flake.Shape, flake.Depth, orientation);
            if (fade < 1) drawing.PushOpacity(fade);
            drawing.DrawImage(image, bounds);
            if (fade < 1) drawing.Pop();
            drawn++;
        }
        return drawn;
    }

    private DrawingImage CreateImage(int shape, int depth, int angle)
    {
        var pen = _pens[depth];
        if (pen is null)
        {
            var color = _color;
            color.A = (byte)Math.Round(color.A * (depth switch { 0 => 0.50, 1 => 0.73, _ => 0.93 }));
            pen = new Pen(new SolidColorBrush(color), depth switch { 0 => 3.2, 1 => 2.5, _ => 1.95 })
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            _pens[depth] = pen;
        }
        return CreateCrystalImage(shape, angle, pen);
    }

    internal static DrawingImage CreateCrystalImage(int shape, int angle, Pen pen)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            // Fixed transparent bounds keep each crystal centered as it turns.
            context.DrawRectangle(Brushes.Transparent, null, ArtBounds);
            context.PushTransform(Rotations[angle]);
            context.DrawGeometry(null, pen, Crystals[shape]);
            context.Pop();
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static StreamGeometry[] CreateCrystals()
    {
        var crystals = new StreamGeometry[Shapes];
        for (var shape = 0; shape < crystals.Length; shape++)
        {
            var crystal = new StreamGeometry();
            using (var path = crystal.Open())
            {
                var core = shape switch { 1 => 3.7, 3 => 4.6, _ => 2.2 };
                for (var arm = 0; arm < 6; arm++)
                {
                    var (sin, cos) = Math.SinCos(arm * Math.Tau / 6);
                    Point PointAt(double along, double across = 0) =>
                        new(sin * along + cos * across, -cos * along + sin * across);
                    void Line(Point start, Point end)
                    {
                        path.BeginFigure(start, false, false);
                        path.LineTo(end, true, false);
                    }
                    void Fork(double start, double length)
                    {
                        for (var side = -1; side <= 1; side += 2)
                            Line(PointAt(start), PointAt(start + length * 0.5, side * length * 0.8660254));
                    }

                    Line(PointAt(core), PointAt(core * 0.5, core * 0.8660254));
                    Line(PointAt(core), PointAt(14.5));
                    switch (shape)
                    {
                        case 0: // Fern with secondary barbs on the longest forks.
                            Fork(5.3, 5.9);
                            Fork(8.8, 4.9);
                            Fork(11.7, 2.8);
                            for (var side = -1; side <= 1; side += 2)
                                Line(PointAt(7.2, side * 3.29), PointAt(9.3, side * 3.29));
                            break;
                        case 1: // Hollow pointed plates.
                            Fork(6, 3.7);
                            for (var side = -1; side <= 1; side += 2)
                            {
                                Line(PointAt(8), PointAt(10.4, side * 2.4));
                                Line(PointAt(10.4, side * 2.4), PointAt(14.5));
                            }
                            break;
                        case 2: // Long open needles.
                            Fork(6.2, 6.9);
                            Fork(10.3, 4.1);
                            break;
                        case 3: // Broad hexagonal center and stepped branches.
                            Line(PointAt(2.3), PointAt(1.15, 1.992));
                            Fork(7.2, 5);
                            Fork(9.9, 3.9);
                            Fork(12.2, 2.4);
                            break;
                        default: // Short paired forks around a long central spine.
                            Fork(4.6, 3.1);
                            Fork(7.8, 5.7);
                            Fork(11.1, 3.4);
                            break;
                    }
                }
            }
            crystal.Freeze();
            crystals[shape] = crystal;
        }
        return crystals;
    }

    private static RotateTransform[] CreateRotations()
    {
        var rotations = new RotateTransform[RotationSteps];
        for (var i = 0; i < rotations.Length; i++)
        {
            // The artwork repeats every 60 degrees. A step moves the largest tip less than half a DIP.
            rotations[i] = new RotateTransform(i * 60d / RotationSteps);
            rotations[i].Freeze();
        }
        return rotations;
    }

    private static double Wrap(double value, double range) => value - range * Math.Floor(value / range);

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static uint Emission(uint seed, uint cycle)
    {
        // Mix the cycle into the seed so a paused or skipped frame always samples the same emission.
        var value = unchecked(seed + cycle * 0x9e3779b9u);
        value = unchecked((value ^ (value >> 16)) * 0x21f0aaadu);
        value = unchecked((value ^ (value >> 15)) * 0x735a2d97u);
        return value ^ (value >> 15);
    }

    private readonly record struct Flake(uint Seed, double Offset, double CycleLength, double Radius, int Depth, int Shape,
        double Fall, double Wind, double Sway, double SwayRate, double Phase, double Angle, double Spin);
}
