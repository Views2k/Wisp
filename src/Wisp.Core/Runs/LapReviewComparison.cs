using System.Numerics;

namespace Wisp.Core.Runs;

public static partial class LapReviewAnalysis
{
    private const float MatchRadius = 20;
    private const int MaximumNearbyEdges = 512;
    private const double MatchSpanMeters = 1;
    private readonly record struct MatchSpan(int Start, int End);

    /// <summary>Current minus reference time at a matched world position; negative is ahead.</summary>
    public static LapReviewComparison Compare(LapReviewLap lap, LapReviewLap reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lap);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (lap.Points.Length > MaximumPoints || reference.Points.Length > MaximumPoints)
            return Unavailable("The selected lap exceeds the comparison sample limit.");
        if (!lap.IsComplete || !reference.IsComplete || lap.Points.Length < 3 || reference.Points.Length < 3)
            return Unavailable("Choose two complete laps with recorded positions.");
        if (lap.CarOrdinal != reference.CarOrdinal)
            return Unavailable("These laps use different cars. A benchmark must use the same car.");
        if (lap.TimingMode != reference.TimingMode)
            return Unavailable("These laps use different timing modes.");
        const LapReviewQuality unsuitable = LapReviewQuality.TelemetryGap | LapReviewQuality.Rewind |
            LapReviewQuality.Discontinuity | LapReviewQuality.MissingPosition | LapReviewQuality.MissingTiming;
        if ((lap.Quality & unsuitable) != 0 || (reference.Quality & unsuitable) != 0)
            return Unavailable("A selected lap has missing or interrupted telemetry. Its time delta is unavailable.");
        if (Vector3.Distance(lap.Points[0].Position.ToVector(), reference.Points[0].Position.ToVector()) > MatchRadius ||
            lap.RecordedDistanceMeters <= 0 || reference.RecordedDistanceMeters <= 0 ||
            lap.RecordedDistanceMeters / reference.RecordedDistanceMeters is < .8 or > 1.25)
            return Unavailable("The recorded start positions or route lengths do not agree.");

        var grid = Index(reference, cancellationToken);
        var matches = Match(lap, reference, grid, cancellationToken);
        var reverse = Match(reference, lap, Index(lap, cancellationToken), cancellationToken);
        var coverage = Math.Min(Coverage(lap, matches), Coverage(reference, reverse));
        // Most of both routes must agree in order and direction. A shared straight alone is
        // insufficient evidence that these laps belong to the same route.
        if (coverage < .9)
            return Unavailable("The routes could not be matched reliably in both directions. No lap delta is shown.", coverage);
        return new(matches, coverage, true,
            "Delta is current minus reference time at matching recorded positions. Unmatched or ambiguous positions have no delta. " +
            "Matching geometry does not establish identical tunes, conditions or official lap validity.");

