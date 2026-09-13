using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal readonly record struct DashboardRimSpark(Point Position, double Radius, double Opacity);

internal sealed class DashboardRimScene
{
    internal const int SparkCount = 400;
    private readonly RimSeed[] _seeds = new RimSeed[SparkCount];
    private readonly RimAnchor[] _anchors = new RimAnchor[SparkCount];
    private readonly DashboardRimSpark[] _sparks = new DashboardRimSpark[SparkCount];
    private Geometry? _silhouette;

    internal DashboardRimScene()
    {
        var state = 0x57495350u;
        for (var index = 0; index < _seeds.Length; index++)
            _seeds[index] = new RimSeed(Next(ref state), 0.85 + Next(ref state) * 1.3,
                Next(ref state) * Math.Tau, Next(ref state), Next(ref state) < 0.5 ? -1 : 1);
    }

    internal ReadOnlySpan<DashboardRimSpark> Sparks => _sparks;
    internal int AnchorBuildCount { get; private set; }

    internal void Update(Geometry silhouette, double seconds)
    {
        if (!ReferenceEquals(_silhouette, silhouette))
        {
            var path = PathGeometry.CreateFromGeometry(silhouette);
            for (var index = 0; index < _anchors.Length; index++)
            {
                var fraction = (index + _seeds[index].Phase) / SparkCount;
                path.GetPointAtFractionLength(fraction, out var point, out var tangent);
                var direction = new Vector(tangent.X, tangent.Y);
                if (direction.LengthSquared > 0)
                    direction.Normalize();
                else
                    direction = new Vector(1, 0);
                _anchors[index] = new RimAnchor(point, direction, new Vector(direction.Y, -direction.X));
            }
            _silhouette = silhouette;
            AnchorBuildCount++;
        }
        seconds = double.IsFinite(seconds) && seconds >= 0 ? seconds : 0;
        for (var index = 0; index < _seeds.Length; index++)
        {
            var seed = _seeds[index];
            var anchor = _anchors[index];
            var age = Fraction(seconds / seed.Lifetime + seed.Phase);
            // A short, smooth arrival hides recycling at the rim. Outward speed
            // then decays while each separate point fades, without a trailing shape.
            var arrival = Math.Clamp(age / 0.12, 0, 1);
            var envelope = arrival * arrival * (3 - 2 * arrival) * Math.Pow(1 - age, 1.25);
            var upward = Math.Max(0, -anchor.Normal.Y);
            var emphasis = 0.68 + upward * 0.32;
            var distance = 0.9 + (8 + seed.Energy * 19) * age * (2 - age);
            var drift = seed.Direction * age * (1 + seed.Energy * 2) +
                Math.Sin(age * Math.PI) * Math.Sin(seed.Wave) * 1.2;
            var position = anchor.Position + anchor.Normal * distance + anchor.Tangent * drift;
            _sparks[index] = new DashboardRimSpark(position,
                (0.70 + seed.Energy * 0.65) * (1 - age * 0.15),
                envelope * (0.52 + seed.Energy * 0.42) * emphasis);
        }
    }

    private static double Fraction(double value) => value - Math.Floor(value);

    private static double Next(ref uint state)
    {
        state = unchecked(state * 1664525 + 1013904223);
        return state / 4294967296d;
    }

    private readonly record struct RimSeed(double Phase, double Lifetime, double Wave, double Energy,
        double Direction);
    private readonly record struct RimAnchor(Point Position, Vector Tangent, Vector Normal);
}
