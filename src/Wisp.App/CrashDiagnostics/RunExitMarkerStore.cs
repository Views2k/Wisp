using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Wisp.App.DebugLogging;
using NativeFile = Wisp.App.Clips.ClipBufferStore.NativeFile;

namespace Wisp.App.CrashDiagnostics;

internal enum ShutdownIntent { None, Requested, OperatingSystem, Update }

internal sealed record RunExitMarker(Guid RunId, int ProcessId, long ProcessCreationFileTime,
    DateTimeOffset StartedAtUtc, string ExecutableVersion, DiagnosticBuildIdentity Build, bool CleanExit = false)
{
    public HealthContextSnapshot? Context { get; init; }
    public ShutdownIntent ShutdownIntent { get; init; }

    internal RunExitMarker? Sanitize()
    {
        if (RunId == Guid.Empty || ProcessId <= 0 || ProcessCreationFileTime <= 0 || Build is null ||
            StartedAtUtc.Offset != TimeSpan.Zero || !SafeVersion(ExecutableVersion) || !Enum.IsDefined(ShutdownIntent)) return null;
        try
        {
            if (DateTime.FromFileTimeUtc(ProcessCreationFileTime).Ticks != StartedAtUtc.UtcTicks) return null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
        var report = (ToReport(StartedAtUtc) with { Origin = CrashOrigin.BackgroundThread }).Sanitize();
        if (report?.ModuleVersionId is not { } module || report.ProcessArchitecture is not { } architecture) return null;
        return this with
        {
            Build = new(report.WispVersion, report.PrivateBuildId, module,
            report.RuntimeVersion, report.WindowsVersion, architecture),
            Context = ValidContext(Context) ? Context : null
        };
    }

    internal CrashReport ToReport(DateTimeOffset detectedAtUtc) => new(RunId, detectedAtUtc,
        Build.WispVersion, Build.PrivateBuildId, Build.RuntimeVersion, CrashOrigin.UnexpectedExit, true, [])
    {
        RunId = RunId,
        ModuleVersionId = Build.ModuleVersionId,
        WindowsVersion = Build.WindowsVersion,
        ProcessArchitecture = Build.ProcessArchitecture,
        Context = ValidContext(Context) ? Context : null
    };

    private bool ValidContext(HealthContextSnapshot? context) => HealthContextRecorder.IsValidSnapshot(context) &&
        context!.Samples.Length <= 3 && context.Breadcrumbs.Length == 0 && context.CapturedAtUtc >= StartedAtUtc &&
        context.Samples.All(sample => sample.TimestampUtc >= StartedAtUtc && sample.TimestampUtc <= context.CapturedAtUtc &&
            context.CapturedAtUtc - sample.TimestampUtc <= TimeSpan.FromSeconds(30));

    private static bool SafeVersion(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(character => char.IsAsciiDigit(character) || character == '.') && Version.TryParse(value, out _);
}

// One per-user lease also excludes a live owner in another Windows session.
// A clean marker records completed application shutdown, not a crash diagnosis.
internal sealed class RunExitMarkerStore : IDisposable
{
    private const int MaximumMarkerBytes = 8192;
    private readonly string _directory;
    private readonly SafeFileHandle _parent, _lease;
    private RunExitMarker _current;
    private readonly object _gate = new();
    private Timer? _checkpointTimer;
    private bool _disposed;
    internal RunExitMarker? Previous { get; }
    internal Guid RunId => _current.RunId;
    internal DateTimeOffset StartedAtUtc => _current.StartedAtUtc;

    private RunExitMarkerStore(string directory, SafeFileHandle parent, SafeFileHandle lease,
        RunExitMarker current, RunExitMarker? previous)
    { _directory = directory; _parent = parent; _lease = lease; _current = current; Previous = previous; }

    internal static RunExitMarkerStore? BeginCurrent(Guid runId)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var started = process.StartTime.ToUniversalTime();
            var version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
            var executableVersion = string.Create(CultureInfo.InvariantCulture,
                $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}");
            var store = TryBegin(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wisp", "CrashReports"), new(runId, Environment.ProcessId, started.ToFileTimeUtc(),
                new DateTimeOffset(started), executableVersion, DiagnosticBuildIdentity.Current));
            if (store is not null)
                store._checkpointTimer = new Timer(_ => store.Checkpoint(HealthContextRecorder.Current.Snapshot()), null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            return store;
        }
        catch (Exception) { return null; }
    }

    internal static RunExitMarkerStore? TryBegin(string directory, RunExitMarker current)
    {
        SafeFileHandle? parent = null, lease = null;
        try
        {
            if (current.Sanitize() is not { } safe || safe.CleanExit) return null;
            directory = Path.GetFullPath(directory);
            parent = NativeFile.OpenDirectoryTree(directory, create: true);
            lease = NativeFile.Open(parent, "running.lock", NativeFile.ReadWriteDelete, 0, 3, false);
            var previous = Read(parent);
            var result = new RunExitMarkerStore(directory, parent, lease, safe,
                previous is { CleanExit: false } && previous.StartedAtUtc < safe.StartedAtUtc ? previous : null);
            result.CleanupTemporaryMarkers();
            result.Write(safe);
            parent = null; lease = null;
            return result;
        }
        catch (Exception) { return null; }
        finally { lease?.Dispose(); parent?.Dispose(); }
    }

    internal bool MarkClean()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                if (Read(_parent)?.RunId != _current.RunId) return false;
                Write(_current with { CleanExit = true });
                _current = _current with { CleanExit = true };
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    internal bool MarkShutdownStarted(ShutdownIntent intent)
    {
        if (intent == ShutdownIntent.None || !Enum.IsDefined(intent)) return false;
        lock (_gate)
        {
            if (_disposed || _current.CleanExit) return false;
            try
            {
                if (Read(_parent)?.RunId != _current.RunId) return false;
                var next = _current with { ShutdownIntent = _current.ShutdownIntent == ShutdownIntent.None ? intent : _current.ShutdownIntent };
                Write(next); _current = next;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    internal bool Checkpoint(HealthContextSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || _current.CleanExit || !HealthContextRecorder.IsValidSnapshot(snapshot)) return false;
            var samples = snapshot.Samples.Where(sample => sample.TimestampUtc >= _current.StartedAtUtc &&
                sample.TimestampUtc <= snapshot.CapturedAtUtc && snapshot.CapturedAtUtc - sample.TimestampUtc <= TimeSpan.FromSeconds(30))
                .OrderBy(sample => sample.TimestampUtc).TakeLast(3).ToImmutableArray();
            if (samples.Length == 0 || _current.Context?.CapturedAtUtc >= snapshot.CapturedAtUtc) return false;
            var next = _current with { Context = new(snapshot.CapturedAtUtc, samples, []) };
            try
            {
                if (Read(_parent)?.RunId != _current.RunId) return false;
                Write(next);
                _current = next;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    private static RunExitMarker? Read(SafeFileHandle parent)
    {
        try
        {
            using var file = NativeFile.Open(parent, "running.json", NativeFile.ReadAccess, 1, 1, false);
            using var stream = NativeFile.Stream(file, FileAccess.Read);
            if (stream.Length is <= 0 or > MaximumMarkerBytes) return null;
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            return JsonSerializer.Deserialize<RunExitMarker>(bytes, new JsonSerializerOptions { MaxDepth = 8 })?.Sanitize();
        }
        catch (Exception) { return null; }
    }

    private void Write(RunExitMarker marker)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker);
        if (bytes.Length > MaximumMarkerBytes) throw new IOException("The running marker is too large.");
        var temporaryName = $"running-{Guid.NewGuid():N}.tmp";
        try
        {
            using var file = NativeFile.Open(_parent, temporaryName, NativeFile.ReadWriteDelete, 0, 2, false);
            using (var stream = NativeFile.Stream(file, FileAccess.ReadWrite))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            NativeFile.RenameNew(file, _parent, "running.json", replaceExisting: true);
        }
        finally { DeleteOwned(temporaryName); }
    }

    private void CleanupTemporaryMarkers()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_directory).Take(64))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 44 && name.StartsWith("running-", StringComparison.Ordinal) &&
                name.EndsWith(".tmp", StringComparison.Ordinal) && name.AsSpan(8, 32).IndexOfAnyExcept("0123456789abcdef") < 0)
                DeleteOwned(name);
        }
    }

    private void DeleteOwned(string name)
    {
        try
        {
            using var file = NativeFile.Open(_parent, name, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
            NativeFile.Delete(file);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _checkpointTimer?.Dispose(); _lease.Dispose(); _parent.Dispose();
        }
    }
}