        LapReviewComparison Unavailable(string message, double coverage = 0) => new([], coverage, false, message);
    }

    private static Dictionary<(int X, int Y, int Z), List<MatchSpan>> Index(LapReviewLap lap, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(int X, int Y, int Z), List<MatchSpan>>();
        var start = 0;
        for (var index = 1; index < lap.Points.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!ContinuousPoints(lap.Points[index - 1], lap.Points[index]))
            {
                Add(start, index - 1);
                start = index;
            }
            else if (lap.Points[index].DistanceMeters - lap.Points[start].DistanceMeters >= MatchSpanMeters)
            {
                Add(start, index);
                start = index;
            }
        }
        Add(start, lap.Points.Length - 1);
        return result;

        void Add(int first, int last)
        {
            if (last <= first) return;
            var a = lap.Points[first];
            var b = lap.Points[last];
            var from = a.Position.ToVector();
            var to = b.Position.ToVector();
            if (Vector3.DistanceSquared(from, to) is < .0001f or > 2500) return;
            var low = Cell(Vector3.Min(from, to));
            var high = Cell(Vector3.Max(from, to));
            for (var x = low.X; x <= high.X; x++)
                for (var y = low.Y; y <= high.Y; y++)
                    for (var z = low.Z; z <= high.Z; z++)
                    {
                        var key = (x, y, z);
                        if (!result.TryGetValue(key, out var edges)) result[key] = edges = [];
                        // Bound geometric complexity, not packet density. Original points stay
                        // intact, and timing is recovered from them after the spatial match.
                        if (edges.Count <= MaximumNearbyEdges) edges.Add(new(first, last));
                    }
        }
    }

    private static LapReviewComparisonPoint[] Match(LapReviewLap lap, LapReviewLap reference,
        Dictionary<(int X, int Y, int Z), List<MatchSpan>> grid, CancellationToken cancellationToken)
    {
        var result = new LapReviewComparisonPoint[lap.Points.Length];
        var nearby = new HashSet<MatchSpan>();
        var candidates = new List<(MatchSpan Span, double Distance, float Error)>();
        double progress = 0, drivenAtMatch = 0;
        for (var index = 0; index < lap.Points.Length; index++)
        {
            if ((index & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            var point = lap.Points[index];
            result[index] = new(index, point.DistanceMeters, null, null);
            if (index > 0 && point.BreakBefore) continue;
            var position = point.Position.ToVector();
            var before = lap.Points[Math.Max(0, index - 1)].Position.ToVector();
            var after = lap.Points[Math.Min(lap.Points.Length - 1, index + 1)].Position.ToVector();
            var heading = after - before;
            if (heading.LengthSquared() < .0001f) continue;
            heading = Vector3.Normalize(heading);
            var cell = Cell(position);
            nearby.Clear();
            for (var x = cell.X - 1; x <= cell.X + 1 && nearby.Count <= MaximumNearbyEdges; x++)
                for (var y = cell.Y - 1; y <= cell.Y + 1 && nearby.Count <= MaximumNearbyEdges; y++)
                    for (var z = cell.Z - 1; z <= cell.Z + 1 && nearby.Count <= MaximumNearbyEdges; z++)
                        if (grid.TryGetValue((x, y, z), out var edges))
                            foreach (var edge in edges)
                            {
                                nearby.Add(edge);
                                if (nearby.Count > MaximumNearbyEdges) break;
                            }
            if (nearby.Count > MaximumNearbyEdges) continue;
            var maximum = progress + Math.Max(30, (point.DistanceMeters - drivenAtMatch) * 1.5 + 15);
            candidates.Clear();
            foreach (var span in nearby)
            {
                var a = reference.Points[span.Start];
                var b = reference.Points[span.End];
                if (b.DistanceMeters < progress - 2 || a.DistanceMeters > maximum) continue;
                var edge = b.Position.ToVector() - a.Position.ToVector();
                if (Vector3.Dot(heading, Vector3.Normalize(edge)) < .5f) continue;
                var fraction = Math.Clamp(Vector3.Dot(position - a.Position.ToVector(), edge) / edge.LengthSquared(), 0, 1);
                var distance = a.DistanceMeters + (b.DistanceMeters - a.DistanceMeters) * fraction;
                if (distance < progress - 2 || distance > maximum) continue;
                var error = Vector3.DistanceSquared(position, a.Position.ToVector() + edge * fraction);
                if (error > MatchRadius * MatchRadius) continue;
                candidates.Add((span, distance, error));
            }
            if (candidates.Count == 0) continue;
            var best = candidates.MinBy(candidate => candidate.Error);
            if (candidates.Any(candidate => Math.Abs(candidate.Distance - best.Distance) > 20 &&
                candidate.Error <= best.Error + 4)) continue;
            // The meter-scale span only locates the candidate. Refine on original
            // adjacent samples so the result retains their actual position and timing.
            var approximate = OriginalEdge(reference, best.Span, best.Distance);
            var exactIndex = -1;
            var exactFraction = 0f;
            var exactDistance = 0d;
            var exactError = float.MaxValue;
            for (var edgeIndex = Math.Max(best.Span.Start, approximate - 2);
                edgeIndex <= Math.Min(best.Span.End - 1, approximate + 2); edgeIndex++)
            {
                var start = reference.Points[edgeIndex];
                var end = reference.Points[edgeIndex + 1];
                var edge = end.Position.ToVector() - start.Position.ToVector();
                if (edge.LengthSquared() <= 0 || Vector3.Dot(heading, Vector3.Normalize(edge)) < .5f) continue;
                var fraction = Math.Clamp(Vector3.Dot(position - start.Position.ToVector(), edge) / edge.LengthSquared(), 0, 1);
                var distance = start.DistanceMeters + (end.DistanceMeters - start.DistanceMeters) * fraction;
                if (distance < progress - 2 || distance > maximum) continue;
                var error = Vector3.DistanceSquared(position, start.Position.ToVector() + edge * fraction);
                if (error > MatchRadius * MatchRadius || error >= exactError) continue;
                exactIndex = edgeIndex;
                exactFraction = fraction;
                exactDistance = distance;
                exactError = error;
            }
            if (exactIndex < 0) continue;
            var first = reference.Points[exactIndex];
            var last = reference.Points[exactIndex + 1];
            var seconds = first.LapSeconds + (last.LapSeconds - first.LapSeconds) * exactFraction;
            result[index] = new(index, point.DistanceMeters, seconds, point.LapSeconds - seconds, exactIndex);
            progress = Math.Max(progress, exactDistance);
            drivenAtMatch = point.DistanceMeters;
        }
        return result;
    }

    private static int OriginalEdge(LapReviewLap lap, MatchSpan span, double distance)
    {
        var low = span.Start + 1;
        var high = span.End;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lap.Points[middle].DistanceMeters < distance) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    private static double Coverage(LapReviewLap lap, LapReviewComparisonPoint[] matches)
    {
        double matchedDistance = 0;
        for (var index = 1; index < matches.Length; index++)
            if (matches[index - 1].DeltaSeconds is not null && matches[index].DeltaSeconds is not null && !lap.Points[index].BreakBefore)
                matchedDistance += Math.Max(0, lap.Points[index].DistanceMeters - lap.Points[index - 1].DistanceMeters);
        return Math.Clamp(matchedDistance / lap.RecordedDistanceMeters, 0, 1);
    }

    private static (int X, int Y, int Z) Cell(Vector3 position) =>
        ((int)Math.Floor(position.X / MatchRadius), (int)Math.Floor(position.Y / MatchRadius), (int)Math.Floor(position.Z / MatchRadius));
}
