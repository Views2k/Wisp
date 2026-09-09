using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class RunDatagramCaptureTests
{
    [Fact]
    public async Task CapturesEveryDatagramBeforeTheReceiverDrainsItsSharedBuffer()
    {
        var port = AvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        var capture = receiver.BeginRunCapture(32);
        using var firstReceived = new ManualResetEventSlim();
        using var releaseReceiver = new ManualResetEventSlim();
        receiver.DiagnosticObserver = observation =>
        {
            if (observation.Kind == TelemetryPacketDiagnosticKind.Received && observation.CarOrdinal == 4000)
            {
                firstReceived.Set();
                releaseReceiver.Wait(TimeSpan.FromSeconds(3));
            }
        };
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            await Send(4000);
            await WaitUntil(() => firstReceived.IsSet);
            for (var car = 4001; car <= 4012; car++) await Send(car);
        }
        finally { releaseReceiver.Set(); }
        await WaitUntil(() => receiver.Latest?.CarOrdinal == 4012);
        receiver.EndRunCapture(capture);
        var cars = new List<int>();
        await foreach (var packet in capture.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            cars.Add(BinaryPrimitives.ReadInt32LittleEndian(packet.Bytes.Span.Slice(212, 4)));
            Assert.Equal(cars.Count, packet.Sequence);
            Assert.True(packet.Timestamp > 0);
        }
        Assert.Equal(Enumerable.Range(4000, 13), cars);
        Assert.True(receiver.DrainedDatagrams >= 12);
        Assert.Equal(0, capture.DroppedDatagrams);

        async Task Send(int car)
        {
            var bytes = Fh6PacketFixture.Create();
            Fh6PacketFixture.WriteInt32(bytes, 212, car);
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FullCaptureDropsRecordingDataWithoutBlockingLatestTelemetryOrRestart()
    {
        var port = AvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        var capture = receiver.BeginRunCapture(2);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        for (var car = 5000; car < 5010; car++)
        {
            var bytes = Fh6PacketFixture.Create();
            Fh6PacketFixture.WriteInt32(bytes, 212, car);
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        }
        await WaitUntil(() => receiver.Latest?.CarOrdinal == 5009);
        Assert.Equal(8, capture.DroppedDatagrams);
        Assert.True(receiver.IsRunning);
        await receiver.StopAsync();
        var count = 0;
        await foreach (var packet in capture.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            count++;
            Assert.Equal(324, packet.Bytes.Length);
        }
        Assert.Equal(2, count);
        Assert.Equal("Telemetry listener changed", capture.CompletionReason);
    }

    [Fact]
    public async Task InvalidLengthIsRepresentedWithoutRetainingUnboundedPayload()
    {
        var port = AvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        var capture = receiver.BeginRunCapture(2);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(new byte[1000], new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        await WaitUntil(() => receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets == 1);
        receiver.EndRunCapture(capture);
        var count = 0;
        await foreach (var packet in capture.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            count++;
            Assert.True(packet.Bytes.IsEmpty);
            Assert.False(new Fh6PacketParser().TryParse(packet.Bytes.Span, DateTimeOffset.UtcNow, out _, out _));
        }
        Assert.Equal(1, count);
        Assert.True(receiver.IsRunning);
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
