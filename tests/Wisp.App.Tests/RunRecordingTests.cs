using System.Diagnostics;
using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunRecordingTests
{
    [Fact]
    public void TimestampWrapDuplicatesAndResetRetainDataWithoutInventingTime()
    {
        var start = Stopwatch.GetTimestamp();
        long At(double value) => start + (long)(value * Stopwatch.Frequency);
        var context = new RunRecordingContext(start, 2468, DrivetrainType.RearWheelDrive, true, .35, .35);
        var builder = new RunSampleBuilder(start);
        var first = builder.Add(RunTestData.State(uint.MaxValue - 49), start, 1, context);
        var wrapped = builder.Add(RunTestData.State(50), At(.1), 2, context);
        var repeated = builder.Add(RunTestData.State(50) with { EngineRpm = 7000 }, At(.11), 3, context);
        var reset = builder.Add(RunTestData.State(2), At(.2), 4, context);
        Assert.Equal(0, first.ElapsedSeconds);
        Assert.Equal(.1, wrapped.ElapsedSeconds, 6);
        Assert.Equal(.1, repeated.ElapsedSeconds, 6);
        Assert.Equal(7000, repeated.State.EngineRpm);
        Assert.Equal(first.Segment, repeated.Segment);
        Assert.True(reset.Segment > repeated.Segment);
        Assert.InRange(reset.ElapsedSeconds, .18, .2);
        Assert.True(builder.HasGap);
    }

    [Fact]
    public void NativeVisibilityLossFutureContextAndCalibrationChangeSplitSegments()
    {
        var start = Stopwatch.GetTimestamp();
        long At(double value) => start + (long)(value * Stopwatch.Frequency);
        var context = new RunRecordingContext(start, 2468, DrivetrainType.RearWheelDrive, true, .35, .35);
        var builder = new RunSampleBuilder(start);
        var driving = builder.Add(RunTestData.State(0), start, 1, context);
        var hidden = builder.Add(RunTestData.State(100), At(.1), 2, context with { IsDriving = false });
        var future = builder.Add(RunTestData.State(200), At(.2), 3, context with { EffectiveTimestamp = At(.25), RearRadiusMeters = .4 });
        var changed = builder.Add(RunTestData.State(300), At(.3), 4, context with { EffectiveTimestamp = At(.25), RearRadiusMeters = .4 });
        Assert.True(driving.IsDriving);
        Assert.False(hidden.IsDriving);
        Assert.True(hidden.State.IsRaceOn);
        Assert.True(hidden.Segment > driving.Segment);
        Assert.Null(future.WheelSpeedMetersPerSecond);
        Assert.True(changed.IsDriving);
        Assert.Equal(4, changed.WheelSpeedMetersPerSecond);
        Assert.True(changed.Segment > future.Segment);
    }

    [Fact]
    public void QueueLossRejectedPacketAndStaleContextCannotBridgeMetrics()
    {
        var start = Stopwatch.GetTimestamp();
        long At(double value) => start + (long)(value * Stopwatch.Frequency);
        var context = new RunRecordingContext(start, 2468, DrivetrainType.RearWheelDrive, true, .35, .35);
        var builder = new RunSampleBuilder(start);
        var first = builder.Add(RunTestData.State(0), start, 1, context);
        var loss = builder.Add(RunTestData.State(100), At(.1), 3, context);
        builder.MarkGap();
        var rejected = builder.Add(RunTestData.State(200), At(.2), 4, context);
        var stale = builder.Add(RunTestData.State(400), At(.4), 5, context);
        Assert.True(loss.Segment > first.Segment);
        Assert.True(rejected.Segment > loss.Segment);
        Assert.False(stale.IsDriving);
        Assert.Null(stale.WheelSpeedMetersPerSecond);
        Assert.True(builder.HasGap);
    }

    [Fact]
    public void DrivingDeadlineCannotBeExtendedByPublishingNewCalibrationContext()
    {
        var start = Stopwatch.GetTimestamp();
        long At(double value) => start + (long)(value * Stopwatch.Frequency);
        var context = new RunRecordingContext(At(.24), 2468, DrivetrainType.RearWheelDrive, true, .35, .4, At(.25));
        var builder = new RunSampleBuilder(start);
        var beforePublication = builder.Add(RunTestData.State(230), At(.23), 1, context);
        var valid = builder.Add(RunTestData.State(245), At(.245), 2, context);
        var expired = builder.Add(RunTestData.State(260), At(.26), 3, context);
        Assert.False(beforePublication.IsDriving);
        Assert.Null(beforePublication.WheelSpeedMetersPerSecond);
        Assert.True(valid.IsDriving);
        Assert.Equal(4, valid.WheelSpeedMetersPerSecond);
        Assert.False(expired.IsDriving);
        Assert.True(expired.Segment > valid.Segment);
        Assert.True(builder.HasGap);
    }

    [Fact]
    public async Task IdleRefreshDoesNotContinuouslyDispatchAndMissingTelemetryCannotStart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispRunTests", Guid.NewGuid().ToString("N"));
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, directory);
        var notifications = 0;
        service.StateChanged += (_, _) => notifications++;
        for (var index = 0; index < 100; index++) service.RefreshStatus();
        Assert.Equal(1, notifications);
        Assert.False(service.CanStart);
        Assert.False(service.Start());
        Assert.False(Directory.Exists(directory));
        Assert.Null(await service.StopAsync());
    }

    [Fact]
    public async Task RecordsAllSamplesKeepsMenuGapAndIgnoresThrowingSubscribers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispRunTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            var port = AvailablePort();
            await receiver.StartAsync(port, TestContext.Current.CancellationToken);
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            await Send(0);
            await WaitUntil(() => receiver.Latest is not null);
            await using var service = new RunRecordingService(receiver, directory);
            var savedEvents = 0;
            service.StateChanged += (_, _) => throw new InvalidOperationException("Test subscriber");
            service.RunSaved += _ => throw new InvalidOperationException("Test subscriber");
            service.RunSaved += _ => savedEvents++;
            service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468, DrivetrainType.RearWheelDrive, true, .35, .35));
            Assert.True(service.Start());
            await Send(10);
            await Send(20, 0, false);
            await Send(30, 9753, false);
            await Send(40);
            await Send(40, rpm: 7000);
            await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == 40 && receiver.Latest.EngineRpm == 7000);
            var run = await service.StopAsync();
            Assert.NotNull(run);
            Assert.Equal(3, run.Samples.Length);
            Assert.Equal(new uint[] { 10, 40, 40 }, run.Samples.Select(sample => sample.State.GameTimestampMilliseconds));
            Assert.Equal(run.Samples[1].ElapsedSeconds, run.Samples[2].ElapsedSeconds);
            Assert.Equal(7000, run.Samples[2].State.EngineRpm);
            Assert.True(run.Samples[1].Segment > run.Samples[0].Segment);
            Assert.True(run.IsIncomplete);
            Assert.Equal(2, run.RejectedDatagrams);
            Assert.Equal(1, savedEvents);
            Assert.Single(await service.Store.ListAsync());
            await Send(50);
            await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == 50);
            Assert.True(receiver.IsRunning);

            async Task Send(int time, int car = 2468, bool driving = true, float rpm = 3000) =>
                await sender.SendAsync(Packet(time, car, driving, rpm), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StorageFailureLeavesReceiverRunningAndStartRequiresDrivingContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispRunTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocked = Path.Combine(directory, "file");
        await File.WriteAllTextAsync(blocked, "preserved", TestContext.Current.CancellationToken);
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            var port = AvailablePort();
            await receiver.StartAsync(port, TestContext.Current.CancellationToken);
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            await sender.SendAsync(Packet(0), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
            await WaitUntil(() => receiver.Latest is not null);
            await using var service = new RunRecordingService(receiver, blocked);
            Assert.True(service.CanStart);
            Assert.False(service.Start());
            Assert.Contains("confirmed driving scene", service.Status);
            service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468, DrivetrainType.RearWheelDrive, true,
                DrivingValidUntilTimestamp: Stopwatch.GetTimestamp() - 1));
            Assert.False(service.Start());
            Assert.True(service.CanStart);
            service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468, DrivetrainType.RearWheelDrive, true));
            Assert.True(service.Start());
            await WaitUntil(() => service.Error is not null && !service.IsRecording && !service.IsPreparing);
            await sender.SendAsync(Packet(100), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
            await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == 100);
            Assert.True(receiver.IsRunning);
            Assert.Equal("preserved", await File.ReadAllTextAsync(blocked, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task BusyStoreRejectsStartBeforeAttachingCaptureAndAllowsRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispRunTests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var receiver = new TelemetryUdpReceiver();
            var port = AvailablePort();
            await receiver.StartAsync(port, TestContext.Current.CancellationToken);
            using var sender = new UdpClient(AddressFamily.InterNetwork);
            await sender.SendAsync(Packet(0), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
            await WaitUntil(() => receiver.Latest is not null);
            await using var service = new RunRecordingService(receiver, directory);
            service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468, DrivetrainType.RearWheelDrive, true));
            using (var reservation = service.Store.TryReserveJournalStart())
            {
                Assert.NotNull(reservation);
                Assert.False(service.Start());
                Assert.Contains("still being read or written", service.Status);
                Assert.False(service.IsRecording);
                Assert.False(Directory.Exists(directory));
                var probe = receiver.BeginRunCapture(2);
                receiver.EndRunCapture(probe);
                await sender.SendAsync(Packet(10), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
                await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == 10);
            }
            service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468, DrivetrainType.RearWheelDrive, true));
            Assert.True(service.Start());
            await sender.SendAsync(Packet(20), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
            await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == 20);
            Assert.NotNull(await service.StopAsync());
            Assert.True(receiver.IsRunning);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LargeGameClockJumpUsesReceiptTimeAndSplitsTheRun()
    {
        var start = Stopwatch.GetTimestamp();
        var builder = new RunSampleBuilder(start);
        var context = new RunRecordingContext(start, 2468, DrivetrainType.RearWheelDrive, true);
        var first = builder.Add(RunTestData.State(100), start, 1, context);
        var jump = builder.Add(RunTestData.State(6100), start + Stopwatch.Frequency / 100, 2, context);
        Assert.InRange(jump.ElapsedSeconds, .009, .011);
        Assert.True(jump.Segment > first.Segment);
    }

    private static byte[] Packet(int time, int car = 2468, bool driving = true, float rpm = 3000)
    {
        var bytes = new byte[324];
        Write(0, driving ? 1 : 0);
        Write(4, time);
        Write(8, BitConverter.SingleToInt32Bits(8000));
        Write(16, BitConverter.SingleToInt32Bits(rpm));
        Write(212, car);
        Write(224, 1);
        Write(228, 8);
        Write(256, BitConverter.SingleToInt32Bits(10));
        for (var offset = 100; offset <= 112; offset += 4) Write(offset, BitConverter.SingleToInt32Bits(10));
        bytes[319] = 1;
        return bytes;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static int AvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(predicate());
    }
}
