using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.Telemetry;

namespace Wisp.App.Runs;

public sealed record RunRecordingContext(long EffectiveTimestamp, int CarOrdinal, DrivetrainType Drivetrain,
    bool IsDriving, double? FrontRadiusMeters = null, double? RearRadiusMeters = null, long? DrivingValidUntilTimestamp = null);

public sealed class RunRecordingService : IAsyncDisposable
{
    private readonly TelemetryUdpReceiver _receiver;
    private readonly object _gate = new();
    private Session? _session;
    private RunRecordingContext? _latestContext;
    private string? _error;
    private string _status = "Ready to record";
    private string _markerStatus = string.Empty;
    private long _lastElapsedTicks;
    private readonly object _notificationGate = new();
    private StateSnapshot? _lastNotified;
    private bool _disposed;

    public RunRecordingService(TelemetryUdpReceiver receiver, string directory)
    {
        _receiver = receiver;
        Store = new RunStore(directory);
    }

    public RunStore Store { get; }
    public event EventHandler? StateChanged;
    public event Action<RecordedRun>? RunSaved;
    public bool IsRecording => Volatile.Read(ref _session) is { Stopping: false };
    public bool IsPreparing => Volatile.Read(ref _session) is { Stopping: true };
    public TimeSpan Elapsed => Volatile.Read(ref _session) is { } session
        ? Stopwatch.GetElapsedTime(session.StartedTimestamp, EndTimestamp(session))
        : TimeSpan.FromTicks(Interlocked.Read(ref _lastElapsedTicks));
    public string Status => Volatile.Read(ref _status);
    public string? Error => Volatile.Read(ref _error);
    public string MarkerStatus => Volatile.Read(ref _markerStatus);
    public bool CanStart => !_disposed && !Store.IsFull && Volatile.Read(ref _session) is null && _receiver.IsRunning &&
        _receiver.Latest is { IsRaceOn: true, CarOrdinal: > 0, ReceivedTimestamp: { } stamp } &&
        Stopwatch.GetElapsedTime(stamp) is { TotalMilliseconds: >= 0 and <= 300 };

    public void UpdateContext(RunRecordingContext context)
    {
        if (context.EffectiveTimestamp <= 0 || context.CarOrdinal <= 0 || !Enum.IsDefined(context.Drivetrain)) return;
        if (context.FrontRadiusMeters is not { } front || context.RearRadiusMeters is not { } rear || !new RollingRadii(front, rear).IsPlausible)
            context = context with { FrontRadiusMeters = null, RearRadiusMeters = null };
        Volatile.Write(ref _latestContext, context);
        var session = Volatile.Read(ref _session);
        if (session is null || session.Stopping) return;
        session.Contexts.Enqueue(context);
        while (session.Contexts.Count > 2048) session.Contexts.TryDequeue(out _);
    }

    public bool Start(RunRecordingOptions? options = null)
    {
        options ??= new RunRecordingOptions();
        lock (_gate)
        {
            _error = null;
            if (!options.IsValid)
            {
                _status = "Choose a recording time above zero and no longer than ten minutes.";
                return false;
            }
            if (!CanStart)
            {
                _status = Store.IsFull ? "The run library is full. Export or remove a run before recording another." : "Return to free roam with live telemetry to record a run.";
                return false;
            }
            var state = _receiver.Latest;
            var now = Stopwatch.GetTimestamp();
            if (state is not { IsRaceOn: true, CarOrdinal: > 0, ReceivedTimestamp: { } received } ||
                now < received || Stopwatch.GetElapsedTime(received, now).TotalMilliseconds > 300)
            {
                _status = "Return to free roam with live telemetry to record a run.";
                return false;
            }
            var context = Volatile.Read(ref _latestContext);
            if (context is not { IsDriving: true } || context.CarOrdinal != state.CarOrdinal || context.Drivetrain != state.Drivetrain ||
                Stopwatch.GetElapsedTime(context.EffectiveTimestamp, now).TotalMilliseconds is < 0 or > 300 ||
                (context.DrivingValidUntilTimestamp is { } deadline && now > deadline))
            {
                _status = "Waiting for a confirmed driving scene; return to free roam.";
                return false;
            }
            var reservation = Store.TryReserveJournalStart();
            if (reservation is null)
            {
                _status = "A saved run is still being read or written. Try recording again when it finishes.";
                return false;
            }
            RunDatagramCapture? capture = null;
            try
            {
                capture = _receiver.BeginRunCapture();
                var session = new Session(capture, state.CarOrdinal, state.Drivetrain, context, reservation, options);
                _session = session;
                _error = null;
                _status = "Recording";
                _markerStatus = string.Empty;
                session.Completion = Task.Run(async () =>
                {
                    try { return await RecordAsync(session).ConfigureAwait(false); }
                    finally { reservation.Dispose(); }
                });
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or OutOfMemoryException)
            {
                reservation.Dispose();
                if (capture is not null) _receiver.EndRunCapture(capture, "Recording could not start");
                _session = null;
                _error = "Recording could not start. Live telemetry is still running.";
                _status = _error;
                return false;
            }
        }
        NotifyStateChanged();
        return true;
    }

