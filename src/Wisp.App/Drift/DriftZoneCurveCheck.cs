using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Wisp.Core;

namespace Wisp.App.Drift;

internal enum DriftZoneCheckOutcome
{
    Verified,
    NotFound,
    Unstable,
    Incomplete,
    Unavailable
}

// The game memory for one check and the handle that keeps it open.
internal sealed record DriftZoneScanSession(string BuildKey, IDriftZoneScanMemory Memory, IDisposable Owner);

internal sealed record DriftZoneCheckRecord(DriftZoneCheckOutcome Outcome, DateTimeOffset CheckedAt, int Entries);

// Remembers the last result for each game build on this PC, so a build is read once and not on every start.
internal sealed class DriftZoneCheckStore
{
    private const int MaximumBuilds = 32;
    private readonly string? _path;
    private ImmutableDictionary<string, DriftZoneCheckRecord> _records;

    internal DriftZoneCheckStore(string? path)
    {
        _path = path;
        _records = Load(path);
    }

    internal DriftZoneCheckRecord? Find(string buildKey) =>
        Volatile.Read(ref _records).TryGetValue(buildKey, out var record) ? record : null;

    internal void Save(string buildKey, DriftZoneCheckRecord record)
    {
        var records = Volatile.Read(ref _records).SetItem(buildKey, record);
        if (records.Count > MaximumBuilds)
        {
            records = records.OrderByDescending(static pair => pair.Value.CheckedAt).Take(MaximumBuilds)
                .ToImmutableDictionary(StringComparer.Ordinal);
        }

        Volatile.Write(ref _records, records);
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, records.ToDictionary(
                static pair => pair.Key, static pair => new Item(pair.Value.Outcome.ToString(), pair.Value.CheckedAt, pair.Value.Entries)))));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The result still holds for this run; the next start reads the build again.
        }
    }

    private static ImmutableDictionary<string, DriftZoneCheckRecord> Load(string? path)
    {
        var empty = ImmutableDictionary.Create<string, DriftZoneCheckRecord>(StringComparer.Ordinal);
        if (path is null)
        {
            return empty;
        }

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            {
                return empty;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (document is not { Version: 1, Builds: { } builds })
            {
                return empty;
            }

            var records = empty.ToBuilder();
            foreach (var (key, item) in builds.Take(MaximumBuilds))
            {
                if (key.Length is > 0 and <= 256 && item is not null && item.Entries is >= 0 and <= 100_000 &&
                    Enum.TryParse<DriftZoneCheckOutcome>(item.Outcome, out var outcome) && Enum.IsDefined(outcome))
                {
                    records[key] = new DriftZoneCheckRecord(outcome, item.CheckedAt, item.Entries);
                }
            }

            return records.ToImmutable();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return empty;
        }
    }

    private sealed record Document(int Version, Dictionary<string, Item>? Builds);

    private sealed record Item(string Outcome, DateTimeOffset CheckedAt, int Entries);
}

