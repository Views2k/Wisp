using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueLiveIntegrationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("other_car")]
    [InlineData("requested_gear")]
    [InlineData("current_gear")]
    [InlineData("alternate_mode")]
    public void InvalidLiveContextWithdrawsCue(string reason)
    {
        var f = new Fixture();
        f.Publish(6800);
        Assert.Equal(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        f.Tick();
        ShiftCueLiveState? live = f.Live(6800);
        live = reason switch
        {
            "missing" => null,
            "stale" => live with { ObservedTimestamp = f.Now - Stopwatch.Frequency },
            "future" => live with { ObservedTimestamp = f.Now + 1 },
            "other_car" => live with { CarOrdinal = 2 },
            "requested_gear" => live with { RequestedGear = 2 },
            "current_gear" => live with { CurrentGear = 2 },
            _ => live with { AlternateLimiterBranch = true }
        };
        f.Publish(6800, f.Native(live));
        Assert.False(f.Model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Fact]
    public void UncorroboratedNativeRpmCannotLatchTargetCrossing()
    {
        var f = new Fixture();
        f.Publish(6500, f.Native(f.Live(6750)));
        Assert.Equal(2, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.True(f.Model.NativeGaugeFrame.ShiftCue.IsVisible);
        Assert.Null(f.LastTrigger());
        f.Tick();
        f.Publish(6800);
        Assert.Equal(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal("IngressCrossing", f.LastTrigger());
    }

    [Fact]
    public void ConditionalLimiterBelowConfiguredApproachLatchesThroughBounce()
    {
        var f = new Fixture();
        f.Publish(5500, f.Native(f.Live(5500) with
        { LimiterActive = true, SecondaryLimiterActive = true, SecondaryBoundaryRpm = 5550 }));
        Assert.Equal(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Contains("Limiter activity observed", f.Model.ShiftCueStatus);
        Assert.Equal("NativeLimiter", f.LastTrigger());
        for (var i = 0; i < 3; i++)
        {
            f.Tick();
            f.Publish(5300);
            Assert.Equal(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        }
        f.Tick();
        f.Publish(4500);
        Assert.NotEqual(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
    }

    [Fact]
    public void InactiveStoredSecondaryBoundaryIsNotAShiftTarget()
    {
        var f = new Fixture();
        f.Publish(5500, f.Native(f.Live(5500) with { SecondaryBoundaryRpm = 4000 }));
        Assert.Equal(0, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal(20000d / 3, f.Model.NativeGaugeFrame.ShiftCue.TargetRpm, 6);
    }

    [Fact]
    public void NativeOnlyTransitionClearsCueBeforeNextUdpPacket()
    {
        var f = new Fixture();
        f.Publish(6800);
        f.Tick();
        Assert.True(f.Model.UpdateNativeHudSnapshot(f.Native(f.Live(6800) with { RequestedGear = 2 })));
        Assert.False(f.Model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Fact]
    public void MeasuredCadenceProjectsOneUpdateAndRecordsReason()
    {
        var f = new Fixture();
        f.Publish(6300);
        f.Tick();
        f.Publish(6450);
        f.Tick();
        f.Publish(6600);
        Assert.Equal(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal("MeasuredCadence", f.LastTrigger());
    }

    [Fact]
    public void LiftObservedOnlyAtIngressClearsNativeLimiterLatch()
    {
        var f = new Fixture();
        f.Publish(5500, f.Native(f.Live(5500) with { LimiterActive = true }));
        f.Tick();
        f.Model.ObserveShiftCueTelemetry(f.State(5400) with { Accelerator = 0 });
        f.Tick();
        f.Publish(5300);
        Assert.NotEqual(3, f.Model.NativeGaugeFrame.ShiftCue.Stage);
    }

    [Fact]
    public void ExportIncludesLiveStateAndAppliedInertiaFactors()
    {
        var f = new Fixture();
        f.Publish(6500);
        using var export = JsonDocument.Parse(f.Model.ExportShiftTestData());
        Assert.Equal(1, export.RootElement.GetProperty("samples")[0].GetProperty("LiveState").GetProperty("CurrentGear").GetInt32());
        Assert.Equal(3, export.RootElement.GetProperty("profiles")[0].GetProperty("gearAccelerationFactors").GetArrayLength());
    }

    [Fact]
    public void ExportDistinguishesRawCueAgeFromPresentationAge()
    {
        var f = new Fixture();
        var state = f.State(6500) with { ReceivedTimestamp = f.Now - Stopwatch.Frequency * 5 / 1000 };
        f.Model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            f.Native(f.Live(6500)), default, TimeSpan.FromMilliseconds(75), SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: false, updateGForce: false, rawShiftState: state);
        using var export = JsonDocument.Parse(f.Model.ExportShiftTestData());
        var sample = export.RootElement.GetProperty("samples")[0];
        Assert.Equal(f.Now, sample.GetProperty("EvaluationTimestamp").GetInt64());
        Assert.Equal(5, sample.GetProperty("RawPacketAgeMilliseconds").GetDouble(), 5);
        Assert.Equal(75, sample.GetProperty("PacketAgeMilliseconds").GetDouble());
    }

    private sealed class Fixture
    {
        internal long Now = Stopwatch.GetTimestamp();
        private uint _gameTime = 1000;
        internal DiagnosticsViewModel Model { get; }
        private readonly AccelerationShiftProfile _profile = new(
            [new(0, 1000), new(9000, 100)], [2, 1, .5]);

        internal Fixture() => Model = new(new AppSettings { AccelerationShiftCueEnabled = true }, () => Now);
        internal void Tick() { Now += Stopwatch.Frequency * 16 / 1000; _gameTime += 16; }
        internal ShiftCueLiveState Live(float rpm) => new(1, Now, rpm, 1, 1, 1, false, false, 0, 1, false);
        internal NativeHudSnapshot Native(ShiftCueLiveState? live) => new(true, 1, 1,
            NativeAssistProviderStatus.Ready, ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000,
            NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 1) with { Available = true },
            NativeGameplayVisibility.Visible, Now, ShiftPerformance: new(1, Now, "fixture", "Research", _profile,
                Enumerable.Range(1, 3).Select(g => AccelerationShiftSolver.Solve(_profile, g, 2000, 8500)).ToArray(),
                LiveState: live, RequiresLiveState: true));
        internal VehicleState State(float rpm) => new()
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = _gameTime,
            ReceivedTimestamp = Now,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            CarOrdinal = 1,
            NumCylinders = 8,
            GroundSpeedMetersPerSecond = 30,
            EngineRpm = rpm,
            EngineMaximumRpm = 9000,
            Gear = TransmissionGear.First,
            Accelerator = 255,
            Brake = 0,
            Drivetrain = DrivetrainType.RearWheelDrive,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = default,
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 2,
            Steering = 0
        };
        internal void Publish(float rpm, NativeHudSnapshot? native = null)
        {
            var state = State(rpm);
            Model.ObserveShiftCueTelemetry(state);
            Model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
                native ?? Native(Live(rpm)), default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
                refreshDiagnostics: false, updateGForce: false);
        }
        internal string? LastTrigger()
        {
            using var export = JsonDocument.Parse(Model.ExportShiftTestData());
            var samples = export.RootElement.GetProperty("samples");
            return samples[samples.GetArrayLength() - 1].GetProperty("RedTrigger").GetString();
        }
    }
}
