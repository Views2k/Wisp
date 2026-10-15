using System.Diagnostics;
using System.IO;
using Wisp.App.DebugLogging;

namespace Wisp.App;

internal sealed record NativeGameIdentityObservation(DateTimeOffset ObservedAt, string? GameBuild,
    string Platform, NativeGameVerification Verification);

// One metadata-only worker, independent of UDP and the render/telemetry threads.
// Never enables game memory reads or extends a compatibility admission decision.
internal sealed class NativeGameIdentityProbe : IDisposable
{
    internal static NativeGameIdentityProbe Current { get; } = new();
    private readonly object _gate = new();
    private readonly Func<NativeGameIdentityObservation> _inspect;
    private readonly TimeProvider _clock;
    private Task? _refresh;
    private DateTimeOffset _nextRefresh;
    private NativeGameIdentityObservation? _latest;
    private bool _disposed;
    private sealed record ProcessIdentity(int Id, DateTime Started, string Path, long Length, DateTime LastWrite);
    private ProcessIdentity? _completedIdentity;
    private NativeGameIdentityObservation? _completedObservation;
    internal event Action<NativeGameIdentityObservation>? Updated;

    internal NativeGameIdentityProbe(Func<NativeGameIdentityObservation>? inspect = null, TimeProvider? clock = null)
    {
        _inspect = inspect ?? Inspect;
        _clock = clock ?? TimeProvider.System;
    }

    internal NativeGameIdentityObservation? Recent
    {
        get
        {
            lock (_gate)
                return !_disposed && _latest is { } value && _clock.GetUtcNow() - value.ObservedAt <= TimeSpan.FromSeconds(60)
                    ? value : null;
        }
    }

    internal Task RequestRefresh()
    {
        lock (_gate)
        {
            if (_disposed || _clock.GetUtcNow() < _nextRefresh) return _refresh ?? Task.CompletedTask;
            if (_refresh is { IsCompleted: false }) return _refresh;
            _nextRefresh = _clock.GetUtcNow() + TimeSpan.FromSeconds(30);
            return _refresh = Task.Run(() =>
            {
                NativeGameIdentityObservation result;
                try { result = _inspect(); }
                catch (Exception error) when (NativeHudFingerprintCache.IsExpectedFailure(error) || error is NotSupportedException)
                { result = new(_clock.GetUtcNow(), null, "unknown", NativeGameVerification.Detailed(NativeGameVerification.AccessUnavailable)); }
                Action<NativeGameIdentityObservation>? updated;
                lock (_gate)
                {
                    if (_disposed) return;
                    _latest = result;
                    updated = Updated;
                }
                updated?.Invoke(result);
            });
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _latest = null; Updated = null; }
    }

    private NativeGameIdentityObservation Inspect()
    {
        var processes = Process.GetProcessesByName("ForzaHorizon6");
        try
        {
            if (processes.Length == 0)
            {
                _completedIdentity = null;
                _completedObservation = null;
                return new(DateTimeOffset.UtcNow, null, "unknown", NativeGameVerification.Detailed(new("not-running", "game-not-running")));
            }
            if (processes.Length != 1)
                return new(DateTimeOffset.UtcNow, null, "unknown", NativeGameVerification.Detailed(NativeGameVerification.AccessUnavailable));
            var process = processes[0];
            var path = process.MainModule?.FileName;
            if (path is null) return new(DateTimeOffset.UtcNow, null, "unknown", NativeGameVerification.Detailed(NativeGameVerification.AccessUnavailable));
            var before = process.StartTime;
            var file = new FileInfo(path);
            var identity = new ProcessIdentity(process.Id, before, path, file.Length, file.LastWriteTimeUtc);
            // Completed trust checks remain valid for the same live process/file identity.
            // Pending and unavailable observations are retried without repeating completed hashes.
            if (identity == _completedIdentity && _completedObservation is { } completed && !process.HasExited)
                return completed with { ObservedAt = DateTimeOffset.UtcNow };
            var version = HealthContextRecorder.SafeGameVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
            var result = NativeGameVerification.DescribeRejected(process, path, null, version);
            process.Refresh();
            if (process.HasExited || process.StartTime != before || !string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                return new(DateTimeOffset.UtcNow, null, "unknown", NativeGameVerification.IdentityPending);
            file.Refresh();
            if (file.Length != identity.Length || file.LastWriteTimeUtc != identity.LastWrite)
                return new(DateTimeOffset.UtcNow, null, "unknown", NativeGameVerification.IdentityPending);
            var observation = new NativeGameIdentityObservation(DateTimeOffset.UtcNow, result.GameVersion,
                result.Platform switch { DiagnosticGamePlatform.Steam => "steam", DiagnosticGamePlatform.XboxStore => "store", _ => "unknown" },
                result.Verification.WithCompatibility("unknown"));
            if (observation.Verification.Inspection == "complete")
            {
                _completedIdentity = identity;
                _completedObservation = observation;
            }
            return observation;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