// Decides whether Drift Zone guidance may use the known scoring curve on the attached game build.
// A build in the catalog has it at once. Any other accepted build gets it only after the running game
// was read and held the curve; until then the gauge shows the measured angle alone. The read runs once
// per build on a background thread, and its result is kept for later starts.
internal sealed class DriftZoneCurveCheck : IDisposable
{
    internal static readonly TimeSpan RevalidateAfter = TimeSpan.FromDays(14);
    internal static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    private readonly DriftZoneScoringProfile _curve;
    private readonly Func<CancellationToken, DriftZoneScanSession?> _open;
    private readonly DriftZoneCheckStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string, double?> _report;
    private readonly Func<DriftZoneScanSession, CancellationToken, DriftZoneCurveScanResult> _scan;
    private readonly Action<Action> _run;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset NextAllowed)> _attempts = new(StringComparer.Ordinal);
    private readonly HashSet<(string BuildKey, string Outcome)> _reported = [];
    private bool _running;
    private bool _disposed;

    internal DriftZoneCurveCheck(
        DriftZoneScoringProfile curve,
        Func<CancellationToken, DriftZoneScanSession?> open,
        DriftZoneCheckStore store,
        Action<string, double?> report,
        TimeProvider? time = null,
        Func<DriftZoneScanSession, CancellationToken, DriftZoneCurveScanResult>? scan = null,
        Action<Action>? run = null)
    {
        _curve = curve ?? throw new ArgumentNullException(nameof(curve));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _time = time ?? TimeProvider.System;
        _scan = scan ?? ((session, cancellation) =>
            DriftZoneCurveScan.Run(session.Memory, _curve, new DriftZoneCurveScanOptions(), cancellation));
        _run = run ?? StartBackground;
    }

    // Called by the drift renderer on every frame in Drift Zone mode. It only looks at stored results and
    // may start the background read; it never waits for it.
    internal DriftZoneScoringProfile? Resolve(string? buildKey, DriftZoneScoringProfile? listed, bool driving, bool allowed)
    {
        if (buildKey is null)
        {
            return listed;
        }

        var record = _store.Find(buildKey);
        if (allowed && driving && IsDue(record))
        {
            TryStart(buildKey);
        }

        if (listed is not null)
        {
            return listed;
        }

        return allowed && record is { Outcome: DriftZoneCheckOutcome.Verified } ? _curve : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancellation.Cancel();
    }

    private bool IsDue(DriftZoneCheckRecord? record) => record switch
    {
        null => true,
        { Outcome: DriftZoneCheckOutcome.Verified } => _time.GetUtcNow() - record.CheckedAt >= RevalidateAfter,
        _ => _time.GetUtcNow() - record.CheckedAt >= RetryDelays[0]
    };

    private void TryStart(string buildKey)
    {
        lock (_gate)
        {
            if (_disposed || _running ||
                (_attempts.TryGetValue(buildKey, out var attempt) && _time.GetUtcNow() < attempt.NextAllowed))
            {
                return;
            }

            _running = true;
        }

        try
        {
            _run(() => Execute(buildKey));
        }
        catch (Exception error) when (error is OutOfMemoryException or ThreadStartException or InvalidOperationException)
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private void Execute(string buildKey)
    {
        var outcome = DriftZoneCheckOutcome.Unavailable;
        var entries = 0;
        double? duration = null;
        DriftZoneScanSession? session = null;
        try
        {
            session = _open(_cancellation.Token);
            if (session is not null && string.Equals(session.BuildKey, buildKey, StringComparison.Ordinal))
            {
                var result = _scan(session, _cancellation.Token);
                entries = result.Entries;
                duration = result.Duration.TotalMilliseconds;
                outcome = result.Verdict switch
                {
                    DriftZoneCurveVerdict.Verified => DriftZoneCheckOutcome.Verified,
                    DriftZoneCurveVerdict.NotFound => DriftZoneCheckOutcome.NotFound,
                    DriftZoneCurveVerdict.Unstable => DriftZoneCheckOutcome.Unstable,
                    _ => DriftZoneCheckOutcome.Incomplete
                };
            }
        }
        catch (OperationCanceledException)
        {
            outcome = DriftZoneCheckOutcome.Incomplete;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException)
        {
            outcome = DriftZoneCheckOutcome.Unavailable;
        }
        finally
        {
            try
            {
                session?.Owner.Dispose();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
            }

            Complete(buildKey, outcome, entries, duration);
        }
    }

    private void Complete(string buildKey, DriftZoneCheckOutcome outcome, int entries, double? duration)
    {
        var now = _time.GetUtcNow();
        var report = false;
        lock (_gate)
        {
            _running = false;
            if (_disposed)
            {
                return;
            }

            if (outcome == DriftZoneCheckOutcome.Verified)
            {
                _attempts.Remove(buildKey);
            }
            else
            {
                var count = _attempts.TryGetValue(buildKey, out var attempt) ? attempt.Count : 0;
                _attempts[buildKey] = (count + 1, now + RetryDelays[Math.Min(count, RetryDelays.Count - 1)]);
            }

            report = _reported.Add((buildKey, Token(outcome)));
        }

        // A read that did not finish says nothing about the build, so the stored result stays as it was.
        if (outcome is DriftZoneCheckOutcome.Verified or DriftZoneCheckOutcome.NotFound or DriftZoneCheckOutcome.Unstable)
        {
            _store.Save(buildKey, new DriftZoneCheckRecord(outcome, now, entries));
        }

        if (report)
        {
            _report(Token(outcome), duration);
        }
    }

    internal static string Token(DriftZoneCheckOutcome outcome) => outcome switch
    {
        DriftZoneCheckOutcome.Verified => "success",
        DriftZoneCheckOutcome.NotFound => "unsupported",
        DriftZoneCheckOutcome.Unstable => "unknown",
        DriftZoneCheckOutcome.Incomplete => "timeout",
        _ => "failure"
    };

    private static void StartBackground(Action work)
    {
        var thread = new Thread(() => work())
        {
            IsBackground = true,
            Name = "Wisp drift zone curve check",
            Priority = ThreadPriority.Lowest
        };
        thread.Start();
    }
}
