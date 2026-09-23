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
    public async Task IndependentMotionClockAndGenerationFieldsSurviveArchiveExport()
    {
        var root = TemporaryDirectory();
        try
        {
            var motion = RendererEvent() with
            {
                Stage = "compositor_motion",
                Result = "ready",
                CurveEndTimestamp = 40,
                CompositorCommitTimestamp = 45,
                MotionGeneration = 7,
                MotionGeometryAccepted = false
            };
            var capture = Capture() with { RendererRecent = [motion] };
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => Now, tachSnapshot: () => capture);
            Assert.True(service.TryEnable(Now + DebugLogService.EnableDuration));
            var destination = Path.Combine(root, "capture.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));
            using var archive = ZipFile.OpenRead(destination);
            using var row = JsonDocument.Parse(Read(archive, "tach-renderer-recent.ndjson"));
            Assert.Equal("compositor_motion", row.RootElement.GetProperty("stage").GetString());
            Assert.Equal(40, row.RootElement.GetProperty("curve_end_timestamp").GetInt64());
            Assert.Equal(45, row.RootElement.GetProperty("compositor_commit_timestamp").GetInt64());
            Assert.Equal(7, row.RootElement.GetProperty("motion_generation").GetInt64());
            Assert.False(row.RootElement.GetProperty("motion_geometry_accepted").GetBoolean());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
                RendererStartup = [RendererEvent()],
                RendererRecent = [RendererEvent(), RendererEvent() with { Sequence = 2, Result = "submitted" }],
                RendererOverwritten = 5,
                NativeContexts = [new TachNativeContext(100, "fh6-steam-test", 1, "6.440.853.0", 1, 4, false, true)]
            };
            await using var service = new DebugLogService(logs, () => Now, tachSnapshot: () => capture);
            Assert.True(service.TryEnable(Now + DebugLogService.EnableDuration));
            service.TryLogTachInterval(Interval(OldCaptureId, Now.AddMinutes(-1), 177777));
            service.TryLogTachInterval(Interval(CurrentCaptureId, Now, 123));
            var destination = Path.Combine(root, "capture.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));

            using var archive = ZipFile.OpenRead(destination);
            Assert.Equal(18, archive.Entries.Count);
            var report = Read(archive, "tach-report.txt");
            Assert.Contains(CurrentCaptureId, report);
            Assert.DoesNotContain("177777", report);
            Assert.Contains("Selected persisted intervals: 1", report);
            Assert.Contains("physical display are not measured", report);
            Assert.Contains("validated angle changes", report);
            Assert.Contains("Present attempts submitted/busy/occluded/error: 0/2/0/0", report);
            Assert.Contains("Longest observed renderer operation: present/busy, 20 ms", report);
            Assert.Contains("not why the operation waited", report);
            Assert.Equal(2, Read(archive, "tach-intervals.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(2, Read(archive, "tach-input-recent.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("fh6-steam-test", Read(archive, "tach-native-context.ndjson"));
            Assert.Equal(2, Read(archive, "tach-renderer-recent.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            using var renderer = JsonDocument.Parse(Read(archive, "tach-renderer-startup.ndjson"));
            Assert.Equal("busy", renderer.RootElement.GetProperty("result").GetString());
            Assert.Equal(25, renderer.RootElement.GetProperty("queued_timestamp").GetInt64());
            Assert.Equal(-123, renderer.RootElement.GetProperty("h_result").GetInt32());
            var priority = renderer.RootElement.GetProperty("gpu_priority");
            Assert.True(priority.GetProperty("attempted").GetBoolean());
            Assert.Equal(4, priority.GetProperty("requested_process_class").GetInt32());
            Assert.Equal(unchecked((int)0xC0000022), priority.GetProperty("process_set_status").GetInt32());
            Assert.Equal(2, priority.GetProperty("effective_process_class").GetInt32());
            Assert.Equal(unchecked((int)0x80070005), priority.GetProperty("device_set_h_result").GetInt32());
            using var manifest = JsonDocument.Parse(Read(archive, "tach-manifest.json"));
            Assert.Equal(CurrentCaptureId, manifest.RootElement.GetProperty("selected_capture_id").GetString());
            Assert.True(manifest.RootElement.GetProperty("raw_lost_after_process_restart").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("gpu_presentation_measured").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("physical_display_measured").GetBoolean());
            Assert.Equal(3, manifest.RootElement.GetProperty("renderer_schema_version").GetInt32());
            Assert.Equal(5, manifest.RootElement.GetProperty("renderer").GetProperty("recent_overwritten").GetInt64());
            Assert.Equal(1, manifest.RootElement.GetProperty("renderer").GetProperty("startup_recent_duplicate_records").GetInt32());
            Assert.Contains("not displayed", manifest.RootElement.GetProperty("renderer_policy").GetString());
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
            Assert.Null(archive.GetEntry("tach-renderer-recent.ndjson"));
            var report = Read(archive, "tach-report.txt");
            Assert.Contains(CurrentCaptureId, report);
            Assert.Contains("Raw histories are unavailable", report);
            Assert.DoesNotContain("177777", report);
            Assert.Contains("Present attempts submitted/busy/occluded/error: 0/2/0/0", report);
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
                0, 0, 1, 1, 1, 1, 0, 0)],
            Renderer = [RendererCounts() with { Stage = "private-sentinel", Result = "private-sentinel" }]
        };
        var sanitized = Assert.IsType<TachIntervalDiagnostic>(TachDiagnosticReport.SanitizeInterval(interval));
        Assert.DoesNotContain("private-sentinel", JsonSerializer.Serialize(sanitized, JsonOptions));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { CaptureId = string.Empty }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { NativeReads = null! }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { IntervalMilliseconds = double.NaN }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { Renderer = null! }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { Renderer = [RendererCounts() with { MeanMilliseconds = double.NaN }] }));
        Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { Renderer = new TachRendererCounts[TachDiagnostics.RendererAggregateCapacity + 1] }));
    }

    [Fact]
    public void OlderPersistedIntervalsWithoutRendererFieldsRemainReadable()
    {
        var json = $$"""
            {"timestamp_utc":"2026-09-08T12:00:00Z","capture_id":"{{CurrentCaptureId}}","timestamp":1000,"interval_milliseconds":1000,"native_reads":[],"needles":[]}
            """;
        var interval = JsonSerializer.Deserialize<TachIntervalDiagnostic>(json, JsonOptions)!;
        var safe = Assert.IsType<TachIntervalDiagnostic>(TachDiagnosticReport.SanitizeInterval(interval));
        Assert.Empty(safe.Renderer);
        var report = TachDiagnosticReport.Build([safe], null);
        Assert.Contains("Older builds did not record these timings", report);
        Assert.DoesNotContain("Present attempts submitted", report);
    }

    [Fact]
    public async Task WaitDetailsSurviveRawAndPersistedArchivePaths()
    {
        var root = TemporaryDirectory();
        try
        {
            var wait = RendererEvent() with
            {
                Stage = "frame_wait",
                Result = "ready",
                CompletedTimestamp = 124,
                HResult = 0,
                NativeThreadId = 120,
                NativeWaitTicks = 22,
                WaitPrecheckTicks = 1,
                WaitCallTicks = 16,
                WaitPostcheckTicks = 0,
                PacingWaitTicks = 4,
                PresentationSyncInterval = 0,
                PresentationRefreshRate = 240,
                PacingHResult = 0,
                PacingWaitReturnCode = 0,
                CpuThreadTicks = 5,
                SwapChainGeneration = 1,
                WaitReturnCode = 1
            };
            var replacement = wait with { Sequence = 2, SwapChainGeneration = 2, CpuThreadTicks = null };
            var capture = Capture() with { RendererStartup = [wait], RendererRecent = [wait, replacement] };
            var counts = RendererCounts() with
            {
                Stage = "frame_wait",
                Result = "ready",
                MeanMilliseconds = 24,
                MaximumMilliseconds = 24,
                TotalNativePresentMilliseconds = 0,
                NativeThreadId = 120,
                NativeWaitSamples = 2,
                TotalNativeWaitMilliseconds = 44,
                WaitPrecheckSamples = 2,
                TotalWaitPrecheckMilliseconds = 2,
                WaitCallSamples = 2,
                TotalWaitCallMilliseconds = 32,
                MaximumWaitCallMilliseconds = 16,
                WaitPostcheckSamples = 2,
                TotalWaitPostcheckMilliseconds = 0,
                PacingWaitSamples = 2,
                TotalPacingWaitMilliseconds = 8,
                MaximumPacingWaitMilliseconds = 4,
                PresentationSyncInterval = 0,
                PresentationRefreshRate = 240,
                PacingHResult = 0,
                PacingWaitReturnCode = 0,
                CpuThreadSamples = 1,
                TotalCpuThreadMilliseconds = 5,
                SwapChainGenerationSamples = 2,
                MinimumSwapChainGeneration = 1,
                MaximumSwapChainGeneration = 2,
                WaitReturnCode = 1
            };
            var interval = Interval(CurrentCaptureId, Now, 10) with { Renderer = [counts] };
            var logs = Path.Combine(root, "logs");
            await using (var service = new DebugLogService(logs, () => Now, tachSnapshot: () => capture))
            {
                Assert.True(service.TryEnable(Now + DebugLogService.EnableDuration));
                service.TryLogTachInterval(interval);
                var path = Path.Combine(root, "live.zip");
                Assert.True(await service.ExportAsync(path, ApplicationVersionInfo.MachineVersion));
                using var archive = ZipFile.OpenRead(path);
                var raw = Read(archive, "tach-renderer-recent.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => JsonSerializer.Deserialize<TachRendererDiagnostic>(value, JsonOptions)).ToArray();
                Assert.Equal(new[] { wait, replacement }, raw);
                Assert.Equal(wait, JsonSerializer.Deserialize<TachRendererDiagnostic>(Read(archive, "tach-renderer-startup.ndjson"), JsonOptions));
                Assert.Equal(counts, Assert.Single(JsonSerializer.Deserialize<TachIntervalDiagnostic>(Read(archive, "tach-intervals.ndjson"), JsonOptions)!.Renderer));
                using var manifest = JsonDocument.Parse(Read(archive, "tach-manifest.json"));
                Assert.Equal(3, manifest.RootElement.GetProperty("renderer_schema_version").GetInt32());
                var policy = manifest.RootElement.GetProperty("renderer_policy").GetString()!;
                Assert.Contains("full managed wrapper", policy);
                Assert.Contains("coarse GetThreadTimes CPU accounting", policy);
                Assert.Contains("error before the wait was reached", policy);
                Assert.Contains("Missing split fields mean unavailable", policy);
                Assert.Contains("intentional software pacing before DXGI readiness", policy);
                Assert.Contains("including both software pacing and DXGI readiness", policy);
                var report = Read(archive, "tach-report.txt");
                Assert.Contains("Native wait total: 44 ms (2 observations)", report);
                Assert.Contains("Intentional software pacing: 8 ms (2 observations); maximum: 4 ms", report);
                Assert.Contains("Effective presentation sync interval: 0; refresh rate: 240 Hz", report);
                Assert.Contains("wait call: 32 ms (2 observations)", report);
                Assert.Contains("postcheck: 0 ms (2 observations)", report);
                Assert.Contains("Maximum wait call: 16 ms", report);
                Assert.Contains("Win32 wait outcome: 0x00000001", report);
                Assert.Contains("Native render thread: 120", report);
                Assert.Contains("swap-chain generation range: 1-2 (2 observations)", report);
                Assert.Contains("Coarse thread CPU accounting: 5 ms (1 observations)", report);
            }

            await using var restarted = new DebugLogService(logs, () => Now, tachSnapshot: () => null);
            var restartedPath = Path.Combine(root, "restarted.zip");
            Assert.True(await restarted.ExportAsync(restartedPath, ApplicationVersionInfo.MachineVersion));
            using var persisted = ZipFile.OpenRead(restartedPath);
            Assert.Null(persisted.GetEntry("tach-renderer-recent.ndjson"));
            Assert.Equal(counts, Assert.Single(JsonSerializer.Deserialize<TachIntervalDiagnostic>(Read(persisted, "tach-intervals.ndjson"), JsonOptions)!.Renderer));
            Assert.Contains("Native wait total: 44 ms (2 observations)", Read(persisted, "tach-report.txt"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PacingFallbackExportsSignedFailureUnknownRefreshAndMeasuredZero()
    {
        var root = TemporaryDirectory();
        try
        {
            const int failure = unchecked((int)0x80070005);
            var wait = RendererEvent() with
            {
                Stage = "frame_wait",
                Result = "ready",
                HResult = 0,
                PacingWaitTicks = 0,
                PresentationSyncInterval = 1,
                PresentationRefreshRate = 0,
                PacingHResult = failure,
                PacingWaitReturnCode = uint.MaxValue
            };
            var counts = RendererCounts() with
            {
                Stage = "frame_wait",
                Result = "ready",
                HResult = 0,
                PacingWaitSamples = 2,
                TotalPacingWaitMilliseconds = 0,
                MaximumPacingWaitMilliseconds = 0,
                PresentationSyncInterval = 1,
                PresentationRefreshRate = 0,
                PacingHResult = failure,
                PacingWaitReturnCode = uint.MaxValue
            };
            var capture = Capture() with { RendererRecent = [wait, wait with { Sequence = 2 }] };
            await using var service = new DebugLogService(Path.Combine(root, "logs"), () => Now, tachSnapshot: () => capture);
            Assert.True(service.TryEnable(Now + DebugLogService.EnableDuration));
            service.TryLogTachInterval(Interval(CurrentCaptureId, Now, 1) with { Renderer = [counts] });
            var destination = Path.Combine(root, "pacing.zip");
            Assert.True(await service.ExportAsync(destination, ApplicationVersionInfo.MachineVersion));

            using var archive = ZipFile.OpenRead(destination);
            var raw = Read(archive, "tach-renderer-recent.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(wait, JsonSerializer.Deserialize<TachRendererDiagnostic>(raw[0], JsonOptions));
            using var values = JsonDocument.Parse(raw[0]);
            Assert.Equal(0, values.RootElement.GetProperty("pacing_wait_ticks").GetInt64());
            Assert.Equal(1u, values.RootElement.GetProperty("presentation_sync_interval").GetUInt32());
            Assert.Equal(0u, values.RootElement.GetProperty("presentation_refresh_rate").GetUInt32());
            Assert.Equal(failure, values.RootElement.GetProperty("pacing_h_result").GetInt32());
            Assert.Equal(uint.MaxValue, values.RootElement.GetProperty("pacing_wait_return_code").GetUInt32());
            Assert.Equal(counts, Assert.Single(JsonSerializer.Deserialize<TachIntervalDiagnostic>(Read(archive, "tach-intervals.ndjson"), JsonOptions)!.Renderer));
            var report = Read(archive, "tach-report.txt");
            Assert.Contains("Intentional software pacing: 0 ms (2 observations)", report);
            Assert.Contains("pacing HRESULT: 0x80070005", report);
            Assert.Contains("synchronized presentation fallback is active", report);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OlderRendererTimingsDoNotImplyZeroNativeWaitOrThreadCpuWork()
    {
        var json = $$"""
            {"timestamp_utc":"2026-09-08T12:00:00Z","capture_id":"{{CurrentCaptureId}}","timestamp":1000,"interval_milliseconds":1000,"native_reads":[],"needles":[],"renderer":[{"control_id":1,"stage":"frame_wait","result":"ready","count":2,"mean_milliseconds":20,"maximum_milliseconds":40}]}
            """;
        var interval = JsonSerializer.Deserialize<TachIntervalDiagnostic>(json, JsonOptions)!;
        var safe = Assert.IsType<TachIntervalDiagnostic>(TachDiagnosticReport.SanitizeInterval(interval));
        var counts = Assert.Single(safe.Renderer);
        Assert.Equal(0, counts.NativeWaitSamples);
        Assert.Null(counts.TotalNativeWaitMilliseconds);
        Assert.Null(counts.TotalWaitPrecheckMilliseconds);
        Assert.Null(counts.TotalWaitCallMilliseconds);
        Assert.Null(counts.MaximumWaitCallMilliseconds);
        Assert.Null(counts.TotalWaitPostcheckMilliseconds);
        Assert.Null(counts.MinimumSwapChainGeneration);
        Assert.Null(counts.WaitReturnCode);
        Assert.Null(counts.NativeThreadId);
        Assert.Equal(0, counts.PacingWaitSamples);
        Assert.Null(counts.TotalPacingWaitMilliseconds);
        Assert.Null(counts.MaximumPacingWaitMilliseconds);
        Assert.Null(counts.PresentationSyncInterval);
        Assert.Null(counts.PresentationRefreshRate);
        Assert.Null(counts.PacingHResult);
        Assert.Null(counts.PacingWaitReturnCode);
        var report = TachDiagnosticReport.Build([safe], null);
        Assert.Contains("Native wait total: unavailable (0 observations)", report);
        Assert.Contains("Maximum wait call: unavailable", report);
        Assert.Contains("Coarse thread CPU accounting: unavailable (0 observations)", report);
        Assert.Contains("Intentional software pacing: unavailable (0 observations)", report);
        Assert.DoesNotContain("synchronized presentation fallback is active", report);
        var raw = JsonSerializer.Deserialize<TachRendererDiagnostic>("""{"stage":"frame_wait","result":"ready","started_timestamp":100,"completed_timestamp":120}""", JsonOptions);
        Assert.Null(raw.NativeWaitTicks);
        Assert.Null(raw.WaitPrecheckTicks);
        Assert.Null(raw.WaitCallTicks);
        Assert.Null(raw.WaitPostcheckTicks);
        Assert.Null(raw.CpuThreadTicks);
        Assert.Null(raw.SwapChainGeneration);
        Assert.Null(raw.WaitReturnCode);
        Assert.Null(raw.NativeThreadId);
        Assert.Null(raw.PacingWaitTicks);
        Assert.Null(raw.PresentationSyncInterval);
        Assert.Null(raw.PresentationRefreshRate);
        Assert.Null(raw.PacingHResult);
        Assert.Null(raw.PacingWaitReturnCode);
    }

    [Fact]
    public void InvalidPersistedWaitCoverageAndDurationsAreRejected()
    {
        var valid = RendererCounts() with
        {
            NativeWaitSamples = 1,
            TotalNativeWaitMilliseconds = 10,
            WaitPrecheckSamples = 1,
            TotalWaitPrecheckMilliseconds = 1,
            WaitCallSamples = 1,
            TotalWaitCallMilliseconds = 8,
            MaximumWaitCallMilliseconds = 8,
            WaitPostcheckSamples = 1,
            TotalWaitPostcheckMilliseconds = 0,
            PacingWaitSamples = 1,
            TotalPacingWaitMilliseconds = 4,
            MaximumPacingWaitMilliseconds = 4,
            PresentationSyncInterval = 0,
            SwapChainGenerationSamples = 1,
            MinimumSwapChainGeneration = 1,
            MaximumSwapChainGeneration = 2
        };
        var interval = Interval(CurrentCaptureId, Now, 1) with { Renderer = [valid] };
        Assert.NotNull(TachDiagnosticReport.SanitizeInterval(interval));
        foreach (var invalid in new[]
        {
            valid with { NativeWaitSamples = -1 },
            valid with { NativeWaitSamples = 3 },
            valid with { NativeWaitSamples = 0 },
            valid with { TotalNativeWaitMilliseconds = -1 },
            valid with { TotalNativeWaitMilliseconds = null },
            valid with { TotalWaitPrecheckMilliseconds = double.NaN },
            valid with { TotalWaitCallMilliseconds = double.PositiveInfinity },
            valid with { MaximumWaitCallMilliseconds = 9 },
            valid with { MaximumWaitCallMilliseconds = null },
            valid with { TotalWaitPostcheckMilliseconds = -1 },
            valid with { PacingWaitSamples = -1 },
            valid with { PacingWaitSamples = 3 },
            valid with { PacingWaitSamples = 0 },
            valid with { TotalPacingWaitMilliseconds = -1 },
            valid with { TotalPacingWaitMilliseconds = double.NaN },
            valid with { TotalPacingWaitMilliseconds = null },
            valid with { MaximumPacingWaitMilliseconds = double.PositiveInfinity },
            valid with { MaximumPacingWaitMilliseconds = 5 },
            valid with { MaximumPacingWaitMilliseconds = null },
            valid with { PresentationSyncInterval = 5 },
            valid with { SwapChainGenerationSamples = -1 },
            valid with { SwapChainGenerationSamples = 3 },
            valid with { SwapChainGenerationSamples = 0 },
            valid with { MinimumSwapChainGeneration = 0 },
            valid with { MinimumSwapChainGeneration = null },
            valid with { MinimumSwapChainGeneration = 3 },
            valid with { MaximumSwapChainGeneration = null }
        })
            Assert.Null(TachDiagnosticReport.SanitizeInterval(interval with { Renderer = [invalid] }));
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
            var matchingPrivate = text.Replace("\"build_kind\":\"release\"",
                "\"build_kind\":\"private\",\"private_build_id\":\"" + ApplicationVersionInfo.DiagnosticBuildId + "\"",
                StringComparison.Ordinal);
            File.WriteAllText(path, matchingPrivate);
            var privateProvenance = TachDiagnosticReport.ReadPackagedBuild(path);
            Assert.NotNull(privateProvenance);
            using var privateParsed = JsonDocument.Parse(JsonSerializer.Serialize(privateProvenance, JsonOptions));
            Assert.Equal("private", privateParsed.RootElement.GetProperty("build_kind").GetString());
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
        [new TachNativeCounts("Angle", "None", "Hit", "refresh", 100, 0.1, 0.2) { Changed = 10 }], [])
    { Renderer = [RendererCounts()] };

    private static TachRendererDiagnostic RendererEvent() => new()
    {
        GpuPriority = new(true, 4, unchecked((int)0xC0000022), 0, 2, 1, unchecked((int)0x80070005), 0, 0),
        ControlId = 1,
        HostWindowHandle = 123,
        Sequence = 1,
        Stage = "present",
        Result = "busy",
        StartedTimestamp = 100,
        CompletedTimestamp = 120,
        SampleTimestamp = 50,
        ReceivedTimestamp = 20,
        QueuedTimestamp = 25,
        HResult = -123
    };

    private static TachRendererCounts RendererCounts() => new(1, 123, "present", "busy", 2,
        10, 20, 0, 0, 0, 0, 0, 0, 0, 20, 0, 2, 75, 80, 2, 80, 90, 2, 50, 60, 0, 0);

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
