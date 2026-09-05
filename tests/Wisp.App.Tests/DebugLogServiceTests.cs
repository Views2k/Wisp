using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DebugLogServiceTests
{
    [Fact]
    public async Task DisabledLoggerCreatesNoDirectoryOrFiles()
    {
        var root = TemporaryDirectory();
        try
        {
            var logDirectory = Path.Combine(root, "logs");
            await using (var service = new DebugLogService(logDirectory))
            {
                service.TryLogSample(Sample(DateTimeOffset.UtcNow));
                service.TryLogHealthSample(new DebugHealthSample { TimestampUtc = DateTimeOffset.UtcNow });
            }

            Assert.False(Directory.Exists(logDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportContainsOnlyTheFixedLocalHealthSchema()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            var exportPath = Path.Combine(root, "debug.zip");
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => now);
            Assert.True(service.TryEnable(now + DebugLogService.EnableDuration));
            service.TryLogSample(Sample(now));
            service.TryLogEvent(new DebugEvent(
                now,
                DebugEventCode.TelemetryConnected,
                DebugEventCategory.TelemetryState));

            Assert.True(await service.ExportAsync(exportPath, "1.0.10"));

            using var archive = ZipFile.OpenRead(exportPath);
            Assert.Equal(
                ["events.ndjson", "health.ndjson", "manifest.json", "samples.ndjson", "summary.txt"],
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
            var samples = ReadEntry(archive, "samples.ndjson");
            Assert.Contains("\"telemetry_processed_hz\":60", samples, StringComparison.Ordinal);
            Assert.Contains("\"wisp_composition_hz\":59.5", samples, StringComparison.Ordinal);
            Assert.Contains("\"game_fps\":null", samples, StringComparison.Ordinal);
            Assert.Contains("not_available_in_fh6_data_out", samples, StringComparison.Ordinal);
            Assert.DoesNotContain("packet_bytes", samples, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"process\":", samples, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"address\":", samples, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"machine\":", samples, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"username\":", samples, StringComparison.OrdinalIgnoreCase);
            using (var sampleDocument = JsonDocument.Parse(
                       samples.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single()))
            {
                var expectedSampleFields = typeof(DebugTelemetrySample).GetProperties()
                    .Select(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name))
                    .Append("game_fps")
                    .Append("game_fps_status")
                    .OrderBy(name => name)
                    .ToArray();
                Assert.Equal(
                    expectedSampleFields,
                    sampleDocument.RootElement.EnumerateObject()
                        .Select(property => property.Name)
                        .OrderBy(name => name)
                        .ToArray());
            }
            var events = ReadEntry(archive, "events.ndjson");
            foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var debugEvent = JsonDocument.Parse(line);
                Assert.Equal(
                    ["category", "code", "timestamp_utc"],
                    debugEvent.RootElement.EnumerateObject()
                        .Select(property => property.Name)
                        .OrderBy(name => name)
                        .ToArray());
            }

            using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
            Assert.True(manifest.RootElement.GetProperty("local_only").GetBoolean());
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("game_fps").ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentExportsShareOnePendingDrainWithoutDeadlocking()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => now);
            Assert.True(service.TryEnable(now + DebugLogService.EnableDuration));

            var fileGate = Assert.IsType<SemaphoreSlim>(typeof(DebugLogService)
                .GetField("_fileGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service));
            await fileGate.WaitAsync(TestContext.Current.CancellationToken);
            Task<bool> firstExport;
            Task<bool> secondExport;
            try
            {
                service.TryLogSample(Sample(now));
                firstExport = service.ExportAsync(Path.Combine(root, "first.zip"), "1.0.10");
                secondExport = service.ExportAsync(Path.Combine(root, "second.zip"), "1.0.10");
                Assert.False(firstExport.IsCompleted);
                Assert.False(secondExport.IsCompleted);
            }
            finally
            {
                fileGate.Release();
            }

            var results = await Task.WhenAll(firstExport, secondExport)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.All(results, Assert.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnablementExpiresWithoutASeparateTimer()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => now);
            Assert.True(service.TryEnable(now + DebugLogService.EnableDuration));

            now += DebugLogService.EnableDuration;

            Assert.True(service.ExpireIfNeeded(now));
            Assert.False(service.IsEnabled);
            Assert.Null(service.ExpiresAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupPrunesSegmentsOlderThanSevenDaysWhileDisabled()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var oldSegment = Path.Combine(logs, "segment-20260801-000000-000-old.ndjson");
            File.WriteAllText(oldSegment, "{}\n");
            File.SetLastWriteTimeUtc(oldSegment, now.UtcDateTime - TimeSpan.FromDays(8));

            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => now);

            Assert.False(service.IsEnabled);
            Assert.False(File.Exists(oldSegment));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RotationKeepsAtMostThreeBoundedSegmentsAndDeleteClearsThem()
    {
        var root = TemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            var logs = Path.Combine(root, "logs");
            var exportPath = Path.Combine(root, "flush.zip");
            await using var service = new DebugLogService(
                logs,
                () => now,
                maximumSegmentBytes: 4096,
                maximumSegments: 3,
                maximumAge: TimeSpan.FromDays(7));
            Assert.True(service.TryEnable(now + DebugLogService.EnableDuration));
            for (var index = 0; index < 80; index++)
            {
                now += TimeSpan.FromSeconds(1);
                service.TryLogSample(Sample(now));
            }

            Assert.True(await service.ExportAsync(exportPath, "1.0.10"));
            var segments = Directory.GetFiles(logs, "segment-*.ndjson");
            Assert.InRange(segments.Length, 1, 3);
            Assert.All(segments, path => Assert.InRange(new FileInfo(path).Length, 1, 4096));

            Assert.True(await service.DeleteLocalLogsAsync());
            Assert.Empty(Directory.GetFiles(logs, "segment-*.ndjson"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptRecordsAreCountedWithoutIncludingTheirContentsInTheReport()
    {
        var root = TemporaryDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "segment-partial.ndjson"), "not-json-private-sentinel\n");
            await using var service = new DebugLogService(logs);
            var export = Path.Combine(root, "debug.zip");
            Assert.True(await service.ExportAsync(export, "1.0.12"));
            using var archive = ZipFile.OpenRead(export);
            using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
            Assert.Equal(1, manifest.RootElement.GetProperty("omitted_records").GetInt64());
            var summary = ReadEntry(archive, "summary.txt");
            Assert.Contains("Unreadable or unsupported records omitted: 1", summary);
            Assert.DoesNotContain("private-sentinel", summary);
            Assert.Empty(ReadEntry(archive, "health.ndjson"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DebugTelemetrySample Sample(DateTimeOffset now) => new(
        TimestampUtc: now,
        TelemetryState: DebugTelemetryState.Connected,
        ListenerState: DebugListenerState.Ready,
        RaceOn: true,
        GameTimestampMilliseconds: 1000,
        GameTimestampAdvanced: true,
        GameTimestampStalled: false,
        TelemetryProcessedHz: 60,
        WispCompositionHz: 59.5,
        WispCpuPercent: 2.5,
        WispWorkingSetBytes: 100_000_000,
        ManagedHeapBytes: 10_000_000,
        Gen0Collections: 2,
        Gen1Collections: 1,
        Gen2Collections: 0,
        PacketAgeMilliseconds: 4,
        AcceptedPackets: 120,
        RejectedPackets: 0,
        CarOrdinal: 123,
        Drivetrain: "RearWheelDrive",
        GroundSpeedMetersPerSecond: 20,
        IndicatedSpeedAvailable: true,
        IndicatedSpeedMetersPerSecond: 20.1,
        IndicatedSpeedDisplayValue: 45,
        SpeedUnit: "MilesPerHour",
        SpeedSource: "WheelIndicated",
        WheelRotationRadiansPerSecond: new DebugWheelValues(60, 60, 61, 61),
        TireSlipRatio: new DebugWheelValues(0.01, 0.01, 0.02, 0.02),
        TrustedFrontRadiusMeters: 0.33,
        TrustedRearRadiusMeters: 0.34,
        ProvisionalFrontRadiusMeters: null,
        ProvisionalRearRadiusMeters: null,
        CalibrationConfidence: 1,
        CalibrationAcceptedSamples: 20,
        CalibrationTrusted: true,
        CalibrationState: "trusted",
        EngineRpm: 4000,
        EngineMaximumRpm: 8000,
        Gear: "Third",
        PowerWatts: 200_000,
        TorqueNm: 450,
        BoostPressurePsi: 12,
        TireTemperatureFahrenheit: new DebugWheelValues(180, 181, 182, 183),
        LateralAccelerationMetersPerSecondSquared: 1.2,
        LongitudinalAccelerationMetersPerSecondSquared: 0.5,
        Steering: 3,
        Accelerator: 200,
        Brake: 0,
        NativeProviderStatus: "Ready",
        ExactRedlineStatus: "Exact",
        NativeCapabilitiesAvailable: true,
        GameplayHudVisibility: "Visible",
        GameplayHudVisibilityFresh: true,
        OverlayLayout: "Native",
        OverlayRequestedVisible: true,
        OverlayManuallyHidden: false,
        OverlayLocked: true);

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wisp-debug-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
