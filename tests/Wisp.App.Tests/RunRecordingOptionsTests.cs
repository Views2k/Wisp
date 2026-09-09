using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunRecordingOptionsTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(601)]
    public async Task InvalidDurationNeverAttachesCapture(double seconds)
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        Assert.False(fixture.Service.Start(new(TimeSpan.FromSeconds(seconds))));
        Assert.Contains("above zero", fixture.Service.Status);
        Assert.False(fixture.Service.IsRecording);
        var probe = fixture.Receiver.BeginRunCapture(2);
        fixture.Receiver.EndRunCapture(probe);
    }

    [Fact]
    public async Task TimedRecordingStopsAtItsDeadlineAndSavesWithoutUserStop()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var saved = new TaskCompletionSource<RecordedRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.RunSaved += run => saved.TrySetResult(run);
        Assert.True(fixture.Service.Start(new(TimeSpan.FromMilliseconds(250))));
        for (var index = 1; index <= 100 && fixture.Service.IsRecording; index++)
        {
            await fixture.SendAsync(index * 10);
            fixture.Service.RefreshStatus();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        var run = await saved.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("Timed recording completed", run.FinishReason);
        Assert.False(run.IsIncomplete);
        Assert.False(fixture.Service.IsRecording);
        Assert.InRange(fixture.Service.Elapsed.TotalMilliseconds, 249, 251);
        Assert.NotEmpty(run.Samples);
        var count = run.Samples.Length;
        await fixture.SendAsync(9999);
        Assert.Equal(count, (await fixture.Service.Store.LoadAsync(run.Id)).Samples.Length);
        Assert.True(fixture.Receiver.IsRunning);
    }

    [Fact]
    public async Task MomentsUseProcessedGameTimeRejectGapsAndRemainBounded()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        Assert.True(fixture.Service.Start());
        Assert.False(fixture.Service.MarkMoment("Too early"));
        await fixture.SendAsync(10);
        await fixture.SendAsync(110);
        await AddWhenProcessed("Limiter");
        Assert.Null(fixture.Service.Error);

        var rejected = fixture.Receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets;
        await fixture.SendInvalidAsync();
        await RecordingFixture.WaitUntil(() => fixture.Receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets > rejected);
        Assert.False(fixture.Service.MarkMoment("Missing data"));
        Assert.Contains("Recording continues", fixture.Service.MarkerStatus);
        await fixture.SendAsync(210, driving: false);
        Assert.False(fixture.Service.MarkMoment("Menu"));
        await fixture.SendAsync(310);
        await AddWhenProcessed("Exit");

        fixture.PublishContext(expired: true);
        Assert.False(fixture.Service.MarkMoment("Expired driving observation"));
        fixture.PublishContext();
        Assert.False(fixture.Service.MarkMoment(new string('x', 81)));
        Assert.False(fixture.Service.MarkMoment("two\nlines"));
        for (var index = 2; index < RunStore.MaximumMarkers; index++) Assert.True(fixture.Service.MarkMoment());
        Assert.False(fixture.Service.MarkMoment("One too many"));
        Assert.Contains("128", fixture.Service.MarkerStatus);
        Assert.True(fixture.Service.IsRecording);
        var run = await fixture.Service.StopAsync();
        Assert.NotNull(run);
        Assert.Equal(RunStore.MaximumMarkers, run.Markers.Length);
        Assert.Equal(new RunMarker(.1, "Limiter"), run.Markers[0]);
        Assert.Equal(.3, run.Markers[1].ElapsedSeconds, 6);
        Assert.Equal("Exit", run.Markers[1].Label);
        Assert.Equal(run.Markers, (await fixture.Service.Store.LoadAsync(run.Id)).Markers);
        Assert.True(run.IsIncomplete);
        Assert.True(fixture.Receiver.IsRunning);

        async Task AddWhenProcessed(string label)
        {
            var added = false;
            for (var retry = 0; retry < 30 && !added; retry++)
            {
                added = fixture.Service.MarkMoment(label);
                if (!added) await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            Assert.True(added, fixture.Service.MarkerStatus);
        }
    }

    [Fact]
    public async Task TimedFinishWithMissingTailIsPartialWithoutInventingSamples()
    {
        await using var fixture = await RecordingFixture.CreateAsync();
        var saved = new TaskCompletionSource<RecordedRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.RunSaved += run => saved.TrySetResult(run);
        Assert.True(fixture.Service.Start(new(TimeSpan.FromMilliseconds(500))));
        await fixture.SendAsync(10);
        await Task.Delay(550, TestContext.Current.CancellationToken);
        fixture.Service.RefreshStatus();
        var run = await saved.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("Timed recording completed", run.FinishReason);
        Assert.True(run.IsIncomplete);
        Assert.Equal(0, Assert.Single(run.Samples).ElapsedSeconds);
        Assert.Empty(run.Markers);
    }

    private sealed class RecordingFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.RunOptionsTests", Guid.NewGuid().ToString("N"));
        private readonly UdpClient _sender = new(AddressFamily.InterNetwork);
        private readonly IPEndPoint _endpoint = new(IPAddress.Loopback, AvailablePort());
        internal TelemetryUdpReceiver Receiver { get; } = new();
        internal RunRecordingService Service { get; }
        private RecordingFixture() => Service = new(Receiver, _directory);

        internal static async Task<RecordingFixture> CreateAsync()
        {
            var fixture = new RecordingFixture();
            await fixture.Receiver.StartAsync(fixture._endpoint.Port, TestContext.Current.CancellationToken);
            await fixture.SendAsync(0);
            fixture.PublishContext();
            return fixture;
        }
        internal void PublishContext(bool driving = true, bool expired = false)
        {
            var now = Stopwatch.GetTimestamp();
            Service.UpdateContext(new(now, 2468, DrivetrainType.RearWheelDrive, driving, .35, .35,
                expired ? now - 1 : now + Stopwatch.Frequency / 4));
        }
        internal async Task SendAsync(int timestamp, bool driving = true)
        {
            PublishContext(driving);
            var bytes = new byte[324];
            Write(0, 1); Write(4, timestamp); Write(8, BitConverter.SingleToInt32Bits(8000));
            Write(16, BitConverter.SingleToInt32Bits(4000)); Write(212, 2468); Write(224, 1); Write(228, 8);
            Write(256, BitConverter.SingleToInt32Bits(20));
            for (var offset = 100; offset <= 112; offset += 4) Write(offset, BitConverter.SingleToInt32Bits(50));
            bytes[319] = 3;
            await _sender.SendAsync(bytes, _endpoint, TestContext.Current.CancellationToken);
            await WaitUntil(() => Receiver.Latest?.GameTimestampMilliseconds == timestamp);
            void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }
        internal async Task SendInvalidAsync() => await _sender.SendAsync(new byte[8], _endpoint, TestContext.Current.CancellationToken);
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Receiver.DisposeAsync();
            _sender.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        private static int AvailablePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        internal static async Task WaitUntil(Func<bool> predicate)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while (!predicate() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
            Assert.True(predicate());
        }
    }
}
