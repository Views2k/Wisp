using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NativeFile = Wisp.App.Clips.ClipBufferStore.NativeFile;

namespace Wisp.App.CrashDiagnostics;

internal interface ICrashReportSink
{
    bool TrySave(CrashReport report);
}

internal sealed class CrashReportStore(string directory, Action<string, byte[]>? atomicWrite = null) : ICrashReportSink
{
    internal const int MaximumReports = 8;
    internal const int MaximumReportBytes = 128 * 1024;
    internal const int MaximumStoreBytes = MaximumReports * MaximumReportBytes + 1024;
    internal const int MaximumCleanupEntries = 64;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };
    private static readonly Lazy<CrashReportStore> Production = new(ForCurrentUser);
    internal static CrashReportStore Current => Production.Value;
    private readonly object _gate = new();
    private long _recoveryGeneration;
    internal long RecoveryGeneration { get { lock (_gate) return _recoveryGeneration; } }
    private readonly string _path = Path.Combine(directory, "reports.json");
    private sealed record Entry(CrashReport Report, bool Acknowledged);
    private sealed record Document(int Version, Entry[] Reports);

    internal static CrashReportStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "CrashReports"));

    public bool TrySave(CrashReport report) => TrySave(report, null);
    internal bool TrySaveRecovered(CrashReport report, long generation) => TrySave(report, generation);
    private bool TrySave(CrashReport report, long? recoveryGeneration)
    {
        if (!Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(100))) return false;
        try
        {
            if (recoveryGeneration is { } expected && expected != _recoveryGeneration) return false;
            var safe = report.Sanitize();
            if (safe is null || JsonSerializer.SerializeToUtf8Bytes(safe, Json).Length > MaximumReportBytes) return false;
            var entries = new[] { new Entry(safe, false) }.Concat(Read().Where(entry => entry.Report.Id != safe.Id))
                .Take(MaximumReports).ToArray();
            Write(entries);
            return true;
        }
        catch (Exception) { return false; } // Crash logging must never replace the original failure.
        finally { Monitor.Exit(_gate); }
    }

    internal CrashReport? LatestPending()
    {
        lock (_gate)
        {
            try
            {
                _ = CleanupOwnedFiles(includeReport: false);
                return Read().FirstOrDefault(entry => !entry.Acknowledged)?.Report;
            }
            catch (Exception) { return null; }
        }
    }

    internal bool AcknowledgeThrough(Guid id)
    {
        lock (_gate)
        {
            try
            {
                var entries = Read();
                var selected = Array.FindIndex(entries, entry => entry.Report.Id == id);
                if (selected < 0) { _recoveryGeneration++; return true; }
                for (var i = selected; i < entries.Length; i++) entries[i] = entries[i] with { Acknowledged = true };
                Write(entries);
                _recoveryGeneration++;
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    internal bool TryReadAll(out CrashReport[] reports)
    {
        lock (_gate)
        {
            try
            {
                reports = Read(out var available).Select(entry => entry.Report).ToArray();
                return available;
            }
            catch (Exception) { reports = []; return false; }
        }
    }

    internal bool TryDeleteAll()
    {
        lock (_gate)
        {
            _recoveryGeneration++;
            return CleanupOwnedFiles(includeReport: true);
        }
    }

    // Startup and explicit deletion only; fatal saves never enumerate the directory.
    private bool CleanupOwnedFiles(bool includeReport)
    {
        try
        {
            using var parent = NativeFile.OpenDirectoryTree(Path.GetFullPath(directory), create: false);
            var complete = !includeReport || DeleteOwnedFile(parent, "reports.json");
            var names = Directory.EnumerateFileSystemEntries(directory).Take(MaximumCleanupEntries + 1)
                .Select(Path.GetFileName).ToArray();
            if (names.Length > MaximumCleanupEntries) complete = false;
            foreach (var name in names.Take(MaximumCleanupEntries))
            {
                if (name is { Length: 44 } && name.StartsWith("reports-", StringComparison.Ordinal) &&
                    name.EndsWith(".tmp", StringComparison.Ordinal) &&
                    name.AsSpan(8, 32).IndexOfAnyExcept("0123456789abcdef") < 0)
                    complete &= DeleteOwnedFile(parent, name);
            }
            return complete;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 2 or 3) { return true; }
        catch (Exception) { return false; }
    }

    private static bool DeleteOwnedFile(SafeFileHandle parent, string name)
    {
        try
        {
            // Relative open refuses reparse points/directories. This handle excludes
            // write/delete sharing, preventing replacement before its deletion.
            using var file = NativeFile.Open(parent, name, NativeFile.ReadAccess | NativeFile.DeleteAccess, 1, 1, false);
            NativeFile.Delete(file);
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 2) { return true; }
        catch (Exception) { return false; }
    }

    private Entry[] Read() => Read(out _);

    private Entry[] Read(out bool available)
    {
        available = true;
        if (!File.Exists(_path)) return [];
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumStoreBytes) { available = false; return []; }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(bytes, Json); }
        catch (JsonException) { available = false; return []; }
        if (document is not { Version: 1, Reports.Length: <= MaximumReports }) { available = false; return []; }
        return document.Reports.Where(entry => entry?.Report is not null)
            .Select(entry => (Entry: entry, Safe: entry.Report.Sanitize()))
            .Where(value => value.Safe is not null)
            .Select(value => new Entry(value.Safe!, value.Entry.Acknowledged)).ToArray();
    }

    private void Write(Entry[] entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries), Json);
        if (bytes.Length > MaximumStoreBytes) throw new IOException("The error report limit was reached.");
        if (atomicWrite is not null) { atomicWrite(_path, bytes); return; }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"reports-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception) { }
        }
    }
}
