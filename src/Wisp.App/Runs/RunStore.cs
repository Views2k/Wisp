using System.IO;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record RunSummary(Guid Id, string Name, string Tune, string Notes,
    DateTimeOffset StartedAtUtc, double DurationSeconds, int SampleCount, int CarOrdinal,
    bool IsIncomplete, string FinishReason);

public sealed class RunStore
{
    public const int MaximumSamples = 180_000;
    public const int MaximumMarkers = 128;
    public const int MaximumMarkerLabelLength = 80;
    public const int MaximumLibraryEntries = 2000;
    public const long MaximumFileBytes = 64 * 1024 * 1024;
    internal const long MaximumJsonBytes = 256 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _activeJournals = new();
    private bool _recovered;
    private bool _isFull;
    public string? Warning { get; private set; }
    public bool IsFull => Volatile.Read(ref _isFull);

    public RunStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public Task<IReadOnlyList<RunSummary>> ListAsync() => InBackground<IReadOnlyList<RunSummary>>(async () =>
    {
        await RecoverAsync().ConfigureAwait(false);
        var summaries = new List<RunSummary>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.wisprun"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
            try
            {
                var summaryPath = SummaryPath(id);
                RunSummary? summary = null;
                if (File.Exists(summaryPath) && new FileInfo(summaryPath).Length <= 65_536 &&
                    File.GetLastWriteTimeUtc(summaryPath) >= File.GetLastWriteTimeUtc(path))
                {
                    try
                    {
                        CheckPath(summaryPath);
                        summary = JsonSerializer.Deserialize<RunSummary>(await File.ReadAllTextAsync(summaryPath).ConfigureAwait(false), JsonOptions);
                        if (summary is null || summary.Id != id || !ValidSummary(summary)) summary = null;
                    }
                    catch (Exception error) when (IsDataError(error)) { summary = null; }
                }
                if (summary is null)
                {
                    var run = await ReadAsync(path, id).ConfigureAwait(false);
                    summary = Summarize(run);
                    try { await WriteSummaryAsync(summary).ConfigureAwait(false); }
                    catch (Exception error) when (IsDataError(error)) { Warning = "A run summary could not be refreshed. The saved run is available."; }
                }
                summaries.Add(summary);
            }
            catch (Exception error) when (IsDataError(error))
            {
                Warning = "A saved run could not be read. Its file has been kept.";
            }
        }
        Volatile.Write(ref _isFull, summaries.Count >= MaximumLibraryEntries);
        return summaries.OrderByDescending(value => value.StartedAtUtc).ToArray();
    });

    public Task<RecordedRun> LoadAsync(Guid id) => InBackground(() => ReadAsync(RunPath(id), id));

    public Task<RunSummary> SaveAsync(RecordedRun run) => InBackground(async () =>
    {
        await WriteAsync(run, overwrite: false).ConfigureAwait(false);
        return Summarize(run);
    });

    public Task<RunSummary> UpdateMetadataAsync(Guid id, string name, string tune, string notes) => InBackground(async () =>
    {
        var run = await ReadAsync(RunPath(id), id).ConfigureAwait(false);
        var updated = run with { Name = name.Trim(), Tune = tune.Trim(), Notes = notes.Trim() };
        await WriteAsync(updated, overwrite: true).ConfigureAwait(false);
        return Summarize(updated);
    });

    public Task DeleteAsync(Guid id) => InBackground(async () =>
    {
        var trash = Path.Combine(_directory, "Deleted");
        CheckPath(trash);
        Directory.CreateDirectory(trash);
        var source = RunPath(id);
        var destination = Path.Combine(trash, $"{id:N}.wisprun");
        CheckPath(destination);
        File.Move(source, destination, overwrite: false);
        Volatile.Write(ref _isFull, false);
        await Task.CompletedTask;
        return true;
    });

    public Task RestoreAsync(Guid id) => InBackground(async () =>
    {
        var source = Path.Combine(_directory, "Deleted", $"{id:N}.wisprun");
        CheckPath(source);
        var run = await ReadAsync(source, id).ConfigureAwait(false);
        if (run.Id != id) throw new InvalidDataException("The deleted run identity is invalid.");
        EnsureCapacity(id);
        File.Move(source, RunPath(id), overwrite: false);
        return true;
    });

    public Task ExportAsync(Guid id, string destination) => InBackground(async () =>
    {
        _ = await ReadAsync(RunPath(id), id).ConfigureAwait(false);
        ValidateExternalPath(destination);
        if (File.Exists(destination)) throw new IOException("Choose a different export filename; that file already exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".wisp-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = File.OpenRead(RunPath(id)))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await input.CopyToAsync(output).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    });

    public Task<RunSummary> ImportAsync(string source) => InBackground(async () =>
    {
        ValidateExternalPath(source);
        var run = await ReadAsync(source).ConfigureAwait(false);
        run = run with { Id = Guid.NewGuid() };
        await WriteAsync(run, overwrite: false).ConfigureAwait(false);
        return Summarize(run);
    });

    private Task<T> InBackground<T>(Func<Task<T>> operation) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { EnsureDirectory(); return await operation().ConfigureAwait(false); }
        finally { _gate.Release(); }
    });

    internal RunJournal CreateJournal(RecordedRun header)
    {
        _gate.Wait();
        using var reservation = new RunJournalStartReservation(this, _gate);
        return CreateJournal(header, reservation);
    }

    internal RunJournalStartReservation? TryReserveJournalStart()
    {
        if (!_gate.Wait(0)) return null;
        try { return new RunJournalStartReservation(this, _gate); }
        catch { _gate.Release(); throw; }
    }

    internal RunJournal CreateJournal(RecordedRun header, RunJournalStartReservation reservation)
    {
        if (!reservation.BelongsTo(this)) throw new InvalidOperationException("The recording startup reservation is no longer valid.");
        try
        {
            EnsureDirectory();
            EnsureCapacity(header.Id);
            if (!_activeJournals.TryAdd(header.Id, 0)) throw new InvalidOperationException("This run is already being recorded.");
            try { return new RunJournal(Path.Combine(_directory, $"{header.Id:N}.partial"), header, () => _activeJournals.TryRemove(header.Id, out _)); }
            catch { _activeJournals.TryRemove(header.Id, out _); throw; }
        }
        finally { reservation.Dispose(); }
    }

    private void EnsureCapacity(Guid id)
    {
        var count = Directory.EnumerateFiles(_directory, "*.wisprun").Take(MaximumLibraryEntries).Count();
        var reserved = _activeJournals.Count - (_activeJournals.ContainsKey(id) ? 1 : 0);
        if (count + reserved >= MaximumLibraryEntries)
        {
            Volatile.Write(ref _isFull, true);
            throw new RunLibraryFullException();
        }
    }

    private void EnsureDirectory()
    {
        CheckPath(_directory);
        Directory.CreateDirectory(_directory);
    }

    private string RunPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose a saved run.", nameof(id));
        var path = Path.Combine(_directory, $"{id:N}.wisprun");
        CheckPath(path);
        return path;
    }

    private string SummaryPath(Guid id) => Path.Combine(_directory, $"{id:N}.summary.json");

    internal static void CheckPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Run files cannot use linked folders or files.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void ValidateExternalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".wisprun", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .wisprun file using a full path.");
        CheckPath(path);
    }

    private static async Task<RecordedRun> ReadAsync(string path, Guid? expectedId = null)
    {
        CheckPath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The run file is missing or too large.");
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var bounded = new BoundedReadStream(gzip, MaximumJsonBytes);
        using var reader = new StreamReader(bounded);
        var lines = new BoundedLineReader(reader);
        var header = await lines.ReadLineAsync(131_072).ConfigureAwait(false) ?? throw new InvalidDataException("The run is empty.");
        var run = JsonSerializer.Deserialize<RecordedRun>(header, JsonOptions) ?? throw new InvalidDataException("The run header is invalid.");
        if (run.SchemaVersion != RecordedRun.CurrentSchemaVersion || run.Samples is null || run.Samples.Length != 0 || run.Markers is null || run.Markers.Length > MaximumMarkers)
            throw new InvalidDataException("The run format is unsupported.");
        var samples = new List<RunSample>();
        while (await lines.ReadLineAsync(8192).ConfigureAwait(false) is { } line)
        {
            if (samples.Count >= MaximumSamples) throw new InvalidDataException("The run has too many samples.");
            var sample = JsonSerializer.Deserialize<RunSample>(line, JsonOptions) ?? throw new InvalidDataException("The run contains a missing sample.");
            ValidateSample(sample, samples.LastOrDefault());
            samples.Add(sample);
        }
        run = run with { Samples = samples.ToArray() };
        Validate(run);
        if (expectedId is { } expected && expected != run.Id)
            throw new InvalidDataException("The run file identity does not match its contents.");
        return run;
    }

    private async Task WriteAsync(RecordedRun run, bool overwrite)
    {
        Validate(run);
        if (!overwrite) EnsureCapacity(run.Id);
        var destination = RunPath(run.Id);
        var temporary = Path.Combine(_directory, $".{run.Id:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                await using (var writer = new StreamWriter(gzip, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true))
                {
                    long decodedBytes = 0;
                    await WriteRecordAsync(JsonSerializer.Serialize(run with { Samples = [] }, JsonOptions)).ConfigureAwait(false);
                    foreach (var sample in run.Samples)
                        await WriteRecordAsync(JsonSerializer.Serialize(sample, JsonOptions)).ConfigureAwait(false);

                    async Task WriteRecordAsync(string line)
                    {
                        decodedBytes += System.Text.Encoding.UTF8.GetByteCount(line) + System.Text.Encoding.UTF8.GetByteCount(writer.NewLine);
                        if (decodedBytes > MaximumJsonBytes) throw new InvalidDataException("The decoded run is too large to save.");
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                }
                file.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length > MaximumFileBytes) throw new InvalidDataException("The run is too large to save.");
            File.Move(temporary, destination, overwrite);
            try { await WriteSummaryAsync(Summarize(run)).ConfigureAwait(false); }
            catch (Exception error) when (IsDataError(error)) { Warning = "The run is saved. Its library summary will be rebuilt when needed."; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task WriteSummaryAsync(RunSummary summary)
    {
        var destination = SummaryPath(summary.Id);
        CheckPath(destination);
        var temporary = Path.Combine(_directory, $".{summary.Id:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(summary, JsonOptions)).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RecoverAsync()
    {
        if (_recovered) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.partial"))
        {
            try
            {
                CheckPath(path);
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var activeId) && _activeJournals.ContainsKey(activeId)) continue;
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumJsonBytes) continue;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                using var reader = new StreamReader(file);
                var lines = new BoundedLineReader(reader);
                var headerLine = await lines.ReadLineAsync(131_072).ConfigureAwait(false);
                if (headerLine is null) continue;
                var header = JsonSerializer.Deserialize<RecordedRun>(headerLine, JsonOptions);
                if (header is null || header.Samples is null || header.Samples.Length != 0 || header.Markers is null || header.Markers.Length > MaximumMarkers || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || header.Id != id) continue;
                var samples = new List<RunSample>();
                var markers = header.Markers.ToList();
                while (await lines.ReadLineAsync(8192).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 8192) break;
                    try
                    {
                        if (line.StartsWith("{\"marker\":", StringComparison.Ordinal))
                        {
                            var entry = JsonSerializer.Deserialize<RunJournalMarker>(line, JsonOptions);
                            if (entry is null || markers.Count >= MaximumMarkers) break;
                            ValidateMarker(entry.Marker, markers.LastOrDefault(), samples.LastOrDefault()?.ElapsedSeconds ?? -1);
                            markers.Add(entry.Marker);
                            continue;
                        }
                        if (samples.Count >= MaximumSamples) break;
                        var sample = JsonSerializer.Deserialize<RunSample>(line, JsonOptions);
                        if (sample is null) break;
                        ValidateSample(sample, samples.LastOrDefault());
                        samples.Add(sample);
                    }
                    catch (Exception error) when (IsDataError(error)) { break; }
                }
                if (samples.Count == 0) continue;
                if (!File.Exists(RunPath(header.Id)))
                    await WriteAsync(header with { Samples = samples.ToArray(), Markers = markers.ToArray(), IsIncomplete = true, FinishReason = "Recovered after Wisp closed before the run finished" }, false).ConfigureAwait(false);
                reader.Dispose();
                file.Dispose();
                File.Delete(path);
            }
            catch (Exception error) when (IsDataError(error))
            {
                Warning = "An unfinished run could not be recovered. Its file has been kept.";
            }
        }
        _recovered = true;
    }

    internal static RunSummary Summarize(RecordedRun run) => new(run.Id, run.Name, run.Tune, run.Notes,
        run.StartedAtUtc, run.Samples.Length == 0 ? 0 : run.Samples[^1].ElapsedSeconds,
        run.Samples.Length, run.Samples.Length == 0 ? 0 : run.Samples[0].State.CarOrdinal, run.IsIncomplete, run.FinishReason);

    private static bool ValidSummary(RunSummary value) => value.Id != Guid.Empty &&
        ValidText(value.Name, 100, false) && !string.IsNullOrWhiteSpace(value.Name) && ValidText(value.Tune, 150, false) &&
        ValidText(value.Notes, 4000, true) && ValidText(value.FinishReason, 200, false) &&
        double.IsFinite(value.DurationSeconds) && value.DurationSeconds is >= 0 and <= 600 &&
        value.SampleCount is > 0 and <= MaximumSamples && value.CarOrdinal is > 0 and <= 10_000_000;

    internal static void Validate(RecordedRun run)
    {
        if (run.SchemaVersion != RecordedRun.CurrentSchemaVersion || run.Samples is null || run.Samples.Length is 0 or > MaximumSamples ||
            run.Markers is null || run.Markers.Length > MaximumMarkers ||
            run.RejectedDatagrams < 0 || run.DroppedDatagrams < 0 ||
            run.StartedAtUtc < new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) || run.StartedAtUtc > DateTimeOffset.UtcNow.AddDays(1))
            throw new InvalidDataException("The run metadata, version, or sample count is invalid.");
        RunSample? previous = null;
        foreach (var sample in run.Samples)
        {
            ValidateSample(sample, previous);
            if (sample.State.CarOrdinal != run.Samples[0].State.CarOrdinal || sample.State.Drivetrain != run.Samples[0].State.Drivetrain)
                throw new InvalidDataException("A run cannot combine different cars or drivetrains.");
            previous = sample;
        }
        if (!ValidSummary(Summarize(run))) throw new InvalidDataException("The run metadata is invalid.");
        RunMarker? previousMarker = null;
        foreach (var marker in run.Markers)
        {
            ValidateMarker(marker, previousMarker, run.Samples[^1].ElapsedSeconds);
            previousMarker = marker;
        }
    }

    internal static bool ValidMarkerLabel(string label) => ValidText(label, MaximumMarkerLabelLength, false) && !string.IsNullOrWhiteSpace(label);

    private static void ValidateMarker(RunMarker marker, RunMarker? previous, double duration)
    {
        if (marker is null || !double.IsFinite(marker.ElapsedSeconds) || marker.ElapsedSeconds < 0 || marker.ElapsedSeconds > duration ||
            (previous is not null && marker.ElapsedSeconds < previous.ElapsedSeconds) || !ValidMarkerLabel(marker.Label))
            throw new InvalidDataException("The run contains an invalid moment marker.");
    }

    internal static void ValidateSample(RunSample sample, RunSample? previous)
    {
        if (sample is null) throw new InvalidDataException("The run contains a missing sample.");
        var state = sample.State;
        if (state is null || !double.IsFinite(sample.ElapsedSeconds) || sample.ElapsedSeconds is < 0 or > 600 ||
            sample.Segment < 0 || (previous is not null && (sample.ElapsedSeconds < previous.ElapsedSeconds || sample.Segment < previous.Segment)) ||
            state.CarOrdinal is <= 0 or > 10_000_000 || !Enum.IsDefined(state.Drivetrain) || !Enum.IsDefined(state.Gear) || state.NumCylinders is < 0 or > 32 ||
            !FiniteBound(state.GroundSpeedMetersPerSecond, 500) || !FiniteBound(state.EngineRpm, 40_000) || state.EngineRpm < 0 ||
            !FiniteBound(state.EngineMaximumRpm, 30_000) || state.EngineMaximumRpm < 0 ||
            !FiniteBound(state.PowerWatts, 100_000_000) || !FiniteBound(state.TorqueNm, 10_000_000) || !FiniteBound(state.BoostPressurePsi, 200) ||
            !FiniteBound(state.LateralAccelerationMetersPerSecondSquared, 10_000) || !FiniteBound(state.LongitudinalAccelerationMetersPerSecondSquared, 10_000) ||
            !ValidWheels(state.WheelRotationRadiansPerSecond, 10_000) || !ValidWheels(state.TireSlipRatio, 10_000) ||
            !ValidWheels(state.TireSlipAngle, 10_000) || !ValidWheels(state.NormalizedSuspensionTravel, 10_000) || !ValidWheels(state.TireTemperatureFahrenheit, 5000))
            throw new InvalidDataException("The run contains invalid or out-of-order samples.");
        var hasRadii = sample.FrontRadiusMeters is { } front && sample.RearRadiusMeters is { } rear && new RollingRadii(front, rear).IsPlausible;
        if ((sample.FrontRadiusMeters is not null || sample.RearRadiusMeters is not null || sample.WheelSpeedMetersPerSecond is not null) &&
            (!hasRadii || sample.WheelSpeedMetersPerSecond is not { } wheel || !double.IsFinite(wheel) || wheel < 0 ||
             Math.Abs(wheel - DrivenWheelSpeed.MetersPerSecond(state, new RollingRadii(sample.FrontRadiusMeters!.Value, sample.RearRadiusMeters!.Value))) > .001))
            throw new InvalidDataException("The run's wheel-speed calibration is inconsistent.");
    }

    private static bool ValidText(string? text, int maximum, bool multiline) => text is not null && text.Length <= maximum &&
        !text.Any(character => char.IsControl(character) && (!multiline || character is not '\r' and not '\n' and not '\t'));
    private static bool FiniteBound(float value, float bound) => float.IsFinite(value) && MathF.Abs(value) <= bound;
    private static bool ValidWheels(WheelValues value, float bound) => value.AreFinite() && value.MaximumAbsolute() <= bound;
    internal static bool IsDataError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException;

    internal sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private char[] _lineBuffer = new char[4096];
        private int _offset;
        private int _length;
        internal async ValueTask<string?> ReadLineAsync(int maximum)
        {
            var lineLength = 0;
            while (true)
            {
                if (_offset == _length)
                {
                    _length = await reader.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
                    _offset = 0;
                    if (_length == 0) return lineLength == 0 ? null : Finish(lineLength);
                }
                var newline = Array.IndexOf(_buffer, '\n', _offset, _length - _offset);
                var count = (newline < 0 ? _length : newline) - _offset;
                if (count > maximum - lineLength) throw new InvalidDataException("A run record is too large.");
                var required = lineLength + count;
                if (required > _lineBuffer.Length)
                    Array.Resize(ref _lineBuffer, Math.Min(maximum, Math.Max(required, _lineBuffer.Length * 2)));
                Array.Copy(_buffer, _offset, _lineBuffer, lineLength, count);
                lineLength = required;
                _offset += count;
                if (newline >= 0)
                {
                    _offset++;
                    return Finish(lineLength);
                }
            }
        }
        private string Finish(int length)
        {
            while (length > 0 && _lineBuffer[length - 1] == '\r') length--;
            return new string(_lineBuffer, 0, length);
        }
    }

    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        private int Count(int count) { _read += count; if (_read > maximum) throw new InvalidDataException("The decoded run is too large."); return count; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class RunJournalStartReservation(RunStore owner, SemaphoreSlim gate) : IDisposable
{
    private RunStore? _owner = owner;
    internal bool BelongsTo(RunStore store) => ReferenceEquals(Volatile.Read(ref _owner), store);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _owner, null) is not null) gate.Release();
    }
}

