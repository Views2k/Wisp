using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Wisp.App.DebugLogging;

internal enum DebugTelemetryState
{
    Waiting,
    Connected,
    Lost
}

internal enum DebugListenerState
{
    Ready,
    Error
}

internal readonly record struct DebugWheelValues(
    double? FrontLeft,
    double? FrontRight,
    double? RearLeft,
    double? RearRight);

internal enum DebugEventCode
{
    LoggingEnabled,
    LoggingDisabled,
    LoggingExpired,
    TelemetryWaiting,
    TelemetryConnected,
    TelemetryLost,
    TelemetryListenerUnavailable,
    TelemetryListenerRecovered
}

internal enum DebugEventCategory
{
    Lifecycle,
    TelemetryState,
    LocalListener
}

internal sealed record DebugTelemetrySample(
    DateTimeOffset TimestampUtc,
    DebugTelemetryState TelemetryState,
    DebugListenerState ListenerState,
    bool RaceOn,
    uint? GameTimestampMilliseconds,
    bool? GameTimestampAdvanced,
    bool? GameTimestampStalled,
    double TelemetryProcessedHz,
    double WispCompositionHz,
    double? WispCpuPercent,
    long WispWorkingSetBytes,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double? PacketAgeMilliseconds,
    long AcceptedPackets,
    long RejectedPackets,
    int? CarOrdinal,
    string? Drivetrain,
    double? GroundSpeedMetersPerSecond,
    bool IndicatedSpeedAvailable,
    double? IndicatedSpeedMetersPerSecond,
    double? IndicatedSpeedDisplayValue,
    string SpeedUnit,
    string SpeedSource,
    DebugWheelValues WheelRotationRadiansPerSecond,
    DebugWheelValues TireSlipRatio,
    double? TrustedFrontRadiusMeters,
    double? TrustedRearRadiusMeters,
    double? ProvisionalFrontRadiusMeters,
    double? ProvisionalRearRadiusMeters,
    double? CalibrationConfidence,
    int? CalibrationAcceptedSamples,
    bool CalibrationTrusted,
    string? CalibrationState,
    double? EngineRpm,
    double? EngineMaximumRpm,
    string? Gear,
    double? PowerWatts,
    double? TorqueNm,
    double? BoostPressurePsi,
    DebugWheelValues TireTemperatureFahrenheit,
    double? LateralAccelerationMetersPerSecondSquared,
    double? LongitudinalAccelerationMetersPerSecondSquared,
    int? Steering,
    int? Accelerator,
    int? Brake,
    string NativeProviderStatus,
    string ExactRedlineStatus,
    bool NativeCapabilitiesAvailable,
    string GameplayHudVisibility,
    bool GameplayHudVisibilityFresh,
    string OverlayLayout,
    bool OverlayRequestedVisible,
    bool OverlayManuallyHidden,
    bool OverlayLocked);

internal sealed record DebugEvent(
    DateTimeOffset TimestampUtc,
    DebugEventCode Code,
    DebugEventCategory Category);

internal sealed class DebugLogService : IAsyncDisposable
{
    internal const long DefaultMaximumSegmentBytes = 5L * 1024 * 1024;
    internal const int DefaultMaximumSegments = 3;
    internal static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(7);
    internal static readonly TimeSpan EnableDuration = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string _rootDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly long _maximumSegmentBytes;
    private readonly int _maximumSegments;
    private readonly TimeSpan _maximumAge;
    private readonly Channel<QueuedRecord> _records;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _pendingDrained = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _writerTask;
    private DateTimeOffset? _expiresAtUtc;
    private int _enabled;
    private long _droppedRecords;
    private long _pendingRecords;
    private int _disposed;
    private string? _currentSegmentPath;
    private long _currentSegmentLength;

