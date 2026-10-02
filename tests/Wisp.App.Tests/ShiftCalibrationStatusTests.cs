using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCalibrationStatusTests
{
    [Fact]
    public async Task UncalibratedIdleLabelDoesNotAlternateOnFreshNativeRefreshes()
    {
        var f = new Fixture();
        await f.Initialize();
        f.Draw();
        var expected = f.Model.ShiftCueStatus;
        Assert.StartsWith("Calibrate this car", expected);
        var changes = new List<string>();
        f.Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiagnosticsViewModel.ShiftCueStatus))
                changes.Add(f.Model.ShiftCueStatus);
        };
        for (var i = 0; i < 10; i++)
        {
            f.Model.UpdateNativeHudSnapshot(f.Native());
            f.Draw();
        }
        Assert.Empty(changes);
        Assert.False(f.Model.NativeGaugeFrame.ShiftCue.Enabled);
        f.Model.AccelerationShiftCueEnabled = false;
        Assert.Equal("Off", f.Model.ShiftCueStatus);
        await f.Manager.PendingWork;
    }

    [Fact]
    public async Task ActiveCalibrationKeepsOneGuidanceLabelAcrossDrivingNativeAndWaitingUpdates()
    {
        var f = new Fixture();
        await f.Initialize();
        f.Model.StartShiftCalibration();
        Assert.True(f.Model.CanCancelShiftCalibration);
        var activeStatus = f.Model.ShiftCueStatus;
        Assert.StartsWith("Calibrating", activeStatus);
        var statusChanges = new List<string>();
        f.Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiagnosticsViewModel.ShiftCueStatus))
                statusChanges.Add(f.Model.ShiftCueStatus);
        };

        for (var i = 0; i < 5; i++)
        {
            f.Draw();
            await f.Manager.PendingWork;
            Assert.Equal(activeStatus, f.Model.ShiftCueStatus);
            f.Model.UpdateNativeHudSnapshot(f.Native());
            Assert.Equal(activeStatus, f.Model.ShiftCueStatus);

            var hidden = f.Native() with { GameplayVisibility = NativeGameplayVisibility.Hidden };
            f.Model.RefreshShiftCalibration(f.State(), hidden, "build-A", f.Now);
            Assert.Contains("paused", f.Model.ShiftCalibrationStatus);
            f.Model.UpdateNativeHudSnapshot(hidden);
            f.Model.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 60);
            Assert.Equal(activeStatus, f.Model.ShiftCueStatus);
            Assert.True(f.Model.CanCancelShiftCalibration);
            Assert.False(f.Model.NativeGaugeFrame.ShiftCue.Enabled);
        }

        Assert.Empty(statusChanges);
        await f.Model.SuspendShiftCalibration();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("suspend")]
    [InlineData("disable")]
    [InlineData("manager-end")]
    public async Task EndingCalibrationClearsItsGuidanceLabelImmediately(string transition)
    {
        var f = new Fixture();
        await f.Initialize();
        f.Model.StartShiftCalibration();
        Assert.StartsWith("Calibrating", f.Model.ShiftCueStatus);

        switch (transition)
        {
            case "cancel": f.Model.CancelShiftCalibration(); break;
            case "suspend": await f.Model.SuspendShiftCalibration(); break;
            case "disable": f.Model.AccelerationShiftCueEnabled = false; break;
            case "manager-end":
                f.Manager.Cancel();
                f.Model.RefreshShiftCalibration(f.State(), f.Native(), "build-A", f.Now);
                break;
        }

        Assert.False(f.Model.CanCancelShiftCalibration);
        Assert.False(f.Model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal(transition == "disable" ? "Off" : "Waiting for driving data", f.Model.ShiftCueStatus);
        await f.Manager.PendingWork;
    }

    [Fact]
    public void RejectedStartDoesNotShowActiveCalibration()
    {
        var f = new Fixture();
        f.Model.StartShiftCalibration();
        Assert.False(f.Model.CanCancelShiftCalibration);
        Assert.DoesNotContain("Calibrating", f.Model.ShiftCueStatus);
    }

    private sealed class Fixture
    {
        internal long Now = Stopwatch.Frequency * 10;
        private uint _gameTime = 1000;
        internal ShiftCalibrationManager Manager { get; }
        internal DiagnosticsViewModel Model { get; }

        internal Fixture()
        {
            Manager = new(null, () => Now);
            Model = new(new AppSettings { AccelerationShiftCueEnabled = true }, () => Now);
            Model.InitializeShiftCalibration(Manager);
        }

        internal async Task Initialize()
        {
            Model.RefreshShiftCalibration(State(), Native(), "build-A", Now);
            await Manager.PendingWork;
        }

        internal void Draw()
        {
            Now += Stopwatch.Frequency / 100;
            _gameTime += 10;
            var state = State();
            var native = Native();
            Model.RefreshShiftCalibration(state, native, "build-A", Now);
            Model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
                native, default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
                refreshDiagnostics: false, updateGForce: false);
        }

        internal VehicleState State() => new()
        {
            IsRaceOn = true,
            CarOrdinal = 100,
            NumCylinders = 8,
            GameTimestampMilliseconds = _gameTime,
            ReceivedTimestamp = Now,
            ReceivedAtUtc = DateTimeOffset.UnixEpoch,
            Gear = (TransmissionGear)1,
            EngineRpm = 3000,
            EngineMaximumRpm = 9000,
            GroundSpeedMetersPerSecond = 30,
            Drivetrain = DrivetrainType.RearWheelDrive,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            Steering = 0,
            Accelerator = 0,
            Brake = 0
        };

        internal NativeHudSnapshot Native()
        {
            var profile = new AccelerationShiftProfile([new(1000, 900), new(9000, 100)], [2, 1]);
            var live = new ShiftCueLiveState(100, Now, 3000, 1, 1, 1, false, false, 9000, 1, false);
            var data = new ShiftCuePerformance(100, Now, "status-test-car-tune", "Research", profile, [],
                new(8500, 9000, [], [], 900, 100000, 0, false, "configuration-only"), live, true);
            return new(true, 1, 100, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000,
                NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 100),
                NativeGameplayVisibility.Visible, Now, ShiftPerformance: data);
        }
    }
}