internal sealed class RunJournal : IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _file;
    private readonly StreamWriter _writer;
    private readonly Action _onClosed;
    private int _disposed;
    private long _decodedBytes;
    internal RunJournal(string path, RecordedRun header, Action onClosed)
    {
        _path = path;
        _onClosed = onClosed;
        RunStore.CheckPath(path);
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, true);
        _writer = new StreamWriter(_file, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true);
        try
        {
            var line = JsonSerializer.Serialize(header, RunStore.JsonOptions);
            _decodedBytes = System.Text.Encoding.UTF8.GetByteCount(line) + System.Text.Encoding.UTF8.GetByteCount(_writer.NewLine);
            _writer.WriteLine(line);
        }
        catch { _writer.Dispose(); _file.Dispose(); throw; }
    }
    internal async Task AppendAsync(RunSample sample)
    {
        await AppendLineAsync(JsonSerializer.Serialize(sample, RunStore.JsonOptions)).ConfigureAwait(false);
    }
    internal Task AppendMarkerAsync(RunMarker marker) => AppendLineAsync(JsonSerializer.Serialize(new RunJournalMarker(marker), RunStore.JsonOptions));

    private async Task AppendLineAsync(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(line) + System.Text.Encoding.UTF8.GetByteCount(_writer.NewLine);
        if (_decodedBytes + bytes > RunStore.MaximumJsonBytes) throw new InvalidDataException("The run reached its storage limit.");
        await _writer.WriteLineAsync(line).ConfigureAwait(false);
        _decodedBytes += bytes;
    }
    internal async Task FlushAsync()
    {
        await _writer.FlushAsync().ConfigureAwait(false);
        _file.Flush(flushToDisk: true);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await _file.DisposeAsync().ConfigureAwait(false); }
            finally { _onClosed(); }
        }
    }
    internal void RemoveAfterSave() { File.Delete(_path); }
}

internal sealed record RunJournalMarker(RunMarker Marker);

internal sealed class RunLibraryFullException() : IOException("The run library is full. Export or remove a run before recording another.");