    public Task<RecordedRun?> StopAsync(string reason = "Stopped by you")
    {
        Task<RecordedRun?> completion;
        lock (_gate)
        {
            var session = _session;
            if (session is null) return Task.FromResult<RecordedRun?>(null);
            if (!session.Stopping)
            {
                var now = Stopwatch.GetTimestamp();
                if (now >= session.DeadlineTimestamp) reason = session.LimitReason;
                Interlocked.CompareExchange(ref session.StopTimestamp, Math.Min(now, session.DeadlineTimestamp), 0);
                session.Stopping = true;
                session.Reason ??= SafeReason(reason);
                if (session.Reason is not "Stopped by you" and not "10-minute recording limit reached" and not "Timed recording completed") session.Incomplete = true;
                _status = "Preparing your run…";
                _receiver.EndRunCapture(session.Capture, session.Reason);
            }
            completion = session.Completion;
        }
        NotifyStateChanged();
        return completion;
    }

    public void RefreshStatus()
    {
        var session = Volatile.Read(ref _session);
        if (session is { Stopping: false })
        {
            if (Stopwatch.GetTimestamp() >= session.DeadlineTimestamp) _ = StopAsync(session.LimitReason);
            else if (Stopwatch.GetElapsedTime(Interlocked.Read(ref session.LastValidTimestamp)) >= TimeSpan.FromSeconds(10))
            {
                session.Incomplete = true;
                _ = StopAsync("Telemetry was unavailable for 10 seconds");
            }
        }
        NotifyStateChanged();
    }

    public bool MarkMoment(string? label = null)
    {
        var added = false;
        lock (_gate)
        {
            var session = _session;
            var position = session is null ? null : Volatile.Read(ref session.MarkerPosition);
            var context = Volatile.Read(ref _latestContext);
            var now = Stopwatch.GetTimestamp();
            if (session is null || session.Stopping || now >= session.DeadlineTimestamp)
                _markerStatus = "Start recording before adding a moment.";
            else if (position is null || position.Sequence != session.Capture.ObservedDatagrams || now > position.ValidUntilTimestamp ||
                context is not { IsDriving: true } || context.CarOrdinal != session.CarOrdinal || context.Drivetrain != session.Drivetrain ||
                now < context.EffectiveTimestamp || Stopwatch.GetElapsedTime(context.EffectiveTimestamp, now).TotalMilliseconds > 300 ||
                (context.DrivingValidUntilTimestamp is { } deadline && now > deadline))
                _markerStatus = "Moment not added; waiting for current driving data. Recording continues.";
            else if (session.MarkerCount >= RunStore.MaximumMarkers)
                _markerStatus = "All 128 moments are used. Recording continues.";
            else
            {
                label = label is null ? $"Moment {session.MarkerCount + 1}" : label.Trim();
                if (!RunStore.ValidMarkerLabel(label))
                    _markerStatus = "Use a moment label from 1 to 80 characters, without line breaks. Recording continues.";
                else
                {
                    session.PendingMarkers.Enqueue(new RunMarker(position.ElapsedSeconds, label));
                    session.MarkerCount++;
                    _markerStatus = $"Moment marked at {position.ElapsedSeconds:0.0}s.";
                    added = true;
                }
            }
        }
        NotifyStateChanged();
        return added;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync("Wisp closed before the run finished").ConfigureAwait(false);
    }

