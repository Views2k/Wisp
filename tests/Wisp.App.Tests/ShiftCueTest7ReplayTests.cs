using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueTest7ReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void RecordedIncreasingThrottlePredictsBeforeTheNegativeOutputPacket()
    {
        var result = Replay("rising-throttle-first-limit");
        var red = Assert.Single(result.Onsets);
        Assert.Equal(16251, red.Sequence);
        Assert.Equal("MeasuredCadence", red.Trigger);
        Assert.Equal(9749.995009796077, red.Target, 6);
        Assert.True(red.Rpm < red.Target);
        Assert.Equal(2, red.RecordedStage);
        Assert.True(red.Time < result.FirstNegativeOutput);
        Assert.InRange((result.FirstNegativeOutput - red.Time) * 1000d / Stopwatch.Frequency, 12, 14);
    }

    [Fact]
    public void RecordedNativePriorityDoesNotInventAnEarlierTargetOrPredictionMargin()
    {
        var result = Replay("steady-native-priority");
        var red = Assert.Single(result.Onsets);
        Assert.Equal(6590, red.Sequence);
        Assert.Equal("NativeLimiter", red.Trigger);
        Assert.Equal(9749.995009796077, red.Target, 6);
        Assert.True(result.WithdrawnByDrivingGuard > 0);
    }

    [Fact]
    public void RecordedSteadyPredictionStillPrecedesTheCutAndWithdrawsOnCoasting()
    {
        var result = Replay("steady-prediction-and-lift");
        var red = Assert.Single(result.Onsets);
        Assert.Equal(29606, red.Sequence);
        Assert.Equal("MeasuredCadence", red.Trigger);
        Assert.Equal(9749.995009796077, red.Target, 6);
        Assert.True(red.Time < result.FirstNegativeOutput);
        Assert.True(result.WithdrawnByDrivingGuard > 0);
    }

    [Fact]
    public void RecordedPartialThrottleDecreasesDoNotProduceANewCadencePrediction()
    {
        var fixture = Fixture();
        var decreases = 0;
        foreach (var scenario in fixture.GetProperty("cases").EnumerateArray())
        {
            var predictor = new ShiftCueCadencePredictor();
            VehicleState? previous = null;
            foreach (var row in scenario.GetProperty("rows").EnumerateArray())
            {
                if (row.GetProperty("kind").GetString() != "cue_evaluation") continue;
                if (row.GetProperty("recordedDecision").GetString() is not ("FullLoadTarget" or "FullLoadLimitTarget"))
                {
                    predictor.Reset();
                    previous = null;
                    continue;
                }

                var state = State(row.GetProperty("state"));
                var predicted = predictor.ObserveAndPredict(state, At(row.GetProperty("evaluationTimestamp").GetInt64()),
                    scenario.GetProperty("expectedTargetRpm").GetDouble());
                if (previous is not null && state.ReceivedTimestamp > previous.ReceivedTimestamp &&
                    state.Accelerator < previous.Accelerator)
                {
                    Assert.False(predicted);
                    decreases++;
                }
                previous = state;
            }
        }
        Assert.Equal(2, decreases);
    }

    private static ReplayResult Replay(string name)
    {
        var fixture = Fixture();
        var data = fixture.GetProperty("profile");
        var metadata = data.GetProperty("metadata").Deserialize<ShiftCueMetadata>(JsonOptions)!;
        var profile = new AccelerationShiftProfile(
            data.GetProperty("samples").Deserialize<AccelerationShiftSample[]>(JsonOptions)!,
            data.GetProperty("forwardRatios").Deserialize<double[]>(JsonOptions)!,
            data.GetProperty("gearAccelerationFactors").Deserialize<double[]>(JsonOptions)!,
            metadata.ConfiguredOperatingCeilingRpm);
        var gears = data.GetProperty("gears").Deserialize<AccelerationShiftResult[]>(JsonOptions)!;
        var scenario = fixture.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        long now = 0;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        var onsets = new List<RedOnset>();
        var firstNegative = long.MaxValue;
        var previousStage = 0;
        var withdrawn = 0;
        foreach (var row in scenario.GetProperty("rows").EnumerateArray())
        {
            var state = State(row.GetProperty("state"));
            if (row.GetProperty("kind").GetString() == "telemetry")
            {
                now = At(row.GetProperty("timestamp").GetInt64());
                if (state.TorqueNm < 0) firstNegative = Math.Min(firstNegative, state.ReceivedTimestamp!.Value);
                model.ObserveShiftCueTelemetry(state);
                continue;
            }

            // Test7 records the actual cue clock, separately from the presentation packet age.
            now = At(row.GetProperty("evaluationTimestamp").GetInt64());
            Assert.Equal(row.GetProperty("rawPacketAgeMs").GetDouble(),
                (now - state.ReceivedTimestamp!.Value) * 1000d / Stopwatch.Frequency, 6);
            var live = row.GetProperty("live").Deserialize<ShiftCueLiveState>(JsonOptions);
            if (live is not null) live = live with { ObservedTimestamp = At(live.ObservedTimestamp) };
            var performance = new ShiftCuePerformance(state.CarOrdinal,
                At(row.GetProperty("profileObserved").GetInt64()), data.GetProperty("fingerprint").GetString()!,
                "Research", profile, gears, metadata, live, RequiresLiveState: true);
            var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, state.CarOrdinal)
                with
            { Available = true, IsLCAvailable = true, IsLCOn = row.GetProperty("launchControl").GetBoolean() };
            var native = new NativeHudSnapshot(true, 1, state.CarOrdinal, NativeAssistProviderStatus.Ready,
                ExactRedlineResult.Exact(metadata.ExactRedlineRpm * Math.PI / 30), 11000, assists,
                NativeGameplayVisibility.Visible, At(row.GetProperty("visibilityObserved").GetInt64()), ShiftPerformance: performance);
            model.Update(state, new IndicatedSpeed(state.GroundSpeedMetersPerSecond, 0, true, false, "Rear"),
                new CalibrationResult(null, .3, .2, 0, true, string.Empty, false), native, default,
                TimeSpan.FromMilliseconds(row.GetProperty("presentationAgeMs").GetDouble()), SpeedUnit.MilesPerHour, 60,
                refreshDiagnostics: false, updateGForce: false, rawShiftState: state);
            var cue = model.NativeGaugeFrame.ShiftCue;
            if (state.Accelerator == 0 || state.Brake > 1 || (int)state.Gear < 1 ||
                live is not null && (live.CurrentGear != (int)state.Gear || live.RequestedGear != (int)state.Gear))
            {
                Assert.False(cue.Enabled);
                withdrawn++;
            }
            if (cue.Stage == 3 && previousStage != 3)
            {
                Assert.True(cue.IsVisible);
                using var export = JsonDocument.Parse(model.ExportShiftTestData());
                var samples = export.RootElement.GetProperty("samples");
                var trigger = samples[samples.GetArrayLength() - 1].GetProperty("RedTrigger").GetString();
                onsets.Add(new(row.GetProperty("sequence").GetInt32(), now, state.EngineRpm, cue.TargetRpm,
                    row.GetProperty("recordedStage").GetInt32(), trigger));
            }
            previousStage = cue.Stage;
        }
        return new(onsets, firstNegative, withdrawn);
    }

    private static long At(long recordedQpc) => recordedQpc * Stopwatch.Frequency / 10_000_000;

    private static VehicleState State(JsonElement row) => new()
    {
        IsRaceOn = row.GetProperty("isRaceOn").GetBoolean(),
        GameTimestampMilliseconds = row.GetProperty("gameTimestampMilliseconds").GetUInt32(),
        ReceivedTimestamp = At(row.GetProperty("receivedTimestamp").GetInt64()),
        CarOrdinal = row.GetProperty("carOrdinal").GetInt32(),
        NumCylinders = row.GetProperty("numCylinders").GetInt32(),
        GroundSpeedMetersPerSecond = row.GetProperty("groundSpeedMetersPerSecond").GetSingle(),
        EngineRpm = row.GetProperty("engineRpm").GetSingle(),
        EngineMaximumRpm = row.GetProperty("engineMaximumRpm").GetSingle(),
        Gear = (TransmissionGear)row.GetProperty("gear").GetInt32(),
        Accelerator = row.GetProperty("accelerator").GetByte(),
        Brake = row.GetProperty("brake").GetByte(),
        PowerWatts = row.GetProperty("powerWatts").GetSingle(),
        TorqueNm = row.GetProperty("torqueNm").GetSingle(),
        ReceivedAtUtc = DateTimeOffset.UnixEpoch,
        Drivetrain = DrivetrainType.RearWheelDrive,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Steering = 0
    };

    private static JsonElement Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", "test7-throttle-ramp-3608.json")));
        return document.RootElement.Clone();
    }

    private sealed record RedOnset(int Sequence, long Time, double Rpm, double Target, int RecordedStage, string? Trigger);
    private sealed record ReplayResult(List<RedOnset> Onsets, long FirstNegativeOutput, int WithdrawnByDrivingGuard);
}
