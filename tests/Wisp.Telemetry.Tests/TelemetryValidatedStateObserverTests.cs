using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Wisp.Core;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class TelemetryValidatedStateObserverTests
{
    [Fact]
    public async Task EarlierQueuedCrossingIsObservedOnceBeforeNewestPacketReplacesIt()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var observed = new ConcurrentQueue<VehicleState?>();
        receiver.ValidatedStateObserver = observed.Enqueue;
        var timestamps = await SendQueuedBurstAsync(receiver,
            [Packet(1, 7800), Packet(2, 6500), Packet(3, 6400)]);

        var states = observed.ToArray();
        Assert.Equal(3, states.Length);
        Assert.Equal(new uint[] { 1, 2, 3 }, states.Select(s => s!.GameTimestampMilliseconds));
        Assert.Equal(new float[] { 7800, 6500, 6400 }, states.Select(s => s!.EngineRpm));
        for (var i = 0; i < states.Length; i++)
        {
            Assert.NotNull(states[i]!.ReceivedTimestamp);
            Assert.Equal(timestamps[states[i]!.GameTimestampMilliseconds], states[i]!.ReceivedTimestamp!.Value);
            if (i > 0) Assert.True(states[i]!.ReceivedTimestamp!.Value >= states[i - 1]!.ReceivedTimestamp!.Value);
        }
        Assert.Equal(6400, receiver.Latest!.EngineRpm);
        Assert.Equal(1, receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets);
        Assert.Equal(2, receiver.DrainedDatagrams);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDatagramClearsEventEvidenceWithoutPublishingInvalidState(bool newestInvalid)
    {
        await using var receiver = new TelemetryUdpReceiver();
        var observed = new ConcurrentQueue<VehicleState?>();
        var pendingCrossing = 0;
        receiver.ValidatedStateObserver = state =>
        {
            observed.Enqueue(state);
            if (state is null) Interlocked.Exchange(ref pendingCrossing, 0);
            else if (state.EngineRpm >= 7600) Interlocked.Exchange(ref pendingCrossing, 1);
        };
        byte[][] packets = newestInvalid
            ? [Packet(1, 7800), new byte[8]]
            : [Packet(1, 7800), new byte[8], Packet(3, 6400)];
        await SendQueuedBurstAsync(receiver, packets);

        Assert.Equal(packets.Length, observed.Count);
        Assert.Null(observed.ToArray()[1]);
        Assert.Equal(0, Volatile.Read(ref pendingCrossing));
        var stats = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(newestInvalid ? 0 : 1, stats.AcceptedPackets);
        Assert.Equal(newestInvalid ? 1 : 0, stats.RejectedPackets);
        Assert.Equal(newestInvalid ? PacketParseError.IncorrectLength : PacketParseError.None, stats.LastParseError);
        if (newestInvalid) Assert.Null(receiver.Latest);
        else Assert.Equal(6400, receiver.Latest!.EngineRpm);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotInterruptDrainOrLatestPublication()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var calls = 0;
        receiver.ValidatedStateObserver = _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Expected observer test failure");
        };
        await SendQueuedBurstAsync(receiver, [Packet(1, 7800), Packet(2, 6500), Packet(3, 6400)]);

        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(6400, receiver.Latest!.EngineRpm);
        Assert.True(receiver.IsRunning);
        Assert.Null(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
    }

    [Fact]
    public async Task DisabledObserverPreservesLatestOnlyCountersAndDiagnostics()
    {
        await using var receiver = new TelemetryUdpReceiver();
        var calls = 0;
        receiver.ValidatedStateObserver = _ => Interlocked.Increment(ref calls);
        receiver.ValidatedStateObserver = null;
        Assert.Null(receiver.ValidatedStateObserver);
        await SendQueuedBurstAsync(receiver, [Packet(1, 7800), new byte[8], Packet(3, 6400)]);

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(3, receiver.ReceivedDatagrams);
        Assert.Equal(2, receiver.DrainedDatagrams);
        Assert.Equal(1, receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets);
        Assert.Equal(0, receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets);
        Assert.Equal(PacketParseError.None, receiver.GetStatistics(DateTimeOffset.UtcNow).LastParseError);
        Assert.Equal(6400, receiver.Latest!.EngineRpm);
    }

    private static byte[] Packet(int time, float rpm)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, 4, time);
        Fh6PacketFixture.WriteSingle(packet, 16, rpm);
        return packet;
    }

    private static async Task<ConcurrentDictionary<uint, long>> SendQueuedBurstAsync(
        TelemetryUdpReceiver receiver, byte[][] packets)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var releaseFirst = new ManualResetEventSlim();
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new ConcurrentQueue<TelemetryPacketDiagnostic>();
        var timestamps = new ConcurrentDictionary<uint, long>();
        var notifications = 0;
        receiver.PacketAvailable += (_, _) => Interlocked.Increment(ref notifications);
        receiver.DiagnosticObserver = diagnostic =>
        {
            diagnostics.Enqueue(diagnostic);
            if (diagnostic.Kind is TelemetryPacketDiagnosticKind.Received or TelemetryPacketDiagnosticKind.Drained)
                timestamps[diagnostic.GameTimestampMilliseconds] = receiver.LastDatagramTimestamp;
            if (diagnostic.Kind == TelemetryPacketDiagnosticKind.Received && firstReceived.TrySetResult())
            {
                // Test-only barrier makes the kernel queue deterministic. The
                // new observer itself is never used to hold the receive worker.
                releaseFirst.Wait(TimeSpan.FromSeconds(3));
            }
        };
        var port = await StartOnAvailablePortAsync(receiver, cancellation);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            await sender.SendAsync(packets[0], endpoint, cancellation);
            await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            for (var i = 1; i < packets.Length; i++) await sender.SendAsync(packets[i], endpoint, cancellation);
        }
        finally { releaseFirst.Set(); }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stats = receiver.GetStatistics(DateTimeOffset.UtcNow);
            if (receiver.ReceivedDatagrams == packets.Length &&
                (stats.RejectedPackets == 1 || Volatile.Read(ref notifications) == 1) &&
                diagnostics.Any(d => d.Kind is TelemetryPacketDiagnosticKind.Accepted or TelemetryPacketDiagnosticKind.Rejected)) break;
            await Task.Delay(5, cancellation);
        }
        Assert.Equal(packets.Length, receiver.ReceivedDatagrams);
        Assert.Equal(packets.Length - 1, receiver.DrainedDatagrams);
        var final = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.Equal(1, final.AcceptedPackets + final.RejectedPackets);
        Assert.Equal(final.AcceptedPackets, Volatile.Read(ref notifications));
        var evidence = diagnostics.ToArray();
        Assert.Single(evidence, d => d.Kind == TelemetryPacketDiagnosticKind.Received);
        Assert.Equal(packets.Length - 1, evidence.Count(d => d.Kind == TelemetryPacketDiagnosticKind.Drained));
        Assert.Single(evidence, d => d.Kind is TelemetryPacketDiagnosticKind.Accepted or TelemetryPacketDiagnosticKind.Rejected);
        receiver.DiagnosticObserver = null;
        return timestamps;
    }

    private static async Task<int> StartOnAvailablePortAsync(TelemetryUdpReceiver receiver,
        CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ExclusiveAddressUse = true
            };
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
            reservation.Dispose();
            try
            {
                await receiver.StartAsync(port, cancellation);
                return port;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 2)
            {
                // A parallel socket may claim the released probe port. Reselect
                // only for that setup race; the third failure and all others escape.
            }
        }
    }
}
