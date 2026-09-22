using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueStateTests
{
    private static readonly AccelerationShiftProfile Profile = new(
        [new(0, 1000), new(9000, 100)], [2, 1, .5]);

    [Fact]
    public void ExistingSettingsAndOldProfilesKeepResearchCueOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"OverlayOpacity":0.7}""")!;
        settings.MigrateSettings();
        var model = new DiagnosticsViewModel(settings);
        Assert.False(settings.AccelerationShiftCueEnabled);
        Assert.False(model.AccelerationShiftCueEnabled);
        Assert.Null(settings.ShiftCueGreenColor);
        Assert.Null(settings.ShiftCueYellowColor);
        Assert.Null(settings.ShiftCueRedColor);
        Update(model, State(1000), Snapshot());
        Update(model, State(1250), Snapshot());
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal("Off", model.ShiftCueStatus);

        settings.AccelerationShiftCueEnabled = true;
        settings.ShiftCueRedColor = "#FF123456";
        var oldProfile = JsonSerializer.Deserialize<HudPreset>("""{"Name":"Existing HUD"}""")!;
        oldProfile.ApplyTo(settings);
        Assert.False(settings.AccelerationShiftCueEnabled);
        Assert.Null(settings.ShiftCueRedColor);
    }

    [Fact]
    public void SettingsAndHudProfilesRoundTripCueAndIndependentColors()
    {
        var settings = new AppSettings
        {
            AccelerationShiftCueEnabled = true,
            ShiftCueGreenColor = "#FF112233",
            ShiftCueYellowColor = "#FF445566",
            ShiftCueRedColor = "#FF778899"
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var preset = JsonSerializer.Deserialize<HudPreset>(JsonSerializer.Serialize(HudPreset.Capture(restored, "Shift research")))!;
        var target = new AppSettings();
        preset.ApplyTo(target);
        Assert.True(target.AccelerationShiftCueEnabled);
        Assert.Equal(settings.ShiftCueGreenColor, target.ShiftCueGreenColor);
        Assert.Equal(settings.ShiftCueYellowColor, target.ShiftCueYellowColor);
        Assert.Equal(settings.ShiftCueRedColor, target.ShiftCueRedColor);
    }

    [Theory]
    [InlineData(5000, 0, 0xFF778899u)]
    [InlineData(5800, 1, 0xFF112233u)]
    [InlineData(6400, 2, 0xFF445566u)]
    [InlineData(6800, 3, 0xFF778899u)]
    public void CueStagesUseFullLoadModelTargetAndCustomColors(float rpm, int stage, uint color)
    {
        var model = Model(new AppSettings
        {
            AccelerationShiftCueEnabled = true,
            ShiftCueGreenColor = "#FF112233",
            ShiftCueYellowColor = "#FF445566",
            ShiftCueRedColor = "#FF778899"
        });
        Arm(model, rpm);
        var cue = model.NativeGaugeFrame.ShiftCue;
        Assert.True(cue.Enabled);
        Assert.Equal(stage, cue.Stage);
        Assert.Equal(color, cue.ColorArgb);
        Assert.Equal(20000d / 3, cue.TargetRpm, 6);
        Assert.Contains("Estimated", model.ShiftCueStatus);
        Assert.Contains("acceleration crossover", model.ShiftCueStatus);
        if (stage == 0) Assert.False(cue.IsVisible);
        if (stage is 1 or 2) Assert.True(cue.IsVisible);
        // Initial red is on; actual presentation remains a separate measurement.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RedStartsOnAtEveryGlobalPhaseAndDoesNotRestartEachPacket(int phase)
    {
        var quarter = Stopwatch.Frequency / 4;
        var start = quarter * (100 + phase) + quarter / 2;
        var now = start;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        uint gameTime = 1000;
        var previousRpm = 6800f;
        void Publish(float rpm)
        {
            var native = Snapshot();
            native = native with
            {
                VisibilityObservedTimestamp = now,
                ShiftPerformance = native.ShiftPerformance! with { ObservedTimestamp = now }
            };
            var state = State(gameTime++, rpm) with { ReceivedTimestamp = now };
            model.ObserveShiftCueTelemetry(state);
            Update(model, state, native);
            var cue = model.NativeGaugeFrame.ShiftCue;
            model.UpdateNativeHudSnapshot(native);
            Assert.Equal(cue, model.NativeGaugeFrame.ShiftCue);
        }
        void Observe(double milliseconds, float rpm)
        {
            var targetTime = start + (long)(milliseconds * Stopwatch.Frequency / 1000);
            // Exercise continuous fresh packets across flash boundaries. A gap
            // longer than the 150 ms freshness limit intentionally rearms red.
            while (now + Stopwatch.Frequency / 20 < targetTime)
            {
                now += Stopwatch.Frequency / 20;
                Publish(previousRpm);
            }
            now = targetTime;
            Publish(rpm);
            previousRpm = rpm;
        }

        Observe(0, 6800);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
        using (var export = JsonDocument.Parse(model.ExportShiftTestData()))
        {
            var sample = Assert.Single(export.RootElement.GetProperty("samples").EnumerateArray());
            Assert.True(sample.GetProperty("CueEnabled").GetBoolean());
            Assert.True(sample.GetProperty("CueFlashOn").GetBoolean());
            Assert.True(sample.GetProperty("CueVisible").GetBoolean());
            Assert.Equal(start, sample.GetProperty("ObservedTimestamp").GetInt64());
            Assert.Equal(start, sample.GetProperty("RedStartedTimestamp").GetInt64());
        }
        Observe(249, 6800);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
        Observe(250, 6800);
        Assert.False(model.NativeGaugeFrame.ShiftCue.IsVisible);
        Observe(499, 6400); // Existing reached-target persistence must not restart the flash.
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.False(model.NativeGaugeFrame.ShiftCue.IsVisible);
        Observe(500, 6800);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
        Observe(750, 5500); // Leave the approach band, then begin a new red indication.
        Assert.Equal(0, model.NativeGaugeFrame.ShiftCue.Stage);
        Observe(751, 6800);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
    }

    [Fact]
    public void InvalidColorFallsBackToAuthoredStageColor()
    {
        var model = Model(new AppSettings { AccelerationShiftCueEnabled = true, ShiftCueGreenColor = "invalid" });
        Arm(model, 5800);
        Assert.Equal(0xFF70E7A1u, model.NativeGaugeFrame.ShiftCue.ColorArgb);
    }

    [Fact]
    public void SupportedFreshDataDoesNotRequireAContinuousCleanPull()
    {
        var model = Model();
        Update(model, State(1000), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Contains("full-load", model.ShiftCueStatus);
    }

    [Theory]
    [InlineData("gear")]
    [InlineData("fingerprint")]
    [InlineData("clock_rewind")]
    public void GearTuneIdentityAndClockChangesUseCurrentSupportedModelWithoutSettlingDelay(string change)
    {
        var model = Model();
        Arm(model);
        var start = change == "clock_rewind" ? 100u : 1500u;
        var gear = change == "gear" ? TransmissionGear.Second : TransmissionGear.First;
        var identity = change == "fingerprint" ? "tune-B" : "tune-A";
        Update(model, State(start, gear: gear), Snapshot(fingerprint: identity));
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Contains($"{(int)gear} → {(int)gear + 1}", model.ShiftCueStatus);
    }

    [Fact]
    public void MissingCrossoverDoesNotFallBackToTachRedline()
    {
        var flat = new AccelerationShiftProfile([new(0, 200), new(9000, 200)], [2, 1]);
        var model = Model();
        var first = State(1000, 8490) with { TorqueNm = 200, PowerWatts = 100000 };
        Update(model, first, Snapshot(flat));
        Update(model, first with { GameTimestampMilliseconds = 1250 }, Snapshot(flat));
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Contains("No supported crossover", model.ShiftCueStatus);
    }

    [Theory]
    [InlineData(TransmissionGear.Unknown)]
    [InlineData(TransmissionGear.Reverse)]
    [InlineData(TransmissionGear.Neutral)]
    [InlineData(TransmissionGear.Third)]
    public void NoForwardUpshiftIsRecommendedForUnknownReverseNeutralOrTopGear(TransmissionGear gear)
    {
        var model = Model();
        Arm(model);
        Update(model, State(1500, gear: gear), Snapshot());
        Update(model, State(1750, gear: gear), Snapshot());
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        if (gear == TransmissionGear.Third) Assert.Contains("Top gear", model.ShiftCueStatus);
    }

    [Theory]
    [InlineData("stale_profile")]
    [InlineData("future_profile")]
    [InlineData("stale_visibility")]
    [InlineData("unknown_visibility")]
    [InlineData("menu")]
    [InlineData("car_mismatch")]
    [InlineData("provider_mismatch")]
    [InlineData("unavailable_provider")]
    [InlineData("invalid_provider")]
    [InlineData("missing_profile")]
    [InlineData("invalid_profile")]
    [InlineData("stale_packet")]
    [InlineData("negative_packet_age")]
    [InlineData("race_off")]
    [InlineData("electric")]
    public void UnavailableOrMismatchedSourcesImmediatelyClearPreviouslyActiveCue(string failure)
    {
        var model = Model();
        Arm(model);
        var state = State(1500);
        var native = Snapshot();
        var age = TimeSpan.Zero;
        switch (failure)
        {
            case "stale_profile": native = native with { ShiftPerformance = native.ShiftPerformance! with { ObservedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 } }; break;
            case "future_profile": native = native with { ShiftPerformance = native.ShiftPerformance! with { ObservedTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2 } }; break;
            case "stale_visibility": native = native with { VisibilityObservedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 }; break;
            case "unknown_visibility": native = native with { GameplayVisibility = NativeGameplayVisibility.Unknown }; break;
            case "menu": native = native with { GameplayVisibility = NativeGameplayVisibility.Hidden }; break;
            case "car_mismatch": native = native with { ShiftPerformance = native.ShiftPerformance! with { CarOrdinal = 2 } }; break;
            case "provider_mismatch": native = native with { CarOrdinal = 2 }; break;
            case "unavailable_provider": native = native with { Available = false }; break;
            case "invalid_provider": native = native with { Status = NativeAssistProviderStatus.InvalidProvider }; break;
            case "missing_profile": native = native with { ShiftPerformance = null }; break;
            case "invalid_profile": native = native with { ShiftPerformance = native.ShiftPerformance! with { Profile = new AccelerationShiftProfile([], [2, 1]) } }; break;
            case "stale_packet": age = TimeSpan.FromMilliseconds(151); break;
            case "negative_packet_age": age = TimeSpan.FromMilliseconds(-1); break;
            case "race_off": state = state with { IsRaceOn = false }; break;
            case "electric": state = state with { NumCylinders = 0 }; break;
        }
        Update(model, state, native, age);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1750), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Theory]
    [InlineData("nonfinite_speed")]
    [InlineData("nonfinite_rpm")]
    [InlineData("negative_rpm")]
    [InlineData("brake")]
    [InlineData("zero_throttle")]
    [InlineData("stationary")]
    [InlineData("launch_control")]
    public void StationaryCoastingBrakingAndInvalidStateSuppressCue(string failure)
    {
        var model = Model();
        Arm(model);
        var state = State(1500);
        var native = Snapshot();
        state = failure switch
        {
            "nonfinite_speed" => state with { GroundSpeedMetersPerSecond = float.PositiveInfinity },
            "nonfinite_rpm" => state with { EngineRpm = float.NaN },
            "negative_rpm" => state with { EngineRpm = -1 },
            "brake" => state with { Brake = 2 },
            "zero_throttle" => state with { Accelerator = 0 },
            "stationary" => state with { GroundSpeedMetersPerSecond = 0 },
            _ => state
        };
        if (failure == "launch_control") native = native with { Assists = native.Assists with { IsLCOn = true } };
        Update(model, state, native);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1750), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Theory]
    [InlineData("output_mismatch")]
    [InlineData("engine_cut")]
    [InlineData("boost")]
    [InlineData("vacuum")]
    [InlineData("wheel_slip")]
    [InlineData("part_throttle")]
    [InlineData("cornering")]
    [InlineData("steering")]
    [InlineData("slow_moving")]
    public void DevelopmentSampleSelectionDoesNotHideConfiguredFullLoadTarget(string condition)
    {
        var model = Model();
        var state = State(1000);
        state = condition switch
        {
            "output_mismatch" => state with { TorqueNm = state.TorqueNm * .25f },
            "engine_cut" => state with { TorqueNm = -100, PowerWatts = -1000 },
            "boost" => state with { BoostPressurePsi = 20 },
            "vacuum" => state with { BoostPressurePsi = -14.7f },
            "wheel_slip" => state with { TireSlipRatio = new(0, 0, 1, 1) },
            "part_throttle" => state with { Accelerator = 100 },
            "cornering" => state with { LateralAccelerationMetersPerSecondSquared = 10 },
            "steering" => state with { Steering = 80 },
            "slow_moving" => state with { GroundSpeedMetersPerSecond = 1 },
            _ => state
        };
        Update(model, state, Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal(20000d / 3, model.NativeGaugeFrame.ShiftCue.TargetRpm, 6);
        Assert.Contains("full-load", model.ShiftCueStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetOvershootRemainsRedForCrossoverAndVerifiedOperatingLimit(bool limitBound)
    {
        var profile = limitBound
            ? new AccelerationShiftProfile([new(0, 200), new(9000, 200)], [2, 1], verifiedOperatingCeilingRpm: 7500)
            : Profile;
        var native = Snapshot(profile);
        var result = native.ShiftPerformance!.Gears[0];
        Assert.Equal(limitBound ? AccelerationShiftStatus.VerifiedLimitBound : AccelerationShiftStatus.EstimatedCrossover, result.Status);
        var model = Model();
        Update(model, State(1000, (float)result.MaximumAnalysisRpm + 100), native);
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal(result.EstimatedTargetRpm, model.NativeGaugeFrame.ShiftCue.TargetRpm);
        Assert.Contains(limitBound ? "engine-limit" : "crossover", model.ShiftCueStatus);
    }

    [Fact]
    public void ReachedTargetStaysRedThroughLimiterCutAndReboundUntilGearOrLiftChanges()
    {
        var profile = new AccelerationShiftProfile([new(0, 200), new(12000, 200)], [2, 1, .5],
            verifiedOperatingCeilingRpm: 10249.995);
        var model = Model();
        Update(model, State(1000, 10251), Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Update(model, State(1050, 9906) with { TorqueNm = -200, PowerWatts = -200000 }, Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Update(model, State(1100, 10200), Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.Equal(10249.995, model.NativeGaugeFrame.ShiftCue.TargetRpm);

        Update(model, State(1150, 9906, TransmissionGear.Second), Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
        Update(model, State(1200, 10251, TransmissionGear.Second), Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Update(model, State(1250, 9906, TransmissionGear.Second) with { Accelerator = 0 }, Snapshot(profile, maximumRpm: 11000));
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1300, 9906, TransmissionGear.Second), Snapshot(profile, maximumRpm: 11000));
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
    }

    [Theory]
    [InlineData("below_green")]
    [InlineData("brake")]
    [InlineData("stationary")]
    [InlineData("launch_control")]
    [InlineData("tune_change")]
    [InlineData("clock_rewind")]
    public void ReachedTargetLatchDoesNotLeakIntoAnotherPull(string change)
    {
        var model = Model();
        Arm(model);
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        var state = State(change == "clock_rewind" ? 100u : 1500u, 6400);
        state = change switch
        {
            "below_green" => state with { EngineRpm = 5500 },
            "brake" => state with { Brake = 2 },
            "stationary" => state with { GroundSpeedMetersPerSecond = 0 },
            _ => state
        };
        var fingerprint = change == "tune_change" ? "tune-B" : "tune-A";
        var native = Snapshot(fingerprint: fingerprint);
        if (change == "launch_control") native = native with { Assists = native.Assists with { IsLCOn = true } };
        Update(model, state, native);
        Assert.NotEqual(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Update(model, State(1750, 6400), Snapshot(fingerprint: fingerprint));
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
    }

    [Fact]
    public void ReachedTargetLatchCannotSurviveFrozenGameDataOrStaleMetadata()
    {
        var model = Model();
        Arm(model);
        var state = State(1500, 6400);
        var native = Snapshot();
        Update(model, state, native);
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        model.InvalidateStaleShiftCue(state, native, TimeSpan.Zero, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, state, Snapshot());
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, state with { GameTimestampMilliseconds = 1750 }, Snapshot());
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);

        Update(model, State(1800, 6800), Snapshot());
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        var stale = Snapshot();
        stale = stale with { ShiftPerformance = stale.ShiftPerformance! with { ObservedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2 } };
        Update(model, State(1850, 6400), stale);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1900, 6400), Snapshot());
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
    }

    [Fact]
    public void RpmBelowAnalysisRangeDoesNotEraseTheCachedTarget()
    {
        var model = Model();
        Update(model, State(1000, 1000), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal(0, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.False(model.NativeGaugeFrame.ShiftCue.IsVisible);
    }

    [Fact]
    public void ClearHudAndDisableNeverLeaveALatchedResearchCue()
    {
        var model = Model();
        Arm(model);
        model.ClearHudVisuals();
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1500), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
        model.AccelerationShiftCueEnabled = false;
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.Equal("Off", model.ShiftCueStatus);
        model.AccelerationShiftCueEnabled = true;
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(2000), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostTelemetryClearsCueEvenWhenOtherHudVisualsArePreserved(bool preserveHudVisuals)
    {
        var model = Model();
        Arm(model);
        model.UpdateWaiting(default, TelemetryConnectionState.Lost, TimeSpan.FromSeconds(1), 60, preserveHudVisuals);
        Assert.False(model.NativeGaugeFrame.ShiftCue.Enabled);
        Update(model, State(1500), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    [Fact]
    public void DiagnosticExportIsBoundedDeduplicatedAndPreservesNonfiniteData()
    {
        var model = Model();
        var snapshot = Snapshot();
        var lastState = State(0);
        for (uint i = 0; i < 2010; i++)
        {
            lastState = State(i);
            Update(model, lastState, snapshot);
        }
        Update(model, lastState, snapshot);
        Update(model, State(2010) with { PowerWatts = float.PositiveInfinity }, snapshot);
        using var data = JsonDocument.Parse(model.ExportShiftTestData());
        var samples = data.RootElement.GetProperty("samples");
        Assert.Equal(2000, samples.GetArrayLength());
        Assert.Equal(11u, samples[0].GetProperty("GameMilliseconds").GetUInt32());
        Assert.Equal("Infinity", samples[1999].GetProperty("PowerWatts").GetString());
        Assert.Equal(1, data.RootElement.GetProperty("profiles").GetArrayLength());
        Assert.Equal(2, data.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void DisabledCueDoesNotCollectDiagnosticSamples()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        Update(model, State(1000), Snapshot());
        using var data = JsonDocument.Parse(model.ExportShiftTestData());
        Assert.Equal(0, data.RootElement.GetProperty("samples").GetArrayLength());
        Assert.Equal(0, data.RootElement.GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public void EarlierGearDecisionCountsSurviveTheRollingSampleWindow()
    {
        var model = Model();
        var firstState = State(1000);
        Update(model, firstState, Snapshot());
        Update(model, firstState, Snapshot());
        for (uint i = 1001; i <= 3010; i++) Update(model, State(i, gear: TransmissionGear.Third), Snapshot());
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var root = export.RootElement;
        Assert.Equal(2011, root.GetProperty("totalUniqueSamples").GetInt64());
        Assert.All(root.GetProperty("samples").EnumerateArray(), sample => Assert.Equal(3, sample.GetProperty("Gear").GetInt32()));
        var firstGear = Assert.Single(root.GetProperty("decisionCounts").EnumerateArray(), entry => entry.GetProperty("Gear").GetInt32() == 1);
        Assert.Equal(1, firstGear.GetProperty("Samples").GetInt64());
        Assert.Equal("FullLoadTarget", firstGear.GetProperty("Decision").GetString());
        Assert.Equal(20000d / 3, firstGear.GetProperty("TargetRpm").GetDouble(), 6);
        Assert.Equal(2011, root.GetProperty("decisionCounts").EnumerateArray().Sum(entry => entry.GetProperty("Samples").GetInt64()));
    }

    [Fact]
    public void DecisionHistoryAndProfileRetentionAreBoundedAcrossTuneChanges()
    {
        var model = Model();
        for (uint i = 0; i < 270; i++) Update(model, State(i), Snapshot(fingerprint: $"tune-{i}"));
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var root = export.RootElement;
        Assert.Equal(256, root.GetProperty("decisionCounts").GetArrayLength());
        Assert.Equal(14, root.GetProperty("discardedDecisionGroups").GetInt64());
        Assert.Equal(8, root.GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public void ExportIncludesSuppressionReasonAndFormerlyMissingInputChannels()
    {
        var model = Model();
        var native = Snapshot();
        native = native with { Assists = native.Assists with { IsLCOn = true } };
        Update(model, State(1000) with { BoostPressurePsi = 15, Steering = 80 }, native, TimeSpan.FromMilliseconds(20));
        using var export = JsonDocument.Parse(model.ExportShiftTestData());
        var sample = Assert.Single(export.RootElement.GetProperty("samples").EnumerateArray());
        Assert.Equal("LaunchControl", sample.GetProperty("Decision").GetString());
        Assert.True(sample.GetProperty("IsRaceOn").GetBoolean());
        Assert.True(sample.GetProperty("LaunchControlActive").GetBoolean());
        Assert.Equal(15, sample.GetProperty("BoostPressurePsi").GetDouble());
        Assert.Equal(80, sample.GetProperty("Steering").GetInt32());
        Assert.Equal(20, sample.GetProperty("PacketAgeMilliseconds").GetDouble());
        Assert.True(sample.GetProperty("ObservedTimestamp").GetInt64() > 0);
        Assert.Equal(native.VisibilityObservedTimestamp, sample.GetProperty("VisibilityObservedTimestamp").GetInt64());
        Assert.Equal(native.ShiftPerformance!.ObservedTimestamp, sample.GetProperty("MetadataObservedTimestamp").GetInt64());
        Assert.Equal("Ready", sample.GetProperty("NativeStatus").GetString());
        Assert.Equal("Visible", sample.GetProperty("GameplayVisibility").GetString());
        Assert.Equal(20000d / 3, sample.GetProperty("TargetRpm").GetDouble(), 6);
    }

    [Fact]
    public void CrossingThenLimiterDropBetweenUiUpdatesStartsVisibleRedFromObservedEvent()
    {
        var start = Stopwatch.Frequency * 10;
        var now = start;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        NativeHudSnapshot Native() => Snapshot() with
        {
            VisibilityObservedTimestamp = now,
            ShiftPerformance = Snapshot().ShiftPerformance! with { ObservedTimestamp = now }
        };
        VehicleState Sample(uint game, float rpm) => State(game, rpm) with { ReceivedTimestamp = now };
        Update(model, Sample(1000, 6400), Native());
        Assert.Equal(2, model.NativeGaugeFrame.ShiftCue.Stage);
        now += Stopwatch.Frequency / 100;
        var crossingAt = now;
        model.ObserveShiftCueTelemetry(Sample(1000, 6800)); // A repeated game timestamp still has new RPM.
        now += Stopwatch.Frequency / 100;
        var newest = Sample(1016, 6400);
        model.ObserveShiftCueTelemetry(newest);
        now += Stopwatch.Frequency / 100;
        Update(model, newest, Native());
        Assert.Equal(3, model.NativeGaugeFrame.ShiftCue.Stage);
        Assert.True(model.NativeGaugeFrame.ShiftCue.IsVisible);
        using var exported = JsonDocument.Parse(model.ExportShiftTestData());
        var last = exported.RootElement.GetProperty("samples").EnumerateArray().Last();
        Assert.Equal(crossingAt, last.GetProperty("CrossingObservedTimestamp").GetInt64());
        Assert.Equal(now, last.GetProperty("RedStartedTimestamp").GetInt64());
    }

    [Theory]
    [InlineData("brake")]
    [InlineData("lift")]
    [InlineData("neutral")]
    [InlineData("invalid_packet")]
    [InlineData("expired")]
    [InlineData("tune")]
    [InlineData("launch_control")]
    [InlineData("disabled")]
    public void SupersededCrossingCannotSurviveAnInterveningInvalidation(string interruption)
    {
        var now = Stopwatch.Frequency * 10;
        var model = new DiagnosticsViewModel(new AppSettings { AccelerationShiftCueEnabled = true }, () => now);
        NativeHudSnapshot Native(string key = "tune-A") => Snapshot(fingerprint: key) with
        {
            VisibilityObservedTimestamp = now,
            ShiftPerformance = Snapshot(fingerprint: key).ShiftPerformance! with { ObservedTimestamp = now }
        };
        VehicleState Sample(uint game, float rpm) => State(game, rpm) with { ReceivedTimestamp = now };
        Update(model, Sample(1000, 6400), Native());
        now += Stopwatch.Frequency / 100;
        model.ObserveShiftCueTelemetry(Sample(1016, 6800));
        now += Stopwatch.Frequency / 100;
        switch (interruption)
        {
            case "brake": model.ObserveShiftCueTelemetry(Sample(1032, 6400) with { Brake = 200 }); break;
            case "lift": model.ObserveShiftCueTelemetry(Sample(1032, 6400) with { Accelerator = 0 }); break;
            case "neutral": model.ObserveShiftCueTelemetry(Sample(1032, 6400) with { Gear = TransmissionGear.Neutral }); break;
            case "invalid_packet": model.ObserveShiftCueTelemetry(null); break;
            case "expired": now += Stopwatch.Frequency / 5; break;
            case "disabled": model.AccelerationShiftCueEnabled = false; model.AccelerationShiftCueEnabled = true; break;
        }
        var native = Native(interruption == "tune" ? "tune-B" : "tune-A");
        if (interruption == "launch_control") native = native with { Assists = native.Assists with { IsLCOn = true } };
        var newest = Sample(1048, 6400);
        model.ObserveShiftCueTelemetry(newest);
        Update(model, newest, native);
        Assert.NotEqual(3, model.NativeGaugeFrame.ShiftCue.Stage);
    }

    private static DiagnosticsViewModel Model(AppSettings? settings = null) =>
        new(settings ?? new AppSettings { AccelerationShiftCueEnabled = true });

    private static void Arm(DiagnosticsViewModel model, float rpm = 6800)
    {
        Update(model, State(1000, rpm), Snapshot());
        Update(model, State(1250, rpm), Snapshot());
        Assert.True(model.NativeGaugeFrame.ShiftCue.Enabled);
    }

    private static void Update(DiagnosticsViewModel model, VehicleState state, NativeHudSnapshot snapshot, TimeSpan age = default) =>
        model.Update(state, new IndicatedSpeed(30, 67, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            snapshot, default, age, SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: false, updateGForce: false);

    private static NativeHudSnapshot Snapshot(AccelerationShiftProfile? profile = null, string fingerprint = "tune-A", double maximumRpm = 8500)
    {
        profile ??= Profile;
        var now = Stopwatch.GetTimestamp();
        var performance = new ShiftCuePerformance(1, now, fingerprint, "Research", profile,
            Enumerable.Range(1, profile.ForwardRatios.Count)
                .Select(g => AccelerationShiftSolver.Solve(profile, g, 2000, maximumRpm)).ToArray());
        var assists = NativeAssistSnapshot.Unavailable(NativeAssistProviderStatus.Ready, 1, 1)
            with
        { Available = true, IsLCAvailable = true };
        return new(true, 1, 1, NativeAssistProviderStatus.Ready,
            ExactRedlineResult.Exact(8500 * Math.PI / 30), 9000, assists,
            NativeGameplayVisibility.Visible, now, ShiftPerformance: performance);
    }

    private static VehicleState State(uint time, float rpm = 6800, TransmissionGear gear = TransmissionGear.First)
    {
        var torque = 1000 - rpm * .1f;
        return new()
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = time,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ReceivedTimestamp = Stopwatch.GetTimestamp(),
            CarOrdinal = 1,
            Drivetrain = DrivetrainType.RearWheelDrive,
            NumCylinders = 8,
            GroundSpeedMetersPerSecond = 30,
            EngineRpm = rpm,
            EngineMaximumRpm = 9000,
            PowerWatts = torque * rpm * MathF.PI / 30,
            TorqueNm = torque,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 2,
            Gear = gear,
            Steering = 0,
            Accelerator = 255,
            Brake = 0
        };
    }
}
