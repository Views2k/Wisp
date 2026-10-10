namespace Wisp.App.Supplementary;

internal sealed record SupplementaryObservation(string Kind, string Feature, string Outcome, string Stage,
    DateTimeOffset ObservedAt, double? DurationMs = null, SupplementaryIncident? Incident = null);

// Disabled hosts do not allocate observations or operations. No subscriber can change feature behavior.
internal static class SupplementaryObservations
{
    private static Action<SupplementaryObservation>? _observer;
    internal static Action<SupplementaryObservation>? Observer
    { get => Volatile.Read(ref _observer); set => Volatile.Write(ref _observer, value); }

    internal static SupplementaryOperation? Begin(string kind, string feature, string stage) => Observer is { } observer
        ? new(observer, kind, feature, stage, TimeProvider.System) : null;

    internal static void Record(string kind, string feature, string outcome, string stage, double? durationMs = null, SupplementaryIncident? incident = null)
    {
        if (Observer is not { } observer) return;
        Emit(observer, new(kind, feature, outcome, stage, DateTimeOffset.UtcNow, durationMs, incident));
    }

    internal static void Emit(Action<SupplementaryObservation> observer, SupplementaryObservation value)
    { try { observer(value); } catch (Exception) { /* Optional reporting cannot fail an application operation. */ } }
}

internal sealed class SupplementaryOperation : IDisposable
{
    private readonly Action<SupplementaryObservation> _observer;
    private readonly string _kind, _feature, _stage;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private int _completed;
    internal SupplementaryOperation(Action<SupplementaryObservation> observer, string kind, string feature, string stage, TimeProvider clock)
    {
        _observer = observer; _kind = kind; _feature = feature; _stage = stage; _clock = clock; _started = clock.GetTimestamp();
        SupplementaryObservations.Emit(observer, new(kind, feature, "attempt", stage, clock.GetUtcNow()));
    }
    internal void Complete(string outcome, SupplementaryIncident? incident = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        var elapsed = _clock.GetElapsedTime(_started).TotalMilliseconds;
        SupplementaryObservations.Emit(_observer, new(_kind, _feature, outcome, _stage, _clock.GetUtcNow(),
            elapsed is >= 0 and <= 604800000 ? elapsed : null, incident));
    }
    // An interrupted/uncategorized path is not silently counted as success or a confirmed failure.
    public void Dispose() => Complete("unknown");
}
