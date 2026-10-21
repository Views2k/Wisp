using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class TelemetryUdpReceiverTests
{
    [Fact]
    public async Task UnexpectedSocketClosureRetainsFaultCodeAndRestartClearsIt()
    {
        await using var receiver = new TelemetryUdpReceiver();
        await StartOnAvailablePortAsync(receiver);
        var session = typeof(TelemetryUdpReceiver).GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(receiver)!;
        var socket = (Socket)session.GetType().GetProperty("Socket")!.GetValue(session)!;
        var receive = (Task)session.GetType().GetProperty("ReceiveTask")!.GetValue(session)!;
        socket.Dispose();
        await receive.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.NotNull(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
        Assert.NotNull(receiver.ListenerFailure);
        Assert.NotEqual(0, receiver.ListenerFailure.Code);
        await RestartOnAvailablePortAsync(receiver);
        Assert.Null(receiver.ListenerFailure);
        Assert.Null(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
        await receiver.StopAsync();
        Assert.Null(receiver.ListenerFailure);
    }

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
        await using var receiver = new TelemetryUdpReceiver { CapturePublicationTimings = true };
        var port = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var packet = Fh6PacketFixture.Create();
        await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);

        await WaitForCarAsync(receiver, 2468);

        Assert.NotNull(receiver.Latest);
        Assert.True(receiver.TryGetPublicationTiming(receiver.Latest!, out var timing));
        Assert.True(timing.IsOrdered);
        Assert.Equal(receiver.Latest!.ReceivedTimestamp, timing.Received);
        Assert.Equal(2468, receiver.Latest!.CarOrdinal);
        Assert.Equal(1, receiver.ReceivedDatagrams);
        Assert.Equal(0, receiver.DrainedDatagrams);
        Assert.True(receiver.LastDatagramTimestamp > 0);
        await receiver.StopAsync();
        Assert.Equal(0, receiver.ReceivedDatagrams);
        Assert.Equal(0, receiver.LastDatagramTimestamp);
    }

    [Fact]
    public async Task FailedRestartKeepsExistingListenerAndSettingsStateAlive()
    {
        using var reservation = CreateExclusiveReservation();
        var occupiedPort = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var receiver = new TelemetryUdpReceiver();
        var originalPort = await StartOnAvailablePortAsync(receiver);
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
        await using var receiver = new TelemetryUdpReceiver();
        var originalPort = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(Fh6PacketFixture.Create(), new IPEndPoint(IPAddress.Loopback, originalPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 2468);

        // Probe after the sender has auto-bound, so its ephemeral source port
        // cannot claim our released replacement port before the restart.
        var replacementPort = await RestartOnAvailablePortAsync(receiver, originalPort);

        Assert.Null(receiver.Latest);
        Assert.Equal(replacementPort, receiver.ListeningPort);
        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(0, statistics.AcceptedPackets);
        Assert.Equal(0, statistics.RejectedPackets);
        AssertLoopbackPortReleased(originalPort);

        var replacementPacket = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(replacementPacket, 212, 9753);
        await sender.SendAsync(replacementPacket, new IPEndPoint(IPAddress.Loopback, replacementPort), TestContext.Current.CancellationToken);
        await WaitForCarAsync(receiver, 9753);
    }

    [Fact]
    public async Task ConcurrentStopsAreIdempotentAndClearLatestState()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);
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
        await using var receiver = new TelemetryUdpReceiver();
        receiver.PacketAvailable += (_, _) => throw new InvalidOperationException("Test subscriber failure");
        var port = await StartOnAvailablePortAsync(receiver);
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
        await using var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);

        for (var index = 0; index < 16; index++)
        {
            var packet = Fh6PacketFixture.Create();
            Fh6PacketFixture.WriteInt32(packet, 212, 4000 + index);
            await sender.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), TestContext.Current.CancellationToken);
        }

        await WaitForCarAsync(receiver, 4015);
        Assert.Equal(4015, receiver.Latest?.CarOrdinal);
        Assert.Equal(16, receiver.ReceivedDatagrams);
        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(receiver.ReceivedDatagrams,
            receiver.DrainedDatagrams + statistics.AcceptedPackets + statistics.RejectedPackets);
    }

    [Fact]
    public async Task MalformedTrafficIsCountedWithoutPretendingValidTelemetryIsFresh()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(new byte[8], new IPEndPoint(IPAddress.Loopback, port),
            TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, receiver.ReceivedDatagrams);
        Assert.Equal(1, receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets);
        Assert.True(receiver.LastDatagramTimestamp > 0);
        Assert.Null(receiver.Latest);
    }

    [Theory]
    [InlineData(2049)]
    [InlineData(4096)]
    public async Task OversizedDatagramIsRejectedWithoutStoppingTheListener(int size)
    {
        await using var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        await sender.SendAsync(new byte[size], endpoint, TestContext.Current.CancellationToken);
        await WaitForStatistics(receiver, statistics => statistics.RejectedPackets == 1);
        await sender.SendAsync(Fh6PacketFixture.Create(), endpoint, TestContext.Current.CancellationToken);
        await WaitForStatistics(receiver, statistics => statistics.AcceptedPackets == 1);
        Assert.Null(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
        Assert.NotNull(receiver.Latest);
    }

    [Fact]
    public async Task OversizedDatagramInABurstLeavesTheNewestValidPacket()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        // One burst fits the socket buffer; the listener may drain the later datagrams.
        await sender.SendAsync(Fh6PacketFixture.Create(), endpoint, TestContext.Current.CancellationToken);
        await sender.SendAsync(new byte[4096], endpoint, TestContext.Current.CancellationToken);
        await sender.SendAsync(Fh6PacketFixture.Create(), endpoint, TestContext.Current.CancellationToken);
        await WaitForStatistics(receiver, _ => receiver.ReceivedDatagrams == 3);
        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Null(statistics.ListenerError);
        Assert.NotNull(receiver.Latest);
        await sender.SendAsync(Fh6PacketFixture.Create(), endpoint, TestContext.Current.CancellationToken);
        await WaitForStatistics(receiver, _ => receiver.ReceivedDatagrams == 4);
        Assert.Null(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
    }

    private static async Task WaitForStatistics(TelemetryUdpReceiver receiver, Func<ReceiverStatistics, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready(receiver.GetStatistics(DateTimeOffset.UtcNow)) && DateTime.UtcNow < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(ready(receiver.GetStatistics(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task DisposeReleasesPortAndPreventsRestart()
    {
        var receiver = new TelemetryUdpReceiver();
        var port = await StartOnAvailablePortAsync(receiver);

        await receiver.DisposeAsync();

        Assert.False(receiver.IsRunning);
        Assert.Null(receiver.ListeningPort);
        AssertLoopbackPortReleased(port);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => receiver.RestartAsync(GetAvailablePortExcept(port), TestContext.Current.CancellationToken));
    }

    private static Task<int> StartOnAvailablePortAsync(TelemetryUdpReceiver receiver) =>
        BindAvailablePortAsync(port => receiver.StartAsync(port, TestContext.Current.CancellationToken));

    private static Task<int> RestartOnAvailablePortAsync(TelemetryUdpReceiver receiver, int excludedPort = 0) =>
        BindAvailablePortAsync(port => receiver.RestartAsync(port, TestContext.Current.CancellationToken), excludedPort);

    private static async Task<int> BindAvailablePortAsync(Func<int, Task> bind, int excludedPort = 0)
    {
        // Probing releases the port before the receiver binds it. Another test's
        // ephemeral sender can claim it in between; retry only that setup race.
        for (var attempt = 0; ; attempt++)
        {
            var port = GetAvailablePortExcept(excludedPort);
            try
            {
                await bind(port);
                return port;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 7)
            {
            }
        }
    }

    private static void AssertLoopbackPortReleased(int port)
    {
        try
        {
            using var probe = CreateExclusiveSocket();
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // Once released, another test's ephemeral sender can take the port on the
            // wildcard address. Only a loopback binding means the receiver still holds it.
            var loopback = new IPEndPoint(IPAddress.Loopback, port);
            Assert.DoesNotContain(loopback, IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners());
        }
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
        var socket = CreateExclusiveSocket();
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static Socket CreateExclusiveSocket() =>
        new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };

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
