using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCalibrationManagerTests
{
    [Fact]
    public async Task ProductionPathNeverUsesUncalibratedNativeCurve()
    {
        var f = new Fixture();
        await f.Initialize();
        Assert.True(f.Manager.CanStart);
        var withheld = f.Manager.Apply(f.Native().ShiftPerformance)!;
        Assert.Equal("CalibrationRequired", withheld.Status);
        Assert.Null(withheld.Profile);
        Assert.Empty(withheld.Gears);
        Assert.True(f.Manager.Start());
        Assert.False(f.Manager.CanStart);
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
    }

    [Fact]
    public async Task FullPullAndIndependentGearConfirmationEnableMeasuredTarget()
    {
        var f = new Fixture();
        await f.Initialize();
        Assert.True(f.Manager.Start());
        f.Sweep(1, 1200, 8500);
        await f.Manager.PendingWork;
        Assert.True(f.Manager.Active);
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
        f.Sweep(2, 4300, 6400);
        await f.Evaluate();
        Assert.False(f.Manager.Active, f.Manager.Status);
        var measured = f.Manager.Apply(f.Native().ShiftPerformance)!;
        Assert.Equal("measured-calibration", measured.Metadata!.TorqueModel);
        Assert.Equal(20000d / 3, measured.Gears[0].EstimatedTargetRpm!.Value, 2);
        Assert.Null(measured.Profile!.VerifiedOperatingCeilingRpm);
        Assert.Contains("saved", f.Manager.Status);

        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => f.Now);
        model.InitializeShiftCalibration(f.Manager);
        f.Draw(model, 5800);
        Assert.Equal(1, model.NativeGaugeFrame.ShiftCue.Stage);
        f.Draw(model, 6400);
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
        f.Draw(model, 6800);
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal(20000d / 3, model.NativeGaugeFrame.ShiftCue.TargetRpm, 2);
        Assert.Contains("Calibrated", model.ShiftCueStatus);
        Assert.True(model.CanStartShiftCalibration);
        model.StartShiftCalibration();
        Assert.True(model.CanCancelShiftCalibration);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);

        f.Key = "different-tune";
        f.Publish();
        await f.Manager.PendingWork;
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
        Assert.Contains("Ready to calibrate", f.Manager.Status);
    }

    [Fact]
    public async Task SuspensionInvalidatesActiveWorkAndCanResumeWithoutReusingPartialPull()
    {
        var f = new Fixture();
        await f.Initialize();
        Assert.True(f.Manager.Start());
        f.Sweep(1, 1200, 8500);
        await f.Manager.Suspend();
        Assert.False(f.Manager.Active);
        Assert.False(f.Manager.CanStart);
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
        f.Publish();
        await f.Manager.PendingWork;
        Assert.True(f.Manager.CanStart);
        Assert.True(f.Manager.Start());
        f.Sweep(2, 4300, 6400);
        await f.Evaluate();
        Assert.True(f.Manager.Active);
        Assert.Null(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("electric")]
    [InlineData("wrong_car")]
    public async Task InvalidContextCannotStartCalibration(string reason)
    {
        var f = new Fixture();
        await f.Initialize();
        var state = f.State();
        var native = f.Native();
        if (reason == "hidden") native = native with { GameplayVisibility = NativeGameplayVisibility.Hidden };
        if (reason == "stale") state = state with { ReceivedTimestamp = f.Now - Stopwatch.Frequency };
        if (reason == "electric") state = state with { NumCylinders = 0 };
        if (reason == "wrong_car") state = state with { CarOrdinal = 101 };
        f.Manager.Update(state, native, "build-A", reason != "disabled", f.Now);
        Assert.False(f.Manager.Start());
        Assert.Null(f.Manager.Apply(native.ShiftPerformance)!.Profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningWispCanArmCachedCarWhileCollectionWaitsForFreshDriving(bool stoppedTelemetry)
    {
        var f = new Fixture();
        await f.Initialize();
        var state = f.State() with { IsRaceOn = false };
        if (stoppedTelemetry) state = state with
        {
            ReceivedTimestamp = f.Now - Stopwatch.Frequency,
            CarOrdinal = 0,
            NumCylinders = 0,
            Gear = (TransmissionGear)(-1)
        };
        var native = f.Native() with { GameplayVisibility = NativeGameplayVisibility.Hidden };
        f.Manager.Update(state, native, "build-A", true, f.Now);
        Assert.True(f.Manager.CanStart);
        Assert.True(f.Manager.Start());
        for (var i = 0; i < 100; i++) f.Manager.Observe(state);
        Assert.Null(f.Manager.Apply(native.ShiftPerformance)!.Profile);
        f.Sweep(1, 1200, 8500);
        f.Sweep(2, 4300, 6400);
        await f.Evaluate();
        Assert.False(f.Manager.Active, f.Manager.Status);
        Assert.NotNull(f.Manager.Apply(f.Native().ShiftPerformance)!.Profile);
    }

    private sealed class Fixture
    {
        internal long Now = Stopwatch.Frequency * 10;
        internal string Key = "car-tune-A";
        private uint _game = 1000;
        private int _gear = 1;
        private float _rpm = 1200;
        internal ShiftCalibrationManager Manager { get; }
        internal Fixture() => Manager = new(null, () => Now);

        internal async Task Initialize()
        {
            Publish();
            await Manager.PendingWork;
        }

        internal void Publish() => Manager.Update(State(), Native(), "build-A", true, Now);

        internal void Draw(DiagnosticsViewModel model, float rpm)
        {
            Now += Stopwatch.Frequency / 100;
            _game += 10;
            _gear = 1;
            _rpm = rpm;
            var state = State();
            var native = Native();
            model.RefreshShiftCalibration(state, native, "build-A", Now);
            model.ObserveShiftCueTelemetry(state);
            model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
                native, default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
                refreshDiagnostics: false, updateGForce: false);
        }

        internal void Sweep(int gear, int first, int last)
        {
            _gear = gear;
            for (var rpm = first; rpm <= last; rpm += 20)
            {
                Now += Stopwatch.Frequency / 100;
                _game += 10;
                _rpm = rpm;
                Publish();
                Manager.Observe(State());
            }
        }

        internal async Task Evaluate()
        {
            await Manager.PendingWork;
            Now += Stopwatch.Frequency;
            Publish();
            await Manager.PendingWork;
        }

        internal VehicleState State() => new()
        {
            IsRaceOn = true,
            CarOrdinal = 100,
            NumCylinders = 8,
            GameTimestampMilliseconds = _game,
            ReceivedTimestamp = Now,
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
            var live = new ShiftCueLiveState(100, Now, _rpm, _gear, _gear, _gear,
                false, false, 9000, 1, false);
            var data = new ShiftCuePerformance(100, Now, Key, "Research", profile, [],
                new(8500, 9000, [], [], 900, 100000, 0, false, "configuration-only"), live, true);
            return new(true, 1, 100, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000,
                NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 100),
                NativeGameplayVisibility.Visible, Now, ShiftPerformance: data);
        }
    }
}
