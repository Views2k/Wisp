namespace Wisp.App;

internal sealed record ForzaProcessSnapshot(
    HashSet<int> ProcessIds,
    HashSet<string> ExecutableDirectories,
    ForzaCaptureCandidate? CaptureCandidate = null)
{
    internal static ForzaProcessSnapshot Empty() => new([], new(StringComparer.OrdinalIgnoreCase));
}

internal sealed record ForzaCaptureCandidate(uint ProcessId, ulong Window, ulong CreationFileTime, int Width, int Height);

// Only a stable native window identity/size establishes a new recording epoch.
// Foreground state and telemetry never enter this policy.
internal sealed class ForzaCaptureObservationTracker
{
    private ForzaCaptureCandidate? _pending, _stable;
    private long _epoch;
    internal Clips.RecorderTargetObservation? Current { get; private set; }

    internal Clips.RecorderTargetObservation? Observe(ForzaCaptureCandidate? candidate)
    {
        if (candidate is null || candidate != _pending)
        {
            _pending = candidate; _stable = null; Current = null;
            return null;
        }
        if (candidate != _stable)
        {
            _stable = candidate;
            Current = new(new(candidate.ProcessId, candidate.Window, candidate.CreationFileTime), checked(++_epoch));
        }
        return Current;
    }
}

// Polled by the UI thread; discovery itself never runs there. No completion callback
// can publish into a suspended/disposed controller, and searches cannot overlap.
internal sealed class ForzaProcessDiscovery(Func<ForzaProcessSnapshot> discover)
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);
    private Task<ForzaProcessSnapshot>? _pending;
    private DateTimeOffset _nextSearchAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _searchStartedAtUtc;

    internal ForzaProcessSnapshot Current { get; private set; } = ForzaProcessSnapshot.Empty();
    internal bool IsSearching => _pending is not null;

    internal bool Refresh(DateTimeOffset nowUtc)
    {
        var published = false;
        if (_pending is { IsCompleted: true } completed)
        {
            var expired = nowUtc - _searchStartedAtUtc >= SearchInterval;
            Current = !expired && completed.IsCompletedSuccessfully ? completed.Result : ForzaProcessSnapshot.Empty();
            _ = completed.Exception; // Observe a failed search; missing identity stays fail-closed.
            _pending = null;
            _nextSearchAtUtc = expired ? nowUtc : _searchStartedAtUtc + SearchInterval;
            published = true;
        }
        if (_pending is null && nowUtc >= _nextSearchAtUtc)
        {
            _searchStartedAtUtc = nowUtc;
            _pending = Task.Run(discover);
        }
        return published;
    }
}
