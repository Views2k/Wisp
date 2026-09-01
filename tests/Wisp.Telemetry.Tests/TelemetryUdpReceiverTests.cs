using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class TelemetryUdpReceiverTests
{
    [Theory]
    [InlineData(5200)]
    [InlineData(5250)]
    [InlineData(5300)]
    public void RejectsForzaReservedPorts(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TelemetryUdpReceiver.ValidatePort(port));
    }

    [Fact]
    public void AcceptsDefaultPort()
    {
        TelemetryUdpReceiver.ValidatePort(5500);
    }

    [Fact]
    public async Task ReceivesAndParsesLocalhostDatagramEndToEnd()
    {
        var port = GetAvailablePort();

        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var packet = Fh6PacketFixture.Create();
        await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

        await WaitForCarAsync(receiver, 2468);

        Assert.NotNull(receiver.Latest);
        Assert.Equal(2468, receiver.Latest!.CarOrdinal);
        await receiver.StopAsync();
    }

    [Fact]
    public async Task FailedRestartKeepsExistingListenerAndSettingsStateAlive()
    {
        var originalPort = GetAvailablePort();
        using var reservation = CreateExclusiveReservation();
        var occupiedPort = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(originalPort, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(Fh6PacketFixture.Create(), new IPEndPoint(IPAddress.Loopback, originalPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 2468);
        var acceptedBeforeRestart = receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets;

        await Assert.ThrowsAsync<SocketException>(() => receiver.RestartAsync(occupiedPort, TestContext.Current.CancellationToken));

        Assert.True(receiver.IsRunning);
        Assert.Equal(originalPort, receiver.ListeningPort);
        Assert.Equal(2468, receiver.Latest?.CarOrdinal);
        Assert.Equal(acceptedBeforeRestart, receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets);

        var followUpPacket = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(followUpPacket, 212, 9753);
        await sender.SendAsync(followUpPacket, new IPEndPoint(IPAddress.Loopback, originalPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 9753);
    }

    [Fact]
    public async Task SuccessfulRestartClearsSessionStateAndReleasesOldPort()
    {
        var originalPort = GetAvailablePort();
        var replacementPort = GetAvailablePortExcept(originalPort);
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(originalPort, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(Fh6PacketFixture.Create(), new IPEndPoint(IPAddress.Loopback, originalPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 2468);

        await receiver.RestartAsync(replacementPort, TestContext.Current.CancellationToken);

        Assert.Null(receiver.Latest);
        Assert.Equal(replacementPort, receiver.ListeningPort);
        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(0, statistics.AcceptedPackets);
        Assert.Equal(0, statistics.RejectedPackets);
        using var oldPortProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        oldPortProbe.Bind(new IPEndPoint(IPAddress.Loopback, originalPort));

        var replacementPacket = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(replacementPacket, 212, 9753);
        await sender.SendAsync(replacementPacket, new IPEndPoint(IPAddress.Loopback, replacementPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 9753);
    }

    [Fact]
    public async Task ConcurrentStopsAreIdempotentAndClearLatestState()
    {
        var port = GetAvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(Fh6PacketFixture.Create(), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 2468);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => receiver.StopAsync()));

        Assert.False(receiver.IsRunning);
        Assert.Null(receiver.ListeningPort);
        Assert.Null(receiver.Latest);
        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(0, statistics.AcceptedPackets);
        Assert.Equal(0, statistics.RejectedPackets);
    }

    [Fact]
    public async Task FailedInitialBindDoesNotLeaveReceiverInRunningState()
    {
        using var reservation = CreateExclusiveReservation();
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var receiver = new TelemetryUdpReceiver();

        await Assert.ThrowsAsync<SocketException>(() => receiver.StartAsync(port, TestContext.Current.CancellationToken));

        Assert.False(receiver.IsRunning);
        Assert.Null(receiver.ListeningPort);
        Assert.Null(receiver.Latest);
    }

    [Fact]
    public async Task PacketSubscriberFailureDoesNotTerminateListener()
    {
        var port = GetAvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        receiver.PacketAvailable += (_, _) => throw new InvalidOperationException("Test subscriber failure");
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);

        await sender.SendAsync(Fh6PacketFixture.Create(), new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 2468);

        var followUpPacket = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(followUpPacket, 212, 9753);
        await sender.SendAsync(followUpPacket, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 9753);

        Assert.True(receiver.IsRunning);
        Assert.Equal(2, receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets);
    }

    [Fact]
    public async Task PacketBurstRetainsNewestDatagram()
    {
        var port = GetAvailablePort();
        await using var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);

        for (var index = 0; index < 16; index++)
        {
            var packet = Fh6PacketFixture.Create();
            Fh6PacketFixture.WriteInt32(packet, 212, 4000 + index);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        }

        await WaitForCarAsync(receiver, 4015);
        Assert.Equal(4015, receiver.Latest?.CarOrdinal);
    }

    [Fact]
    public async Task DisposeReleasesPortAndPreventsRestart()
    {
        var port = GetAvailablePort();
        var receiver = new TelemetryUdpReceiver();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);

        await receiver.DisposeAsync();

        Assert.False(receiver.IsRunning);
        Assert.Null(receiver.ListeningPort);
        using var portProbe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        portProbe.Bind(new IPEndPoint(IPAddress.Loopback, port));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => receiver.RestartAsync(GetAvailablePortExcept(port), TestContext.Current.CancellationToken));
    }

    private static int GetAvailablePort()
    {
        using var reservation = CreateExclusiveReservation();
        return ((IPEndPoint)reservation.LocalEndPoint!).Port;
    }

    private static int GetAvailablePortExcept(int excludedPort)
    {
        int port;
        do
        {
            port = GetAvailablePort();
        }
        while (port == excludedPort);

        return port;
    }

    private static Socket CreateExclusiveReservation()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static async Task WaitForCarAsync(TelemetryUdpReceiver receiver, int carOrdinal)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (receiver.Latest?.CarOrdinal != carOrdinal && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(carOrdinal, receiver.Latest?.CarOrdinal);
    }
}