    private async Task<RecordedRun?> RecordAsync(Session session)
    {
        RunJournal? journal = null;
        RecordedRun? saved = null;
        try
        {
            var parser = new Fh6PacketParser();
            var builder = new RunSampleBuilder(session.StartedTimestamp);
            var samples = new List<RunSample>(8192);
            var markers = new List<RunMarker>();
            var header = new RecordedRun
            {
                StartedAtUtc = session.StartedAtUtc,
                Name = $"Run {session.StartedAtUtc.ToLocalTime():MMM d, h:mm tt}"
            };
            long rejected = 0;
            var lastFlush = session.StartedTimestamp;
            journal = Store.CreateJournal(header, session.JournalReservation);
            await foreach (var packet in session.Capture.ReadAllAsync().ConfigureAwait(false))
            {
                var stopped = Interlocked.Read(ref session.StopTimestamp);
                if (stopped != 0 && packet.Timestamp > stopped) break;
                if (packet.Timestamp >= session.DeadlineTimestamp)
                {
                    Interlocked.CompareExchange(ref session.StopTimestamp, session.DeadlineTimestamp, 0);
                    session.Reason = session.LimitReason;
                    break;
                }
                var receivedAt = session.StartedAtUtc + Stopwatch.GetElapsedTime(session.StartedTimestamp, packet.Timestamp);
                if (!parser.TryParse(packet.Bytes.Span, receivedAt, out var state, out _, packet.Timestamp) || state is null)
                {
                    rejected++;
                    builder.MarkGap();
                    continue;
                }
                Interlocked.Exchange(ref session.LastValidTimestamp, packet.Timestamp);
                if (state.CarOrdinal <= 0 || (!state.IsRaceOn && (state.CarOrdinal != session.CarOrdinal || state.Drivetrain != session.Drivetrain)))
                {
                    rejected++;
                    builder.MarkGap();
                    continue;
                }
                if (state.CarOrdinal != session.CarOrdinal || state.Drivetrain != session.Drivetrain)
                {
                    session.Incomplete = true;
                    session.Reason = "The car or drivetrain changed";
                    break;
                }
                while (session.Contexts.TryPeek(out var pending) && pending.EffectiveTimestamp <= packet.Timestamp)
                {
                    session.Contexts.TryDequeue(out pending);
                    if (pending is not null && (session.Context is null || pending.EffectiveTimestamp >= session.Context.EffectiveTimestamp)) session.Context = pending;
                }
                var sample = builder.Add(state, packet.Timestamp, packet.Sequence, session.Context);
                if (sample.ElapsedSeconds > 600) { session.Reason = "10-minute recording limit reached"; break; }
                try { RunStore.ValidateSample(sample, samples.LastOrDefault()); }
                catch (InvalidDataException) { rejected++; builder.MarkGap(); continue; }
                samples.Add(sample);
                await journal.AppendAsync(sample).ConfigureAwait(false);
                var validUntil = Math.Min(packet.Timestamp + (long)(Stopwatch.Frequency * .3),
                    session.Context?.DrivingValidUntilTimestamp ?? long.MaxValue);
                if (session.Context is { } markerContext)
                    validUntil = Math.Min(validUntil, markerContext.EffectiveTimestamp + (long)(Stopwatch.Frequency * .3));
                Volatile.Write(ref session.MarkerPosition, sample.IsDriving
                    ? new MarkerPosition(sample.ElapsedSeconds, packet.Sequence, validUntil) : null);
                await WritePendingMarkersAsync().ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(lastFlush, packet.Timestamp) >= TimeSpan.FromSeconds(1))
                {
                    await journal.FlushAsync().ConfigureAwait(false);
                    lastFlush = packet.Timestamp;
                }
                if (samples.Count >= RunStore.MaximumSamples || sample.ElapsedSeconds >= 600)
                {
                    session.Reason = samples.Count >= RunStore.MaximumSamples ? "Recording sample limit reached" : "10-minute recording limit reached";
                    break;
                }
            }
            lock (_gate)
            {
                Interlocked.CompareExchange(ref session.StopTimestamp, Math.Min(Stopwatch.GetTimestamp(), session.DeadlineTimestamp), 0);
                session.Stopping = true;
                if (session.Reason is null) { session.Reason = SafeReason(session.Capture.CompletionReason); session.Incomplete = true; }
                _status = "Preparing your run…";
            }
            _receiver.EndRunCapture(session.Capture, session.Reason);
            NotifyStateChanged();
            await WritePendingMarkersAsync().ConfigureAwait(false);
            if (samples.Count == 0)
            {
                _status = "No usable telemetry was recorded.";
                await journal.DisposeAsync().ConfigureAwait(false);
                journal.RemoveAfterSave();
                return null;
            }
            saved = header with
            {
                Samples = samples.ToArray(),
                Markers = markers.ToArray(),
                FinishReason = session.Reason,
                IsIncomplete = session.Incomplete || builder.HasGap || rejected > 0 || session.Capture.DroppedDatagrams > 0 ||
                    Stopwatch.GetElapsedTime(Interlocked.Read(ref session.LastValidTimestamp), EndTimestamp(session)).TotalMilliseconds > 300,
                RejectedDatagrams = rejected,
                DroppedDatagrams = session.Capture.DroppedDatagrams
            };
            await Store.SaveAsync(saved).ConfigureAwait(false);
            try
            {
                await journal.DisposeAsync().ConfigureAwait(false);
                journal.RemoveAfterSave();
                _status = saved.IsIncomplete ? "Run saved with gaps" : "Run saved";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _status = "Run saved; temporary recovery data was kept.";
            }
            return saved;

            async Task WritePendingMarkersAsync()
            {
                while (session.PendingMarkers.TryDequeue(out var marker))
                {
                    await journal.AppendMarkerAsync(marker).ConfigureAwait(false);
                    markers.Add(marker);
                }
            }
        }
        catch (Exception error) when (error is not StackOverflowException)
        {
            saved = null;
            _error = error is RunLibraryFullException ? "The run library is full. Export or remove a run before recording another." :
                "The run could not be saved. Any recovery data has been kept. Live telemetry is still running.";
            _status = _error;
            return null;
        }
        finally
        {
            _receiver.EndRunCapture(session.Capture, session.Reason ?? "Recording stopped after an error");
            if (journal is not null)
            {
                try { await journal.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            }
            lock (_gate)
            {
                Interlocked.Exchange(ref _lastElapsedTicks, Stopwatch.GetElapsedTime(session.StartedTimestamp, EndTimestamp(session)).Ticks);
                if (ReferenceEquals(_session, session)) _session = null;
            }
            NotifyStateChanged();
            if (saved is not null) NotifyRunSaved(saved);
        }
    }

