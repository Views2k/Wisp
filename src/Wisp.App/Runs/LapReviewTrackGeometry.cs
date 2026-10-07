using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

internal readonly record struct LapReviewTrackBounds(Point3D Center, double Span, double MinimumHeight)
{
    internal Point3D Normalize(LapPosition position) => new((position.X - Center.X) / Span,
        (position.Y - Center.Y) / Span, -(position.Z - Center.Z) / Span);

    internal static bool IsFinite(LapPosition position) => float.IsFinite(position.X) &&
        float.IsFinite(position.Y) && float.IsFinite(position.Z);

    internal static LapReviewTrackBounds From(LapReviewPlotData data, LapReviewLap? extraLap = null)
    {
        var minimum = new Point3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Point3D(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        void Include(LapReviewPoint[] points)
        {
            foreach (var point in points)
            {
                var p = point.Position;
                if (!IsFinite(p)) continue;
                minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
                maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
            }
        }
        if (data.Lap is { } lap) Include(lap.Points);
        if (data.HasDistinctReference && data.Reference is { } reference) Include(reference.Points);
        if (extraLap is not null) Include(extraLap.Points);
        if (!double.IsFinite(minimum.X)) return new(new(), 1, 0);
        return new(new((minimum.X + maximum.X) / 2, (minimum.Y + maximum.Y) / 2, (minimum.Z + maximum.Z) / 2),
            Math.Max(1, Math.Max(maximum.X - minimum.X, Math.Max(maximum.Y - minimum.Y, maximum.Z - minimum.Z))), minimum.Y);
    }
}

internal readonly record struct LapReviewCameraFrame(Vector3D Right, Vector3D Up, Vector3D Forward)
{
    internal static LapReviewCameraFrame From(double yaw, double pitch, double roll)
    {
        var y = yaw * Math.PI / 180; var p = pitch * Math.PI / 180; var r = roll * Math.PI / 180;
        var forward = new Vector3D(-Math.Sin(y) * Math.Cos(p), -Math.Sin(p), -Math.Cos(y) * Math.Cos(p));
        var right = new Vector3D(Math.Cos(y), 0, -Math.Sin(y));
        var up = Vector3D.CrossProduct(right, forward);
        return new(right * Math.Cos(r) + up * Math.Sin(r), up * Math.Cos(r) - right * Math.Sin(r), forward);
    }

    internal Point Project(Point3D position, Point3D target, double width, Size viewport)
    {
        var offset = position - target;
        var scale = viewport.Width / width;
        return new(viewport.Width / 2 + Vector3D.DotProduct(offset, Right) * scale,
            viewport.Height / 2 - Vector3D.DotProduct(offset, Up) * scale);
    }
}

internal readonly record struct LapReviewTrackFit(Point3D Target, double HorizontalSpan, double VerticalSpan, double Yaw)
{
    internal const double DefaultPitch = 22;
    internal double Width(double aspect) => Math.Max(.03, Math.Max(HorizontalSpan, VerticalSpan * Math.Max(.01, aspect))) * 1.12;
    internal static LapReviewTrackFit From(LapReviewPlotData data, LapReviewTrackBounds bounds, double? yawOverride = null)
    {
        var yaw = yawOverride ?? PrincipalYaw(data.Lap?.Points ?? [], bounds);
        var frame = LapReviewCameraFrame.From(yaw, DefaultPitch, 0);
        var left = double.PositiveInfinity; var right = double.NegativeInfinity;
        var bottom = double.PositiveInfinity; var top = double.NegativeInfinity;
        void IncludePosition(Point3D p)
        {
            var vector = p - new Point3D();
            var horizontal = Vector3D.DotProduct(vector, frame.Right); var vertical = Vector3D.DotProduct(vector, frame.Up);
            left = Math.Min(left, horizontal); right = Math.Max(right, horizontal);
            bottom = Math.Min(bottom, vertical); top = Math.Max(top, vertical);
        }
        var floor = (bounds.MinimumHeight - bounds.Center.Y) / bounds.Span - .025;
        void Include(LapReviewPoint[] points, bool shadow)
        {
            foreach (var point in points)
            {
                if (!LapReviewTrackBounds.IsFinite(point.Position)) continue;
                var p = bounds.Normalize(point.Position); IncludePosition(p);
                if (shadow) IncludePosition(new(p.X, floor, p.Z));
            }
        }
        if (data.Lap is { } lap) Include(lap.Points, true);
        if (data.HasDistinctReference && data.Reference is { } reference) Include(reference.Points, false);
        if (!double.IsFinite(left)) return new(new(), 1, 1, yaw);
        return new(new Point3D() + frame.Right * ((left + right) / 2) + frame.Up * ((bottom + top) / 2),
            right - left, top - bottom, yaw);
    }

    private static double PrincipalYaw(LapReviewPoint[] points, LapReviewTrackBounds bounds)
    {
        double total = 0, meanX = 0, meanZ = 0, xx = 0, xz = 0, zz = 0;
        for (var i = 1; i < points.Length; i++)
        {
            if (points[i].BreakBefore || !LapReviewTrackBounds.IsFinite(points[i - 1].Position) || !LapReviewTrackBounds.IsFinite(points[i].Position)) continue;
            var a = bounds.Normalize(points[i - 1].Position); var b = bounds.Normalize(points[i].Position);
            var weight = (b - a).Length;
            if (weight < 1e-9) continue;
            var x = (a.X + b.X) / 2; var z = (a.Z + b.Z) / 2;
            var dx = x - meanX; var dz = z - meanZ; var next = total + weight;
            meanX += dx * weight / next; meanZ += dz * weight / next;
            xx += weight * dx * (x - meanX); xz += weight * dx * (z - meanZ); zz += weight * dz * (z - meanZ);
            // Include each segment's spread so even a two-point path has an axis.
            xx += weight * Math.Pow(b.X - a.X, 2) / 12;
            xz += weight * (b.X - a.X) * (b.Z - a.Z) / 12;
            zz += weight * Math.Pow(b.Z - a.Z, 2) / 12;
            total = next;
        }
        if (xx + zz < 1e-12 || Math.Sqrt(Math.Pow(xx - zz, 2) + 4 * xz * xz) < (xx + zz) * .08) return 0;
        return -Math.Atan2(2 * xz, xx - zz) * 90 / Math.PI;
    }
}

internal sealed record LapReviewTrackScene(Model3DGroup Model, LapReviewTrackBounds Bounds, LapReviewTrackFit Fit, int SegmentCount);

internal sealed record LapReviewTrackArrangement(Vector3D PrimaryOffset, Vector3D ReferenceOffset,
    LapReviewTrackFit Overview, LapReviewTrackFit PrimaryFit, LapReviewTrackFit? ReferenceFit,
    Point3D[] PrimaryCorners, Point3D[] ReferenceCorners)
{
    internal static LapReviewTrackArrangement Create(LapReviewPlotData primary, LapReviewPlotData? reference, LapReviewTrackBounds bounds)
    {
        var overview = LapReviewTrackFit.From(primary, bounds);
        if (reference?.Lap is null) return new(new(), new(), overview, overview, null, [], []);
        var first = LapReviewTrackFit.From(primary with { Reference = null, Comparison = null }, bounds, overview.Yaw);
        var second = LapReviewTrackFit.From(reference with { Reference = null, Comparison = null }, bounds, overview.Yaw);
        var frame = LapReviewCameraFrame.From(overview.Yaw, LapReviewTrackFit.DefaultPitch, 0);
        double Horizontal(Point3D point) => Vector3D.DotProduct(point - new Point3D(), frame.Right);
        double Vertical(Point3D point) => Vector3D.DotProduct(point - new Point3D(), frame.Up);
        var center = Horizontal(overview.Target);
        var gap = Math.Max(.06, Math.Max(first.HorizontalSpan, second.HorizontalSpan) * .14);
        var offsetA = frame.Right * (center - gap / 2 - first.HorizontalSpan / 2 - Horizontal(first.Target));
        var offsetB = frame.Right * (center + gap / 2 + second.HorizontalSpan / 2 - Horizontal(second.Target));
        first = first with { Target = first.Target + offsetA };
        second = second with { Target = second.Target + offsetB };
        var left = Horizontal(first.Target) - first.HorizontalSpan / 2;
        var right = Horizontal(second.Target) + second.HorizontalSpan / 2;
        var bottom = Math.Min(Vertical(first.Target) - first.VerticalSpan / 2, Vertical(second.Target) - second.VerticalSpan / 2);
        var top = Math.Max(Vertical(first.Target) + first.VerticalSpan / 2, Vertical(second.Target) + second.VerticalSpan / 2);
        overview = new(new Point3D() + frame.Right * ((left + right) / 2) + frame.Up * ((bottom + top) / 2), right - left, top - bottom, overview.Yaw);
        return new(offsetA, offsetB, overview, first, second,
            Corners(primary.Lap!, bounds, offsetA), Corners(reference.Lap, bounds, offsetB));
    }

    internal LapReviewTrackFit ProjectedFit(double yaw, double pitch, double roll, int lap = 0)
    {
        if (ReferenceCorners.Length == 0) return Overview;
        var frame = LapReviewCameraFrame.From(yaw, pitch, roll);
        var left = double.PositiveInfinity; var right = double.NegativeInfinity;
        var bottom = double.PositiveInfinity; var top = double.NegativeInfinity;
        void Include(Point3D[] points)
        {
            foreach (var point in points)
            {
                var vector = point - new Point3D();
                var x = Vector3D.DotProduct(vector, frame.Right); var y = Vector3D.DotProduct(vector, frame.Up);
                left = Math.Min(left, x); right = Math.Max(right, x); bottom = Math.Min(bottom, y); top = Math.Max(top, y);
            }
        }
        if (lap != 2) Include(PrimaryCorners);
        if (lap != 1) Include(ReferenceCorners);
        return new(new Point3D() + frame.Right * ((left + right) / 2) + frame.Up * ((bottom + top) / 2), right - left, top - bottom, yaw);
    }

    private static Point3D[] Corners(LapReviewLap lap, LapReviewTrackBounds bounds, Vector3D offset)
    {
        var low = new Point3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var high = new Point3D(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var sample in lap.Points)
        {
            if (!LapReviewTrackBounds.IsFinite(sample.Position)) continue;
            var point = bounds.Normalize(sample.Position) + offset;
            low = new(Math.Min(low.X, point.X), Math.Min(low.Y, point.Y), Math.Min(low.Z, point.Z));
            high = new(Math.Max(high.X, point.X), Math.Max(high.Y, point.Y), Math.Max(high.Z, point.Z));
        }
        if (!double.IsFinite(low.X)) return [new Point3D() + offset];
        low.Y = Math.Min(low.Y, (bounds.MinimumHeight - bounds.Center.Y) / bounds.Span - .025 + offset.Y);
        return [new(low.X, low.Y, low.Z), new(low.X, low.Y, high.Z), new(low.X, high.Y, low.Z), new(low.X, high.Y, high.Z),
            new(high.X, low.Y, low.Z), new(high.X, low.Y, high.Z), new(high.X, high.Y, low.Z), new(high.X, high.Y, high.Z)];
    }
}

internal static class LapReviewTrackGeometry
{
    // At most six recorded samples per bucket, including height and channel extrema. The
    // full-resolution source remains available for cursor selection and analysis.
    internal const int MaximumBuckets = 1000;
    internal const int MaximumPathPoints = MaximumBuckets * 6;

    internal static int[] RetainedIndices(LapReviewPoint[] points, Func<int, double?>? values = null, CancellationToken cancellationToken = default)
    {
        if (points.Length <= MaximumPathPoints) return Enumerable.Range(0, points.Length).ToArray();
        var stride = (int)Math.Ceiling(points.Length / (double)MaximumBuckets);
        var kept = new List<int>(MaximumPathPoints);
        for (var first = 0; first < points.Length; first += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var last = Math.Min(points.Length - 1, first + stride - 1);
            var low = first; var high = first;
            var valueLow = first; var valueHigh = first;
            var minimum = double.PositiveInfinity; var maximum = double.NegativeInfinity;
            for (var i = first; i <= last; i++)
            {
                if (!LapReviewTrackBounds.IsFinite(points[i].Position)) continue;
                if (!LapReviewTrackBounds.IsFinite(points[low].Position) || points[i].Position.Y < points[low].Position.Y) low = i;
                if (!LapReviewTrackBounds.IsFinite(points[high].Position) || points[i].Position.Y > points[high].Position.Y) high = i;
                if (values?.Invoke(i) is { } value && double.IsFinite(value))
                {
                    if (value < minimum) { valueLow = i; minimum = value; }
                    if (value > maximum) { valueHigh = i; maximum = value; }
                }
            }
            foreach (var index in new[] { first, low, high, valueLow, valueHigh, last }.Distinct().Order()) kept.Add(index);
        }
        return kept.ToArray();
    }

    internal static IEnumerable<(int From, int To)> Segments(LapReviewPoint[] points, int[] retained)
    {
        for (var i = 1; i < retained.Length; i++)
        {
            var from = retained[i - 1]; var to = retained[i];
            var valid = LapReviewTrackBounds.IsFinite(points[from].Position);
            for (var j = from + 1; valid && j <= to; j++)
                valid = !points[j].BreakBefore && LapReviewTrackBounds.IsFinite(points[j].Position);
            if (valid) yield return (from, to);
        }
    }

    internal static LapReviewTrackScene Build(LapReviewPlotData data, CancellationToken cancellationToken)
    {
        var bounds = LapReviewTrackBounds.From(data);
        return Build(data, bounds, cancellationToken);
    }

    internal static LapReviewTrackScene Build(LapReviewPlotData data, LapReviewTrackBounds bounds, CancellationToken cancellationToken)
    {
        var fit = LapReviewTrackFit.From(data, bounds);
        var model = new Model3DGroup();
        if (data.Lap is not { Points.Length: > 0 } lap) { model.Freeze(); return new(model, bounds, fit, 0); }
        var floor = (bounds.MinimumHeight - bounds.Center.Y) / bounds.Span - .025;
        var colors = Enumerable.Range(0, 65).Select(_ => new MeshBuilder()).ToArray();
        var shadow = new MeshBuilder(); var posts = new MeshBuilder(); var referenceMesh = new MeshBuilder();
        var points = lap.Points; var retained = RetainedIndices(points, i => LapReviewPlot.Value(points[i], data, i), cancellationToken);
        var range = LapReviewColorRange.From(data);
        var segments = Segments(points, retained).ToArray();
        var frames = Frames(points, segments, bounds);
        var pathLength = segments.Sum(segment => (bounds.Normalize(points[segment.To].Position) - bounds.Normalize(points[segment.From].Position)).Length);
        var postSpacing = Math.Max(.0001, pathLength / 100);
        var nextPost = postSpacing / 2; var traveled = 0d;
        var segmentCount = 0;
        foreach (var (from, to) in segments)
        {
            if ((segmentCount++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = bounds.Normalize(points[from].Position); var b = bounds.Normalize(points[to].Position);
            var value = LapReviewPlot.Value(points[to], data, to);
            var color = value is { } finite && double.IsFinite(finite) ? Math.Clamp((int)Math.Round(range.Fraction(finite) * 63), 0, 63) : 64;
            var selected = to >= data.SectionStart && from <= data.SectionEnd;
            colors[color].Tube(a, b, selected ? .0034 : .0024, frames[from].Direction, frames[to].Direction,
                frames[from].Connections == 1, frames[to].Connections == 1);
            shadow.Tube(new(a.X, floor, a.Z), new(b.X, floor, b.Z), .0017);
            var length = (b - a).Length;
            while (nextPost < traveled + length)
            {
                var point = a + (b - a) * ((nextPost - traveled) / length);
                posts.Tube(new(point.X, floor, point.Z), point, .00038);
                nextPost += postSpacing;
            }
            traveled += length;
        }
        Add(model, shadow, Brush(26, 32, 42));
        Add(model, posts, Brush(40, 48, 59));
        if (data.ShowReferencePath && data.HasDistinctReference && data.Reference is { } reference)
        {
            var referenceCount = 0;
            var referenceSegments = Segments(reference.Points, RetainedIndices(reference.Points, cancellationToken: cancellationToken)).ToArray();
            var referenceFrames = Frames(reference.Points, referenceSegments, bounds);
            foreach (var (from, to) in referenceSegments)
            {
                if ((referenceCount++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                referenceMesh.Tube(bounds.Normalize(reference.Points[from].Position), bounds.Normalize(reference.Points[to].Position), .0016,
                    referenceFrames[from].Direction, referenceFrames[to].Direction, referenceFrames[from].Connections == 1, referenceFrames[to].Connections == 1);
            }
            Add(model, referenceMesh, RunComparisonColors.RunB);
        }
        for (var i = 0; i < colors.Length; i++) Add(model, colors[i], i == 64 ? Brush(139, 146, 159) : LapReviewPalette.GetBrush(i / 63d));
        cancellationToken.ThrowIfCancellationRequested();
        model.Freeze();
        return new(model, bounds, fit, segmentCount);
    }

    private readonly record struct PathFrame(Vector3D Direction, int Connections);
    private static Dictionary<int, PathFrame> Frames(LapReviewPoint[] points, (int From, int To)[] segments, LapReviewTrackBounds bounds)
    {
        var frames = new Dictionary<int, PathFrame>();
        foreach (var (from, to) in segments)
        {
            var direction = bounds.Normalize(points[to].Position) - bounds.Normalize(points[from].Position);
            if (direction.LengthSquared > 1e-18) direction.Normalize();
            foreach (var index in new[] { from, to })
            {
                frames.TryGetValue(index, out var previous);
                frames[index] = new(previous.Direction + direction, previous.Connections + 1);
            }
        }
        return frames;
    }

    private static Brush Brush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static void Add(Model3DGroup group, MeshBuilder builder, Brush brush)
    {
        if (builder.Count == 0) return;
        // Emissive materials add overlapping tube faces without writing depth.
        // Opaque diffuse + the viewport's white ambient light keeps palette colors exact.
        var material = new DiffuseMaterial(brush); material.Freeze();
        var model = new GeometryModel3D(builder.Finish(), material) { BackMaterial = material }; model.Freeze();
        group.Children.Add(model);
    }

    private sealed class MeshBuilder
    {
        private readonly List<Point3D> _positions = [];
        private readonly List<int> _triangles = [];
        internal int Count => _positions.Count;
        internal void Tube(Point3D a, Point3D b, double radius, Vector3D? startDirection = null,
            Vector3D? endDirection = null, bool capStart = false, bool capEnd = false)
        {
            var direction = b - a;
            if (direction.LengthSquared < 1e-18) return;
            direction.Normalize();
            (Vector3D U, Vector3D V) Basis(Vector3D? tangent)
            {
                var axis = tangent is { LengthSquared: > 1e-18 } given ? given : direction;
                axis.Normalize();
                var u = Vector3D.CrossProduct(axis, Math.Abs(axis.Y) < .9 ? new(0, 1, 0) : new(1, 0, 0));
                u.Normalize(); u *= radius;
                return (u, Vector3D.CrossProduct(axis, u));
            }
            var firstBasis = Basis(startDirection); var lastBasis = Basis(endDirection);
            var offset = _positions.Count;
            const int sides = 8;
            for (var side = 0; side < sides; side++)
            {
                var angle = side * Math.PI * 2 / sides;
                _positions.Add(a + firstBasis.U * Math.Cos(angle) + firstBasis.V * Math.Sin(angle));
                _positions.Add(b + lastBasis.U * Math.Cos(angle) + lastBasis.V * Math.Sin(angle));
            }
            for (var side = 0; side < sides; side++)
            {
                var first = offset + side * 2; var next = offset + (side + 1) % sides * 2;
                _triangles.AddRange([first, next, first + 1, first + 1, next, next + 1]);
            }
            if (capStart)
            {
                var center = _positions.Count; _positions.Add(a);
                for (var side = 0; side < sides; side++) _triangles.AddRange([center, offset + (side + 1) % sides * 2, offset + side * 2]);
            }
            if (capEnd)
            {
                var center = _positions.Count; _positions.Add(b);
                for (var side = 0; side < sides; side++) _triangles.AddRange([center, offset + side * 2 + 1, offset + (side + 1) % sides * 2 + 1]);
            }
        }
        internal MeshGeometry3D Finish()
        {
            var mesh = new MeshGeometry3D { Positions = new Point3DCollection(_positions), TriangleIndices = new Int32Collection(_triangles) };
            mesh.Freeze(); return mesh;
        }
    }
}
