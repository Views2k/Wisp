using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wisp.Core;
using Xunit;

namespace Wisp.Telemetry.Tests;

public sealed class TelemetryPacketDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DisabledRecordingAndThrowingObserversPreserveParsing(int observerMode)
    {
        var events = new ConcurrentQueue<TelemetryPacketDiagnostic>();
        using var accepted = new SemaphoreSlim(0);
        await using var receiver = new TelemetryUdpReceiver();
        if (observerMode != 0)
        {
            receiver.DiagnosticObserver = item =>
            {
                events.Enqueue(item);
                if (observerMode == 2) throw new InvalidOperationException("Diagnostic observer failure");
            };
        }
        receiver.PacketAvailable += (_, _) => accepted.Release();
        var port = GetAvailablePort();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var started = Stopwatch.GetTimestamp();
        var first = Fh6PacketFixture.Create();
        await sender.SendAsync(first, endpoint, TestContext.Current.CancellationToken);
        Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        var firstState = Assert.IsType<VehicleState>(receiver.Latest);
        AssertMatchesParser(first, firstState);

        await sender.SendAsync(new byte[8], endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => receiver.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets == 1);
        var last = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(last, 212, 9753);
        Fh6PacketFixture.WriteSingle(last, 16, 7200);
        await sender.SendAsync(last, endpoint, TestContext.Current.CancellationToken);
        Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        var lastState = Assert.IsType<VehicleState>(receiver.Latest);
        AssertMatchesParser(last, lastState);

        var statistics = receiver.GetStatistics(DateTimeOffset.UtcNow);
        Assert.True(receiver.IsRunning);
        Assert.Null(statistics.ListenerError);
        Assert.Equal(2, statistics.AcceptedPackets);
        Assert.Equal(1, statistics.RejectedPackets);
        Assert.Equal(3, receiver.ReceivedDatagrams);
        Assert.Equal(0, receiver.DrainedDatagrams);
        if (observerMode == 0)
        {
            Assert.Empty(events);
            return;
        }

        var recorded = events.ToArray();
        Assert.Equal(6, recorded.Length);
        Assert.Equal(new long[] { 1, 1, 2, 2, 3, 3 }, recorded.Select(item => item.Sequence));
        Assert.Equal(new[] { TelemetryPacketDiagnosticKind.Received, TelemetryPacketDiagnosticKind.Accepted,
            TelemetryPacketDiagnosticKind.Received, TelemetryPacketDiagnosticKind.Rejected,
            TelemetryPacketDiagnosticKind.Received, TelemetryPacketDiagnosticKind.Accepted },
            recorded.Select(item => item.Kind));
        Assert.All(recorded, item => Assert.InRange(item.Timestamp, started, Stopwatch.GetTimestamp()));
        AssertAccepted(recorded[0], recorded[1], firstState);
        AssertAccepted(recorded[4], recorded[5], lastState);
        Assert.Equal(PacketParseError.IncorrectLength, recorded[2].ParseError);
        Assert.Equal(PacketParseError.IncorrectLength, recorded[3].ParseError);
        Assert.Equal(recorded[2].Timestamp, recorded[2].ReceivedTimestamp);
        Assert.InRange(recorded[3].ReceivedTimestamp, recorded[2].Timestamp, recorded[3].Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainedDatagramsKeepTheirOwnIdentityAndNewestParseTimestamp(bool throwWhenDrained)
    {
        var events = new ConcurrentQueue<TelemetryPacketDiagnostic>();
        var firstObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        using var accepted = new SemaphoreSlim(0);
        await using var receiver = new TelemetryUdpReceiver();
        var gateTimedOut = false;
        receiver.DiagnosticObserver = item =>
        {
            events.Enqueue(item);
            if (item.Kind == TelemetryPacketDiagnosticKind.Received && item.Sequence == 1)
            {
                firstObserved.TrySetResult();
                // Hold only this test observer until the burst is already queued in the real socket.
                gateTimedOut = !releaseFirst.Wait(TimeSpan.FromSeconds(3));
            }
            if (throwWhenDrained && item.Kind == TelemetryPacketDiagnosticKind.Drained)
                throw new InvalidOperationException("Drained observer failure");
        };
        receiver.PacketAvailable += (_, _) => accepted.Release();
        var port = GetAvailablePort();
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            await sender.SendAsync(PacketForCar(4000), endpoint, TestContext.Current.CancellationToken);
            await firstObserved.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            for (var index = 1; index <= 8; index++)
                await sender.SendAsync(PacketForCar(4000 + index), endpoint, TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseFirst.Set();
        }
        Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(gateTimedOut);
        Assert.True(receiver.IsRunning);
        Assert.Null(receiver.GetStatistics(DateTimeOffset.UtcNow).ListenerError);
        Assert.Equal(9, receiver.ReceivedDatagrams);
        Assert.Equal(8, receiver.DrainedDatagrams);
        Assert.Equal(1, receiver.GetStatistics(DateTimeOffset.UtcNow).AcceptedPackets);
        var state = Assert.IsType<VehicleState>(receiver.Latest);
        AssertMatchesParser(PacketForCar(4008), state);

        var recorded = events.ToArray();
        Assert.Equal(10, recorded.Length);
        for (var index = 0; index < 9; index++)
        {
            Assert.Equal(index == 0 ? TelemetryPacketDiagnosticKind.Received : TelemetryPacketDiagnosticKind.Drained,
                recorded[index].Kind);
            Assert.Equal(index + 1, recorded[index].Sequence);
            Assert.Equal(4000 + index, recorded[index].CarOrdinal);
            Assert.Equal(recorded[index].Timestamp, recorded[index].ReceivedTimestamp);
            if (index > 0) Assert.True(recorded[index].Timestamp >= recorded[index - 1].Timestamp);
        }
        Assert.Equal(9, recorded[9].Sequence);
        AssertAccepted(recorded[8], recorded[9], state);
    }

    private static void AssertAccepted(TelemetryPacketDiagnostic raw, TelemetryPacketDiagnostic parsed, VehicleState state)
    {
        Assert.Equal(TelemetryPacketDiagnosticKind.Accepted, parsed.Kind);
        Assert.Equal(PacketParseError.None, parsed.ParseError);
        Assert.Equal(raw.Sequence, parsed.Sequence);
        Assert.Equal(raw.Timestamp, raw.ReceivedTimestamp);
        Assert.Equal(state.ReceivedTimestamp, parsed.ReceivedTimestamp);
        Assert.InRange(parsed.ReceivedTimestamp, raw.Timestamp, parsed.Timestamp);
        Assert.Equal(state.GameTimestampMilliseconds, raw.GameTimestampMilliseconds);
        Assert.Equal(state.GameTimestampMilliseconds, parsed.GameTimestampMilliseconds);
        Assert.Equal(state.EngineRpm, raw.Rpm);
        Assert.Equal(state.EngineRpm, parsed.Rpm);
        Assert.Equal(state.EngineMaximumRpm, raw.MaximumRpm);
        Assert.Equal(state.EngineMaximumRpm, parsed.MaximumRpm);
        Assert.Equal(state.CarOrdinal, raw.CarOrdinal);
        Assert.Equal(state.CarOrdinal, parsed.CarOrdinal);
        Assert.Equal(state.IsRaceOn, raw.RaceOn);
        Assert.Equal(state.IsRaceOn, parsed.RaceOn);
    }

    private static void AssertMatchesParser(byte[] packet, VehicleState actual)
    {
        Assert.True(new Fh6PacketParser().TryParse(packet, actual.ReceivedAtUtc, out var expected,
            out var error, actual.ReceivedTimestamp));
        Assert.Equal(PacketParseError.None, error);
        Assert.Equal(expected, actual);
    }

    private static byte[] PacketForCar(int car)
    {
        var packet = Fh6PacketFixture.Create();
        Fh6PacketFixture.WriteInt32(packet, 212, car);
        return packet;
    }

    private static int GetAvailablePort()
    {
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        { ExclusiveAddressUse = true };
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)reservation.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        while (!predicate() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(predicate());
    }
}
