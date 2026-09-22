using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueFreshnessTests
{
    [Theory]
    [InlineData(TransmissionGear.Neutral)]
    [InlineData(TransmissionGear.Unknown)]
    public void FilteredGearDoesNotProduceGuidanceOrRelabelRawTrace(TransmissionGear actualGear)
    {
        var model = Armed();
        var raw = State(1500) with { Gear = actualGear };
        Update(model, raw with { Gear = TransmissionGear.First }, Snapshot(), raw);

        Assert.Equal(TransmissionGear.First, model.NativeGaugeFrame.Gear);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var samples = export.RootElement.GetProperty("samples");
        Assert.Equal((int)actualGear, samples[samples.GetArrayLength() - 1].GetProperty("Gear").GetInt32());
        AssertResumesWithFreshSupportedData(model);
    }

    [Fact]
    public void RawRaceOffSuppressesCueEvenIfDisplayStateIsRetained()
    {
        var model = Armed();
        Update(model, State(1500), Snapshot(), State(1500) with { IsRaceOn = false });
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        AssertResumesWithFreshSupportedData(model);
    }

    [Fact]
    public void ExistingUiTickClearsCueAtPacketTimeoutWithoutAnotherPacket()
    {
        var model = Armed();
        model.InvalidateStaleShiftCue(State(1250), Snapshot(), TimeSpan.FromMilliseconds(151));
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        AssertResumesWithFreshSupportedData(model);
    }

    [Fact]
    public void ExistingUiTickClearsNonadvancingGameTimeEvenWithFreshReceipt()
    {
        var model = Armed();
        var now = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        model.InvalidateStaleShiftCue(State(1250), Snapshot(now), TimeSpan.Zero, now);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1250), Snapshot());
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1250), Snapshot());
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        AssertResumesWithFreshSupportedData(model);
    }

    [Theory]
    [InlineData("race_off")]
    [InlineData("menu")]
    [InlineData("neutral")]
    [InlineData("different_gear")]
    [InlineData("no_state")]
    [InlineData("changed_tune")]
    [InlineData("stale_visibility")]
    public void TimerInvalidationPrecedesControllerEarlyReturns(string reason)
    {
        var model = Armed();
        VehicleState? state = State(1250);
        var native = Snapshot();
        switch (reason)
        {
            case "race_off": state = state with { IsRaceOn = false }; break;
            case "menu": native = native with { GameplayVisibility = NativeGameplayVisibility.Hidden }; break;
            case "neutral": state = state with { Gear = TransmissionGear.Neutral }; break;
            case "different_gear": state = state with { Gear = TransmissionGear.Second }; break;
            case "no_state": state = null; break;
            case "changed_tune": native = native with { ShiftPerformance = native.ShiftPerformance! with { Fingerprint = "tune-B" } }; break;
            case "stale_visibility": native = native with { VisibilityObservedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency }; break;
        }
        model.InvalidateStaleShiftCue(state, native, TimeSpan.Zero);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        AssertResumesWithFreshSupportedData(model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOnlyMenuOrStaleVisibilityRefreshClearsCue(bool stale)
    {
        var model = Armed();
        var native = Snapshot();
        native = stale
            ? native with { VisibilityObservedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency }
            : native with { GameplayVisibility = NativeGameplayVisibility.Hidden };
        Assert.True(model.UpdateNativeHudSnapshot(native));
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        AssertResumesWithFreshSupportedData(model);
    }

    private static DiagnosticsViewModel Armed()
    {
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true });
        Update(model, State(1000), Snapshot());
        Update(model, State(1250), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        return model;
    }

    private static void AssertResumesWithFreshSupportedData(DiagnosticsViewModel model)
    {
        Update(model, State(2000), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    private static void Update(DiagnosticsViewModel model, VehicleState display,
        NativeHudSnapshot native, VehicleState? raw = null) =>
        model.Update(display, new IndicatedSpeed(30, 67, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            native, default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: false, updateGForce: false, rawShiftState: raw);

    private static NativeHudSnapshot Snapshot(long? timestamp = null)
    {
        var now = timestamp ?? Stopwatch.GetTimestamp();
        var profile = new AccelerationShiftProfile([new(0, 1000), new(9000, 100)], [2, 1, .5]);
        var performance = new ShiftCuePerformance(1, now, "tune-A", "Research", profile,
            Enumerable.Range(1, 3).Select(g => AccelerationShiftSolver.Solve(profile, g, 2000, 8500)).ToArray());
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 1)
            with
        { Available = true, IsLCAvailable = true };
        return new(true, 1, 1, NativeAssistProviderStatus.Ready,
            ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000, assists,
            NativeGameplayVisibility.Visible, now, ShiftPerformance: performance);
    }

    private static VehicleState State(uint time) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = time,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        ReceivedTimestamp = Stopwatch.GetTimestamp(),
        CarOrdinal = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        GroundSpeedMetersPerSecond = 30,
        EngineRpm = 6800,
        EngineMaximumRpm = 9000,
        PowerWatts = 320 * 6800 * MathF.PI / 30,
        TorqueNm = 320,
        WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 2,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 255,
        Brake = 0
    };
}
