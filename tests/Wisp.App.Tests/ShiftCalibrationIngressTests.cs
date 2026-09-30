using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCalibrationIngressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshNativeRefreshBetweenReceiptAndObservationDoesNotSplitPull(bool interleaveRefresh)
    {
        var f = new Fixture();
        await f.Start();
        f.Pull(1, 1200, 8500, interleaveRefresh: interleaveRefresh);
        f.Pull(2, 4300, 6400, interleaveRefresh: interleaveRefresh);
        await f.Evaluate();
        Assert.False(f.Manager.Active, f.Manager.Status);
        var measured = f.Manager.Apply(f.Native().ShiftPerformance)!;
        Assert.NotNull(measured.Profile);
        Assert.Equal("measured-calibration", measured.Metadata!.TorqueModel);
        Assert.Equal(20000d / 3, measured.Gears[0].EstimatedTargetRpm!.Value, 2);
    }

    [Fact]
    public async Task PullContinuityUsesReceiptTimeWhenProcessingIsBrieflyDelayed()
    {
        var f = new Fixture();
        await f.Start();
        f.Pull(1, 1200, 8500, delayedProcessing: true);
        f.Pull(2, 4300, 6400, delayedProcessing: true);
        await f.Evaluate();
        Assert.False(f.Manager.Active, f.Manager.Status);
        Assert.NotNull(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
    }

    [Theory]
    [InlineData("stale_packet")]
    [InlineData("future_packet")]
    [InlineData("zero_packet")]
    [InlineData("profile_expires_before_observe")]
    [InlineData("stale_live")]
    [InlineData("future_live")]
    [InlineData("wrong_live_car")]
    [InlineData("gear_transition")]
    [InlineData("alternate_limiter")]
    public async Task InvalidIngressCannotCompleteCalibration(string invalid)
    {
        var f = new Fixture();
        await f.Start();
        f.Pull(1, 1200, 8500, invalid: invalid);
        f.Pull(2, 4300, 6400, invalid: invalid);
        await f.Evaluate();
        Assert.True(f.Manager.Active, f.Manager.Status);
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
    }

    private sealed class Fixture
    {
        private long _now = Stopwatch.Frequency * 10;
        private long _receipt = Stopwatch.Frequency * 10;
        private uint _game = 1000;
        private int _gear = 1;
        private float _rpm = 1200;
        private int _sequence;
        internal ShiftCalibrationManager Manager { get; }

        internal Fixture() => Manager = new(null, () => _now);
        private static long Milliseconds(int value) => Stopwatch.Frequency * value / 1000;

        internal async Task Start()
        {
            Publish(State(), Native());
            await Manager.PendingWork;
            Assert.True(Manager.Start());
        }

        internal void Pull(int gear, int first, int last, bool interleaveRefresh = false,
            bool delayedProcessing = false, string? invalid = null)
        {
            _gear = gear;
            for (var rpm = first; rpm <= last; rpm += 20)
            {
                _rpm = rpm;
                _sequence++;
                var delayed = delayedProcessing && _sequence % 25 == 0;
                var catchingUp = delayedProcessing && _sequence % 25 == 1 && _sequence > 1;
                var receiptStep = delayed ? 90 : catchingUp ? 30 : 10;
                _receipt = Math.Max(_receipt + Milliseconds(receiptStep), _now + Milliseconds(1));
                _now = _receipt + Milliseconds(delayed ? 25 : 1);
                _game += (uint)receiptStep;
                var state = State();
                var native = Native();
                var observed = interleaveRefresh && _sequence % 25 == 0 || delayed
                    ? _now : _receipt - Milliseconds(1);
                var data = native.ShiftPerformance! with
                {
                    ObservedTimestamp = observed,
                    LiveState = native.ShiftPerformance!.LiveState! with { ObservedTimestamp = observed }
                };
                data = invalid switch
                {
                    "profile_expires_before_observe" => data with { ObservedTimestamp = _now - Milliseconds(990) },
                    "stale_live" => data with { LiveState = data.LiveState! with { ObservedTimestamp = _now - Milliseconds(151) } },
                    "future_live" => data with { LiveState = data.LiveState! with { ObservedTimestamp = _now + Milliseconds(1) } },
                    "wrong_live_car" => data with { LiveState = data.LiveState! with { CarOrdinal = 101 } },
                    "gear_transition" => data with { LiveState = data.LiveState! with { RequestedGear = _gear + 1 } },
                    "alternate_limiter" => data with { LiveState = data.LiveState! with { AlternateLimiterBranch = true } },
                    _ => data
                };
                // The UI previously published fresh context; the receiver then observes its own packet.
                Publish(state, native with { ShiftPerformance = data });
                if (invalid == "profile_expires_before_observe") _now += Milliseconds(20);
                state = invalid switch
                {
                    "stale_packet" => state with { ReceivedTimestamp = _now - Milliseconds(151) },
                    "future_packet" => state with { ReceivedTimestamp = _now + Milliseconds(1) },
                    "zero_packet" => state with { ReceivedTimestamp = 0 },
                    _ => state
                };
                Manager.Observe(state);
            }
        }

        private void Publish(VehicleState state, NativeHudSnapshot native) =>
            Manager.Update(state, native, "build-A", true, _now);

        internal async Task Evaluate()
        {
            await Manager.PendingWork;
            _now += Stopwatch.Frequency;
            _receipt = _now;
            Publish(State(), Native());
            await Manager.PendingWork;
        }

        private VehicleState State() => new()
        {
            IsRaceOn = true,
            CarOrdinal = 100,
            NumCylinders = 8,
            GameTimestampMilliseconds = _game,
            ReceivedTimestamp = _receipt,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            Gear = (TransmissionGear)_gear,
            EngineRpm = _rpm,
            EngineMaximumRpm = 9000,
            TorqueNm = 1000 - _rpm / 10,
            PowerWatts = (1000 - _rpm / 10) * _rpm * MathF.PI / 30,
            GroundSpeedMetersPerSecond = 30,
            Accelerator = 255,
            Brake = 0,
            Steering = 0,
            Drivetrain = DrivetrainType.RearWheelDrive,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 2
        };

        internal NativeHudSnapshot Native()
        {
            var profile = new AccelerationShiftProfile([new(1000, 900), new(9000, 100)], [2, 1]);
            var live = new ShiftCueLiveState(100, _now, _rpm, _gear, _gear, _gear,
                false, false, 9000, 1, false);
            var data = new ShiftCuePerformance(100, _now, "car-tune-A", "Research", profile, [],
                new(8500, 9000, [], [], 900, 100000, 0, false, "configuration-only"), live, true);
            return new(true, 1, 100, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000,
                NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 100),
                NativeGameplayVisibility.Visible, _now, ShiftPerformance: data);
        }
    }
}
