using System.Text.Json;

namespace Wisp.App.Supplementary;

// Single low-priority worker owns automatic serialization and transmission. Producers only submit fixed DTOs.
internal sealed class SupplementaryClient : IDisposable
{
    internal const int QueueCapacity = 128;
    internal const int MaximumAttempts = 3;
    internal static readonly TimeSpan MaximumEventAge = TimeSpan.FromHours(1);
    private readonly object _gate = new();
    private readonly Queue<PendingEvent> _queue = new(QueueCapacity);
    private readonly SupplementaryContentStore _content;
    private readonly TimeProvider _clock;
    private readonly ISupplementaryTransport? _transport;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellation;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Func<double> _jitter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<SupplementaryClient>? _collect;
    private readonly Action? _contentChanged;
    private Task _completion = Task.CompletedTask;
    private long _dropped;
    private long _batchSequence;
    private SupplementaryEvent? _runIdentity;
    private int _flushing;
    private bool _started, _stopped;
    private int _disposed;
    private int _supportPending;
    private sealed record PendingEvent(SupplementaryEvent Event, long QueuedAt);

    internal SupplementaryClient(SupplementaryConfiguration configuration, SupplementaryContentStore content,
        TimeProvider? clock = null, ISupplementaryTransport? transport = null, Func<double>? jitter = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<SupplementaryClient>? collect = null, Action? contentChanged = null)
    {
        _content = content;
        _cancellation = _lifetime.Token;
        _clock = clock ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _delay = delay ?? ((duration, cancellation) => Task.Delay(duration, _clock, cancellation));
        _collect = collect;
        _contentChanged = contentChanged;
        if (configuration.Origin is not null && content.IsConfigured)
            _transport = transport ?? new SupplementaryHttpTransport(configuration.Origin, _clock);
    }
    internal bool IsConfigured => _transport is not null;
    internal bool IsReportingEnabled => IsConfigured && _content.IsHealthy && !Content.ReportingDisabled && !_lifetime.IsCancellationRequested;
    internal SupplementaryContentSnapshot Content => _content.Current(_clock.GetUtcNow());
    internal Task Completion { get { lock (_gate) return _completion; } }
    internal long DroppedEvents => Interlocked.Read(ref _dropped);
    internal int PendingEvents { get { lock (_gate) return _queue.Count; } }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped || !IsConfigured) return;
            _started = true;
            _completion = Task.Run(RunAsync);
        }
    }

    internal bool TryEnqueue(SupplementaryEvent value)
    {
        if (!IsReportingEnabled || !SupplementarySchema.Valid(value, _clock.GetUtcNow())) return false;
        lock (_gate)
        {
            if (_stopped || _queue.Count >= QueueCapacity) { Interlocked.Increment(ref _dropped); return false; }
            if (_runIdentity is not null && !SameRun(_runIdentity, value)) return false;
            _runIdentity ??= value;
            _queue.Enqueue(new(value, _clock.GetTimestamp()));
            return true;
        }
    }

    // The caller previews SupplementarySchema.RedactForPreview(report) and passes that unchanged DTO after confirmation.
    // A deliberately submitted report is never automatically retried; its eventId permits a safe user retry.
    internal async Task<SupplementarySupportResult> SubmitSupportAsync(SupplementarySupportReport report, CancellationToken cancellation = default)
    {
        if (!IsConfigured) return new(SupplementaryRequestStatus.Unconfigured);
        if (!IsReportingEnabled) return new(SupplementaryRequestStatus.Disabled);
        if (!SupplementarySchema.Valid(report, _clock.GetUtcNow())) return new(SupplementaryRequestStatus.Invalid);
        if (Interlocked.CompareExchange(ref _supportPending, 1, 0) != 0) return new(SupplementaryRequestStatus.Unavailable);
        try { return await SubmitReviewedSupportAsync(report, cancellation).ConfigureAwait(false); }
        finally { Volatile.Write(ref _supportPending, 0); }
    }

    private async Task<SupplementarySupportResult> SubmitReviewedSupportAsync(SupplementarySupportReport report, CancellationToken cancellation)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(report, SupplementarySchema.Json); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return new(SupplementaryRequestStatus.Invalid); }
        if (bytes.Length > SupplementarySchema.MaximumSupportBytes) return new(SupplementaryRequestStatus.Invalid);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cancellation);
        var response = await SendAsync("/api/v1/support", bytes, 2048, linked.Token).ConfigureAwait(false);
        return SupplementarySupportReceipts.Read(response, _clock.GetUtcNow());
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            Interlocked.Add(ref _dropped, _queue.Count);
            _queue.Clear();
        }
        // Cancellation callbacks run outside the queue lock. Shutdown never waits for network completion.
        _lifetime.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            var nextContent = long.MinValue;
            while (!_lifetime.IsCancellationRequested)
            {
                var now = _clock.GetTimestamp();
                if (nextContent == long.MinValue || _clock.GetElapsedTime(nextContent, now) >= TimeSpan.FromHours(1))
                {
                    await RefreshContentAsync(_cancellation).ConfigureAwait(false);
                    nextContent = _clock.GetTimestamp();
                }
                _collect?.Invoke(this);
                if (IsReportingEnabled)
                {
                    // Drain at most the bounded queue per cycle. An outage stops this cycle after one failed batch.
                    for (var batch = 0; batch < QueueCapacity / SupplementarySchema.MaximumBatchEvents && PendingEvents > 0; batch++)
                        if (!await FlushBatchAsync(_cancellation).ConfigureAwait(false)) break;
                }
                else ClearPending();
                await _delay(TimeSpan.FromSeconds(60 + 30 * Math.Clamp(_jitter(), 0, 1)), _cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // Optional service failures cannot reach the app dispatcher, shutdown, renderer or native readers.
            Stop();
        }
    }

    internal async Task RefreshContentAsync(CancellationToken cancellation)
    {
        if (!IsConfigured || !_content.IsHealthy) return;
        var result = await SendAsync("/api/v1/content", null, SupplementaryContentVerifier.MaximumEnvelopeBytes, cancellation).ConfigureAwait(false);
        if (result.Status == SupplementaryRequestStatus.Success && result.Body.Length > 0)
        {
            _content.Accept(result.Body, _clock.GetUtcNow());
            try { _contentChanged?.Invoke(); } catch (Exception) { } // Presentation cannot interrupt the reporting worker.
            if (!IsReportingEnabled) ClearPending();
        }
    }

    internal async Task<bool> FlushBatchAsync(CancellationToken cancellation)
    {
        // Sequence assignment and the complete retry window have one owner. Concurrent callers cannot reorder batches.
        if (Interlocked.CompareExchange(ref _flushing, 1, 0) != 0) return false;
        try { return await FlushNextBatchAsync(cancellation).ConfigureAwait(false); }
        finally { Volatile.Write(ref _flushing, 0); }
    }

    private async Task<bool> FlushNextBatchAsync(CancellationToken cancellation)
    {
        if (!IsReportingEnabled) { ClearPending(); return false; }
        var values = new List<SupplementaryEvent>(SupplementarySchema.MaximumBatchEvents);
        var encodedBytes = 80; // Includes sequence, wrapper and punctuation; DTOs are encoded only on this worker.
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            while (_queue.Count > 0 && values.Count < SupplementarySchema.MaximumBatchEvents)
            {
                var item = _queue.Peek();
                if (_clock.GetElapsedTime(item.QueuedAt, now) > MaximumEventAge || !SupplementarySchema.Valid(item.Event, _clock.GetUtcNow()))
                { _queue.Dequeue(); Interlocked.Increment(ref _dropped); }
                else
                {
                    if (values.Count > 0 && !SameRun(values[0], item.Event)) break;
                    var size = JsonSerializer.SerializeToUtf8Bytes(item.Event, SupplementarySchema.Json).Length + 1;
                    if (encodedBytes + size > SupplementarySchema.MaximumBatchBytes) break;
                    encodedBytes += size;
                    values.Add(_queue.Dequeue().Event);
                }
            }
        }
        if (values.Count == 0) return true;
        if (_batchSequence >= 9_007_199_254_740_991) { Interlocked.Add(ref _dropped, values.Count); return false; }
        // Assign once, before serialization. Gaps after an exhausted retry are explicit delivery coverage loss.
        var sequence = ++_batchSequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, batchSequence = sequence, events = values }, SupplementarySchema.Json);
        if (bytes.Length > SupplementarySchema.MaximumBatchBytes) { Interlocked.Add(ref _dropped, values.Count); return false; }
        for (var attempt = 0; attempt < MaximumAttempts && IsReportingEnabled; attempt++)
        {
            var response = await SendAsync("/api/v1/events", bytes, 2048, cancellation).ConfigureAwait(false);
            if (response.Status == SupplementaryRequestStatus.Success)
            {
                if (ValidEventReceipt(response.Body, values.Count)) return true;
                break;
            }
            if (response.Status is not (SupplementaryRequestStatus.Unavailable or SupplementaryRequestStatus.TimedOut)) break;
            if (attempt + 1 < MaximumAttempts)
                await _delay(TimeSpan.FromSeconds((attempt + 1) * 15 + 15 * Math.Clamp(_jitter(), 0, 1)), cancellation).ConfigureAwait(false);
        }
        Interlocked.Add(ref _dropped, values.Count);
        return false;
    }

    private async Task<SupplementaryResponse> SendAsync(string route, byte[]? bytes, int maximum, CancellationToken cancellation)
    {
        if (_transport is null) return new(SupplementaryRequestStatus.Unconfigured, []);
        if (_stopped) return new(SupplementaryRequestStatus.Cancelled, []);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cancellation);
        try
        {
            await _requestGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (route != "/api/v1/content" && !IsReportingEnabled) return new(SupplementaryRequestStatus.Disabled, []);
                return await _transport.SendAsync(route, bytes, maximum, linked.Token).ConfigureAwait(false);
            }
            finally { _requestGate.Release(); }
        }
        catch (OperationCanceledException) { return new(SupplementaryRequestStatus.Cancelled, []); }
        catch (Exception error) when (error is ObjectDisposedException or System.IO.IOException or System.Net.Http.HttpRequestException)
        { return new(SupplementaryRequestStatus.Unavailable, []); }
    }
    private static bool ValidEventReceipt(byte[] bytes, int expected)
    {
        try
        {
            using var doc = SupplementaryJson.Parse(bytes, 2048);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var p = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
                if (!p.TryAdd(property.Name, property.Value)) return false;
            if (!p.ContainsKey("schemaVersion") || !p.ContainsKey("accepted") || !p.ContainsKey("duplicate")) return false;
            var accepted = SupplementaryJson.Int(p["accepted"]); var duplicate = SupplementaryJson.Int(p["duplicate"]);
            return SupplementaryJson.Int(p["schemaVersion"]) == 1 && accepted >= 0 && duplicate >= 0 &&
                accepted <= expected && duplicate <= expected && accepted + duplicate == expected;
        }
        catch (Exception e) when (e is SupplementaryValidationException or JsonException or FormatException or InvalidOperationException)
        { return false; }
    }
    private void ClearPending() { lock (_gate) { Interlocked.Add(ref _dropped, _queue.Count); _queue.Clear(); } }
    private static bool SameRun(SupplementaryEvent first, SupplementaryEvent next) => first.SessionId == next.SessionId &&
        first.InstallationId == next.InstallationId && first.AppVersion == next.AppVersion && first.BuildId == next.BuildId && first.Channel == next.Channel;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _transport?.Dispose();
        // The worker may still be unwinding; do not dispose its token/semaphore underneath pending continuations.
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
