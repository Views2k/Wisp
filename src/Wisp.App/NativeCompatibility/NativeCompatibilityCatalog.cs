using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App;

public enum NativeCompatibilityInstallCode
{
    Installed,
    AlreadyInstalled,
    InvalidEnvelope,
    UntrustedPublisher,
    InvalidSignature,
    NotYetValid,
    Expired,
    RollbackRejected,
    RevisionConflict,
    CatalogFull,
    CacheUnavailable,
    CacheWriteFailed
}

public sealed record NativeCompatibilityInstallResult(
    bool Success,
    bool Changed,
    NativeCompatibilityInstallCode Code,
    string Message,
    NativeHudCompatibilityPack? Pack = null);

public sealed class NativeCompatibilityEnvelopeException : FormatException
{
    internal NativeCompatibilityEnvelopeException(NativeCompatibilityInstallCode code, string message)
        : base(message) => Code = code;

    public NativeCompatibilityInstallCode Code { get; }
}

public sealed class NativeVerifiedCompatibilityEnvelope
{
    internal NativeVerifiedCompatibilityEnvelope(
        NativeHudCompatibilityPack pack,
        string keyId,
        string payloadSha256,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        Pack = pack;
        KeyId = keyId;
        PayloadSha256 = payloadSha256;
        IssuedUtc = issuedUtc;
        ExpiresUtc = expiresUtc;
    }

    public NativeHudCompatibilityPack Pack { get; }
    public string KeyId { get; }
    public string PayloadSha256 { get; }
    public DateTimeOffset IssuedUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
}

/// <summary>Public, deterministic signing inputs; this code never creates or persists a private key.</summary>
public static class NativeCompatibilitySignature
{
    // Exact bytes: ASCII "Wisp.NativeHud.Compatibility/v1", one NUL byte, then the raw UTF-8 payload.
    // Sign with ECDSA NIST P-256 / SHA-256 and a 64-byte IEEE P1363 (r || s) signature, not DER.
    public const string DomainPrefix = "Wisp.NativeHud.Compatibility/v1\0";

    public static byte[] CreateSigningInput(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > NativeCompatibilityEnvelope.MaximumPayloadBytes)
        {
            throw new ArgumentException("The signed payload is empty or exceeds the size limit.", nameof(payload));
        }

