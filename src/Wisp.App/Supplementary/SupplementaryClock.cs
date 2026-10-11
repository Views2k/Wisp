namespace Wisp.App.Supplementary;

// Server time is learned only from a successful fixed-origin content response. Wall-clock edits cannot age the anchor.
internal sealed class SupplementaryClock(TimeProvider clock)
{
    private sealed record Anchor(DateTimeOffset ServerTime, long ReceivedAt, int UncertaintyMs);
    private Anchor? _anchor;
    internal bool IsFresh
    {
        get
        {
            var anchor = Volatile.Read(ref _anchor);
            if (anchor is null) return false;
            var age = clock.GetElapsedTime(anchor.ReceivedAt, clock.GetTimestamp());
            return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(55);
        }
    }
    internal void Observe(SupplementaryResponse response)
    {
        if (response.Status == SupplementaryRequestStatus.Success && response.ServerTime is { Offset.Ticks: 0 } time &&
            response.UncertaintyMs is >= 0 and <= 15000)
            Volatile.Write(ref _anchor, new(time, clock.GetTimestamp(), response.UncertaintyMs.Value));
    }
    internal SupplementaryObservationClock? Capture(long queuedAt, long originalAgeMs)
    {
        var anchor = Volatile.Read(ref _anchor);
        if (anchor is null || originalAgeMs < 0) return null;
        var now = clock.GetTimestamp();
        var anchorAge = clock.GetElapsedTime(anchor.ReceivedAt, now).TotalMilliseconds;
        var observedAge = originalAgeMs + clock.GetElapsedTime(queuedAt, now).TotalMilliseconds;
        if (!double.IsFinite(anchorAge) || anchorAge is < 0 or > 3_600_000 ||
            !double.IsFinite(observedAge) || observedAge is < 0 or > 86_400_000) return null;
        return new(1, anchor.ServerTime, (long)anchorAge, (long)observedAge, anchor.UncertaintyMs);
    }
}
