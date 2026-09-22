using System.Diagnostics;
using System.Text.Json;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueRecordedReplayTests
{
    [Fact]
    public void RecordedSecondGearLimitCrossingStaysRedThroughFollowingPowerCuts()
    {
        var fixture = Fixture("limit-1335-replay");
        var capture = Assert.Single(fixture.GetProperty("captures").EnumerateArray(),
            capture => capture.GetProperty("file").GetString() == "787b2.csv");
        var rows = capture.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(26, rows.Length);
        var performance = Performance();
        var target = performance.Gears[1];
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, target.Status);
        Assert.Equal(fixture.GetProperty("candidate_rpm").GetDouble(), target.EstimatedTargetRpm!.Value, 6);

        var firstReached = Array.FindIndex(rows, row => row.GetProperty("rpm").GetDouble() >= target.EstimatedTargetRpm);
        Assert.True(firstReached > 0);
        Assert.Equal(456, rows[firstReached].GetProperty("csv_row").GetInt32());
        var model = Model();
        var cutSamplesAfterTarget = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            Update(model, State(row), performance);
            var frame = model.NativeGaugeFrame;
            Assert.True(frame.ShiftCue.Enabled, model.ShiftCueStatus);
            Assert.Equal(target.EstimatedTargetRpm, frame.ShiftCue.TargetRpm);
            Assert.Equal(i < firstReached ? 2 : 3, frame.ShiftCue.Stage);
            if (i > firstReached && row.GetProperty("power_w").GetDouble() < 0)
            {
                cutSamplesAfterTarget++;
                Assert.True(row.GetProperty("torque_nm").GetDouble() < 0);
                Assert.True(frame.EngineRpm < target.EstimatedTargetRpm);
                Assert.Equal(3, frame.ShiftCue.Stage);
            }
            AssertRingCommand(frame);
        }
        Assert.True(cutSamplesAfterTarget >= 3);
    }

    [Fact]
    public void RecordedThirdGearCrossoverShowsRedBeforeTheFirstPowerCut()
    {
        var capture = Assert.Single(Fixture("limit-1335-replay").GetProperty("captures").EnumerateArray(),
            capture => capture.GetProperty("file").GetString() == "787b.csv");
        var rows = capture.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(43, rows.Length);
        var performance = Performance();
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, performance.Gears[2].Status);
        Assert.Equal(10209.418, performance.Gears[2].EstimatedTargetRpm!.Value, 2);
        var firstCut = Array.FindIndex(rows, row => row.GetProperty("power_w").GetDouble() < 0);
        var firstRed = -1;
        var model = Model();
        for (var i = 0; i < rows.Length; i++)
        {
            Update(model, State(rows[i]), performance);
            var frame = model.NativeGaugeFrame;
            Assert.True(frame.ShiftCue.Enabled, model.ShiftCueStatus);
            if (frame.ShiftCue.Stage == 3 && firstRed < 0) firstRed = i;
            if (firstRed >= 0) Assert.Equal(3, frame.ShiftCue.Stage);
            AssertRingCommand(frame);
        }
        Assert.True(firstRed >= 0 && firstRed < firstCut);
        Assert.Equal(997, rows[firstRed].GetProperty("csv_row").GetInt32());
        Assert.Equal(999, rows[firstCut].GetProperty("csv_row").GetInt32());
        Assert.True(rows[firstRed].GetProperty("power_w").GetDouble() > 0);
        Assert.True(rows[firstRed].GetProperty("rpm").GetDouble() < performance.Profile!.VerifiedOperatingCeilingRpm);
    }

    [Fact]
    public void RecordedCarMetadataSupportsAllFourUpshiftsAndKeepsTopGearOff()
    {
        var performance = Performance();
        Assert.Equal(5, performance.Gears.Count);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, performance.Gears[0].Status);
        Assert.Equal(AccelerationShiftStatus.VerifiedLimitBound, performance.Gears[1].Status);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, performance.Gears[2].Status);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, performance.Gears[3].Status);
        double[] expected = [10249.994873445685, 10249.994873445685, 10209.418, 10033.466];
        var row = Fixture("limit-1335-replay").GetProperty("captures")[0].GetProperty("rows")[0];
        var model = Model();
        for (var gear = 1; gear <= 4; gear++)
        {
            Assert.True(performance.Gears[gear - 1].HasEstimatedTarget);
            Assert.InRange(Math.Abs(performance.Gears[gear - 1].EstimatedTargetRpm!.Value - expected[gear - 1]), 0, .01);
            // Gear/RPM substitution here exercises otherwise unrecorded gears;
            // only the preceding two tests replay recorded driving samples.
            var state = State(row) with
            {
                Gear = (TransmissionGear)gear,
                EngineRpm = (float)Math.Ceiling(expected[gear - 1]),
                GameTimestampMilliseconds = (uint)(1000 + gear * 100)
            };
            Update(model, state, performance);
            Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
            AssertRingCommand(model.NativeGaugeFrame);
        }
        Update(model, State(row) with { Gear = TransmissionGear.Fifth, GameTimestampMilliseconds = 1500 }, performance);
        Assert.Equal(AccelerationShiftStatus.NoNextGear, performance.Gears[4].Status);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Contains("Top gear", model.ShiftCueStatus);
        Assert.DoesNotContain(AnalogHudScene.Build(model.NativeGaugeFrame, 280, 0, true, false, default),
            command => command.TextureId == ShiftCueArtwork.AnalogTextureId);
    }

    [Fact]
    public void RecordedTurboFourthGearCrossoverShowsRedBeforeTheFirstPowerCut()
    {
        var fixture = Fixture("limit-turbo-3289-replay");
        var rows = fixture.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(101, rows.Length);
        var performance = Performance("turbo-3289");
        var target = performance.Gears[3];
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, target.Status);
        Assert.Equal(9582.36218911425, target.EstimatedTargetRpm!.Value, 3);
        Assert.Equal(fixture.GetProperty("candidate_rpm").GetDouble(), performance.Profile!.VerifiedOperatingCeilingRpm!.Value, 6);
        var firstCut = Array.FindIndex(rows, row => row.GetProperty("power_w").GetDouble() < 0);
        var firstRed = -1;
        var model = Model();
        for (var i = 0; i < rows.Length; i++)
        {
            var state = State(rows[i], 3289) with { BoostPressurePsi = rows[i].GetProperty("boost_psi").GetSingle() };
            Update(model, state, performance);
            var frame = model.NativeGaugeFrame;
            Assert.True(frame.ShiftCue.Enabled, model.ShiftCueStatus);
            Assert.Equal(target.EstimatedTargetRpm, frame.ShiftCue.TargetRpm);
            if (frame.ShiftCue.Stage == 3 && firstRed < 0) firstRed = i;
            if (firstRed >= 0) Assert.Equal(3, frame.ShiftCue.Stage);
            if (frame.ShiftCue.Stage > 0) AssertRingCommand(frame);
        }
        Assert.True(firstRed >= 0 && firstRed < firstCut);
        Assert.Equal(2735, rows[firstRed].GetProperty("csv_row").GetInt32());
        Assert.Equal(2757, rows[firstCut].GetProperty("csv_row").GetInt32());
        Assert.True(rows[firstRed].GetProperty("power_w").GetDouble() > 0);
        Assert.True(rows[firstRed].GetProperty("rpm").GetDouble() < performance.Profile.VerifiedOperatingCeilingRpm);
    }

    private static DiagnosticsViewModel Model() => new(new AppSettings { AccelerationShiftCueEnabled = true });

    private static ShiftCuePerformance Performance(string name = "na-1335")
    {
        var fixture = Fixture(name);
        const double radiansToRpm = 30 / Math.PI;
        var gears = fixture.GetProperty("gears").EnumerateArray().Skip(1).ToArray();
        var data = new NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus.Ready,
            fixture.GetProperty("car").GetInt32(), Stopwatch.GetTimestamp(), "recorded-configuration-" + name,
            stepRpm: fixture.GetProperty("step").GetSingle() * radiansToRpm,
            exactRedlineRpm: fixture.GetProperty("redline").GetSingle() * radiansToRpm,
            torqueNm: fixture.GetProperty("expected_full_modified").EnumerateArray().Select(value => value.GetSingle() * 100d).ToArray(),
            forwardRatios: gears.Select(gear => (double)gear[0].GetSingle()).ToArray(),
            finalDrive: fixture.GetProperty("final_drive").GetSingle(),
            nativeCeilingCandidateRpm: fixture.GetProperty("ceiling_candidate").GetSingle() * radiansToRpm,
            nativeBaselineUpperRpm: gears.Select(gear => gear[3].GetSingle() * radiansToRpm).ToArray(),
            nativeAdjustedUpperRpm: gears.Select(gear => gear[4].GetSingle() * radiansToRpm).ToArray(),
            configuredPeakTorqueNm: fixture.GetProperty("configured_peak_torque").GetSingle() * 100d,
            configuredPeakPowerWatts: fixture.GetProperty("configured_peak_power").GetSingle() * 100d,
            officialExportCount: fixture.GetProperty("expected_export_count").GetInt32(),
            hasModifiers: name == "turbo-3289");
        return NativeHudMemoryResolver.CreateShiftPerformance(data);
    }

    private static void Update(DiagnosticsViewModel model, VehicleState state, ShiftCuePerformance performance)
    {
        // Recorded RPM, output, inputs and game elapsed times are unchanged.
        // Transport freshness, visibility and native metadata arrival are
        // synthetic: this tests integration, not live delivery/display timing.
        var now = Stopwatch.GetTimestamp();
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, performance.CarOrdinal)
            with
        { Available = true, IsLCAvailable = true };
        var native = new NativeHudSnapshot(true, 1, performance.CarOrdinal, NativeAssistProviderStatus.Ready,
            ExactRedlineResult.Exact(performance.Metadata!.ExactRedlineRpm * Math.PI / 30), 11000, assists,
            NativeGameplayVisibility.Visible, now, ShiftPerformance: performance with { ObservedTimestamp = now });
        model.Update(state, new IndicatedSpeed(state.GroundSpeedMetersPerSecond, state.GroundSpeedMetersPerSecond * 2.236936292, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            native, default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: false, updateGForce: false);
    }

    private static VehicleState State(JsonElement row, int carOrdinal = 1335) => new()
    {
        // Supporting combustion/road-contact fields were not selected into the
        // numeric capture fixture; they provide a synthetic driving context.
        IsRaceOn = true,
        CarOrdinal = carOrdinal,
        NumCylinders = 1,
        Drivetrain = DrivetrainType.RearWheelDrive,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        ReceivedTimestamp = Stopwatch.GetTimestamp(),
        GameTimestampMilliseconds = row.GetProperty("game_elapsed_ms").GetUInt32(),
        EngineRpm = row.GetProperty("rpm").GetSingle(),
        EngineMaximumRpm = 11000,
        Gear = (TransmissionGear)row.GetProperty("gear").GetInt32(),
        Accelerator = row.GetProperty("accelerator_raw").GetByte(),
        Brake = row.GetProperty("brake_raw").GetByte(),
        PowerWatts = row.GetProperty("power_w").GetSingle(),
        TorqueNm = row.GetProperty("torque_nm").GetSingle(),
        GroundSpeedMetersPerSecond = row.GetProperty("ground_speed_mps").GetSingle(),
        // Unused channels absent from this numeric excerpt are neutral fixtures.
        WheelRotationRadiansPerSecond = default,
        TireSlipAngle = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Steering = 0,
        TireSlipRatio = new(0, 0, row.GetProperty("rear_slip_max").GetSingle(), row.GetProperty("rear_slip_max").GetSingle()),
        NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f)
    };

    private static void AssertRingCommand(NativeGaugeFrame frame)
    {
        Assert.True(frame.ShiftCue.Stage > 0);
        // Force only the flash phase to avoid wall-clock-sensitive assertions.
        // A draw command is not evidence of a physically displayed frame.
        var renderFrame = frame with { ShiftCue = frame.ShiftCue with { FlashOn = true } };
        var ring = Assert.Single(AnalogHudScene.Build(renderFrame, 280, 0, true, false, default),
            command => command.TextureId == ShiftCueArtwork.AnalogTextureId);
        var color = frame.ShiftCue.ColorArgb;
        Assert.Equal((byte)(color >> 16) / 255f, ring.TintR);
        Assert.Equal((byte)(color >> 8) / 255f, ring.TintG);
        Assert.Equal((byte)color / 255f, ring.TintB);
        Assert.True(ring.TintA > 0 && ring.AxisXX > 0 && ring.AxisYY > 0);
    }

    private static JsonElement Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", name + ".json")));
        return document.RootElement.Clone();
    }
}
