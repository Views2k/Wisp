using System.IO;
using System.Text.Json;

namespace Wisp.App.Supplementary;

// A separate atomic receipt preserves the control revision across restarts and policy expiry.
internal sealed class FeatureControlStore
{
    private readonly FeatureControlVerifier _verifier;
    private readonly string? _directory, _ownedRoot;
    private readonly object _gate = new();
    private VerifiedFeatureControlPolicy? _current;
    private bool _healthy = true;
    private const int MaximumReceiptBytes = 96 * 1024;
    internal bool IsConfigured => _verifier.IsConfigured && _directory is not null;
    internal bool IsHealthy => Volatile.Read(ref _healthy);
    internal long HighestRevision => Volatile.Read(ref _current)?.Policy.Revision ?? 0;
    internal string? LastFailureCode { get; private set; }

    internal FeatureControlStore(FeatureControlVerifier verifier, string? directory, string? ownedRoot = null)
    {
        _verifier = verifier;
        _directory = directory is null ? null : Path.GetFullPath(directory);
        _ownedRoot = ownedRoot is null ? null : Path.GetFullPath(ownedRoot);
        if (!IsConfigured) return;
        try { _current = ReadReceipt(); }
        catch (Exception error) when (IsExpected(error)) { _healthy = false; LastFailureCode = "cache-unavailable"; }
    }

    internal FeatureControlPolicy Current(DateTimeOffset now)
    {
        var current = Volatile.Read(ref _current);
        // A later disk error cannot cancel a policy already accepted in this process.
        return current?.Policy.IsCurrent(now) == true ? current.Policy : FeatureControlPolicy.Empty;
    }

    internal SupplementaryContentAcceptance Accept(ReadOnlyMemory<byte> bytes, DateTimeOffset now)
    {
        VerifiedFeatureControlPolicy incoming;
        try { incoming = _verifier.Verify(bytes, now); }
        catch (Exception error) when (IsExpected(error)) { return SupplementaryContentAcceptance.Rejected; }
        lock (_gate)
        {
            if (!_healthy || !IsConfigured) return SupplementaryContentAcceptance.CacheUnavailable;
            try
            {
                CheckDirectory();
                Directory.CreateDirectory(_directory!);
                CheckDirectory();
                using var lease = new FileStream(Path.Combine(_directory!, "controls.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                var disk = ReadReceipt();
                if (_current is not null && (disk is null || disk.Policy.Revision < _current.Policy.Revision ||
                    disk.Policy.Revision == _current.Policy.Revision && disk.PayloadHash != _current.PayloadHash))
                { Volatile.Write(ref _healthy, false); return SupplementaryContentAcceptance.CacheUnavailable; }
                if (disk is not null) Volatile.Write(ref _current, disk);
                if (_current is not null)
                {
                    if (incoming.Policy.Revision < _current.Policy.Revision ||
                        incoming.Policy.Revision == _current.Policy.Revision && incoming.PayloadHash != _current.PayloadHash)
                        return SupplementaryContentAcceptance.Rejected;
                    if (incoming.Policy.Revision == _current.Policy.Revision) return SupplementaryContentAcceptance.Unchanged;
                }
                WriteReceipt(incoming.Envelope);
                Volatile.Write(ref _current, incoming);
                LastFailureCode = null;
                return SupplementaryContentAcceptance.Accepted;
            }
            catch (Exception error) when (IsExpected(error))
            { LastFailureCode = "cache-unavailable"; return SupplementaryContentAcceptance.CacheUnavailable; }
        }
    }

    private VerifiedFeatureControlPolicy? ReadReceipt()
    {
        if (_directory is null || !Directory.Exists(_directory)) return null;
        CheckDirectory();
        var path = Path.Combine(_directory, "controls.json");
        if (!File.Exists(path)) return null;
        CheckFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (file.Length is <= 0 or > MaximumReceiptBytes) throw new SupplementaryValidationException("cache-size");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        using var document = SupplementaryJson.Parse(bytes, MaximumReceiptBytes);
        var p = SupplementaryJson.Object(document.RootElement, "schemaVersion", "envelope");
        SupplementaryJson.Require(SupplementaryJson.Int(p["schemaVersion"]) == 1, "cache-schema");
        return _verifier.Verify(SupplementaryJson.Base64(p["envelope"], FeatureControlVerifier.MaximumEnvelopeBytes),
            DateTimeOffset.UtcNow, cached: true);
    }

    private void WriteReceipt(byte[] envelope)
    {
        var path = Path.Combine(_directory!, "controls.json");
        if (File.Exists(path)) CheckFile(path);
        var temporary = Path.Combine(_directory!, $"controls-{Guid.NewGuid():N}.tmp");
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
        SupplementaryPaths.CheckDirectory(_directory!, _ownedRoot);
        var lease = Path.Combine(_directory!, "controls.lock");
        if (File.Exists(lease)) CheckFile(lease);
    }

    private static void CheckFile(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Feature control storage cannot follow a file link."); }

    private static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or SupplementaryValidationException or JsonException or FormatException or ArgumentException;
}
