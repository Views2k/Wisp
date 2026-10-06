using System.Numerics;

namespace Wisp.Core.Runs;

public enum LapReviewContactKind { PossibleContact, UserMarkedContact, SmashableObject }

public sealed record LapReviewContact(int PointIndex, double RunSeconds, double DistanceMeters,
    LapReviewContactKind Kind)
{
    public string Label => Kind switch
    {
        LapReviewContactKind.UserMarkedContact => "Marked contact",
        LapReviewContactKind.SmashableObject => "Object contact",
        _ => "Possible contact"
    };
}

public static class LapReviewContacts
{
    public const string ContactMarkerLabel = "Contact";
    public const string EvidenceNote = "Object contacts are reported by Forza for breakable objects. Possible contacts are estimates that may miss or misidentify contact. Mark contacts yourself where needed.";
    private const double Gravity = 9.80665;
    private const double WindowSeconds = .2;
    private const double CooldownSeconds = 1;

    // Data Out reports breakable-object impulses but has no general collision flag.
    // Keep reported and inferred evidence distinct. Analyze once, never while drawing.
    public static LapReviewContact[] Find(LapReviewLap lap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lap);
        cancellationToken.ThrowIfCancellationRequested();
        if (lap.Points.Length > LapReviewAnalysis.MaximumPoints) return [];
        var points = lap.Points;
        var result = new List<LapReviewContact>();
        for (var index = 1; index < points.Length; index++)
        {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var before = points[index - 1].Sample.State;
            var state = points[index].Sample.State;
            // A positive value at the start or after a gap may describe an old hit.
            // Emit only an observed zero-to-positive impulse; never count a latched
            // mass or decaying impulse repeatedly as new collisions.
            if (before.SmashableVelocityLossMetersPerSecond == 0 &&
                before.SmashableMassKilograms is >= 0 and <= 10_000_000 &&
                state.SmashableVelocityLossMetersPerSecond is > 0 and <= 500 &&
                state.SmashableMassKilograms is > 0 and <= 10_000_000 && Continuous(points[index - 1], points[index]))
                result.Add(new(index, points[index].RunSeconds, points[index].DistanceMeters, LapReviewContactKind.SmashableObject));
        }
        var objectTimes = result.Select(contact => contact.RunSeconds).ToArray();
        var lastContact = double.NegativeInfinity;
        var lastStrongAcceleration = points.Length > 0 && HorizontalAcceleration(points[0]) >= 6 * Gravity
            ? points[0].RunSeconds : double.NegativeInfinity;
        for (var index = 1; index + 1 < points.Length; index++)
        {
            if ((index & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var current = points[index];
            if (HorizontalAcceleration(current) >= 6 * Gravity) lastStrongAcceleration = current.RunSeconds;
            if (current.RunSeconds - lastContact < CooldownSeconds ||
                current.RunSeconds - lastStrongAcceleration > WindowSeconds + 1e-7 ||
                !Continuous(current, points[index + 1]) || !Grounded(current) || !Grounded(points[index + 1])) continue;
            // A one-packet speed error which immediately recovers is not an impact.
            if (points[index + 1].Sample.State.GroundSpeedMetersPerSecond > current.Sample.State.GroundSpeedMetersPerSecond + 1.5) continue;
            var peakAcceleration = HorizontalAcceleration(current);
            var peakIndex = index;
            for (var before = index - 1; before >= 0; before--)
            {
                var previous = points[before];
                var elapsed = current.RunSeconds - previous.RunSeconds;
                if (elapsed > WindowSeconds + 1e-7 || !Continuous(previous, points[before + 1]) || !Grounded(previous)) break;
                var acceleration = HorizontalAcceleration(previous);
                if (acceleration > peakAcceleration) { peakAcceleration = acceleration; peakIndex = before; }
                var speedLoss = previous.Sample.State.GroundSpeedMetersPerSecond - current.Sample.State.GroundSpeedMetersPerSecond;
                if (elapsed < .02 || previous.Sample.State.GroundSpeedMetersPerSecond < 8 ||
                    speedLoss < 4 || speedLoss / elapsed < 4 * Gravity || peakAcceleration < 6 * Gravity) continue;
                var contact = points[peakIndex];
                if (!NearObjectContact(contact.RunSeconds, objectTimes))
                    result.Add(new(peakIndex, contact.RunSeconds, contact.DistanceMeters, LapReviewContactKind.PossibleContact));
                lastContact = current.RunSeconds;
                break;
            }
        }
        return result.OrderBy(contact => contact.PointIndex).ToArray();
    }

    // Existing run markers provide an explicit alternative when telemetry cannot
    // identify a contact. No new saved-run schema or game-memory field is required.
    public static LapReviewContact[] FromMarkers(LapReviewLap lap, IReadOnlyList<RunMarker> markers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lap);
        ArgumentNullException.ThrowIfNull(markers);
        cancellationToken.ThrowIfCancellationRequested();
        if (lap.Points.Length == 0 || lap.Points.Length > LapReviewAnalysis.MaximumPoints) return [];
        var result = new List<LapReviewContact>();
        var seen = new HashSet<int>();
        foreach (var marker in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(marker.Label?.Trim(), ContactMarkerLabel, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(marker.ElapsedSeconds) || marker.ElapsedSeconds < lap.Points[0].RunSeconds ||
                marker.ElapsedSeconds > lap.Points[^1].RunSeconds) continue;
            var low = 0;
            var high = lap.Points.Length - 1;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (lap.Points[middle].RunSeconds < marker.ElapsedSeconds) low = middle + 1;
                else high = middle;
            }
            // Do not move a marker across an unrecorded gap to make it fit a lap.
            if (low > 0 && lap.Points[low].RunSeconds != marker.ElapsedSeconds &&
                !Continuous(lap.Points[low - 1], lap.Points[low])) continue;
            if (low > 0 && marker.ElapsedSeconds - lap.Points[low - 1].RunSeconds < lap.Points[low].RunSeconds - marker.ElapsedSeconds) low--;
            var point = lap.Points[low];
            if (Math.Abs(point.RunSeconds - marker.ElapsedSeconds) <= .125 && seen.Add(low))
                result.Add(new(low, marker.ElapsedSeconds, point.DistanceMeters, LapReviewContactKind.UserMarkedContact));
        }
        return result.OrderBy(contact => contact.PointIndex).ToArray();
    }

    private static bool Grounded(LapReviewPoint point) => point.Sample.State.LocalVelocityYMetersPerSecond is { } vertical &&
        float.IsFinite(vertical) && Math.Abs(vertical) <= 3 && double.IsFinite(HorizontalAcceleration(point));

    private static bool NearObjectContact(double seconds, double[] times)
    {
        var index = Array.BinarySearch(times, seconds);
        if (index >= 0) return true;
        index = ~index;
        return index < times.Length && times[index] - seconds <= WindowSeconds ||
            index > 0 && seconds - times[index - 1] <= WindowSeconds;
    }

    private static double HorizontalAcceleration(LapReviewPoint point)
    {
        var state = point.Sample.State;
        var lateral = (double)state.LateralAccelerationMetersPerSecondSquared;
        var longitudinal = (double)state.LongitudinalAccelerationMetersPerSecondSquared;
        return Math.Sqrt(lateral * lateral + longitudinal * longitudinal);
    }

    private static bool Continuous(LapReviewPoint previous, LapReviewPoint current)
    {
        if (current.BreakBefore || !RunAnalysis.AreContinuous(previous.Sample, current.Sample) ||
            !double.IsFinite(previous.LapSeconds) || !double.IsFinite(current.LapSeconds) ||
            current.LapSeconds - previous.LapSeconds is <= 0 or > .25) return false;
        var elapsed = current.RunSeconds - previous.RunSeconds;
        if (!double.IsFinite(elapsed) || elapsed <= 0 || elapsed > .25) return false;
        var distance = Vector3.Distance(previous.Position.ToVector(), current.Position.ToVector());
        var speed = Math.Max(previous.Sample.State.GroundSpeedMetersPerSecond, current.Sample.State.GroundSpeedMetersPerSecond);
        return float.IsFinite(distance) && distance <= Math.Max(1, speed * elapsed * 1.5 + .5);
    }
}
