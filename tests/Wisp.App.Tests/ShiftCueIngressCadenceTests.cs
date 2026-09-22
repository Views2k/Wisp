using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueIngressCadenceTests
{
    private const double RecordedTarget = 10749.99473709529;
    private const string RecordedFingerprint = "A23D3CFEA580E689267B43C6619D4B7C8058EF3759F7AF62D67B8CDB2350E3A8";

    // Test9 journal sequences 50955, 50958, 50962, 50963, 50966, 50970.
    // Sequence 50962 was received but omitted by latest-state UI publication.
    private static readonly (long Received, uint GameTime, float Rpm)[] RecordedPackets =
    [
        (25820311107, 2582000, 10582.558f),
        (25820392346, 2582015, 10649.599f),
        (25820489597, 2582015, 10699.827f),
        (25820594487, 2582031, 10722.218f),
        (25820657941, 2582031, 10741.272f),
        (25820756480, 2582046, 10568.222f)
    ];

    [Fact]
    public void RecordedSkippedUiPacketDoesNotDestroyRawCadenceBeforeLimiterCut()
    {
        var tracker = new ShiftCueCrossingTracker(10_000_000);
        var first = RecordedState(0);
        var epoch = tracker.Arm(RecordedFingerprint, 4210, 1, RecordedTarget,
            first.ReceivedTimestamp!.Value, first.ReceivedTimestamp.Value + 1_000_000,
            first.ReceivedTimestamp.Value, initialState: first);
        var oldUiCadence = new ShiftCueCadencePredictor(10_000_000);
        oldUiCadence.ObserveAndPredict(first, first.ReceivedTimestamp.Value, RecordedTarget);
        tracker.Observe(RecordedState(1));
        Assert.False(oldUiCadence.ObserveAndPredict(RecordedState(1), 25820395621, RecordedTarget));
        tracker.Observe(RecordedState(2));
        tracker.Observe(RecordedState(3));
        Assert.False(oldUiCadence.ObserveAndPredict(RecordedState(3), 25820624634, RecordedTarget));
        Assert.False(tracker.TryPredict(epoch, 25820624634, out _));
        tracker.Observe(RecordedState(4));
        Assert.False(oldUiCadence.ObserveAndPredict(RecordedState(4), 25820694126, RecordedTarget));

        Assert.True(tracker.TryPredict(epoch, 25820694126, out var received));
        Assert.Equal(25820657941, received);
        Assert.False(tracker.TryConsume(epoch, 25820694126, out _));
        Assert.Equal(6.2354, (RecordedPackets[5].Received - 25820694126) / 10_000d, 4);

        tracker.Observe(RecordedState(5) with { TorqueNm = -151.24188f });
        Assert.False(tracker.TryPredict(epoch, 25820758190, out _));
        Assert.False(tracker.TryConsume(epoch, 25820758190, out _));
    }

    [Fact]
    public void RecordedUiSelectionNowRequestsRedBeforeNativeLimiterActivity()
    {
        long now = At(RecordedPackets[0].Received);
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        // Target calculation is independently tested. Supply this capture's target
        // while exercising the real view model, ingress watch and cue diagnostics.
        var profile = new AccelerationShiftProfile([new(0, 1000), new(12000, 1000)], [2, 1],
            verifiedOperatingCeilingRpm: RecordedTarget);
        var gears = new[] { AccelerationShiftSolver.Solve(profile, 1, 2000, RecordedTarget) };
        void Publish(int index, long evaluation, double nativeRpm, long nativeObserved)
        {
            now = At(evaluation);
            var state = RecordedState(index) with { ReceivedTimestamp = At(RecordedPackets[index].Received) };
            var live = new ShiftCueLiveState(4210, At(nativeObserved), nativeRpm, 1, 1, 11, false, false, 0, 1, false);
            var native = new NativeHudSnapshot(true, 1, 4210, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(10500 * Math.PI / 30), 11000,
                NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 4210) with { Available = true },
                NativeGameplayVisibility.Visible, now,
                ShiftPerformance: new(4210, now, RecordedFingerprint, "Research", profile, gears,
                    LiveState: live, RequiresLiveState: true));
            model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false), native, default,
                TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false,
                rawShiftState: state);
        }
        void Observe(int index) => model.ObserveShiftCueTelemetry(RecordedState(index) with
        { ReceivedTimestamp = At(RecordedPackets[index].Received) });

        Publish(0, RecordedPackets[0].Received, 10509.139608946434, 25820238276);
        Observe(1);
        Publish(1, 25820395621, 10509.139608946434, 25820238276);
        Observe(2);
        Observe(3);
        Publish(3, 25820624634, 10722.217614495656, 25820576222);
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
        Observe(4);
        Publish(4, 25820694126, 10722.217614495656, 25820576222);
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
        Assert.Equal(RecordedTarget, model.NativeGaugeFrame.ShiftCue.TargetRpm);
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var rows = export.RootElement.GetProperty("samples");
        var last = rows[rows.GetArrayLength() - 1];
        Assert.Equal("MeasuredCadence", last.GetProperty("RedTrigger").GetString());
        Assert.Equal(At(RecordedPackets[4].Received), last.GetProperty("CrossingObservedTimestamp").GetInt64());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RecordedNativeRpmExcursionCannotLatchBeforeCorroboratedCrossing(int gear)
    {
        var target = gear == 2 ? 9466.549266976039 : 9450.275394790677;
        // Exact Test9 selected-state observations surrounding native-only red
        // onsets 8879 and 10776. The final point is each first raw crossing.
        (long Received, long Evaluated, uint GameTime, float Rpm, long NativeObserved, double NativeRpm)[] rows = gear == 2
            ? [
                (24708844216, 24708845469, 2470859, 9189.328f, 24708805710, 9173.613013161355),
                (24708936365, 24708937815, 2470859, 9205.587f, 24708805710, 9173.613013161355),
                (24709020561, 24709024572, 2470875, 9222.15f, 24708980184, 9548.40775022973),
                (24709194437, 24709197241, 2470890, 9257.263f, 24709150243, 9257.263195976877),
                (24710244880, 24710245880, 2470984, 9466.954f, 24710244880, 9466.954)
            ]
            : [
                (24755615198, 24755616519, 2475531, 9338.63f, 24755568363, 9332.498294313538),
                (24755694065, 24755697056, 2475546, 9344.92f, 24755568363, 9332.498294313538),
                (24755793142, 24755795427, 2475546, 9351.346f, 24755737425, 9463.790631193788),
                (24755939339, 24755953706, 2475562, 9364.173f, 24755924446, 9364.172886778122),
                (24757293546, 24757294546, 2475703, 9453.727f, 24757293546, 9453.727)
            ];
        long now = 0;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        var profile = new AccelerationShiftProfile([new(0, 1000), new(12000, 1000)], [5, 4, 3, 2, 1],
            verifiedOperatingCeilingRpm: target);
        var gears = Enumerable.Range(1, 5).Select(g => AccelerationShiftSolver.Solve(profile, g, 2000, target)).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var state = State(At(row.Received), row.Rpm) with
            { GameTimestampMilliseconds = row.GameTime, CarOrdinal = 3289, Gear = (TransmissionGear)gear };
            model.ObserveShiftCueTelemetry(state);
            now = At(row.Evaluated);
            var live = new ShiftCueLiveState(3289, At(row.NativeObserved), row.NativeRpm, gear, gear, 11,
                false, false, 0, 1, false);
            var native = new NativeHudSnapshot(true, 1, 3289, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(9500 * Math.PI / 30), 10000,
                NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 3289) with { Available = true },
                NativeGameplayVisibility.Visible, now,
                ShiftPerformance: new(3289, now, "recorded-3289", "Research", profile, gears,
                    LiveState: live, RequiresLiveState: true));
            model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false), native, default,
                TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false,
                rawShiftState: state);
            Assert.Equal(i == rows.Length - 1 ? 3 : 2, model.NativeGaugeFrame.ShiftCue.Stage);
            Assert.Equal(target, model.NativeGaugeFrame.ShiftCue.TargetRpm);
        }
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var samples = export.RootElement.GetProperty("samples");
        Assert.Equal("IngressCrossing", samples[samples.GetArrayLength() - 1].GetProperty("RedTrigger").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("brake")]
    [InlineData("lift")]
    [InlineData("partial_lift")]
    [InlineData("gear")]
    [InlineData("car")]
    [InlineData("race_off")]
    [InlineData("falling")]
    [InlineData("duplicate")]
    [InlineData("game_rollback")]
    public void ContraryIngressReplacesPredictionBeforeNextUiQuery(string reason)
    {
        var tracker = PredictingTracker(out var epoch);
        var next = State(1060, 6990);
        VehicleState? contrary = reason switch
        {
            "null" => null,
            "brake" => next with { Brake = 2 },
            "lift" => next with { Accelerator = 0 },
            "partial_lift" => next with { Accelerator = 251 },
            "gear" => next with { Gear = TransmissionGear.Third },
            "car" => next with { CarOrdinal = 1 },
            "race_off" => next with { IsRaceOn = false },
            "falling" => next with { EngineRpm = 6970 },
            "duplicate" => State(1040, 6980),
            _ => next with { GameTimestampMilliseconds = 1 }
        };
        tracker.Observe(contrary);
        Assert.False(tracker.TryPredict(epoch, 1061, out _));
        Assert.False(tracker.TryConsume(epoch, 1061, out _));
    }

    [Fact]
    public void PredictionQueriesCannotExtendOneMeasuredIntervalOrReadFutureState()
    {
        var tracker = PredictingTracker(out var epoch);
        Assert.False(tracker.TryPredict(epoch, 1039, out _));
        Assert.True(tracker.TryPredict(epoch, 1040, out var received));
        Assert.Equal(1040, received);
        Assert.True(tracker.TryPredict(epoch, 1050, out _));
        Assert.True(tracker.TryPredict(epoch, 1060, out _));
        Assert.False(tracker.TryPredict(epoch, 1061, out _));
        Assert.False(tracker.TryPredict(epoch, 1190, out _));
        Assert.False(tracker.TryConsume(epoch, 1050, out _));
    }

    [Fact]
    public void SameWatchRefreshCannotFeedCoalescedUiStateBackIntoRawHistory()
    {
        var tracker = PredictingTracker(out var epoch);
        Assert.Equal(epoch, tracker.Arm("timing", 4210, 1, 7000, 1040, 2000, 1045,
            initialState: State(1040, 6980)));
        Assert.True(tracker.TryPredict(epoch, 1045, out var received));
        Assert.Equal(1040, received);
        var changed = tracker.Arm("new-tune", 4210, 1, 7000, 1040, 2000, 1046,
            initialState: State(1040, 6980));
        Assert.NotEqual(epoch, changed);
        Assert.False(tracker.TryPredict(epoch, 1046, out _));
        Assert.False(tracker.TryPredict(changed, 1046, out _));
    }

    [Fact]
    public void WatchExpiryAndExplicitResetClearRawPrediction()
    {
        var tracker = PredictingTracker(out var epoch);
        Assert.Equal(epoch, tracker.Arm("timing", 4210, 1, 7000, 1040, 1050, 1041));
        Assert.False(tracker.TryPredict(epoch, 1051, out _));
        tracker.Reset();
        Assert.False(tracker.TryPredict(epoch, 1045, out _));
    }

    private static ShiftCueCrossingTracker PredictingTracker(out long epoch)
    {
        var tracker = new ShiftCueCrossingTracker(1000);
        epoch = tracker.Arm("timing", 4210, 1, 7000, 1000, 2000, 1000, initialState: State(1000, 6900));
        tracker.Observe(State(1020, 6940));
        tracker.Observe(State(1040, 6980));
        Assert.True(tracker.TryPredict(epoch, 1040, out _));
        return tracker;
    }

    private static VehicleState RecordedState(int index) => State(RecordedPackets[index].Received, RecordedPackets[index].Rpm) with
    { GameTimestampMilliseconds = RecordedPackets[index].GameTime };
    private static long At(long timestamp) => timestamp * Stopwatch.Frequency / 10_000_000;
    private static VehicleState State(long received, float rpm) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = (uint)Math.Max(0, received - 1000),
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        ReceivedTimestamp = received,
        CarOrdinal = 4210,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        GroundSpeedMetersPerSecond = 30,
        EngineRpm = rpm,
        EngineMaximumRpm = 11000,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 255,
        Brake = 0
    };
}
