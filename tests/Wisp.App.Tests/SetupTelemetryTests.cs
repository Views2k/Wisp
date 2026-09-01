using System.Diagnostics;
using System.Net.Sockets;
using Wisp.App;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SetupTelemetryTests
{
    [Fact]
    public void SustainedAdvancingParsedSamplesVerifyWithoutAssumingNativeHudVisibility()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);

        Feed(validator, clock);

        Assert.True(validator.IsVerified);
        Assert.Equal(12, validator.PacketCount);
        Assert.Equal(12, validator.MovingPackets);
        Assert.Equal(TimeSpan.FromMilliseconds(550), validator.Elapsed);
    }

    [Fact]
    public void OnePacketAndRepeatedFrozenPacketsNeverVerify()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        for (var index = 0; index < 40; index++)
        {
            clock.Advance(50);
            validator.Observe(State(clock, 1000), clock.GetUtcNow(), clock.GetTimestamp());
            Assert.False(validator.IsVerified);
            Assert.InRange(validator.PacketCount, 0, 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CachedFutureUnstampedOrStaleSamplesCannotCompleteTheTest(int fault)
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        var beforeTest = State(clock, 1000);
        clock.Advance(50);
        var state = State(clock, 1000);
        state = fault switch
        {
            0 => beforeTest,
            1 => state with { ReceivedTimestamp = null },
            2 => state with { ReceivedTimestamp = clock.GetTimestamp() + Stopwatch.Frequency },
            3 => state with { ReceivedAtUtc = clock.GetUtcNow() + TimeSpan.FromSeconds(1) },
            4 => state with { ReceivedTimestamp = clock.GetTimestamp() - Stopwatch.Frequency },
            _ => state with { ReceivedAtUtc = clock.GetUtcNow() - TimeSpan.FromSeconds(1) }
        };

        validator.Observe(state, clock.GetUtcNow(), clock.GetTimestamp());

        Assert.False(validator.IsVerified);
        Assert.Equal(0, validator.PacketCount);
    }

    [Theory]
    [InlineData("race-off")]
    [InlineData("car")]
    [InlineData("drivetrain")]
    [InlineData("backwards-game")]
    [InlineData("game-jump")]
    [InlineData("receive-gap")]
    public void InterruptedOrChangedSequencesMustStartAgain(string change)
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        Feed(validator, clock, count: 11);
        clock.Advance(change == "receive-gap" ? 600 : 50);
        var state = State(clock, 1600);
        state = change switch
        {
            "race-off" => state with { IsRaceOn = false },
            "car" => state with { CarOrdinal = 2 },
            "drivetrain" => state with { Drivetrain = DrivetrainType.AllWheelDrive },
            "backwards-game" => state with { GameTimestampMilliseconds = 1 },
            "game-jump" => state with { GameTimestampMilliseconds = 9000 },
            _ => state
        };

        validator.Observe(state, clock.GetUtcNow(), clock.GetTimestamp());

        Assert.False(validator.IsVerified);
        Assert.InRange(validator.PacketCount, 0, 1);
    }

    [Fact]
    public void BurstPacketsMustAlsoSpanRealLocalAndGameTime()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        Feed(validator, clock, intervalMilliseconds: 1);
        Assert.Equal(12, validator.PacketCount);
        Assert.False(validator.IsVerified);

        clock = new ManualClock();
        validator = Validator(clock);
        Feed(validator, clock, gameAdvance: 1);
        Assert.Equal(TimeSpan.FromMilliseconds(550), validator.Elapsed);
        Assert.False(validator.IsVerified);
    }

    [Fact]
    public void AdvancingStationaryMenuLikeDataDoesNotMeetTheMotionCheck()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        Feed(validator, clock, speed: 0);

        Assert.False(validator.IsVerified);
        Assert.Equal(0, validator.MovingPackets);
    }

    [Fact]
    public void ElectricVehicleWithZeroEngineRpmCanVerify()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        for (var index = 0; index < 12; index++)
        {
            clock.Advance(50);
            var state = State(clock, (uint)(1000 + index * 50)) with
            {
                EngineRpm = 0,
                EngineMaximumRpm = 0,
                NumCylinders = 0
            };
            validator.Observe(state, clock.GetUtcNow(), clock.GetTimestamp());
        }

        Assert.True(validator.IsVerified);
    }

    [Fact]
    public void UnsignedGameTimestampWrapStillCountsAsAdvancement()
    {
        var clock = new ManualClock();
        var validator = Validator(clock);
        for (var index = 0; index < 12; index++)
        {
            clock.Advance(50);
            var stamp = unchecked(uint.MaxValue - 300 + (uint)(index * 50));
            validator.Observe(State(clock, stamp), clock.GetUtcNow(), clock.GetTimestamp());
        }

        Assert.True(validator.IsVerified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+5500")]
    [InlineData("5500.0")]
    [InlineData("5e3")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("5200")]
    [InlineData("5250")]
    [InlineData("5300")]
    public async Task InvalidPortNeverBindsOrCompletes(string? text)
    {
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source);

        var result = await test.RunAsync(text, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Null(test.SuccessfulEvidence);
        Assert.Equal(0, source.BindCalls);
        Assert.False(test.IsRunning);
    }

    [Theory]
    [InlineData("1024", 1024)]
    [InlineData("5199", 5199)]
    [InlineData("5301", 5301)]
    [InlineData("65535", 65535)]
    [InlineData(" 5500 ", 5500)]
    public async Task ValidPortBoundariesBindButStillRequirePackets(string text, int expectedPort)
    {
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync(text, cancellationToken: cancellation.Token);

        Assert.Equal(expectedPort, source.ListeningPort);
        Assert.Null(test.SuccessfulEvidence);
        cancellation.Cancel();
        var result = await running;

        Assert.False(result.Passed);
        Assert.Equal(1, source.StopCalls);
        Assert.False(source.IsRunning);
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task ExistingReceiverIsUsedAndReleasedAfterSuccessfulTest()
    {
        var clock = new ManualClock();
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source, clock);
        var running = test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, source.BindCalls);
        Assert.Equal(1, source.SubscriberCount);

        EmitSequence(source, clock);
        var result = await running;

        Assert.True(result.Passed);
        Assert.Same(result.Evidence, test.SuccessfulEvidence);
        Assert.Contains("Data Out verified", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.StopCalls);
        Assert.Equal(0, source.SubscriberCount);
        Assert.False(source.IsRunning);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public async Task RetryClearsPreviousEvidenceAndIgnoresCachedLatest()
    {
        var clock = new ManualClock();
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source, clock);
        var first = test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);
        EmitSequence(source, clock);
        Assert.True((await first).Passed);
        source.Seed(State(clock, 1550));
        using var cancellation = new CancellationTokenSource();

        var retry = test.RunAsync("5501", cancellationToken: cancellation.Token);
        Assert.Null(test.SuccessfulEvidence);
        Assert.False(retry.IsCompleted);
        cancellation.Cancel();

        Assert.False((await retry).Passed);
        Assert.Null(test.SuccessfulEvidence);
        Assert.Equal(2, source.BindCalls);
        Assert.Equal(2, source.StopCalls);
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse, "Another app")]
    [InlineData(SocketError.AccessDenied, "Windows could not bind")]
    public async Task BindingFailureIsActionableAndCannotComplete(SocketError error, string expected)
    {
        var source = new FakeSource { BindError = new SocketException((int)error) };
        var test = new SetupTelemetryTest(source);

        var result = await test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Null(test.SuccessfulEvidence);
        Assert.Equal(0, source.SubscriberCount);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public async Task BoundButStoppedReceiverCannotComplete()
    {
        var source = new FakeSource { RemainStoppedAfterBinding = true };
        var test = new SetupTelemetryTest(source);

        var result = await test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains("stay open", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.StopCalls);
    }

    [Fact]
    public async Task TimeoutIsBoundedReleasesListenerAndLeavesSetupUnverified()
    {
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source, timeout: TimeSpan.FromMilliseconds(20));

        var result = await test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains("No FH6 data arrived", result.Message, StringComparison.Ordinal);
        Assert.Contains("match the port", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.StopCalls);
        Assert.Equal(0, source.SubscriberCount);
        Assert.Null(test.SuccessfulEvidence);
    }

    [Fact]
    public async Task RandomMalformedDatagramsCannotSupplyConnectionEvidence()
    {
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync("5500", cancellationToken: cancellation.Token);
        source.EmitDatagram(new byte[1]);
        source.EmitDatagram(Enumerable.Repeat((byte)0xA5, Fh6PacketLayout.PacketLength).ToArray());
        Assert.False(running.IsCompleted);
        Assert.Equal(2, source.GetStatistics(DateTimeOffset.UtcNow).RejectedPackets);
        cancellation.Cancel();

        var result = await running;

        Assert.False(result.Passed);
        Assert.Null(test.SuccessfulEvidence);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("socket")]
    [InlineData("disposed")]
    [InlineData("denied")]
    public async Task ListenerCleanupFailureCannotPublishSuccessfulEvidence(string kind)
    {
        var clock = new ManualClock();
        var source = new FakeSource
        {
            StopError = kind switch
            {
                "io" => new IOException("Synthetic stop failure"),
                "socket" => new SocketException((int)SocketError.OperationAborted),
                "disposed" => new ObjectDisposedException("Synthetic receiver"),
                _ => new UnauthorizedAccessException()
            }
        };
        var test = new SetupTelemetryTest(source, clock);
        var running = test.RunAsync("5500", cancellationToken: TestContext.Current.CancellationToken);
        EmitSequence(source, clock);

        var result = await running;

        Assert.False(result.Passed);
        Assert.Contains("Setup remains unverified", result.Message, StringComparison.Ordinal);
        Assert.Null(test.SuccessfulEvidence);
        Assert.False(test.IsRunning);
        Assert.Equal(1, source.StopCalls);
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public async Task CancelWithFailingCleanupIsAnInlineFailureNotAnUnhandledException()
    {
        var source = new FakeSource { StopError = new IOException("Synthetic stop failure") };
        var test = new SetupTelemetryTest(source);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync("5500", cancellationToken: cancellation.Token);
        cancellation.Cancel();

        var result = await running;

        Assert.False(result.Passed);
        Assert.Null(test.SuccessfulEvidence);
        Assert.Equal(0, source.SubscriberCount);
        Assert.False(test.IsRunning);
    }

    [Fact]
    public async Task ConcurrentTestsCannotRebindOrStealTheListener()
    {
        var source = new FakeSource();
        var test = new SetupTelemetryTest(source);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync("5500", cancellationToken: cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => test.RunAsync("5501", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, source.BindCalls);
        cancellation.Cancel();
        await running;
    }

    [Theory]
    [InlineData(0, 0, true, null, "No FH6 data")]
    [InlineData(0, 5, true, null, "not valid FH6")]
    [InlineData(5, 0, true, null, "required live sequence")]
    [InlineData(5, 0, false, null, "listener stopped")]
    [InlineData(0, 0, true, "synthetic failure", "listener stopped")]
    public void TimeoutGuidanceDistinguishesMissingMalformedFrozenAndStoppedSources(
        long accepted, long rejected, bool running, string? listenerError, string expected)
    {
        var statistics = new ReceiverStatistics(accepted, rejected, 0, PacketParseError.None, listenerError);

        Assert.Contains(expected, SetupTelemetryTest.TimeoutMessage(statistics, running), StringComparison.Ordinal);
    }

    private static SetupTelemetryValidator Validator(ManualClock clock) =>
        new(clock.GetUtcNow(), clock.GetTimestamp());

    private static void Feed(
        SetupTelemetryValidator validator,
        ManualClock clock,
        int count = 12,
        int intervalMilliseconds = 50,
        int gameAdvance = 50,
        float speed = 5)
    {
        for (var index = 0; index < count; index++)
        {
            clock.Advance(intervalMilliseconds);
            validator.Observe(
                State(clock, (uint)(1000 + index * gameAdvance)) with { GroundSpeedMetersPerSecond = speed },
                clock.GetUtcNow(), clock.GetTimestamp());
        }
    }

    private static void EmitSequence(FakeSource source, ManualClock clock)
    {
        for (var index = 0; index < 12; index++)
        {
            clock.Advance(50);
            source.Emit(State(clock, (uint)(1000 + index * 50)));
        }
    }

    private static VehicleState State(ManualClock clock, uint gameTimestamp) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = gameTimestamp,
        ReceivedAtUtc = clock.GetUtcNow(),
        ReceivedTimestamp = clock.GetTimestamp(),
        CarOrdinal = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 4,
        GroundSpeedMetersPerSecond = 5,
        WheelRotationRadiansPerSecond = new WheelValues(10, 10, 10, 10),
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 1000,
        EngineMaximumRpm = 8000,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 10,
        Brake = 0
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = 20 * Stopwatch.Frequency;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int milliseconds)
        {
            _utcNow += TimeSpan.FromMilliseconds(milliseconds);
            _timestamp += milliseconds * Stopwatch.Frequency / 1000;
        }
    }

    private sealed class FakeSource : ISetupTelemetrySource
    {
        private EventHandler? _packetAvailable;
        private long _accepted;
        private long _rejected;
        public int BindCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Exception? BindError { get; init; }
        public Exception? StopError { get; init; }
        public bool RemainStoppedAfterBinding { get; init; }
        public int SubscriberCount => _packetAvailable?.GetInvocationList().Length ?? 0;
        public VehicleState? Latest { get; private set; }
        public bool IsRunning { get; private set; }
        public int? ListeningPort { get; private set; }
        public event EventHandler? PacketAvailable
        {
            add => _packetAvailable += value;
            remove => _packetAvailable -= value;
        }

        public Task BindAsync(int port)
        {
            BindCalls++;
            if (BindError is { } bindError)
            {
                throw bindError;
            }

            ListeningPort = port;
            IsRunning = !RemainStoppedAfterBinding;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalls++;
            IsRunning = false;
            ListeningPort = null;
            if (StopError is { } stopError)
            {
                throw stopError;
            }

            return Task.CompletedTask;
        }

        public void Seed(VehicleState state) => Latest = state;

        public void Emit(VehicleState state)
        {
            Latest = state;
            _accepted++;
            _packetAvailable?.Invoke(this, EventArgs.Empty);
        }

        public void EmitDatagram(byte[] bytes)
        {
            if (new Fh6PacketParser().TryParse(bytes, DateTimeOffset.UtcNow, out var state, out _, Stopwatch.GetTimestamp()))
            {
                Emit(state!);
            }
            else
            {
                _rejected++;
            }
        }

        public ReceiverStatistics GetStatistics(DateTimeOffset nowUtc) =>
            new(_accepted, _rejected, 0, PacketParseError.None, null);
    }
}