    private static string SafeReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "Recording stopped" :
        new string(reason.Where(character => !char.IsControl(character)).Take(200).ToArray());
    private static long EndTimestamp(Session session) => Interlocked.Read(ref session.StopTimestamp) is var timestamp && timestamp != 0 ? timestamp : Math.Min(Stopwatch.GetTimestamp(), session.DeadlineTimestamp);
    private void NotifyStateChanged()
    {
        var snapshot = new StateSnapshot(IsRecording, IsPreparing, CanStart, (int)Elapsed.TotalSeconds, Status, Error, MarkerStatus, Volatile.Read(ref _session)?.MarkerCount ?? 0);
        lock (_notificationGate)
        {
            if (_lastNotified == snapshot) return;
            _lastNotified = snapshot;
        }
        var handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { /* Subscribers cannot affect recording. */ }
    }

    private readonly record struct StateSnapshot(bool IsRecording, bool IsPreparing, bool CanStart, int ElapsedSeconds, string Status, string? Error, string MarkerStatus, int MarkerCount);
    private sealed record MarkerPosition(double ElapsedSeconds, long Sequence, long ValidUntilTimestamp);
    private void NotifyRunSaved(RecordedRun run)
    {
        var handlers = RunSaved;
        if (handlers is null) return;
        foreach (Action<RecordedRun> handler in handlers.GetInvocationList())
            try { handler(run); } catch { /* Subscribers cannot affect the saved run. */ }
    }

