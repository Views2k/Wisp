using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TachDiagnosticExportTests
{
    private const string OldCaptureId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CurrentCaptureId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [Fact]
    public async Task CurrentCaptureExportsRawHistoryCoverageAndOnlyItsOwnReportTotals()
    {
        var root = TemporaryDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            var input = new TachInputDiagnostic("Accepted", 100, 90, 1, 1000, 6000, 8000, 7, true, "None");
            var needle = new TachNeedleDiagnostic
            {
                ControlId = 1,
                ControlKind = "analogue",
                HostKind = "OverlayWindow",
                HostWindowHandle = 123,
                Route = "directcomposition",
                Source = "native",
                IsLoaded = true,
                IsVisible = true,
                IsLive = true,
                NeedleVisible = true,
                CarOrdinal = 7,
                AppliedTimestamp = 100,
                RawRpm = 6000,
                Angle = 300,
                Blur = -.1
            };
            var capture = Capture() with
            {
                InputStartup = [input],
                InputRecent = [input, input with { Timestamp = 200, Sequence = 2 }],
                InputOverwritten = 4,
                NeedleStartup = [needle],
                NativeContexts = [new TachNativeContext(100, "fh6-steam-test", 1, "6.440.853.0", 1, 4, false, true)]
            };
            await using var service = new DebugLogService(logs, () => Now, tachSnapshot: () => capture);
            Assert.True(service.TryEnable(Now + DebugLogService.EnableDuration));
            service.TryLogTachInterval(Interval(OldCaptureId, Now.AddMinutes(-1), 177777));
            service.TryLogTachInterval(Interval(CurrentCaptureId, Now, 123));
            var destination = Path.Combine(root, "capture.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));

            using var archive = ZipFile.OpenRead(destination);
            Assert.Equal(16, archive.Entries.Count);
            var report = Read(archive, "tach-report.txt");
            Assert.Contains(CurrentCaptureId, report);
            Assert.DoesNotContain("177777", report);
            Assert.Contains("Selected persisted intervals: 1", report);
            Assert.Contains("physical display are not measured", report);
            Assert.Contains("validated angle changes", report);
            Assert.Equal(2, Read(archive, "tach-intervals.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(2, Read(archive, "tach-input-recent.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("fh6-steam-test", Read(archive, "tach-native-context.ndjson"));
            using var manifest = JsonDocument.Parse(Read(archive, "tach-manifest.json"));
            Assert.Equal(CurrentCaptureId, manifest.RootElement.GetProperty("selected_capture_id").GetString());
            Assert.True(manifest.RootElement.GetProperty("raw_lost_after_process_restart").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("gpu_presentation_measured").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("physical_display_measured").GetBoolean());
            var routePolicy = manifest.RootElement.GetProperty("needle_route_policy").GetString()!;
            Assert.Contains("WPF transform application", routePolicy);
            Assert.Contains("successful Present submissions only, excluding queue-busy and occluded attempts", routePolicy);
            Assert.Contains("sampling time, not presentation completion", routePolicy);
            Assert.Contains(routePolicy, report);
            using var rawNeedle = JsonDocument.Parse(Read(archive, "tach-needle-startup.ndjson"));
            Assert.Equal("directcomposition", rawNeedle.RootElement.GetProperty("route").GetString());
            var coverage = manifest.RootElement.GetProperty("input");
            Assert.Equal(1, coverage.GetProperty("startup_recent_duplicate_records").GetInt32());
            Assert.Equal(4, coverage.GetProperty("recent_overwritten").GetInt64());
            Assert.Equal(18_000, coverage.GetProperty("startup_capacity").GetInt32());
            Assert.Equal(45_000, coverage.GetProperty("recent_capacity").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RestartedProcessKeepsLatestPersistedCaptureWithoutInventingRawHistory()
    {
        var root = TemporaryDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "segment-retained.ndjson"),
                Envelope("tach_interval", Interval(CurrentCaptureId, Now, 321)) + "\n" +
                Envelope("tach_interval", Interval(OldCaptureId, Now.AddMinutes(-1), 177777)) + "\n");
            await using var service = new DebugLogService(logs, () => Now, tachSnapshot: () => null);
            var destination = Path.Combine(root, "capture.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));
            using var archive = ZipFile.OpenRead(destination);
            Assert.Equal(8, archive.Entries.Count);
            Assert.Null(archive.GetEntry("tach-input-recent.ndjson"));
            var report = Read(archive, "tach-report.txt");
            Assert.Contains(CurrentCaptureId, report);
            Assert.Contains("Raw histories are unavailable", report);
            Assert.DoesNotContain("177777", report);
            using var manifest = JsonDocument.Parse(Read(archive, "tach-manifest.json"));
            Assert.False(manifest.RootElement.GetProperty("raw_capture_available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("stopwatch_frequency").ValueKind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RereadDropsExtraFieldsAndRejectsUnsupportedTachIdentity()
    {
        var root = TemporaryDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "segment-injected.ndjson"), """
                {"kind":"sample","payload":{"timestamp_utc":"2026-09-08T12:00:00Z","telemetry_processed_hz":60,"drivetrain":"private-sentinel","game_fps":999,"game_fps_status":"private-sentinel","extra":{"path":"private-sentinel"}}}
                {"kind":"event","payload":{"timestamp_utc":"2026-09-08T12:00:00Z","code":"logging_enabled","category":"lifecycle","extra":"private-sentinel"}}
                {"kind":"health","payload":{"timestamp_utc":"2026-09-08T12:00:00Z","session_id":"private-sentinel","native_status":"private-sentinel","extra":"private-sentinel"}}
                {"kind":"tach_interval","payload":{"capture_id":"private-sentinel","interval_milliseconds":1000,"native_reads":[],"needles":[]}}
                """);
            await using var service = new DebugLogService(logs, () => Now, tachSnapshot: () => null);
            var destination = Path.Combine(root, "capture.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));
            using var archive = ZipFile.OpenRead(destination);
            Assert.Equal(5, archive.Entries.Count);
            Assert.All(archive.Entries, entry => Assert.DoesNotContain("private-sentinel", Read(archive, entry.FullName)));
            using var sample = JsonDocument.Parse(Read(archive, "samples.ndjson"));
            Assert.False(sample.RootElement.TryGetProperty("extra", out _));
            Assert.Equal(JsonValueKind.Null, sample.RootElement.GetProperty("game_fps").ValueKind);
            Assert.Equal("not_available_in_fh6_data_out", sample.RootElement.GetProperty("game_fps_status").GetString());
            Assert.Equal(60, sample.RootElement.GetProperty("telemetry_processed_hz").GetDouble());
            using var debugEvent = JsonDocument.Parse(Read(archive, "events.ndjson"));
            Assert.Equal(3, debugEvent.RootElement.EnumerateObject().Count());
            using var manifest = JsonDocument.Parse(Read(archive, "manifest.json"));
            Assert.Equal(1, manifest.RootElement.GetProperty("omitted_records").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IntervalStringFieldsUseKnownLabelsAndEmptyCaptureCannotEnterReport()
    {
        var interval = Interval(CurrentCaptureId, Now, 12) with
        {
            NativeReads = [new TachNativeCounts("private-sentinel", "private-sentinel", "private-sentinel", "private-sentinel", 1, 1, 1)],
            Needles = [new TachNeedleCounts(1, "private-sentinel", "private-sentinel", 123, 1, 1, 1, 0, 0,
                0, 0, 1, 1, 1, 1, 0, 0)]
        };
        var sanitized = Assert.IsType<TachIntervalDiagnostic>(TachDiagnosticReport.SanitizeInterval(interval));
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(sanitized, JsonOptions));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { CaptureId = string.Empty }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { NativeReads = null! }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { IntervalMilliseconds = double.NaN }));
    }

    [Fact]
    public async Task SnapshotAndArchiveWorkDoNotRunOnTheCallingSynchronizationContext()
    {
        var root = TemporaryDirectory();
        try
        {
            var context = new SynchronizationContext();
            SynchronizationContext? observed = context;
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => Now,
                tachSnapshot: () => { observed = SynchronizationContext.Current; return null; });
            var previous = SynchronizationContext.Current;
            Task<bool> export;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                export = service.ExportAsync(Path.Combine(root, "capture.zip"), ApplicationVersionInfo.MachineVersion);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.True(await export);
            Assert.Null(observed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PackagedProvenanceIsWhitelistedAndInvalidHashesAreOmitted()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "build-provenance.json");
            var text = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                build_kind = "release",
                version = ApplicationVersionInfo.MachineVersion,
                source_revision = new string('a', 40),
                source_dirty = true,
                created_at_utc = Now,
                app_sha256 = new string('b', 64),
                app_bytes = 100,
                native_renderer = new
                {
                    file = "Wisp.NativeRenderer.dll",
                    sha256 = new string('c', 64),
                    bytes = 200,
                    extra = "private-sentinel"
                },
                extra = "private-sentinel"
            });
            File.WriteAllText(path, text);
            var provenance = TachDiagnosticReport.ReadPackagedBuild(path);
            Assert.NotNull(provenance);
            var result = JsonSerializer.Serialize(provenance, JsonOptions);
            Assert.DoesNotContain("private-sentinel", result);
            Assert.DoesNotContain(root, result);
            using var parsed = JsonDocument.Parse(result);
            Assert.Equal(9, parsed.RootElement.EnumerateObject().Count());
            Assert.Equal("release", parsed.RootElement.GetProperty("build_kind").GetString());
            Assert.Equal(ApplicationVersionInfo.MachineVersion, parsed.RootElement.GetProperty("version").GetString());
            var nativeRenderer = parsed.RootElement.GetProperty("native_renderer");
            Assert.Equal(3, nativeRenderer.EnumerateObject().Count());
            Assert.Equal("Wisp.NativeRenderer.dll", nativeRenderer.GetProperty("file").GetString());
            Assert.Equal(new string('c', 64), nativeRenderer.GetProperty("sha256").GetString());
            Assert.Equal(200, nativeRenderer.GetProperty("bytes").GetInt64());
            foreach (var wrongIdentity in new[]
            {
                text.Replace(ApplicationVersionInfo.MachineVersion, "1.1.3", StringComparison.Ordinal),
                text.Replace(ApplicationVersionInfo.MachineVersion, "1.1.5", StringComparison.Ordinal),
                text.Replace("release", "diagnostics.4", StringComparison.Ordinal),
                text.Replace($"\"build_kind\":\"release\",\"version\":\"{ApplicationVersionInfo.MachineVersion}\"",
                    "\"build_id\":\"diagnostics.4\",\"build_label\":\"1.1.3 Diagnostics4\",\"baseline_version\":\"1.1.3\"", StringComparison.Ordinal)
            })
            {
                File.WriteAllText(path, wrongIdentity);
                Assert.Null(TachDiagnosticReport.ReadPackagedBuild(path));
            }
            File.WriteAllText(path, text.Replace(new string('b', 64), "private-sentinel", StringComparison.Ordinal));
            Assert.Null(TachDiagnosticReport.ReadPackagedBuild(path));
            foreach (var invalid in new[]
            {
                text.Replace(new string('c', 64), "invalid", StringComparison.Ordinal),
                text.Replace("Wisp.NativeRenderer.dll", "other.dll", StringComparison.Ordinal),
                text.Replace("\"bytes\":200", "\"bytes\":0", StringComparison.Ordinal),
                text.Replace("\"native_renderer\":", "\"omitted_renderer\":", StringComparison.Ordinal)
            })
            {
                File.WriteAllText(path, invalid);
                Assert.Null(TachDiagnosticReport.ReadPackagedBuild(path));
            }
            File.WriteAllText(path, "not-json-private-sentinel");
            Assert.Null(TachDiagnosticReport.ReadPackagedBuild(path));
            foreach (var privateIdentity in new[] { "", ",\"private_build_id\":\"unpublished-preview\"" })
            {
                var privateText = text.Replace("\"build_kind\":\"release\"",
                    "\"build_kind\":\"private\"" + privateIdentity, StringComparison.Ordinal);
                File.WriteAllText(path, privateText);
                Assert.Null(TachDiagnosticReport.ReadPackagedBuild(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DisabledTachLoggingCreatesNoFiles()
    {
        var root = TemporaryDirectory();
        try
        {
            var logs = Path.Combine(root, "logs");
            await using (var service = new DebugLogService(logs, () => Now, tachSnapshot: () => null))
                service.TryLogTachInterval(Interval(CurrentCaptureId, Now, 12));
            Assert.False(Directory.Exists(logs));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static TachIntervalDiagnostic Interval(string captureId, DateTimeOffset now, long accepted) => new(
        now, captureId, 1000, 1000, accepted, 0, accepted, 0, 10, 5, 20, 1, 2, 3, 1, 0, 8,
        [new TachNativeCounts("Angle", "None", "Hit", "refresh", 100, 0.1, 0.2) { Changed = 10 }], []);

    private static TachCaptureExport Capture() => new(CurrentCaptureId, Now, 1, 2000, 1000, 2, 3,
        [], [], 0, [], [], 0, [], [], 0, [], 0);

    private static string Envelope(string kind, object payload) => JsonSerializer.Serialize(new { kind, payload }, JsonOptions);
    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
    private static string TemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wisp-tach-export-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
