using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DebugHealthContextTests
{
    [Fact]
    public async Task DisabledLoggingExportsRecentContextWithoutAnExistingLogsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "WispHealthContextTests", Guid.NewGuid().ToString("N"));
        try
        {
            var logs = Path.Combine(root, "logs");
            await using var logger = new DebugLogService(logs);
            Assert.False(logger.IsEnabled); Assert.False(Directory.Exists(logs));
            var export = Path.Combine(root, "health.zip");
            Assert.True(await logger.ExportAsync(export, "2.6.1"));
            Assert.False(logger.IsEnabled); Assert.False(Directory.Exists(logs));
            using var zip = ZipFile.OpenRead(export);
            using var context = JsonDocument.Parse(Read(zip, "health-context.json"));
            Assert.True(context.RootElement.GetProperty("samples").GetArrayLength() <= HealthContextRecorder.SampleCapacity);
            Assert.True(context.RootElement.GetProperty("breadcrumbs").GetArrayLength() <= HealthContextRecorder.BreadcrumbCapacity);
            Assert.Empty(Read(zip, "samples.ndjson")); Assert.Empty(Read(zip, "health.ndjson"));
            Assert.Contains("not displayed-frame measurements", Read(zip, "summary.txt"), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledLoggerStillCollectsStalledDispatcherWithOnlyOnePendingProbe()
    {
        var root = Path.Combine(Path.GetTempPath(), "WispHealthContextTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            var factory = new RejectingFactory();
            await using var native = new NativeHudProcessService(factory);
            await using var logger = new DebugLogService(root);
            var context = new HealthContextRecorder();
            var probes = 0;
            await using var monitor = new DebugHealthMonitor(receiver, native, logger, () => 0,
                _ => Interlocked.Increment(ref probes), () => { },
                focus: () => throw new InvalidOperationException("Automatic health must not query foreground state."), healthContext: context);
            monitor.PublishUiContext(new(Stopwatch.GetTimestamp(), true, false, false, 0, 0));
            monitor.StartMonitoring(); monitor.StartMonitoring();
            var wait = Stopwatch.StartNew();
            while (context.Snapshot().Samples.Length < 2 && wait.Elapsed < TimeSpan.FromSeconds(6))
                await Task.Delay(50, TestContext.Current.CancellationToken);
            await monitor.StopAsync(); await monitor.StopAsync();
            var samples = context.Snapshot().Samples;
            Assert.True(samples.Length >= 2);
            Assert.Equal(1, Volatile.Read(ref probes));
            Assert.True(samples[^1].DispatcherProbePending);
            Assert.True(samples[^1].DispatcherDelayMs >= 1500);
            Assert.True(samples[^1].UiHeartbeatAgeMs >= 1500);
            Assert.False(logger.IsEnabled); Assert.False(Directory.Exists(root));
            Assert.Equal(0, factory.Opens);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AutomaticCollectionContinuesWhenDetailedLoggingIsDisabledAndReenabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "WispHealthContextTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            var factory = new RejectingFactory();
            await using var native = new NativeHudProcessService(factory);
            await using var logger = new DebugLogService(root);
            var context = new HealthContextRecorder();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            Assert.True(logger.TryEnable(expires));
            await using var monitor = new DebugHealthMonitor(receiver, native, logger, () => 0,
                callback => callback(), () => { }, focus: () => DebugFocus.Unknown, healthContext: context);
            monitor.Start(expires);
            await WaitForSampleAfterAsync(context, DateTimeOffset.MinValue);

            Assert.True(monitor.RetainsHealthContext);
            await logger.DisableAsync();
            var disabledAt = DateTimeOffset.UtcNow;
            await WaitForSampleAfterAsync(context, disabledAt);
            Assert.False(logger.IsEnabled);

            expires = DateTimeOffset.UtcNow.AddMinutes(5);
            Assert.True(logger.TryEnable(expires));
            var reenabledAt = DateTimeOffset.UtcNow;
            monitor.Start(expires);
            await WaitForSampleAfterAsync(context, reenabledAt);
            Assert.True(logger.IsEnabled);
            Assert.Equal(0, factory.Opens);
            Assert.True(HealthContextRecorder.IsValidSnapshot(context.Snapshot()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedCollectionRetainsOnlySafeHresultAndContinuesSampling()
    {
        var root = Path.Combine(Path.GetTempPath(), "WispHealthContextTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            await using var native = new NativeHudProcessService(new RejectingFactory());
            await using var logger = new DebugLogService(root);
            var context = new HealthContextRecorder();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            Assert.True(logger.TryEnable(expires));
            var attempts = 0;
            const int hresult = unchecked((int)0x80070005);
            await using var monitor = new DebugHealthMonitor(receiver, native, logger, () => 0,
                callback => callback(), () => { }, sampleInterval: TimeSpan.FromMilliseconds(50),
                focus: () => Interlocked.Increment(ref attempts) == 1
                    ? throw new IOException("PRIVATE_ERROR_TEXT", hresult) : DebugFocus.Unknown,
                healthContext: context);
            monitor.Start(expires);
            await WaitForSampleAfterAsync(context, DateTimeOffset.MinValue);
            await monitor.StopAsync();
            var snapshot = context.CaptureForReport();
            Assert.Equal(1, snapshot.Samples[^1].CollectorFailures);
            var failure = Assert.Single(snapshot.Breadcrumbs);
            Assert.Equal(HealthEventCode.HealthCollectionFailed, failure.Code);
            Assert.Equal(hresult, failure.ErrorCode);
            Assert.DoesNotContain("PRIVATE_ERROR_TEXT", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task WaitForSampleAfterAsync(HealthContextRecorder context, DateTimeOffset timestamp)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(6))
        {
            var samples = context.Snapshot().Samples;
            if (samples.Length > 0 && samples[^1].TimestampUtc > timestamp) return;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.Fail("Automatic health collection did not publish a new sample.");
    }

    private static string Read(ZipArchive archive, string name)
    { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return reader.ReadToEnd(); }

    private sealed class RejectingFactory : INativeHudProcessMemoryFactory
    {
        internal int Opens;
        public bool TryOpen(out INativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
        { Interlocked.Increment(ref Opens); memory = null; status = NativeAssistProviderStatus.GameNotRunning; return false; }
    }
}
