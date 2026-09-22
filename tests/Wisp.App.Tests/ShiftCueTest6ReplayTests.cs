using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueTest6ReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void RecordedFirstGearPredictsBeforeItsFirstNegativeOutputPacket(int evaluationLeadMicroseconds)
    {
        var result = Replay("first-limit", evaluationLeadMicroseconds);
        var firstRed = Assert.Single(result.Onsets);
        Assert.Equal(12903, firstRed.Sequence);
        Assert.Equal("MeasuredCadence", firstRed.Trigger);
        Assert.True(firstRed.Time < result.FirstNegativeOutput);
        Assert.True(firstRed.Rpm < firstRed.Target);
        Assert.Equal(2, firstRed.RecordedStage);
    }

    [Theory]
    [InlineData("third-crossover", 9641.469073909571, 14390)]
    [InlineData("fourth-crossover", 9659.732111935258, 15748)]
    public void RecordedCrossoverKeepsItsCarSpecificTargetAndAdvancesTheCue(string name, double target, int sequence)
    {
        var firstRed = Assert.Single(Replay(name, 0).Onsets);
        Assert.Equal("MeasuredCadence", firstRed.Trigger);
        Assert.Equal(target, firstRed.Target, 6);
        Assert.Equal(sequence, firstRed.Sequence);
        Assert.True(firstRed.Rpm < firstRed.Target);
        Assert.Equal(2, firstRed.RecordedStage);
    }

    [Fact]
    public void RecordedSecondGearActivityDoesNotInventALowerConfiguredLimit()
    {
        var firstRed = Assert.Single(Replay("second-activity", 0).Onsets);
        Assert.Equal("NativeLimiter", firstRed.Trigger);
        Assert.Equal(9749.995009796077, firstRed.Target, 6);
    }

    private static ReplayResult Replay(string name, int evaluationLeadMicroseconds)
    {
        var fixture = Fixture();
        var modelData = fixture.GetProperty("profile");
        var metadata = modelData.GetProperty("metadata").Deserialize<ShiftCueMetadata>(JsonOptions)!;
        var profile = new AccelerationShiftProfile(
            modelData.GetProperty("samples").Deserialize<AccelerationShiftSample[]>(JsonOptions)!,
            modelData.GetProperty("forwardRatios").Deserialize<double[]>(JsonOptions)!,
            modelData.GetProperty("gearAccelerationFactors").Deserialize<double[]>(JsonOptions)!,
            metadata.ConfiguredOperatingCeilingRpm);
        var gears = modelData.GetProperty("gears").Deserialize<AccelerationShiftResult[]>(JsonOptions)!;
        var rows = fixture.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("rows").EnumerateArray();
        long now = 0;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        var onsets = new List<RedOnset>();
        var firstNegative = long.MaxValue;
        var previousStage = 0;
        foreach (var row in rows)
        {
            var state = State(row.GetProperty("state"));
            state = state with { ReceivedTimestamp = At(state.ReceivedTimestamp!.Value) };
            now = At(row.GetProperty("timestamp").GetInt64());
            if (row.GetProperty("kind").GetString() == "telemetry")
            {
                if (state.TorqueNm < 0) firstNegative = Math.Min(firstNegative, state.ReceivedTimestamp.Value);
                model.ObserveShiftCueTelemetry(state);
                continue;
            }

            // The journal is written just after evaluation. Bracket the first-
            // gear result by replaying up to 0.5 ms earlier, never before receipt.
            now = Math.Max(state.ReceivedTimestamp.Value, now - Stopwatch.Frequency * evaluationLeadMicroseconds / 1_000_000);
            var live = row.GetProperty("live").Deserialize<ShiftCueLiveState>(JsonOptions)!;
            live = live with { ObservedTimestamp = At(live.ObservedTimestamp) };
            var performance = new ShiftCuePerformance(state.CarOrdinal,
                At(row.GetProperty("profileObserved").GetInt64()), modelData.GetProperty("fingerprint").GetString()!,
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
            if (cue.Stage == 3 && previousStage != 3)
            {
                Assert.True(cue.IsVisible);
                onsets.Add(new(row.GetProperty("sequence").GetInt32(), now, state.EngineRpm, cue.TargetRpm,
                    row.GetProperty("recordedStage").GetInt32(), LastTrigger(model)));
            }
            previousStage = cue.Stage;
        }
        return new(onsets, firstNegative);
    }

    private static string? LastTrigger(DiagnosticsViewModel model)
    {
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var rows = export.RootElement.GetProperty("samples");
        return rows[rows.GetArrayLength() - 1].GetProperty("RedTrigger").GetString();
    }

    private static long At(long recordedQpc) => recordedQpc * Stopwatch.Frequency / 10_000_000;

    private static VehicleState State(JsonElement row) => new()
    {
        IsRaceOn = row.GetProperty("isRaceOn").GetBoolean(),
        GameTimestampMilliseconds = row.GetProperty("gameTimestampMilliseconds").GetUInt32(),
        ReceivedTimestamp = row.GetProperty("receivedTimestamp").GetInt64(),
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
        // Omitted channels do not participate in cue calculation or gating.
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
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", "test6-cadence-3608.json")));
        return document.RootElement.Clone();
    }

    private sealed record RedOnset(int Sequence, long Time, double Rpm, double Target, int RecordedStage, string? Trigger);
    private sealed record ReplayResult(List<RedOnset> Onsets, long FirstNegativeOutput);
}
