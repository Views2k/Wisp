using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Wisp.App;

// Callers supply immutable snapshots and explicitly selected, non-sensitive provenance.
// Producers never serialize or wait for disk; a full queue is reported, never overwritten.
internal sealed class ShiftCaptureJournal
{
    private const string PayloadName = "events.jsonl";
    private const string ManifestName = "manifest.json";
    private const string ReportName = "capture-report.txt";
    private const int MaximumKinds = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly object _gate = new();
    private readonly Channel<Entry> _channel;
    private readonly Dictionary<string, Counts> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _incompleteErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scenarioGaps = new(StringComparer.Ordinal);
    private ShiftCaptureCoverageSnapshot? _scenarioCoverage;
    private ShiftCaptureExperimentSnapshot? _experimentCoverage;
    private readonly object _provenance;
    private readonly FileStream _output;
    private readonly Task _writer;
    private readonly Timer _deadline;
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxDuration;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private bool _accepting = true;
    private bool _incompleteReasonsTruncated;
    private string? _error, _finishReason, _fatalFailure;
    private long _sequence, _accepted, _written, _queueDropped, _abandoned, _untrackedKindDropped, _bytes, _stoppedTimestamp;
    private Task<string>? _stopTask;

    internal ShiftCaptureJournal(string directory, object provenance, int capacity = 8192,
        long maxBytes = 512 * 1024 * 1024, TimeSpan? maxDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(provenance);
        if (capacity is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxDuration = maxDuration ?? TimeSpan.FromMinutes(30);
        if (_maxDuration <= TimeSpan.Zero || _maxDuration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(maxDuration));
        DirectoryPath = Path.GetFullPath(directory);
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath))
            throw new IOException("The capture directory must be new.");
        Directory.CreateDirectory(DirectoryPath);
        // CreateNew protects against another session racing to claim the same directory.
        _output = new FileStream(Path.Combine(DirectoryPath, PayloadName), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        SessionId = Guid.NewGuid().ToString("N");
        StartedTimestamp = Stopwatch.GetTimestamp();
        _capacity = capacity;
        _maxBytes = maxBytes;
        _provenance = provenance;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writer = Task.Run(WriteAsync);
        _deadline = new Timer(_ => StopForLimit("maximum-duration"), null, _maxDuration, Timeout.InfiniteTimeSpan);
    }

    internal string SessionId { get; }
    internal long StartedTimestamp { get; }
    internal string DirectoryPath { get; }
    internal bool IsAccepting { get { lock (_gate) return _accepting; } }
    internal string? Error { get { lock (_gate) return _error; } }
    internal long DroppedRecords { get { lock (_gate) return _queueDropped + _abandoned; } }
    internal IReadOnlyDictionary<string, long> RecordedCounts
    {
        get { lock (_gate) return _counts.ToDictionary(pair => pair.Key, pair => pair.Value.Written, StringComparer.Ordinal); }
    }

    // Storage integrity does not establish that the session captured its required channels.
    internal void MarkIncomplete(string reason)
    {
        ValidateLabel(reason, nameof(reason));
        lock (_gate)
        {
            if (_stopTask is not null) throw new InvalidOperationException("The capture manifest is already frozen.");
            _error ??= reason;
            if (_incompleteErrors.Count < 64 || _incompleteErrors.Contains(reason)) _incompleteErrors.Add(reason);
            else _incompleteReasonsTruncated = true;
        }
    }