        var prefixLength = Encoding.ASCII.GetByteCount(DomainPrefix);
        var input = new byte[prefixLength + payload.Length];
        Encoding.ASCII.GetBytes(DomainPrefix, input);
        payload.CopyTo(input.AsSpan(prefixLength));
        return input;
    }

    /// <summary>The uppercase SHA-256 of the exact DER SubjectPublicKeyInfo bytes being pinned.</summary>
    public static string GetKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        using var key = ImportPublicKey(subjectPublicKeyInfo);
        return Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
    }

    internal static ECDsa ImportPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (subjectPublicKeyInfo.IsEmpty || subjectPublicKeyInfo.Length > 1024)
        {
            throw new CryptographicException("The configured publisher public key is invalid.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length || key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new CryptographicException("Publisher public keys must use the named NIST P-256 curve.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A small pinned-key signed-pack protocol, not TUF. Envelope and payload property names are case sensitive.
/// Payload: format=1, purpose="wisp-native-hud-compatibility", issuedUtc, expiresUtc, pack.
/// Envelope: format=1, keyId=SHA256(SPKI), payload=canonical base64, signature=canonical base64.
/// UTC timestamps require a literal Z and zero to seven fractional-second digits. No executable content is accepted.
/// </summary>
public static class NativeCompatibilityEnvelope
{
    public const int MaximumEnvelopeBytes = 128 * 1024;
    public const int MaximumPayloadBytes = 96 * 1024;
    public const int MaximumTrustedKeys = 16;
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static NativeVerifiedCompatibilityEnvelope Verify(
        ReadOnlySpan<byte> envelope,
        IReadOnlyDictionary<string, byte[]> trustedPublicKeys,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        if (envelope.IsEmpty || envelope.Length > MaximumEnvelopeBytes)
        {
            throw Invalid("The signed envelope is empty or exceeds the size limit.");
        }

        try
        {
            using var document = NativeCompatibilityJson.Parse(envelope, MaximumEnvelopeBytes);
            var root = NativeCompatibilityJson.ReadObject(document.RootElement, "format", "keyId", "payload", "signature");
            if (NativeCompatibilityJson.ReadInt32(root["format"]) != 1)
            {
                throw Invalid("The signed envelope format is unsupported.");
            }

            var keyId = NativeCompatibilityJson.ReadHash(root["keyId"]);
            var payload = ReadBase64(root["payload"], MaximumPayloadBytes);
            var signature = ReadBase64(root["signature"], 64);
            if (signature.Length != 64)
            {
                throw Invalid("The signed envelope must contain a 64-byte P1363 signature.");
            }

            var publicKey = FindPublicKey(trustedPublicKeys, keyId);
            using (var key = NativeCompatibilitySignature.ImportPublicKey(publicKey))
            {
                if (!key.VerifyData(
                        NativeCompatibilitySignature.CreateSigningInput(payload),
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    throw new NativeCompatibilityEnvelopeException(
                        NativeCompatibilityInstallCode.InvalidSignature,
                        "The compatibility signature is invalid.");
                }
            }

            using var payloadDocument = NativeCompatibilityJson.Parse(payload, MaximumPayloadBytes);
            var signed = NativeCompatibilityJson.ReadObject(
                payloadDocument.RootElement, "format", "purpose", "issuedUtc", "expiresUtc", "pack");
            if (NativeCompatibilityJson.ReadInt32(signed["format"]) != 1 ||
                NativeCompatibilityJson.ReadString(signed["purpose"]) != "wisp-native-hud-compatibility")
            {
                throw Invalid("The signed payload format or purpose is unsupported.");
            }

            var issuedUtc = NativeCompatibilityJson.ReadUtc(signed["issuedUtc"]);
            var expiresUtc = NativeCompatibilityJson.ReadUtc(signed["expiresUtc"]);
            if (issuedUtc >= expiresUtc)
            {
                throw Invalid("The signed payload validity interval is invalid.");
            }

            if (issuedUtc - now > ClockSkew)
            {
                throw new NativeCompatibilityEnvelopeException(
                    NativeCompatibilityInstallCode.NotYetValid,
                    "The signed compatibility pack was issued in the future.");
            }

            if (expiresUtc <= now)
            {
                throw new NativeCompatibilityEnvelopeException(
                    NativeCompatibilityInstallCode.Expired,
                    "Expired compatibility packs cannot be newly installed.");
            }

            var pack = NativeHudCompatibilityPack.Parse(Encoding.UTF8.GetBytes(signed["pack"].GetRawText()));
            return new NativeVerifiedCompatibilityEnvelope(
                pack, keyId, Convert.ToHexString(SHA256.HashData(payload)), issuedUtc, expiresUtc);
        }
        catch (NativeCompatibilityEnvelopeException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or InvalidOperationException or ArgumentException)
        {
            throw Invalid("The signed compatibility envelope is invalid.");
        }
    }

    private static byte[] FindPublicKey(IReadOnlyDictionary<string, byte[]> keys, string keyId)
    {
        if (keys.Count > MaximumTrustedKeys)
        {
            throw Invalid("The publisher key configuration exceeds the supported limit.");
        }

        byte[]? found = null;
        foreach (var pair in keys)
        {
            if (!string.Equals(pair.Key, keyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null || pair.Value is null ||
                !string.Equals(NativeCompatibilitySignature.GetKeyId(pair.Value), keyId, StringComparison.Ordinal))
            {
                throw Invalid("The pinned publisher key configuration is invalid.");
            }

            found = pair.Value;
        }

        return found ?? throw new NativeCompatibilityEnvelopeException(
            NativeCompatibilityInstallCode.UntrustedPublisher,
            "The compatibility publisher key is not trusted.");
    }

    private static byte[] ReadBase64(JsonElement element, int maximumBytes)
    {
        var value = NativeCompatibilityJson.ReadString(element);
        if (value.Length == 0 || value.Length > ((maximumBytes + 2) / 3) * 4)
        {
            throw Invalid("A signed envelope value exceeds its size limit.");
        }

        var bytes = Convert.FromBase64String(value);
        if (bytes.Length > maximumBytes || Convert.ToBase64String(bytes) != value)
        {
            throw Invalid("Signed envelope values must use canonical base64.");
        }

        return bytes;
    }

    private static NativeCompatibilityEnvelopeException Invalid(string message) =>
        new(NativeCompatibilityInstallCode.InvalidEnvelope, message);
}

/// <summary>
/// Offline, exact-fingerprint catalog. Installed packs are immutable; only fully verified snapshots are published.
/// The cache directory and its acceptance ledger are trusted local application state, not an attacker-writable inbox.
/// Deleting or replacing the ledger resets its rollback memory; same-user local tampering/clock rollback is not defended
/// against. A copied envelope without a receipt is never loaded. An existing receipt permits offline use after expiry
/// only because its signature is rechecked at its original, fresh acceptance time against currently pinned keys.
/// </summary>
public sealed class NativeCompatibilityCatalog
{
    public const int MaximumCachedPacks = 128;
    private const int MaximumLedgerBytes = 128 * 1024;
    private const string LedgerFileName = "accepted.json";

    private readonly object _gate = new();
    private readonly NativeHudCompatibilityPack _builtIn;
    private readonly string? _cacheDirectory;
    private readonly FrozenDictionary<string, byte[]> _trustedPublicKeys;
    private CatalogSnapshot _snapshot;
    private string _status;
    private long _generation;

    public NativeCompatibilityCatalog(
        NativeHudCompatibilityPack builtIn,
        string? cacheDirectory,
        IReadOnlyDictionary<string, byte[]> trustedPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        _builtIn = builtIn;
        _cacheDirectory = cacheDirectory is null ? null : Path.GetFullPath(cacheDirectory);
        _trustedPublicKeys = CopyTrustedKeys(trustedPublicKeys);
        _snapshot = LoadCache(DateTimeOffset.UtcNow);
        _status = Describe(_snapshot);
    }

    public long Generation => Interlocked.Read(ref _generation);
    public bool HasTrustedPublishers => _trustedPublicKeys.Count > 0;
    public string Status => Volatile.Read(ref _status);
    public bool CacheLoadHadErrors => Volatile.Read(ref _snapshot).HasErrors;
    public IReadOnlyList<string> Diagnostics => Volatile.Read(ref _snapshot).Diagnostics;

    public NativeHudCompatibilityPack? Find(string? version, long length, string? sha256)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var fingerprint = Fingerprint(version, length, sha256);
        if (IsBlocked(snapshot, fingerprint))
        {
            return null;
        }

        if (snapshot.Packs.TryGetValue(fingerprint, out var pack) &&
            (!_builtIn.Matches(version, length, sha256) || pack.Revision > _builtIn.Revision))
        {
            return pack;
        }

        return _builtIn.Matches(version, length, sha256) ? _builtIn : null;
    }

    public string? GetUnavailableReason(string? version, long length, string? sha256)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var fingerprint = Fingerprint(version, length, sha256);
        return IsBlocked(snapshot, fingerprint) ? snapshot.Issues[fingerprint] : null;
    }

    public NativeCompatibilityInstallResult Install(ReadOnlySpan<byte> envelope, DateTimeOffset now)
    {
        // Capture once: callers cannot mutate a backing byte[] between verification and persistence.
        if (envelope.IsEmpty || envelope.Length > NativeCompatibilityEnvelope.MaximumEnvelopeBytes)
        {
            return Failure(NativeCompatibilityInstallCode.InvalidEnvelope, "The signed envelope exceeds its size limit or is empty.");
        }

        var bytes = envelope.ToArray();
        NativeVerifiedCompatibilityEnvelope verified;
        try
        {
            verified = NativeCompatibilityEnvelope.Verify(bytes, _trustedPublicKeys, now);
        }
        catch (NativeCompatibilityEnvelopeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }

        lock (_gate)
        {
            FileStream? storageLock = null;
            try
            {
                if (_cacheDirectory is not null)
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    // Serialize separate catalog instances/processes as well as this instance. A busy cache fails cleanly.
                    storageLock = new FileStream(
                        Path.Combine(_cacheDirectory, ".catalog.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                    var current = LoadCache(now);
                    if (current.LedgerError is not null)
                    {
                        return Failure(NativeCompatibilityInstallCode.CacheUnavailable,
                            "The acceptance ledger is unavailable or invalid; signed imports are blocked.");
                    }

                    if (_snapshot.Records.Any(pair =>
                            !current.Records.TryGetValue(pair.Key, out var receipt) ||
                            receipt.Revision < pair.Value.Revision ||
                            receipt.Revision == pair.Value.Revision && receipt.PayloadSha256 != pair.Value.PayloadSha256))
                    {
                        return Failure(NativeCompatibilityInstallCode.CacheUnavailable,
                            "The acceptance ledger lost or changed a known revision; signed imports are blocked.");
                    }

                    Publish(current, !SameState(_snapshot, current));
                }

                return InstallVerified(bytes, verified, now.ToUniversalTime());
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                return Failure(NativeCompatibilityInstallCode.CacheUnavailable,
                    "The compatibility cache is busy or unavailable; the existing catalog is unchanged.");
            }
            finally
            {
                storageLock?.Dispose();
            }
        }
    }

    private NativeCompatibilityInstallResult InstallVerified(
        byte[] envelope, NativeVerifiedCompatibilityEnvelope verified, DateTimeOffset acceptedUtc)
    {
        var pack = verified.Pack;
        var fingerprint = Fingerprint(pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256);
        var current = _snapshot;
        if (_builtIn.Matches(pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256) &&
            pack.Revision <= _builtIn.Revision)
        {
            return Failure(
                pack.Revision < _builtIn.Revision
                    ? NativeCompatibilityInstallCode.RollbackRejected
                    : NativeCompatibilityInstallCode.RevisionConflict,
                "A signed pack must have a newer revision than the trusted built-in pack for the same game fingerprint.");
        }

        if (current.Records.TryGetValue(fingerprint, out var previous))
        {
            if (pack.Revision < previous.Revision)
            {
                return Failure(NativeCompatibilityInstallCode.RollbackRejected,
                    "A lower compatibility revision cannot replace a previously accepted revision.");
            }

            if (pack.Revision == previous.Revision)
            {
                if (verified.PayloadSha256 != previous.PayloadSha256)
                {
                    return Failure(NativeCompatibilityInstallCode.RevisionConflict,
                        "The same compatibility revision cannot carry a different signed payload.");
                }

                if (current.Packs.TryGetValue(fingerprint, out var installed) && previous.KeyId == verified.KeyId)
                {
                    Volatile.Write(ref _status, "The signed compatibility pack is already installed.");
                    return new NativeCompatibilityInstallResult(
                        true, false, NativeCompatibilityInstallCode.AlreadyInstalled, Status, installed);
                }
            }
        }
        else if (current.Records.Count >= MaximumCachedPacks)
        {
            return Failure(NativeCompatibilityInstallCode.CatalogFull,
                "The compatibility cache has reached its supported pack limit.");
        }

        var record = new CacheRecord(
            pack.GameVersion, pack.ExecutableLength, pack.ExecutableSha256, pack.Revision,
            Convert.ToHexString(SHA256.HashData(envelope)), verified.PayloadSha256, verified.KeyId, acceptedUtc);
        var records = current.Records.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var packs = current.Packs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var issues = current.Issues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        records[fingerprint] = record;
        packs[fingerprint] = pack;
        issues.Remove(fingerprint);

        if (_cacheDirectory is not null)
        {
            try
            {
                // Commit the complete content-addressed envelope first, then atomically switch the sole ledger pointer.
                // An interrupted write may leave an orphan, never a partial replacement or a selected older revision.
                WriteAtomically(EnvelopePath(record), envelope);
                WriteAtomically(Path.Combine(_cacheDirectory, LedgerFileName), EncodeLedger(records));
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                return Failure(NativeCompatibilityInstallCode.CacheWriteFailed,
                    "The signed pack could not be committed to the cache; the accepted catalog is unchanged.");
            }
        }

        Publish(new CatalogSnapshot(records, packs, issues), incrementGeneration: true);
        var message = _cacheDirectory is null
            ? "The signed compatibility pack is installed for this session; no persistent cache is configured."
            : "The signed compatibility pack is verified and installed in the offline cache.";
        Volatile.Write(ref _status, message);
        return new NativeCompatibilityInstallResult(true, true, NativeCompatibilityInstallCode.Installed, message, pack);
    }

    private CatalogSnapshot LoadCache(DateTimeOffset now)
    {
        if (_cacheDirectory is null)
        {
            return new CatalogSnapshot();
        }

        Dictionary<string, CacheRecord> records;
        try
        {
            records = ReadLedger(ReadBounded(Path.Combine(_cacheDirectory, LedgerFileName), MaximumLedgerBytes), now);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Never scan/import loose signed files. A ledger is the only evidence of a previous acceptance.
            return new CatalogSnapshot();
        }
        catch (Exception exception) when (IsStorageException(exception) ||
                                         exception is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new CatalogSnapshot(ledgerError:
                "The compatibility acceptance ledger is unavailable or invalid; only the trusted built-in pack is available.");
        }

        var packs = new Dictionary<string, NativeHudCompatibilityPack>(StringComparer.Ordinal);
        var issues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in records)
        {
            var record = pair.Value;
            try
            {
                var bytes = ReadBounded(EnvelopePath(record), NativeCompatibilityEnvelope.MaximumEnvelopeBytes);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != record.EnvelopeSha256)
                {
                    throw new FormatException("The cached envelope digest does not match its acceptance receipt.");
                }

                var verified = NativeCompatibilityEnvelope.Verify(bytes, _trustedPublicKeys, record.AcceptedUtc);
                if (verified.IssuedUtc - now > NativeCompatibilityEnvelope.ClockSkew ||
                    verified.Pack.Revision != record.Revision || verified.KeyId != record.KeyId ||
                    verified.PayloadSha256 != record.PayloadSha256 ||
                    !verified.Pack.Matches(record.GameVersion, record.ExecutableLength, record.ExecutableSha256))
                {
                    throw new FormatException("The cached envelope does not match its acceptance receipt.");
                }

                packs.Add(pair.Key, verified.Pack);
            }
            catch (NativeCompatibilityEnvelopeException exception) when (
                exception.Code == NativeCompatibilityInstallCode.UntrustedPublisher)
            {
                issues.Add(pair.Key,
                    "A previously accepted compatibility publisher is no longer trusted; lower revisions are blocked for this build.");
            }
            catch (Exception exception) when (IsStorageException(exception) || exception is FormatException or JsonException)
            {
                issues.Add(pair.Key,
                    "A previously accepted compatibility pack is missing or invalid; lower revisions are blocked for this build.");
            }
        }

        return new CatalogSnapshot(records, packs, issues);
    }

    private static Dictionary<string, CacheRecord> ReadLedger(byte[] bytes, DateTimeOffset now)
    {
        using var document = NativeCompatibilityJson.Parse(bytes, MaximumLedgerBytes);
        var root = NativeCompatibilityJson.ReadObject(document.RootElement, "format", "entries");
        if (NativeCompatibilityJson.ReadInt32(root["format"]) != 1 ||
            root["entries"].ValueKind != JsonValueKind.Array || root["entries"].GetArrayLength() > MaximumCachedPacks)
        {
            throw new FormatException("The cache ledger format or size is unsupported.");
        }

        var records = new Dictionary<string, CacheRecord>(StringComparer.Ordinal);
        foreach (var element in root["entries"].EnumerateArray())
        {
            var entry = NativeCompatibilityJson.ReadObject(element, "gameVersion", "executableLength", "executableSha256",
                "revision", "envelopeSha256", "payloadSha256", "keyId", "acceptedUtc");
            var version = NativeCompatibilityJson.ReadString(entry["gameVersion"]);
            var versionParts = version.Split('.');
            if (version.Length is < 7 or > 23 || versionParts.Length != 4 || versionParts.Any(part =>
                    part.Length is < 1 or > 5 || !part.All(char.IsAsciiDigit) ||
                    !ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                throw new FormatException("The cached game fingerprint is invalid.");
            }

            var lengthElement = entry["executableLength"];
            if (lengthElement.ValueKind != JsonValueKind.Number || !lengthElement.TryGetInt64(out var length) ||
                length is < 4096 or > 2L * 1024 * 1024 * 1024)
            {
                throw new FormatException("The cached game fingerprint is invalid.");
            }

            var revision = NativeCompatibilityJson.ReadInt32(entry["revision"]);
            var acceptedUtc = NativeCompatibilityJson.ReadUtc(entry["acceptedUtc"]);
            if (revision <= 0 || acceptedUtc - now > NativeCompatibilityEnvelope.ClockSkew)
            {
                throw new FormatException("The cached acceptance record is invalid.");
            }

            var record = new CacheRecord(version, length, NativeCompatibilityJson.ReadHash(entry["executableSha256"]),
                revision, NativeCompatibilityJson.ReadHash(entry["envelopeSha256"]),
                NativeCompatibilityJson.ReadHash(entry["payloadSha256"]), NativeCompatibilityJson.ReadHash(entry["keyId"]),
                acceptedUtc);
            if (!records.TryAdd(Fingerprint(record.GameVersion, record.ExecutableLength, record.ExecutableSha256), record))
            {
                throw new FormatException("The cache ledger has duplicate game fingerprints.");
            }
        }

        return records;
    }

    private static byte[] EncodeLedger(IReadOnlyDictionary<string, CacheRecord> records)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = 1,
            entries = records.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                gameVersion = pair.Value.GameVersion,
                executableLength = pair.Value.ExecutableLength,
                executableSha256 = pair.Value.ExecutableSha256,
                revision = pair.Value.Revision,
                envelopeSha256 = pair.Value.EnvelopeSha256,
                payloadSha256 = pair.Value.PayloadSha256,
                keyId = pair.Value.KeyId,
                acceptedUtc = pair.Value.AcceptedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            })
        });
        if (bytes.Length > MaximumLedgerBytes)
        {
            throw new IOException("The compatibility acceptance ledger exceeds its size limit.");
        }

        return bytes;
    }

    private bool IsBlocked(CatalogSnapshot snapshot, string fingerprint) =>
        snapshot.Issues.ContainsKey(fingerprint) && snapshot.Records.TryGetValue(fingerprint, out var record) &&
        (!_builtIn.Matches(record.GameVersion, record.ExecutableLength, record.ExecutableSha256) ||
         record.Revision > _builtIn.Revision);

    private static FrozenDictionary<string, byte[]> CopyTrustedKeys(IReadOnlyDictionary<string, byte[]> keys)
    {
        if (keys.Count > NativeCompatibilityEnvelope.MaximumTrustedKeys)
        {
            throw new ArgumentException("Too many publisher public keys were configured.", nameof(keys));
        }

        var copy = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in keys)
        {
            try
            {
                if (pair.Value is null)
                {
                    throw new CryptographicException();
                }

                var bytes = (byte[])pair.Value.Clone();
                var keyId = NativeCompatibilitySignature.GetKeyId(bytes);
                if (!string.Equals(pair.Key, keyId, StringComparison.OrdinalIgnoreCase) || !copy.TryAdd(keyId, bytes))
                {
                    throw new CryptographicException();
                }
            }
            catch (CryptographicException)
            {
                throw new ArgumentException("A pinned publisher public key or its SHA-256 identifier is invalid.", nameof(keys));
            }
        }

        return copy.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private void Publish(CatalogSnapshot snapshot, bool incrementGeneration)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Volatile.Write(ref _status, Describe(snapshot));
        if (incrementGeneration)
        {
            Interlocked.Increment(ref _generation);
        }
    }

    private static bool SameState(CatalogSnapshot first, CatalogSnapshot second) =>
        first.LedgerError == second.LedgerError && first.Records.Count == second.Records.Count &&
        first.Issues.Count == second.Issues.Count &&
        first.Records.All(pair => second.Records.TryGetValue(pair.Key, out var record) && pair.Value == record) &&
        first.Issues.All(pair => second.Issues.TryGetValue(pair.Key, out var issue) && pair.Value == issue);

    private NativeCompatibilityInstallResult Failure(NativeCompatibilityInstallCode code, string message)
    {
        Volatile.Write(ref _status, message);
        return new NativeCompatibilityInstallResult(false, false, code, message);
    }

    private static string Describe(CatalogSnapshot snapshot) => snapshot.LedgerError ??
        (snapshot.Issues.Count > 0
            ? "Some previously accepted compatibility packs are unavailable; affected builds cannot use lower revisions."
            : snapshot.Packs.Count > 0
                ? "The built-in pack and verified offline compatibility cache are ready."
                : "The trusted built-in compatibility pack is ready; no signed packs are installed.");

    private string EnvelopePath(CacheRecord record) =>
        Path.Combine(_cacheDirectory!, record.EnvelopeSha256 + ".pack.json");

    private static string Fingerprint(string? version, long length, string? sha256) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (version?.Trim() ?? string.Empty) + "\n" + length.ToString(CultureInfo.InvariantCulture) + "\n" +
            (sha256?.Trim().ToUpperInvariant() ?? string.Empty))));

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new FormatException("A compatibility cache file exceeds its size limit or is empty.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new FormatException("A compatibility cache file changed while being read.");
        }

        return bytes;
    }

    private static void WriteAtomically(string destination, byte[] bytes)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".compatibility-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Same-directory rename keeps replacement atomic; failure leaves the prior file intact.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                // Incomplete staging files are never loaded, even if cleanup is prevented by the filesystem.
            }
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    private sealed record CacheRecord(
        string GameVersion,
        long ExecutableLength,
        string ExecutableSha256,
        int Revision,
        string EnvelopeSha256,
        string PayloadSha256,
        string KeyId,
        DateTimeOffset AcceptedUtc);

    private sealed class CatalogSnapshot
    {
        public CatalogSnapshot(
            IReadOnlyDictionary<string, CacheRecord>? records = null,
            IReadOnlyDictionary<string, NativeHudCompatibilityPack>? packs = null,
            IReadOnlyDictionary<string, string>? issues = null,
            string? ledgerError = null)
        {
            Records = (records ?? new Dictionary<string, CacheRecord>()).ToFrozenDictionary(StringComparer.Ordinal);
            Packs = (packs ?? new Dictionary<string, NativeHudCompatibilityPack>()).ToFrozenDictionary(StringComparer.Ordinal);
            Issues = (issues ?? new Dictionary<string, string>()).ToFrozenDictionary(StringComparer.Ordinal);
            LedgerError = ledgerError;
            Diagnostics = Array.AsReadOnly(ledgerError is null ? Issues.Values.ToArray() : [ledgerError]);
        }

        public FrozenDictionary<string, CacheRecord> Records { get; }
        public FrozenDictionary<string, NativeHudCompatibilityPack> Packs { get; }
        public FrozenDictionary<string, string> Issues { get; }
        public string? LedgerError { get; }
        public bool HasErrors => LedgerError is not null || Issues.Count > 0;
        public IReadOnlyList<string> Diagnostics { get; }
    }
}