    public DebugLogService(
        string? rootDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        long maximumSegmentBytes = DefaultMaximumSegmentBytes,
        int maximumSegments = DefaultMaximumSegments,
        TimeSpan? maximumAge = null)
    {
        if (maximumSegmentBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSegmentBytes));
        }
        if (maximumSegments < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSegments));
        }

        _rootDirectory = rootDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "DebugLogs")
            : Path.GetFullPath(rootDirectory);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _maximumSegmentBytes = maximumSegmentBytes;
        _maximumSegments = maximumSegments;
        _maximumAge = maximumAge ?? DefaultMaximumAge;
        _records = Channel.CreateBounded<QueuedRecord>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        PruneSegments(_utcNow());
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    public DateTimeOffset? ExpiresAtUtc => _expiresAtUtc;
    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);
    public bool HasLocalLogs => SafeSegmentFiles().Length > 0;

    public bool TryEnable(DateTimeOffset expiresAtUtc)
    {
        var nowUtc = _utcNow();
        if (Volatile.Read(ref _disposed) != 0 ||
            expiresAtUtc <= nowUtc ||
            expiresAtUtc > nowUtc + EnableDuration)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_rootDirectory);
            PruneSegments(_utcNow());
            _expiresAtUtc = expiresAtUtc;
            Volatile.Write(ref _enabled, 1);
            TryQueue("event", new DebugEvent(
                _utcNow(),
                DebugEventCode.LoggingEnabled,
                DebugEventCategory.Lifecycle));
            return true;
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
            _expiresAtUtc = null;
            Volatile.Write(ref _enabled, 0);
            return false;
        }
    }

    public bool ExpireIfNeeded(DateTimeOffset nowUtc)
    {
        if (!IsEnabled || _expiresAtUtc is not { } expiresAtUtc || nowUtc < expiresAtUtc)
        {
            return false;
        }

        TryQueue("event", new DebugEvent(
            nowUtc,
            DebugEventCode.LoggingExpired,
            DebugEventCategory.Lifecycle));
        _expiresAtUtc = null;
        Volatile.Write(ref _enabled, 0);
        return true;
    }

    public async Task DisableAsync()
    {
        if (!IsEnabled)
        {
            _expiresAtUtc = null;
            return;
        }

        TryQueue("event", new DebugEvent(
            _utcNow(),
            DebugEventCode.LoggingDisabled,
            DebugEventCategory.Lifecycle));
        _expiresAtUtc = null;
        Volatile.Write(ref _enabled, 0);
        await FlushAsync().ConfigureAwait(false);
    }

    public void TryLogSample(DebugTelemetrySample sample)
    {
        if (IsEnabled && !ExpireIfNeeded(sample.TimestampUtc))
        {
            TryQueue("sample", new
            {
                sample.TimestampUtc,
                sample.TelemetryState,
                sample.RaceOn,
                sample.TelemetryProcessedHz,
                sample.WispCompositionHz,
                game_fps = (double?)null,
                game_fps_status = "not_available_in_fh6_data_out",
                sample.WispCpuPercent,
                sample.WispWorkingSetBytes,
                sample.ManagedHeapBytes,
                sample.Gen0Collections,
                sample.Gen1Collections,
                sample.Gen2Collections,
                sample.PacketAgeMilliseconds,
                sample.AcceptedPackets,
                sample.RejectedPackets,
                sample.ListenerState,
                sample.GameTimestampMilliseconds,
                sample.GameTimestampAdvanced,
                sample.GameTimestampStalled,
                sample.CarOrdinal,
                sample.Drivetrain,
                sample.GroundSpeedMetersPerSecond,
                sample.IndicatedSpeedAvailable,
                sample.IndicatedSpeedMetersPerSecond,
                sample.IndicatedSpeedDisplayValue,
                sample.SpeedUnit,
                sample.SpeedSource,
                sample.WheelRotationRadiansPerSecond,
                sample.TireSlipRatio,
                sample.TrustedFrontRadiusMeters,
                sample.TrustedRearRadiusMeters,
                sample.ProvisionalFrontRadiusMeters,
                sample.ProvisionalRearRadiusMeters,
                sample.CalibrationConfidence,
                sample.CalibrationAcceptedSamples,
                sample.CalibrationTrusted,
                sample.CalibrationState,
                sample.EngineRpm,
                sample.EngineMaximumRpm,
                sample.Gear,
                sample.PowerWatts,
                sample.TorqueNm,
                sample.BoostPressurePsi,
                sample.TireTemperatureFahrenheit,
                sample.LateralAccelerationMetersPerSecondSquared,
                sample.LongitudinalAccelerationMetersPerSecondSquared,
                sample.Steering,
                sample.Accelerator,
                sample.Brake,
                sample.NativeProviderStatus,
                sample.ExactRedlineStatus,
                sample.NativeCapabilitiesAvailable,
                sample.GameplayHudVisibility,
                sample.GameplayHudVisibilityFresh,
                sample.OverlayLayout,
                sample.OverlayRequestedVisible,
                sample.OverlayManuallyHidden,
                sample.OverlayLocked
            });
        }
    }

    public void TryLogEvent(DebugEvent debugEvent)
    {
        if (IsEnabled && !ExpireIfNeeded(debugEvent.TimestampUtc))
        {
            TryQueue("event", debugEvent);
        }
    }

    public async Task<bool> ExportAsync(string destinationPath, string applicationVersion)
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var destination = Path.GetFullPath(destinationPath);
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    return false;
                }

                Directory.CreateDirectory(destinationDirectory);
                var temporaryPath = Path.Combine(
                    destinationDirectory,
                    $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.tmp");
                try
                {
                    var samples = new List<string>();
                    var events = new List<string>();
                    foreach (var segment in SafeSegmentFiles())
                    {
                        foreach (var line in File.ReadLines(segment))
                        {
                            TryCollectExportLine(line, samples, events);
                        }
                    }

                    using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
                    {
                        WriteEntry(archive, "samples.ndjson", samples);
                        WriteEntry(archive, "events.ndjson", events);
                        var manifest = new
                        {
                            schema_version = 1,
                            created_at_utc = _utcNow(),
                            wisp_version = applicationVersion,
                            samples = samples.Count,
                            events = events.Count,
                            dropped_records = DroppedRecords,
                            game_fps = (double?)null,
                            game_fps_status = "not_available_in_fh6_data_out",
                            local_only = true
                        };
                        WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
                        WriteEntry(
                            archive,
                            "summary.txt",
                            $"Wisp local debug export\nSamples: {samples.Count}\nEvents: {events.Count}\n" +
                            "Game FPS: not available in FH6 Data Out\n" +
                            "This export contains only Wisp telemetry health metrics selected by the debug logging whitelist.\n");
                    }

                    File.Move(temporaryPath, destination, overwrite: true);
                    return true;
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }
            }
            finally
            {
                _fileGate.Release();
            }
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception) || exception is InvalidDataException)
        {
            return false;
        }
    }

    public async Task<bool> DeleteLocalLogsAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var segment in SafeSegmentFiles())
                {
                    TryDeleteFile(segment);
                }
                _currentSegmentPath = null;
                _currentSegmentLength = 0;

                return SafeSegmentFiles().Length == 0;
            }
            finally
            {
                _fileGate.Release();
            }
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _enabled, 0);
        _records.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception) || exception is OperationCanceledException)
        {
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _fileGate.Dispose();
        _flushGate.Dispose();
        _pendingDrained.Dispose();
    }

    private void TryQueue<T>(string kind, T payload)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            Interlocked.Increment(ref _pendingRecords);
            if (!_records.Writer.TryWrite(new QueuedRecord(kind, payloadJson)))
            {
                CompletePendingRecord();
                Interlocked.Increment(ref _droppedRecords);
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            Interlocked.Increment(ref _droppedRecords);
        }
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var record in _records.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            try
            {
                await _fileGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    WriteRecord(record);
                }
                finally
                {
                    _fileGate.Release();
                }
            }
            catch (Exception exception) when (IsLocalStorageFailure(exception))
            {
                Interlocked.Increment(ref _droppedRecords);
            }
            finally
            {
                CompletePendingRecord();
            }
        }
    }

    private void WriteRecord(QueuedRecord record)
    {
        Directory.CreateDirectory(_rootDirectory);
        var line = $"{{\"kind\":{JsonSerializer.Serialize(record.Kind)},\"payload\":{record.PayloadJson}}}\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (_currentSegmentPath is null || !File.Exists(_currentSegmentPath))
        {
            _currentSegmentPath = SafeSegmentFiles().LastOrDefault();
            _currentSegmentLength = _currentSegmentPath is null ? 0 : new FileInfo(_currentSegmentPath).Length;
        }
        if (_currentSegmentPath is null || _currentSegmentLength + bytes.Length > _maximumSegmentBytes)
        {
            _currentSegmentPath = Path.Combine(
                _rootDirectory,
                $"segment-{_utcNow():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.ndjson");
            _currentSegmentLength = 0;
        }

        var currentSegmentPath = _currentSegmentPath!;
        using (var stream = new FileStream(currentSegmentPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
                   FileOptions.SequentialScan))
        {
            stream.Write(bytes);
        }
        _currentSegmentLength += bytes.Length;
        PruneSegments(_utcNow());
        if (!File.Exists(currentSegmentPath))
        {
            _currentSegmentPath = null;
            _currentSegmentLength = 0;
        }
    }

    private async Task FlushAsync()
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Interlocked.Read(ref _pendingRecords) > 0)
            {
                await _pendingDrained.WaitAsync().ConfigureAwait(false);
            }

            await _fileGate.WaitAsync().ConfigureAwait(false);
            _fileGate.Release();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void CompletePendingRecord()
    {
        if (Interlocked.Decrement(ref _pendingRecords) == 0)
        {
            _pendingDrained.Release();
        }
    }

    private void PruneSegments(DateTimeOffset nowUtc)
    {
        try
        {
            var files = SafeSegmentFiles();
            foreach (var file in files.Where(file => nowUtc - File.GetLastWriteTimeUtc(file) > _maximumAge))
            {
                TryDeleteFile(file);
            }

            files = SafeSegmentFiles();
            foreach (var file in files.Take(Math.Max(0, files.Length - _maximumSegments)))
            {
                TryDeleteFile(file);
            }
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
            // Retention is retried on the next safe local write or startup.
        }
    }

    private string[] SafeSegmentFiles()
    {
        try
        {
            return Directory.Exists(_rootDirectory)
                ? Directory.GetFiles(_rootDirectory, "segment-*.ndjson")
                    .OrderBy(File.GetLastWriteTimeUtc)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
            return [];
        }
    }

    private static void TryCollectExportLine(string line, List<string> samples, List<string> events)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var payload = root.GetProperty("payload").GetRawText();
            if (kind == "sample")
            {
                samples.Add(payload);
            }
            else if (kind == "event")
            {
                events.Add(payload);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // A partial record is omitted from the user-created export.
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, IEnumerable<string> lines) =>
        WriteEntry(archive, name, string.Join('\n', lines) + (lines.Any() ? "\n" : string.Empty));

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
        }
    }

    private static bool IsLocalStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private sealed record QueuedRecord(string Kind, string PayloadJson);
}