    internal void SetScenarioCoverage(ShiftCaptureCoverageSnapshot coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_gate)
        {
            if (_stopTask is not null) throw new InvalidOperationException("The capture manifest is already frozen.");
            _scenarioCoverage = coverage;
        }
    }

    internal void MarkScenarioIncomplete(string reason)
    {
        ValidateLabel(reason, nameof(reason));
        lock (_gate)
        {
            if (_stopTask is not null) throw new InvalidOperationException("The capture manifest is already frozen.");
            if (_scenarioGaps.Count < 64) _scenarioGaps.Add(reason);
        }
    }

    internal void SetExperimentCoverage(ShiftCaptureExperimentSnapshot coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_gate)
        {
            if (_stopTask is not null) throw new InvalidOperationException("The capture manifest is already frozen.");
            _experimentCoverage = coverage;
        }
    }

    internal bool TryRecord(string kind, object payload, long? timestamp = null)
    {
        ValidateLabel(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(payload);
        if (timestamp is <= 0) throw new ArgumentOutOfRangeException(nameof(timestamp));
        lock (_gate)
        {
            if (!_accepting) return false;
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(StartedTimestamp, now) >= _maxDuration)
            {
                StopCore("maximum-duration", "maximum-duration");
                return false;
            }
            if (!_counts.TryGetValue(kind, out var counts))
            {
                if (_counts.Count == MaximumKinds)
                {
                    _sequence++;
                    _queueDropped++;
                    _untrackedKindDropped++;
                    StopCore("maximum-event-kinds", "maximum-event-kinds");
                    return false;
                }
                _counts.Add(kind, counts = new Counts());
            }
            var sequence = ++_sequence;
            counts.Attempted++;
            if (!_channel.Writer.TryWrite(new Entry(sequence, timestamp ?? now, kind, payload)))
            {
                _queueDropped++;
                counts.QueueDropped++;
                _error ??= "queue-overflow";
                return false;
            }
            _accepted++;
            counts.Accepted++;
            return true;
        }
    }

    internal Task<string> StopAndPackageAsync(string reason)
    {
        ValidateLabel(reason, nameof(reason));
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            StopCore(reason, null);
            _deadline.Dispose();
            return _stopTask = Task.Run(PackageAsync);
        }
    }

    private void StopForLimit(string reason)
    {
        lock (_gate) StopCore(reason, reason);
    }

    private void StopCore(string reason, string? error)
    {
        if (!_accepting) return;
        _accepting = false;
        _finishReason = reason;
        _error ??= error;
        _stoppedTimestamp = Stopwatch.GetTimestamp();
        _channel.Writer.TryComplete();
    }

    private async Task WriteAsync()
    {
        var lastFlush = Stopwatch.GetTimestamp();
        var sinceFlush = 0;
        var capped = false;
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (capped) { Abandon(entry.Kind); continue; }
                using var buffer = new LimitedBuffer(_maxBytes - _bytes - 1);
                try { JsonSerializer.Serialize(buffer, entry, JsonOptions); }
                catch (ByteLimitException)
                {
                    capped = true;
                    lock (_gate)
                    {
                        _error ??= "maximum-bytes";
                        StopCore("maximum-bytes", "maximum-bytes");
                    }
                    Abandon(entry.Kind);
                    continue;
                }
                await _output.WriteAsync(buffer.Bytes).ConfigureAwait(false);
                await _output.WriteAsync(new byte[] { (byte)'\n' }).ConfigureAwait(false);
                lock (_gate)
                {
                    _bytes += buffer.Length + 1;
                    _written++;
                    _counts[entry.Kind].Written++;
                }
                if (++sinceFlush >= 128 || Stopwatch.GetElapsedTime(lastFlush).TotalSeconds >= 1)
                {
                    await _output.FlushAsync().ConfigureAwait(false);
                    lastFlush = Stopwatch.GetTimestamp();
                    sinceFlush = 0;
                }
            }
            await _output.FlushAsync().ConfigureAwait(false);
            _output.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _fatalFailure = "journal-write-failed";
                _error = _fatalFailure;
                StopCore(_fatalFailure, _fatalFailure);
                _abandoned = _accepted - _written;
                foreach (var counts in _counts.Values) counts.Abandoned = counts.Accepted - counts.Written;
            }
            while (_channel.Reader.TryRead(out _)) { }
        }
        finally { await _output.DisposeAsync().ConfigureAwait(false); }
    }

    private void Abandon(string kind)
    {
        lock (_gate) { _abandoned++; _counts[kind].Abandoned++; }
    }

    private async Task<string> PackageAsync()
    {
        try
        {
            await _writer.ConfigureAwait(false);
            if (_fatalFailure is not null) throw new IOException("Capture journal writing failed.");
            var payloadPath = Path.Combine(DirectoryPath, PayloadName);
            var validation = await VerifyPayloadAsync(payloadPath).ConfigureAwait(false);
            var provenance = JsonSerializer.SerializeToElement(_provenance, JsonOptions);
            var checkRecordedChannels = provenance.ValueKind == JsonValueKind.Object &&
                provenance.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String &&
                schema.GetString() == "shift-capture-v2";
            var missingRequiredChannels = checkRecordedChannels ? MissingRequiredChannels(validation.Audit) : [];
            string? error;
            string[] incompleteErrors;
            string[] scenarioGaps;
            Dictionary<string, KindSummary> perKind;
            lock (_gate)
            {
                if (_written == 0) _error ??= "no-records";
                foreach (var channel in missingRequiredChannels)
                {
                    _error ??= "missing-required-capture-channels";
                    var missing = "missing-channel-" + channel;
                    if (_incompleteErrors.Count < 64 || _incompleteErrors.Contains(missing)) _incompleteErrors.Add(missing);
                    else _incompleteReasonsTruncated = true;
                }
                error = _error;
                incompleteErrors = (error is null ? _incompleteErrors : _incompleteErrors.Append(error))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                perKind = _counts.ToDictionary(pair => pair.Key,
                    pair => new KindSummary(pair.Value.Attempted, pair.Value.Accepted, pair.Value.Written,
                        pair.Value.QueueDropped, pair.Value.Abandoned), StringComparer.Ordinal);
                scenarioGaps = _scenarioGaps.Order(StringComparer.Ordinal).ToArray();
            }
            var reportText = "Wisp diagnostic session\n\n" +
                "Payload integrity: verified byte count, record count, sequence and SHA-256.\n" +
                $"Dropped records: {_queueDropped + _abandoned}.\n" +
                "Journal loss accounting excludes receiver datagram loss; see channel errors and capture_summary for those counts.\n" +
                "Required recording channels (" + (checkRecordedChannels ? "checked from recorded evidence" : "caller-reported errors only") + "): " +
                (error is null ? "no reported channel errors" : string.Join(", ", incompleteErrors)) + ".\n" +
                (_scenarioCoverage is null ? "" :
                    $"Moving upshifts recorded: {_scenarioCoverage.RecordedMovingUpshifts}; engine-output timing brackets: {_scenarioCoverage.BracketedEngineOutputTimings}; traction-qualified: {_scenarioCoverage.TractionQualifiedEngineOutputTimings}.\n" +
                    "Engine-output timing can remain useful when tire slip prevents a clean acceleration comparison.\n") +
                "Basic driving checklist: " + (_scenarioCoverage is null ? "not recorded" :
                    _scenarioCoverage.DrivingEvidenceChecklistSatisfied ? "candidate coverage observed for one recorded fingerprint" : "missing candidates listed below") + ".\n" +
                string.Join("\n", _scenarioCoverage?.ActionableMissingEvidence ?? []) + "\n\n" +
                "Comparison protocol: " + (_experimentCoverage is null ? "not recorded" :
                    _experimentCoverage.ExperimentChecklistSatisfied ? "candidate segments and identity round trip observed; validity still requires analysis" : "missing candidates listed below") + ".\n" +
                string.Join("\n", _experimentCoverage?.ActionableMissingEvidence ?? []) + "\n" +
                _experimentCoverage?.Heuristics + "\n" + _experimentCoverage?.Scope + "\n\n" + validation.Report;
            var reportBytes = System.Text.Encoding.UTF8.GetBytes(reportText);
            var reportHash = Convert.ToHexString(SHA256.HashData(reportBytes));
            var reportPath = Path.Combine(DirectoryPath, ReportName);
            await File.WriteAllBytesAsync(reportPath, reportBytes).ConfigureAwait(false);
            var manifest = new
            {
                schemaVersion = 1,
                sessionId = SessionId,
                startedAtUtc = _startedAtUtc,
                startedTimestamp = StartedTimestamp,
                stoppedTimestamp = _stoppedTimestamp,
                stopwatchFrequency = Stopwatch.Frequency,
                finishReason = _finishReason,
                provenance = _provenance,
                limits = new { capacity = _capacity, maxBytes = _maxBytes, maxDurationSeconds = _maxDuration.TotalSeconds, maximumEventKinds = MaximumKinds },
                attemptedRecords = _sequence,
                acceptedRecords = _accepted,
                writtenRecords = _written,
                queueDroppedRecords = _queueDropped,
                abandonedRecords = _abandoned,
                untrackedKindDroppedRecords = _untrackedKindDropped,
                droppedRecords = _queueDropped + _abandoned,
                countsByKind = perKind,
                error,
                incompleteErrors,
                incompleteReasonsTruncated = _incompleteReasonsTruncated,
                captureComplete = error is null && _written > 0 && scenarioGaps.Length == 0,
                captureCompleteMeaning = "Legacy record/channel status plus reported basic driving gaps. It does not certify the full development protocol, model correctness or universal shift accuracy.",
                storageIntegrity = new
                {
                    verified = true,
                    journalEventsLossless = _queueDropped + _abandoned == 0,
                    scope = "Serialized journal events only. Receiver datagram loss and channel usability are reported separately."
                },
                requiredChannelsComplete = error is null && _written > 0,
                requiredChannelsCheckedFromRecordedEvidence = checkRecordedChannels,
                missingRequiredChannels,
                basicDrivingChecklistRecorded = _scenarioCoverage is not null,
                basicDrivingChecklistSatisfied = _scenarioCoverage?.DrivingEvidenceChecklistSatisfied == true,
                basicDrivingChecklistScope = "Heuristic coverage for the selected recorded car/tune fingerprint only; not every captured tune or the full development protocol.",
                scenarioGaps,
                scenarioCoverage = _scenarioCoverage,
                experimentCoverage = _experimentCoverage,
                audit = validation.Audit,
                report = new { file = ReportName, byteLength = reportBytes.Length, sha256 = reportHash },
                validation = new { jsonlVerified = true, validation.RecordCount, validation.LastSequence, validation.ByteLength, validation.Sha256 },
                payload = PayloadName
            };
            var manifestPath = Path.Combine(DirectoryPath, ManifestName);
            await using (var manifestFile = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(manifestFile, manifest, JsonOptions).ConfigureAwait(false);
                await manifestFile.FlushAsync().ConfigureAwait(false);
                manifestFile.Flush(flushToDisk: true);
            }
            var archivePath = Path.Combine(DirectoryPath, "capture.zip");
            var pendingPath = archivePath + ".pending";
            using (var file = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    zip.CreateEntryFromFile(payloadPath, PayloadName, CompressionLevel.Fastest);
                    zip.CreateEntryFromFile(manifestPath, ManifestName, CompressionLevel.Fastest);
                    zip.CreateEntryFromFile(reportPath, ReportName, CompressionLevel.Fastest);
                }
                file.Flush(flushToDisk: true);
            }
            await VerifyArchiveAsync(pendingPath, manifestPath, validation, reportBytes.Length, reportHash).ConfigureAwait(false);
            File.Move(pendingPath, archivePath, overwrite: false);
            return archivePath;
        }
        catch (Exception)
        {
            lock (_gate) _error = _fatalFailure ?? "capture-package-validation-failed";
            throw new IOException("The capture could not be packaged and verified; its local evidence was retained.");
        }
    }

    private static string[] MissingRequiredChannels(ShiftCaptureSessionAuditSnapshot audit)
    {
        var missing = new HashSet<string>(audit.AbsentRecordedChannels, StringComparer.Ordinal);
        if (audit.RawPackets == 0) missing.Add("telemetry");
        if (audit.ConfigurationRecords == 0) missing.Add("native_configuration");
        if (audit.InputEdgeBracketMilliseconds.Count == 0) missing.Add("controller_button");
        if (!audit.Gears.Any(gear => gear.Evaluations > 0)) missing.Add("cue_evaluation");
        if (audit.SuccessfulSubmissions == 0) missing.Add("cue_submission");
        if (audit.QualifiedPixelReads == 0) missing.Add("cue_pixels");
        return missing.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<PayloadValidation> VerifyPayloadAsync(string path)
    {
        long records = 0, sequence = 0;
        var byKind = new Dictionary<string, long>(StringComparer.Ordinal);
        var audit = new ShiftCaptureSessionAudit(Stopwatch.Frequency);
        using (var reader = new StreamReader(path))
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var next = root.GetProperty("sequence").GetInt64();
                var timestamp = root.GetProperty("timestamp").GetInt64();
                var kind = root.GetProperty("kind").GetString() ?? "";
                if (next <= sequence || next > _sequence || timestamp <= 0 || !root.TryGetProperty("payload", out _))
                    throw new InvalidDataException("Invalid journal ordering.");
                sequence = next;
                records++;
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
                audit.Observe(root);
            }
        }
        if (records != _written || _accepted != _written + _abandoned || _sequence != _accepted + _queueDropped ||
            _counts.Any(pair => byKind.GetValueOrDefault(pair.Key) != pair.Value.Written) ||
            byKind.Keys.Any(kind => !_counts.ContainsKey(kind)))
            throw new InvalidDataException("Journal counts do not match.");
        await using var file = File.OpenRead(path);
        if (file.Length != _bytes || file.Length > _maxBytes) throw new InvalidDataException("Journal size does not match.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false));
        return new PayloadValidation(records, sequence, file.Length, hash, audit.Snapshot(), audit.ToPlainText());
    }

    private static async Task VerifyArchiveAsync(string path, string manifestPath, PayloadValidation validation, int reportLength, string reportHash)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count != 3) throw new InvalidDataException("Unexpected archive contents.");
        var payload = archive.GetEntry(PayloadName) ?? throw new InvalidDataException("Missing payload.");
        var manifest = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("Missing manifest.");
        var report = archive.GetEntry(ReportName) ?? throw new InvalidDataException("Missing report.");
        if (report.Length != reportLength) throw new InvalidDataException("Archived report size changed.");
        await using (var input = report.Open())
            if (Convert.ToHexString(await SHA256.HashDataAsync(input).ConfigureAwait(false)) != reportHash)
                throw new InvalidDataException("Archived report changed.");
        if (payload.Length != validation.ByteLength) throw new InvalidDataException("Archived payload size changed.");
        await using (var input = payload.Open())
            if (Convert.ToHexString(await SHA256.HashDataAsync(input).ConfigureAwait(false)) != validation.Sha256)
                throw new InvalidDataException("Archived payload changed.");
        await using var original = File.OpenRead(manifestPath);
        await using var archived = manifest.Open();
        var originalHash = SHA256.HashData(original);
        var archivedHash = await SHA256.HashDataAsync(archived).ConfigureAwait(false);
        if (manifest.Length != original.Length || !originalHash.SequenceEqual(archivedHash))
            throw new InvalidDataException("Archived manifest changed.");
    }

    private static void ValidateLabel(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("Use a short event identifier, without free text or paths.", parameter);
    }

    private sealed record Entry(long Sequence, long Timestamp, string Kind, object Payload);
    private sealed record KindSummary(long Attempted, long Accepted, long Written, long QueueDropped, long Abandoned);
    private sealed record PayloadValidation(long RecordCount, long LastSequence, long ByteLength, string Sha256,
        ShiftCaptureSessionAuditSnapshot Audit, string Report);
    private sealed class Counts { internal long Attempted, Accepted, Written, QueueDropped, Abandoned; }
    private sealed class ByteLimitException : IOException { }

    private sealed class LimitedBuffer(long limit) : Stream
    {
        private readonly MemoryStream _buffer = new();
        internal ReadOnlyMemory<byte> Bytes => _buffer.GetBuffer().AsMemory(0, checked((int)_buffer.Length));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - _buffer.Length) throw new ByteLimitException();
            _buffer.Write(buffer);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}
