using System.Collections.Immutable;

namespace Wisp.App.Supplementary;

internal sealed record FeatureControlState(long Revision, ImmutableDictionary<string, string> Disabled);

// This worker owns its transport. Reporting queues, pauses and retries cannot delay policy refresh.
internal sealed class FeatureControlClient : IDisposable
{
    internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(25);
    private readonly FeatureControlStore _store;
    private readonly ISupplementaryTransport? _transport;
    private readonly TimeProvider _clock;
    private readonly string _version, _channel;
    private readonly Action<FeatureControlState> _changed;
    private readonly Func<double> _jitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<FeatureControlContext> _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellation;
    private FeatureControlState? _published;
    private int _started, _disposed, _refreshing;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal FeatureControlState State => new(_store.HighestRevision,
        _store.Current(_clock.GetUtcNow()).Disabled(_version, _channel, _clock.GetUtcNow(), _context()));

    internal FeatureControlClient(SupplementaryConfiguration configuration, FeatureControlStore store,
        string version, string channel, Action<FeatureControlState> changed, TimeProvider? clock = null,
        ISupplementaryTransport? transport = null, Func<double>? jitter = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<FeatureControlContext>? context = null)
    {
        _store = store;
        _version = version;
        _channel = channel;
        _changed = changed;
        _clock = clock ?? TimeProvider.System;
        _transport = transport ?? (configuration.Origin is not null && store.IsConfigured
            ? new SupplementaryHttpTransport(configuration.Origin, _clock) : null);
        _jitter = jitter ?? Random.Shared.NextDouble;
        _delay = delay ?? ((duration, cancellation) => Task.Delay(duration, _clock, cancellation));
        _context = context ?? (() => FeatureControlContext.Empty);
        _cancellation = _lifetime.Token;
    }

    internal void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
        Completion = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            PublishState();
            while (!_cancellation.IsCancellationRequested)
            {
                await RefreshAsync(_cancellation).ConfigureAwait(false);
                var jitter = _jitter();
                if (!double.IsFinite(jitter)) jitter = 0;
                var duration = MinimumInterval + TimeSpan.FromSeconds(5 * Math.Clamp(jitter, 0, 1));
                await _delay(duration, _cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    internal async Task RefreshAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try
        {
            if (_transport is not null && _store.IsConfigured)
            {
                using var deadline = new CancellationTokenSource(RequestDeadline, _clock);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cancellation, deadline.Token);
                var response = await _transport.SendAsync("/api/v1/feature-controls?schema=2", null,
                    FeatureControlVerifier.MaximumEnvelopeBytes, linked.Token).ConfigureAwait(false);
                if (!linked.IsCancellationRequested && response.Status == SupplementaryRequestStatus.Success && response.Body.Length > 0)
                    ReportAcceptance(_store.Accept(response.Body, _clock.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException or ObjectDisposedException) { }
        finally
        {
            // Expiry is applied even when the service is unavailable. An empty/invalid response cannot cancel a current policy.
            if (!_cancellation.IsCancellationRequested) PublishState();
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private SupplementaryContentAcceptance? _reportedAcceptance;

    // Reported once per change, so Tools can show why a published revision did not take effect here.
    private void ReportAcceptance(SupplementaryContentAcceptance acceptance)
    {
        if (_reportedAcceptance == acceptance) return;
        _reportedAcceptance = acceptance;
        if (acceptance == SupplementaryContentAcceptance.Rejected)
            SupplementaryObservations.Record("feature", "app", "failure", "validation");
        else if (acceptance == SupplementaryContentAcceptance.CacheUnavailable)
            SupplementaryObservations.Record("feature", "app", "failure", "recovery");
    }

    private void PublishState()
    {
        var next = State;
        var previous = _published;
        if (previous is not null && previous.Revision == next.Revision && previous.Disabled.Count == next.Disabled.Count &&
            previous.Disabled.All(p => next.Disabled.TryGetValue(p.Key, out var value) && value == p.Value)) return;
        try { _changed(next); _published = next; }
        catch (Exception) { } // Retry delivery on the next refresh if the consumer was temporarily unavailable.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _transport?.Dispose();
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