    private sealed class Session(RunDatagramCapture capture, int carOrdinal, DrivetrainType drivetrain, RunRecordingContext? context,
        RunJournalStartReservation reservation, RunRecordingOptions options)
    {
        internal readonly RunDatagramCapture Capture = capture;
        internal readonly RunJournalStartReservation JournalReservation = reservation;
        internal readonly int CarOrdinal = carOrdinal;
        internal readonly DrivetrainType Drivetrain = drivetrain;
        internal readonly long StartedTimestamp = Stopwatch.GetTimestamp();
        internal long DeadlineTimestamp => StartedTimestamp + (long)Math.Ceiling(options.Limit.TotalSeconds * Stopwatch.Frequency);
        internal string LimitReason => options.StopAfter is null ? "10-minute recording limit reached" : "Timed recording completed";
        internal readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
        internal readonly ConcurrentQueue<RunRecordingContext> Contexts = new();
        internal readonly ConcurrentQueue<RunMarker> PendingMarkers = new();
        internal MarkerPosition? MarkerPosition;
        internal int MarkerCount;
        internal RunRecordingContext? Context = context;
        internal long LastValidTimestamp = Stopwatch.GetTimestamp();
        internal volatile bool Stopping;
        internal volatile bool Incomplete;
        internal long StopTimestamp;
        internal string? Reason;
        internal Task<RecordedRun?> Completion = Task.FromResult<RecordedRun?>(null);
    }
}

internal sealed class RunSampleBuilder(long startedTimestamp)
{
    private long _lastTimestamp = startedTimestamp;
    private long _lastSequence;
    private uint? _lastGameTimestamp;
    private double _elapsed;
    private int _segment;
    private bool _gap;
    private RunSample? _previous;
    internal bool HasGap { get; private set; }
    internal void MarkGap() { _gap = true; HasGap = true; }

    internal RunSample Add(VehicleState state, long timestamp, long sequence, RunRecordingContext? context)
    {
        var receiptDelta = Math.Max(0, Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds);
        var gameDelta = _lastGameTimestamp is { } previousGame ? unchecked(state.GameTimestampMilliseconds - previousGame) : 0u;
        if (_lastGameTimestamp is not null)
        {
            _elapsed += gameDelta <= 250 ? gameDelta / 1000.0 : receiptDelta;
            if (gameDelta > 250 || receiptDelta > .25 || timestamp < _lastTimestamp || sequence != _lastSequence + 1) MarkGap();
        }
        var validContext = context is not null && context.CarOrdinal == state.CarOrdinal && context.Drivetrain == state.Drivetrain &&
            timestamp >= context.EffectiveTimestamp && Stopwatch.GetElapsedTime(context.EffectiveTimestamp, timestamp).TotalSeconds <= .3;
        var isDriving = validContext && context!.IsDriving && state.IsRaceOn &&
            (context.DrivingValidUntilTimestamp is null || timestamp <= context.DrivingValidUntilTimestamp.Value);
        RollingRadii? radii = validContext && context!.FrontRadiusMeters is { } front && context.RearRadiusMeters is { } rear && new RollingRadii(front, rear).IsPlausible
            ? new RollingRadii(front, rear) : null;
        if (!isDriving) HasGap = true;
        if (_previous is not null && (_gap || _previous.IsDriving != isDriving ||
            _previous.FrontRadiusMeters != radii?.FrontMeters || _previous.RearRadiusMeters != radii?.RearMeters)) _segment++;
        var sample = new RunSample
        {
            ElapsedSeconds = _elapsed,
            Segment = _segment,
            IsDriving = isDriving,
            State = state with { ReceivedTimestamp = null },
            WheelSpeedMetersPerSecond = radii is { } trusted ? DrivenWheelSpeed.MetersPerSecond(state, trusted) : null,
            FrontRadiusMeters = radii?.FrontMeters,
            RearRadiusMeters = radii?.RearMeters
        };
        _lastTimestamp = timestamp;
        _lastSequence = sequence;
        _lastGameTimestamp = state.GameTimestampMilliseconds;
        _previous = sample;
        _gap = false;
        return sample;
    }
}
