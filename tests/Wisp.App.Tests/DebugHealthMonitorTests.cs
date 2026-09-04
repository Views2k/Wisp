using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Wisp.App;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DebugHealthMonitorTests
{
    [Fact(Timeout = 6000)]
    public async Task HeldDispatcherStillCollectsFreshUdpAndReportsUiDelay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wisp-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var port = AvailableUdpPort();
            await using var receiver = new TelemetryUdpReceiver();
            await receiver.StartAsync(port, TestContext.Current.CancellationToken);
            await using var nativeHud = new NativeHudProcessService(new RejectingNativeFactory());
            await using var log = new DebugLogService(Path.Combine(root, "logs"));
            Assert.True(log.TryEnable(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)));

            var postedCallbacks = 0;
            await using var monitor = new DebugHealthMonitor(
                receiver,
                nativeHud,
                log,
                () => 0,
                _ => Interlocked.Increment(ref postedCallbacks),
                () => { },
                sampleInterval: TimeSpan.FromMilliseconds(100),
                focus: () => DebugFocus.Game);

            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var sender = SendAdvancingPacketsAsync(port, sendCancellation.Token);
            try
            {
                await WaitUntilAsync(
                    () => receiver.ReceivedDatagrams >= 2,
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken);

                monitor.PublishUiContext(new DebugHealthUiContext(
                    Stopwatch.GetTimestamp(),
                    OverlayExpectedVisible: true,
                    NativeExpected: false,
                    RaceOn: true,
                    CarOrdinal: 42,
                    GameTimestampMilliseconds: receiver.Latest!.GameTimestampMilliseconds));
                monitor.Start(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));

                await Task.Delay(1350, TestContext.Current.CancellationToken);
            }
            finally
            {
                await monitor.StopAsync();
                sendCancellation.Cancel();
                await sender;
            }

            Assert.Equal(1, Volatile.Read(ref postedCallbacks));
            var exportPath = Path.Combine(root, "debug.zip");
            Assert.True(await log.ExportAsync(exportPath, "1.0.12"));

            using var archive = ZipFile.OpenRead(exportPath);
            var health = ReadEntry(archive, "health.ndjson")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(health.Length >= 10, $"Expected continuous background samples, got {health.Length}.");
            Assert.True(health.Count(line => line.Contains(
                "\"dispatcher_probe_pending\":true", StringComparison.Ordinal)) >= 8);

            var summary = ReadEntry(archive, "summary.txt");
            Assert.Contains("UI processing delay", summary, StringComparison.Ordinal);
            Assert.Contains("Incoming data stayed fresh", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("Composition callback gap", summary, StringComparison.Ordinal);
            Assert.Contains("does not establish a GPU fault", summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData("native", "Native HUD data unavailable or stale")]
    [InlineData("composition", "Composition callback gap")]
    [InlineData("malformed", "Telemetry rejected packets")]
    [InlineData("healthy", null)]
    [InlineData("menu", null)]
    [InlineData("hidden", null)]
    [InlineData("disconnected", null)]
    public async Task ControlledPipelineScenariosProduceSpecificReports(string scenario, string? expectedFinding)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wisp-health-scenario-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var port = AvailableUdpPort();
            await using var receiver = new TelemetryUdpReceiver();
            await receiver.StartAsync(port, TestContext.Current.CancellationToken);
            var factory = new RejectingNativeFactory();
            await using var nativeHud = new NativeHudProcessService(factory);
            await using var log = new DebugLogService(Path.Combine(root, "logs"));
            Assert.True(log.TryEnable(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)));
            long processed = 0;
            await using var monitor = new DebugHealthMonitor(
                receiver, nativeHud, log, () => Interlocked.Read(ref processed),
                callback => callback(), () => { },
                sampleInterval: TimeSpan.FromMilliseconds(100), focus: () => DebugFocus.Game);
            monitor.Start(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            var destination = new IPEndPoint(IPAddress.Loopback, port);
            var elapsed = Stopwatch.StartNew();
            uint gameTimestamp = 1000;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                var packet = scenario == "malformed" ? new byte[3] : ValidPacket(gameTimestamp += 20);
                if (scenario == "menu")
                {
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(Fh6PacketLayout.IsRaceOn), 0);
                }
                if (scenario != "disconnected")
                {
                    await sender.SendAsync(packet, destination, TestContext.Current.CancellationToken);
                }
                var latest = receiver.Latest;
                if (latest is not null)
                {
                    Interlocked.Increment(ref processed);
                    nativeHud.UpdateTelemetry(latest, scenario == "native");
                }
                monitor.PublishUiContext(new DebugHealthUiContext(
                    Stopwatch.GetTimestamp(),
                    OverlayExpectedVisible: scenario is "healthy" or "native" or "composition",
                    NativeExpected: scenario == "native", RaceOn: latest?.IsRaceOn ?? false,
                    CarOrdinal: 42, GameTimestampMilliseconds: latest?.GameTimestampMilliseconds ?? 0));
                if (scenario is "healthy" or "native")
                {
                    monitor.RecordCompositionFrame(Stopwatch.GetTimestamp(), 50);
                }
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            await monitor.StopAsync();
            var exportPath = Path.Combine(root, "debug.zip");
            Assert.True(await log.ExportAsync(exportPath, "1.0.12"));
            using var archive = ZipFile.OpenRead(exportPath);
            var summary = ReadEntry(archive, "summary.txt");
            foreach (var heading in new[]
            {
                "UI processing delay", "Native HUD data unavailable or stale", "Composition callback gap",
                "Telemetry rejected packets", "Telemetry reception interruption"
            })
            {
                if (heading == expectedFinding)
                {
                    Assert.Contains(heading, summary, StringComparison.Ordinal);
                    Assert.Contains("When:", summary, StringComparison.Ordinal);
                    Assert.Contains("Evidence (last sample):", summary, StringComparison.Ordinal);
                    Assert.Contains("Likely affected component:", summary, StringComparison.Ordinal);
                    Assert.Contains("Uncertainty:", summary, StringComparison.Ordinal);
                    Assert.Contains("Next useful step:", summary, StringComparison.Ordinal);
                }
                else
                {
                    Assert.DoesNotContain(heading, summary, StringComparison.Ordinal);
                }
            }
            if (scenario == "native") Assert.True(factory.OpenAttempts > 0);
            if (scenario == "malformed") Assert.True(receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets > 0);
            if (scenario == "disconnected") Assert.Equal(0, receiver.ReceivedDatagrams);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SendAdvancingPacketsAsync(int port, CancellationToken cancellationToken)
    {
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        uint gameTimestamp = 1000;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = ValidPacket(gameTimestamp += 20);
                await sender.SendAsync(packet, destination, cancellationToken);
                await Task.Delay(20, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static byte[] ValidPacket(uint gameTimestamp)
    {
        var packet = new byte[Fh6PacketLayout.PacketLength];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(Fh6PacketLayout.IsRaceOn), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(Fh6PacketLayout.TimestampMilliseconds), gameTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(Fh6PacketLayout.CarOrdinal), 42);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(Fh6PacketLayout.DrivetrainType), 0);
        return packet;
    }

    private static int AvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "Timed out waiting for local UDP telemetry.");
            await Task.Delay(10, cancellationToken);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private sealed class RejectingNativeFactory : INativeHudProcessMemoryFactory
    {
        public int OpenAttempts;

        public bool TryOpen(out INativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
        {
            Interlocked.Increment(ref OpenAttempts);
            memory = null;
            status = NativeAssistProviderStatus.UnsupportedBuild;
            return false;
        }
    }
}
