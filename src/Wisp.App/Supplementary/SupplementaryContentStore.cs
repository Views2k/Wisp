using System.IO;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal enum SupplementaryContentAcceptance { Accepted, Unchanged, Rejected, CacheUnavailable }

// One atomic signed receipt is both cache and replay floor. A corrupt receipt fails closed instead of forgetting its floor.
internal sealed class SupplementaryContentStore
{
    private readonly SupplementaryContentVerifier _verifier;
    private readonly string? _directory;
    private readonly object _gate = new();
    private SupplementaryVerifiedContent? _current;
    private bool _healthy = true;
    private const int MaximumReceiptBytes = 180 * 1024;
    internal bool IsHealthy => Volatile.Read(ref _healthy);
    internal string? LastFailureCode { get; private set; }
    internal bool IsConfigured => _verifier.IsConfigured && _directory is not null;
    internal long HighestRevision => Volatile.Read(ref _current)?.Snapshot.Revision ?? 0;

    internal SupplementaryContentStore(SupplementaryContentVerifier verifier, string? directory)
    {
        _verifier = verifier;
        _directory = directory is null ? null : Path.GetFullPath(directory);
        if (_directory is null || !_verifier.IsConfigured) return;
        try { _current = ReadReceipt(); }
        catch (Exception error) when (IsExpected(error)) { _healthy = false; RecordFailure(error); }
    }

    internal SupplementaryContentSnapshot Current(DateTimeOffset now)
    {
        // UI and event producers read the immutable snapshot without waiting on cache writes or filesystem leases.
        var current = Volatile.Read(ref _current);
        return IsHealthy && current?.Snapshot.IsCurrent(now) == true ? current.Snapshot : SupplementaryContentSnapshot.Empty;
    }

    internal SupplementaryContentAcceptance Accept(ReadOnlyMemory<byte> bytes, DateTimeOffset now)
    {
        SupplementaryVerifiedContent incoming;
        try { incoming = _verifier.Verify(bytes, now); }
        catch (Exception error) when (IsExpected(error)) { return SupplementaryContentAcceptance.Rejected; }
        lock (_gate)
        {
            if (!_healthy || _directory is null) return SupplementaryContentAcceptance.CacheUnavailable;
            var stage = "create-directory";
            try
            {
                Directory.CreateDirectory(_directory);
                stage = "check-directory";
                CheckDirectory();
                // Short file lease coordinates multiple store instances without an unbounded wait or shared global mutex.
                stage = "lease";
                using var lease = new FileStream(Path.Combine(_directory, "content.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                stage = "read-receipt";
                var disk = ReadReceipt();
                if (_current is not null && (disk is null || disk.Snapshot.Revision < _current.Snapshot.Revision))
                { Volatile.Write(ref _healthy, false); return SupplementaryContentAcceptance.CacheUnavailable; }
                if (disk is not null) Volatile.Write(ref _current, disk);
                if (_current is not null)
                {
                    if (incoming.Snapshot.Revision < _current.Snapshot.Revision ||
                        incoming.Snapshot.Revision == _current.Snapshot.Revision && incoming.PayloadHash != _current.PayloadHash)
                        return SupplementaryContentAcceptance.Rejected;
                    if (incoming.Snapshot.Revision == _current.Snapshot.Revision) return SupplementaryContentAcceptance.Unchanged;
                }
                stage = "write-receipt";
                WriteReceipt(incoming.Envelope);
                Volatile.Write(ref _current, incoming);
                LastFailureCode = null;
                return SupplementaryContentAcceptance.Accepted;
            }
            catch (Exception error) when (IsExpected(error)) { RecordFailure(error); LastFailureCode = stage + ":" + LastFailureCode; return SupplementaryContentAcceptance.CacheUnavailable; }
        }
    }

    private SupplementaryVerifiedContent? ReadReceipt()
    {
        if (_directory is null || !Directory.Exists(_directory)) return null;
        CheckDirectory();
        var path = Path.Combine(_directory, "content.json");
        if (!File.Exists(path)) return null;
        CheckFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (file.Length is <= 0 or > MaximumReceiptBytes) throw new SupplementaryValidationException("cache-size");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        using var document = SupplementaryJson.Parse(bytes, MaximumReceiptBytes);
        var p = SupplementaryJson.Object(document.RootElement, "schemaVersion", "envelope");
        SupplementaryJson.Require(SupplementaryJson.Int(p["schemaVersion"]) == 1, "cache-schema");
        return _verifier.Verify(SupplementaryJson.Base64(p["envelope"], SupplementaryContentVerifier.MaximumEnvelopeBytes),
            DateTimeOffset.UtcNow, cached: true);
    }

    private void WriteReceipt(byte[] envelope)
    {
        var path = Path.Combine(_directory!, "content.json");
        if (File.Exists(path)) CheckFile(path);
        var temporary = Path.Combine(_directory!, $"content-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, envelope = Convert.ToBase64String(envelope) });
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private void CheckDirectory()
    {
        for (var directory = new DirectoryInfo(_directory!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Supplementary cache cannot follow a directory link.");
        var lease = Path.Combine(_directory!, "content.lock");
        if (File.Exists(lease)) CheckFile(lease);
    }
    private static void CheckFile(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Supplementary cache cannot follow a file link."); }
    private static bool IsExpected(Exception e) => e is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or SupplementaryValidationException or JsonException or FormatException or ArgumentException;
    private void RecordFailure(Exception e) => LastFailureCode = e is SupplementaryValidationException validation
        ? validation.Code : e.GetType().Name + ":" + e.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}