internal static class NativeCompatibilityJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static JsonDocument Parse(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes)
        {
            throw new FormatException("A compatibility JSON document is empty or exceeds its size limit.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("A compatibility JSON document is not valid UTF-8.");
        }

        return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
    }

    public static Dictionary<string, JsonElement> ReadObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A compatibility JSON value must be an object.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !properties.TryAdd(property.Name, property.Value))
            {
                throw new FormatException("A compatibility JSON object has unknown or duplicate properties.");
            }
        }

        if (properties.Count != names.Length)
        {
            throw new FormatException("A compatibility JSON object is missing required properties.");
        }

        return properties;
    }

    public static int ReadInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new FormatException("A compatibility JSON value must be a 32-bit integer.");

    public static string ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new FormatException("A compatibility JSON value must be a string.");

    public static string ReadHash(JsonElement element)
    {
        var value = ReadString(element);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new FormatException("A compatibility digest must contain 64 hexadecimal digits.");
        }

        return value.ToUpperInvariant();
    }

    public static DateTimeOffset ReadUtc(JsonElement element)
    {
        var value = ReadString(element);
        var validShape = value.Length == 20 || value.Length is >= 22 and <= 28 && value[19] == '.' &&
            value.AsSpan(20, value.Length - 21).ToArray().All(char.IsAsciiDigit);
        if (!validShape || value[^1] != 'Z' || !DateTimeOffset.TryParseExact(
                value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new FormatException("Compatibility timestamps must use strict UTC ISO 8601 with a literal Z.");
        }

        return timestamp;
    }
}
