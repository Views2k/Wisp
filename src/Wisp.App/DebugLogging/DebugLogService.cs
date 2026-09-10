using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Wisp.Core;

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
    private readonly Func<TachCaptureExport?> _tachSnapshot;
    private readonly long _maximumSegmentBytes;
    private readonly int _maximumSegments;
    private readonly TimeSpan _maximumAge;
    private readonly Channel<QueuedRecord> _records;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _pendingDrained = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _writerTask;
    private readonly object _stateGate = new();
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
        TimeSpan? maximumAge = null,
        Func<TachCaptureExport?>? tachSnapshot = null)
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
        _tachSnapshot = tachSnapshot ?? TachDiagnostics.Snapshot;
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
    public DateTimeOffset? ExpiresAtUtc
    {
        get { lock (_stateGate) { return _expiresAtUtc; } }
    }
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
            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || expiresAtUtc <= _utcNow())
                {
                    return false;
                }
                _expiresAtUtc = expiresAtUtc;
                Volatile.Write(ref _enabled, 1);
                TryQueue("event", new DebugEvent(
                    _utcNow(),
                    DebugEventCode.LoggingEnabled,
                    DebugEventCategory.Lifecycle));
            }
            return true;
        }
        catch (Exception exception) when (IsLocalStorageFailure(exception))
        {
            lock (_stateGate)
            {
                _expiresAtUtc = null;
                Volatile.Write(ref _enabled, 0);
            }
            return false;
        }
    }

    public bool ExpireIfNeeded(DateTimeOffset nowUtc)
    {
        lock (_stateGate)
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
    }

    public async Task DisableAsync()
    {
        lock (_stateGate)
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
        }
        await FlushAsync().ConfigureAwait(false);
    }

    public void TryLogSample(DebugTelemetrySample sample)
    {
        lock (_stateGate)
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
    }

    public void TryLogEvent(DebugEvent debugEvent)
    {
        lock (_stateGate)
        {
            if (IsEnabled && !ExpireIfNeeded(debugEvent.TimestampUtc))
            {
                TryQueue("event", debugEvent);
            }
        }
    }

    public void TryLogHealthSample(DebugHealthSample sample)
    {
        lock (_stateGate)
        {
            if (IsEnabled && !ExpireIfNeeded(sample.TimestampUtc))
            {
                TryQueue("health", sample);
            }
        }
    }

    public void TryLogTachInterval(TachIntervalDiagnostic sample)
    {
        lock (_stateGate)
        {
            if (IsEnabled && !ExpireIfNeeded(sample.TimestampUtc))
            {
                TryQueue("tach_interval", sample);
            }
        }
    }

    public Task<bool> ExportAsync(string destinationPath, string applicationVersion) =>
        Task.Run(() => ExportOnWorkerAsync(destinationPath, applicationVersion));

    private async Task<bool> ExportOnWorkerAsync(string destinationPath, string applicationVersion)
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

                PruneSegments(_utcNow());
                Directory.CreateDirectory(destinationDirectory);
                var temporaryPath = Path.Combine(
                    destinationDirectory,
                    $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.tmp");
                try
                {
                    var samples = new List<string>();
                    var events = new List<string>();
                    var health = new List<DebugHealthSample>();
                    var tach = new List<TachIntervalDiagnostic>();
                    var capture = _tachSnapshot();
                    var omittedRecords = 0L;
                    foreach (var segment in SafeSegmentFiles())
                    {
                        foreach (var line in File.ReadLines(segment))
                        {
                            if (!TryCollectExportLine(line, samples, events, health, tach))
                            {
                                omittedRecords++;
                            }
                        }
                    }

                    using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
                    {
                        WriteEntry(archive, "samples.ndjson", samples);
                        WriteEntry(archive, "events.ndjson", events);
                        WriteEntry(archive, "health.ndjson", health.Select(sample => JsonSerializer.Serialize(sample, JsonOptions)));
                        var manifest = new
                        {
                            schema_version = 2,
                            created_at_utc = _utcNow(),
                            wisp_version = applicationVersion,
                            samples = samples.Count,
                            events = events.Count,
                            health_samples = health.Count,
                            omitted_records = omittedRecords,
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
                            $"Unreadable or unsupported records omitted: {omittedRecords}. Missing records limit diagnostic coverage.\n" +
                            "This export contains only Wisp telemetry health metrics selected by the debug logging whitelist.\n\n" +
                            DebugDiagnosticReport.Build(health, DroppedRecords));
                        if (capture is not null || tach.Count > 0)
                        {
                            WriteEntry(archive, "tach-intervals.ndjson", tach.Select(sample => JsonSerializer.Serialize(sample, JsonOptions)));
                            WriteEntry(archive, "tach-manifest.json", JsonSerializer.Serialize(
                                TachDiagnosticReport.CreateManifest(tach, capture, DroppedRecords, omittedRecords), JsonOptions));
                            WriteEntry(archive, "tach-report.txt", TachDiagnosticReport.Build(tach, capture));
                            if (capture is not null)
                            {
                                WriteEntry(archive, "tach-input-startup.ndjson", capture.InputStartup.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-input-recent.ndjson", capture.InputRecent.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-native-startup.ndjson", capture.NativeStartup.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-native-recent.ndjson", capture.NativeRecent.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-needle-startup.ndjson", capture.NeedleStartup.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-needle-recent.ndjson", capture.NeedleRecent.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-renderer-startup.ndjson", capture.RendererStartup.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-renderer-recent.ndjson", capture.RendererRecent.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-lifecycle.ndjson", capture.Lifecycle.Select(SerializeDiagnostic));
                                WriteEntry(archive, "tach-native-context.ndjson", capture.NativeContexts.Select(SerializeDiagnostic));
                            }
                        }
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

        lock (_stateGate)
        {
            _expiresAtUtc = null;
            Volatile.Write(ref _enabled, 0);
        }
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
            // Retention is retried on the next safe local write, startup, enable, or export.
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

    private static string SerializeDiagnostic<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static bool TryCollectExportLine(string line, List<string> samples, List<string> events,
        List<DebugHealthSample> health, List<TachIntervalDiagnostic> tach)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var payload = root.GetProperty("payload").GetRawText();
            if (kind == "sample")
            {
                var sample = JsonSerializer.Deserialize<DebugTelemetrySample>(payload, JsonOptions);
                if (sample is not null)
                {
                    samples.Add(JsonSerializer.Serialize(CreateExportSample(sample), JsonOptions));
                    return true;
                }
            }
            else if (kind == "event")
            {
                var debugEvent = JsonSerializer.Deserialize<DebugEvent>(payload, JsonOptions);
                if (debugEvent is not null && Enum.IsDefined(debugEvent.Code) && Enum.IsDefined(debugEvent.Category))
                {
                    events.Add(JsonSerializer.Serialize(debugEvent, JsonOptions));
                    return true;
                }
            }
            else if (kind == "health")
            {
                var sample = JsonSerializer.Deserialize<DebugHealthSample>(payload, JsonOptions);
                if (sample is not null)
                {
                    health.Add(sample with
                    {
                        SessionId = TachDiagnosticReport.SafeCaptureId(sample.SessionId) ?? string.Empty,
                        NativeStatus = SafeEnum<NativeAssistProviderStatus>(sample.NativeStatus),
                        GameplayVisibility = SafeEnum<NativeGameplayVisibility>(sample.GameplayVisibility)
                    });
                    return true;
                }
            }
            else if (kind == "tach_interval")
            {
                var interval = JsonSerializer.Deserialize<TachIntervalDiagnostic>(payload, JsonOptions);
                if (interval is not null && TachDiagnosticReport.SanitizeInterval(interval) is { } safe)
                {
                    tach.Add(safe);
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or NotSupportedException)
        {
            // Partial records are counted without copying their contents into the export.
        }
        return false;
    }

    private static JsonObject CreateExportSample(DebugTelemetrySample sample)
    {
        var safe = sample with
        {
            Drivetrain = sample.Drivetrain is null ? null : SafeEnum<DrivetrainType>(sample.Drivetrain),
            SpeedUnit = SafeEnum<SpeedUnit>(sample.SpeedUnit),
            SpeedSource = SafeEnum<SpeedSourceMode>(sample.SpeedSource),
            Gear = sample.Gear is null ? null : SafeEnum<TransmissionGear>(sample.Gear),
            CalibrationState = sample.CalibrationState is null ? null : SafeCalibrationState(sample.CalibrationState),
            NativeProviderStatus = SafeEnum<NativeAssistProviderStatus>(sample.NativeProviderStatus),
            ExactRedlineStatus = SafeEnum<ExactRedlineStatus>(sample.ExactRedlineStatus),
            GameplayHudVisibility = SafeEnum<NativeGameplayVisibility>(sample.GameplayHudVisibility),
            OverlayLayout = SafeEnum<HudLayoutMode>(sample.OverlayLayout)
        };
        var payload = (JsonObject)JsonSerializer.SerializeToNode(safe, JsonOptions)!;
        payload["game_fps"] = null;
        payload["game_fps_status"] = "not_available_in_fh6_data_out";
        return payload;
    }

    private static string SafeEnum<T>(string? value) where T : struct, Enum =>
        value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) ? value : "Unknown";

    private static string SafeCalibrationState(string value) => value switch
    {
        "trusted" or "sample_accepted" or "not_driving" or "stale_telemetry" or
        "ground_speed_out_of_range" or "invalid_wheel_values" or "wheel_speed_too_low" or
        "tire_slip" or "steering_input" or "cornering_acceleration" or "longitudinal_acceleration" or
        "longitudinal_deceleration" or "braking_input" or "driven_axle_unloaded" or
        "wheel_speeds_disagree" or "implausible_radius" or "candidate_radius_outlier" or
        "stable_consensus_pending" or "replacement_consensus_pending" or "replacement_sample_rejected" or
        "replacement_window_exhausted" or "not_available" => value,
        _ => "unknown"
    };

    private static void WriteEntry(ZipArchive archive, string name, IEnumerable<string> lines)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n"
        };
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }

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
