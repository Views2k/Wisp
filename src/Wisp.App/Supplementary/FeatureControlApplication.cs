namespace Wisp.App.Supplementary;

// Entry points close synchronously; file finalization must not hold up the next policy.
internal sealed class FeatureControlApplication(Func<FeatureControlState, Task> begin)
{
    private long _failedRevision = -1;
    private int _pending;
    internal int PendingFinalizations => Volatile.Read(ref _pending);
    internal long FailedRevision => Interlocked.Read(ref _failedRevision);
    internal Task LastFinalization { get; private set; } = Task.CompletedTask;

    internal void Apply(FeatureControlState state)
    {
        // A synchronous application failure reaches the refresh worker and is retried.
        var completion = begin(state);
        Interlocked.Increment(ref _pending);
        LastFinalization = ObserveAsync(completion, state.Revision);
    }

    private async Task ObserveAsync(Task completion, long revision)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception)
        {
            long previous;
            do { previous = Interlocked.Read(ref _failedRevision); }
            while (previous < revision && Interlocked.CompareExchange(ref _failedRevision, revision, previous) != previous);
        }
        finally { Interlocked.Decrement(ref _pending); }
    }
}
