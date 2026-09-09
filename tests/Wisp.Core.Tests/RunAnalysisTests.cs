using Wisp.Core.Runs;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class RunAnalysisTests
{
    private static RunSample Sample(double time, float speed = 10, int segment = 0, double? wheel = 12, double? radius = 0.3,
        byte throttle = 0, byte brake = 0, uint? gameTime = null) => new()
        {
            ElapsedSeconds = time,
            Segment = segment,
            IsDriving = true,
            WheelSpeedMetersPerSecond = wheel,
            RearRadiusMeters = radius,
            State = TestVehicleState.Create(groundSpeed: speed) with
            {
                GameTimestampMilliseconds = gameTime ?? (uint)Math.Round(time * 1000),
                NumCylinders = 8,
                Accelerator = throttle,
                Brake = brake,
                TireTemperatureFahrenheit = new(100, 100, 110, 110)
            }
        };

    private static RecordedRun Run(params RunSample[] samples) => new() { Samples = samples };
    private static RunSample[] Steady(int count = 21, double start = 0, int segment = 0) => Enumerable.Range(0, count)
        .Select(i => Sample(start + i * 0.1, segment: segment, wheel: 20, throttle: 255)).ToArray();

    [Fact]
    public void AveragesAndDistanceUseTimeRatherThanSampleDensity()
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 0), Sample(0.1, 10), Sample(0.3, 10))).Statistics;
        Assert.Equal(0.3, stats.RecordedSeconds, 8);
        Assert.Equal(2.5, stats.DistanceMeters, 8);
        Assert.Equal(2.5 / 0.3, stats.AverageSpeedMetersPerSecond!.Value, 8);
        Assert.Equal(0, stats.GapCount);
    }

    [Fact]
    public void SelectedBoundariesAreInterpolatedInsideAdjacentValidSamples()
    {
        var run = Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20));
        var report = RunAnalysis.BuildReport(run, interval: new(0.05, 0.15));
        Assert.Equal(new(0.05, 0.15), report.Interval);
        Assert.Equal(1, report.Statistics.SampleCount);
        Assert.Equal(0.1, report.Statistics.RecordedSeconds, 8);
        Assert.Equal(1, report.Statistics.DistanceMeters, 8);
        Assert.Equal(10, report.Statistics.AverageSpeedMetersPerSecond!.Value, 8);
        Assert.Equal(15, report.Statistics.PeakSpeedMetersPerSecond!.Value, 8);
    }

    [Fact]
    public void DuplicateTimestampsRetainInstantaneousReadingsButNeverAddTime()
    {
        var a = Sample(0.1, 10) with { State = Sample(0.1).State with { GroundSpeedMetersPerSecond = 10, PowerWatts = 100 } };
        var latest = a with { WheelSpeedMetersPerSecond = 30, State = a.State with { GroundSpeedMetersPerSecond = 20, PowerWatts = 999 } };
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 0), a, latest, Sample(0.2, 20))).Statistics;
        Assert.Equal(4, stats.SampleCount);
        Assert.Equal(0.2, stats.RecordedSeconds, 8);
        Assert.Equal(12.5, stats.AverageSpeedMetersPerSecond!.Value, 8);
        Assert.Equal(999, stats.PeakPowerWatts);
        Assert.Equal(0, stats.GapCount);
    }

    [Fact]
    public void OnlyDuplicatesHavePeaksButNoTimeBasedMetricsOrSustainedFindings()
    {
        var samples = Steady(10).Select(s => s with { ElapsedSeconds = 0, State = s.State with { GameTimestampMilliseconds = 0 } }).ToArray();
        var report = RunAnalysis.BuildReport(Run(samples), RunPurpose.Acceleration);
        Assert.Equal(10, report.Statistics.SampleCount);
        Assert.Equal(0, report.Statistics.RecordedSeconds);
        Assert.Null(report.Statistics.AverageSpeedMetersPerSecond);
        Assert.NotNull(report.Statistics.PeakSpeedMetersPerSecond);
        Assert.Single(report.Findings);
        Assert.Contains("not enough", report.Findings[0].Title);
    }

    [Theory]
    [InlineData(0.251, 0)]
    [InlineData(0.1, 1)]
    public void GapsAndSegmentChangesNeverBecomeDrivingTime(double nextTime, int nextSegment)
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 10), Sample(nextTime, 200, nextSegment))).Statistics;
        Assert.Equal(0, stats.RecordedSeconds);
        Assert.Equal(0, stats.DistanceMeters);
        Assert.Equal(1, stats.GapCount);
    }

    [Fact]
    public void MultipleValidSectionsExcludeInterruptionFromAverage()
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 10), Sample(0.1, 10), Sample(1, 30, 1), Sample(1.1, 30, 1))).Statistics;
        Assert.Equal(1.1, stats.DurationSeconds, 8);
        Assert.Equal(0.2, stats.RecordedSeconds, 8);
        Assert.Equal(20, stats.AverageSpeedMetersPerSecond!.Value, 8);
        Assert.Equal(1, stats.GapCount);
    }

    [Fact]
    public void UintTimestampWrapIsContinuousButAResetIsNot()
    {
        Assert.True(RunAnalysis.AreContinuous(Sample(0, gameTime: uint.MaxValue - 50), Sample(0.1, gameTime: 49)));
        Assert.False(RunAnalysis.AreContinuous(Sample(0, gameTime: 1000), Sample(0.1, gameTime: 10)));
        Assert.False(RunAnalysis.AreContinuous(Sample(0, gameTime: 10), Sample(0.1, gameTime: 11)));
        Assert.False(RunAnalysis.AreContinuous(Sample(0, gameTime: 100), Sample(0.1, gameTime: 100)));
    }

    [Theory]
    [InlineData("car")]
    [InlineData("drivetrain")]
    [InlineData("menu")]
    [InlineData("native")]
    [InlineData("nan")]
    public void InvalidContextCannotBridgeMetricsOrProducePeaks(string mode)
    {
        var bad = Sample(0.1, 200);
        bad = mode switch
        {
            "car" => bad with { State = bad.State with { CarOrdinal = 999 } },
            "drivetrain" => bad with { State = bad.State with { Drivetrain = DrivetrainType.AllWheelDrive } },
            "menu" => bad with { State = bad.State with { IsRaceOn = false } },
            "native" => bad with { IsDriving = false },
            _ => bad with { State = bad.State with { GroundSpeedMetersPerSecond = float.NaN } }
        };
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 10), bad, Sample(0.2, 10))).Statistics;
        Assert.Equal(0, stats.RecordedSeconds);
        Assert.Equal(1, stats.GapCount);
        Assert.Equal(mode is "car" or "drivetrain" ? 200d : 10d, stats.PeakSpeedMetersPerSecond);
    }

    [Fact]
    public void ReorderedElapsedTimeCannotDoubleCountAnEarlierSection()
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 10), Sample(0.1, 10), Sample(0.05, 100), Sample(0.2, 10), Sample(0.3, 10))).Statistics;
        Assert.Equal(0.2, stats.RecordedSeconds, 8);
        Assert.Equal(10, stats.PeakSpeedMetersPerSecond);
        Assert.Equal(1, stats.GapCount);
    }

    [Fact]
    public void OptionalNonfiniteMetricsDoNotEraseValidVehicleSpeed()
    {
        var samples = new[] { Sample(0), Sample(0.1) }.Select(s => s with
        { State = s.State with { PowerWatts = float.NaN, TorqueNm = float.PositiveInfinity, LateralAccelerationMetersPerSecondSquared = float.NaN } }).ToArray();
        var stats = RunAnalysis.BuildReport(Run(samples)).Statistics;
        Assert.Equal(10, stats.AverageSpeedMetersPerSecond);
        Assert.Null(stats.PeakPowerWatts);
        Assert.Null(stats.PeakTorqueNm);
        Assert.Null(stats.PeakLateralG);
    }

    [Fact]
    public void InputDurationsInterpolateThresholds()
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0), Sample(0.2, throttle: 255, brake: 26))).Statistics;
        Assert.Equal(0.2 * 5 / 255, stats.FullThrottleSeconds, 8);
        Assert.Equal(0.1, stats.BrakingSeconds, 8);
    }

    [Fact]
    public void WheelExcessIntegratesOnlyPositivePartAndOnlyTrustedCalibration()
    {
        var stats = RunAnalysis.BuildReport(Run(Sample(0, 10, wheel: 0), Sample(0.2, 10, wheel: 20))).Statistics;
        Assert.Equal(2.5, stats.AverageWheelSpeedExcessMetersPerSecond!.Value, 8);
        var missing = RunAnalysis.BuildReport(Run(Sample(0, radius: null), Sample(0.1, radius: null)));
        Assert.Null(missing.Statistics.AverageWheelSpeedExcessMetersPerSecond);
        Assert.Equal(10, missing.Statistics.AverageSpeedMetersPerSecond);
        Assert.Contains("calibration", missing.QualityNote);
        var changed = RunAnalysis.BuildReport(Run(Sample(0, radius: 0.3), Sample(0.1, radius: 0.4)));
        Assert.Null(changed.Statistics.AverageWheelSpeedExcessMetersPerSecond);
    }

    [Fact]
    public void AllWheelDriveRequiresBothCalibratedAxles()
    {
        var samples = new[] { Sample(0), Sample(0.1) }.Select(s => s with { State = s.State with { Drivetrain = DrivetrainType.AllWheelDrive } }).ToArray();
        Assert.Null(RunAnalysis.BuildReport(Run(samples)).Statistics.AverageWheelSpeedExcessMetersPerSecond);
        Assert.NotNull(RunAnalysis.BuildReport(Run(samples.Select(s => s with { FrontRadiusMeters = 0.3 }).ToArray())).Statistics.AverageWheelSpeedExcessMetersPerSecond);
    }

    [Theory]
    [InlineData(8, -15, 0, false)]
    [InlineData(0, -15, 20, false)]
    [InlineData(8, -15, 20, true)]
    public void BoostNeedsEvidenceOfPositiveBoostAndNeverAppliesToEv(int cylinders, float first, float last, bool available)
    {
        var samples = new[] { Sample(0), Sample(0.1) };
        samples[0] = samples[0] with { State = samples[0].State with { NumCylinders = cylinders, BoostPressurePsi = first } };
        samples[1] = samples[1] with { State = samples[1].State with { NumCylinders = cylinders, BoostPressurePsi = last } };
        var stats = RunAnalysis.BuildReport(Run(samples)).Statistics;
        Assert.Equal(available, stats.PeakBoostPsi.HasValue);
        if (available) Assert.Equal(last, stats.PeakBoostPsi);
    }

    [Fact]
    public void VacuumRemainsARealReadingOnceBoostIsEstablishedElsewhereInTheRun()
    {
        var samples = new[] { Sample(0), Sample(0.1), Sample(0.2) };
        for (var i = 0; i < samples.Length; i++)
            samples[i] = samples[i] with { State = samples[i].State with { BoostPressurePsi = i == 2 ? 5 : -10 } };
        Assert.Equal(-10, RunAnalysis.BuildReport(Run(samples), interval: new(0, 0.1)).Statistics.PeakBoostPsi);
    }

    [Fact]
    public void UnavailableTemperaturesStayNullInsteadOfZeroOrLaterValidValue()
    {
        var samples = new[] { Sample(0), Sample(0.1), Sample(0.2) };
        samples[0] = samples[0] with { State = samples[0].State with { TireTemperatureFahrenheit = default } };
        samples[2] = samples[2] with { State = samples[2].State with { TireTemperatureFahrenheit = new(120, 0, 120, 120) } };
        var report = RunAnalysis.BuildReport(Run(samples));
        Assert.Null(report.Statistics.StartingFrontTemperatureFahrenheit);
        Assert.Null(report.Statistics.StartingRearTemperatureFahrenheit);
        Assert.Null(report.Statistics.EndingFrontTemperatureFahrenheit);
        Assert.Equal(120, report.Statistics.EndingRearTemperatureFahrenheit);
        Assert.Contains("unavailable", report.QualityNote);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("warmer"));
    }

    [Fact]
    public void ClippedSignedAccelerationInterpolatesBeforeTakingMagnitude()
    {
        var a = Sample(0) with { State = Sample(0).State with { LateralAccelerationMetersPerSecondSquared = -9.80665f } };
        var b = Sample(0.2) with { State = Sample(0.2).State with { LateralAccelerationMetersPerSecondSquared = 9.80665f } };
        var stats = RunAnalysis.BuildReport(Run(a, b), interval: new(0.09, 0.11)).Statistics;
        Assert.InRange(stats.PeakLateralG!.Value, 0.09999, 0.10001);
    }

    [Fact]
    public void AccelerationUsesAdjacentThresholdInterpolationAndExactSelectedBoundaries()
    {
        var run = Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20));
        var result = RunAnalysis.MeasureAcceleration(run, 5, 15)!;
        Assert.Equal(0.1, result.DurationSeconds, 8);
        Assert.Equal(0.05, result.Interval.StartSeconds, 8);
        Assert.Equal(0.15, result.Interval.EndSeconds, 8);
        Assert.NotNull(RunAnalysis.MeasureAcceleration(run, 5, 15, new(0.05, 0.15)));
        Assert.Null(RunAnalysis.MeasureAcceleration(run, 5, 15, new(0.08, 0.18)));
        Assert.Null(RunAnalysis.MeasureAcceleration(run, 5, 15, new(0.02, 0.12)));
    }

    [Theory]
    [InlineData(TransmissionGear.Reverse)]
    [InlineData(TransmissionGear.Neutral)]
    [InlineData(TransmissionGear.Unknown)]
    public void AccelerationCannotTimeThroughNonForwardGear(TransmissionGear gear)
    {
        var middle = Sample(0.1, 10);
        middle = middle with { State = middle.State with { Gear = gear } };
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(Sample(0, 0), middle, Sample(0.2, 20)), 5, 15));
    }

    [Fact]
    public void AccelerationCannotBridgeSegmentsOrZeroTimeThresholdJumps()
    {
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20, 1)), 5, 15));
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(Sample(0, 0), Sample(0.1, 3), Sample(0.1, 25), Sample(0.2, 30)), 5, 15));
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(Sample(0, 10), Sample(0.1, 20)), 5, 15));
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(Sample(0, 0), Sample(0.1, 10), Sample(0.1, 25), Sample(0.2, 30), Sample(0.3, 10), Sample(0.4, 20)), 5, 15));
    }

    [Fact]
    public void AccelerationSelectsQuickestCompletePassAndRestartsBelowEntrySpeed()
    {
        var samples = new[] { Sample(0, 0), Sample(0.1, 10), Sample(0.2, 3), Sample(0.3, 15), Sample(0.4, 20) };
        var result = RunAnalysis.MeasureAcceleration(Run(samples), 5, 18)!;
        Assert.Equal(0.2 + 0.1 * 2 / 12, result.Interval.StartSeconds, 8);
        Assert.Equal(0.36, result.Interval.EndSeconds, 8);
        var later = samples.Concat(new[] { Sample(1, 0, 1), Sample(1.1, 20, 1) }).ToArray();
        var best = RunAnalysis.MeasureAcceleration(Run(later), 5, 18)!;
        Assert.Equal(0.065, best.DurationSeconds, 8);
        Assert.True(best.Interval.StartSeconds > 1);
    }

    [Fact]
    public void SustainedWheelspinObservationIsPurposeSpecificAndHasEvidence()
    {
        var run = Run(Steady());
        var acceleration = RunAnalysis.BuildReport(run, RunPurpose.Acceleration);
        var finding = Assert.Single(acceleration.Findings, f => f.Title == "Wheel speed ran ahead of car speed");
        Assert.Equal(new RunInterval(0, 2), finding.Interval);
        Assert.DoesNotContain(RunAnalysis.BuildReport(run, RunPurpose.Drifting).Findings, f => f.Title == finding.Title);
        Assert.InRange(acceleration.Findings.Length, 1, 3);
    }

    [Theory]
    [InlineData("stationary")]
    [InlineData("turning")]
    [InlineData("calibration")]
    [InlineData("short")]
    public void WheelspinCounterexamplesCannotBecomeLaunchFindings(string example)
    {
        var samples = Steady(example == "short" ? 4 : 21);
        samples = samples.Select(s => example switch
        {
            "stationary" => s with { State = s.State with { GroundSpeedMetersPerSecond = 0 } },
            "turning" => s with { State = s.State with { Steering = 40 } },
            "calibration" => s with { RearRadiusMeters = null },
            _ => s
        }).ToArray();
        Assert.DoesNotContain(RunAnalysis.BuildReport(Run(samples), RunPurpose.Acceleration).Findings, f => f.Title.StartsWith("Wheel speed"));
    }

    [Fact]
    public void SeparateShortStretchesCannotCombineAcrossAGap()
    {
        var samples = Steady(4).Concat(Steady(4, 1, 1)).ToArray();
        Assert.DoesNotContain(RunAnalysis.BuildReport(Run(samples), RunPurpose.Acceleration).Findings, f => f.Title.StartsWith("Wheel speed"));
    }

    [Fact]
    public void DriftObservationAnchorsSpeedAndInputChangesWithoutClaimingCause()
    {
        var samples = Enumerable.Range(0, 11).Select(i => Sample(i * 0.1, 20 - i * 0.5f, throttle: (byte)(255 - i * 25))).ToArray();
        var report = RunAnalysis.BuildReport(Run(samples), RunPurpose.Drifting);
        var finding = Assert.Single(report.Findings, f => f.Title.Contains("throttle decreased"));
        Assert.Equal(new RunInterval(0, 1), finding.Interval);
        Assert.Equal(RunEvidenceView.Inputs, finding.EvidenceView);
        Assert.Equal("Car speed and throttle both fell in this section. Inspect the input chart alongside speed.", finding.Detail);
        samples[5] = samples[5] with { IsDriving = false };
        Assert.DoesNotContain(RunAnalysis.BuildReport(Run(samples), RunPurpose.Drifting).Findings, f => f.Title.Contains("throttle decreased"));
    }

    [Fact]
    public void IncompleteRecordingAndRecorderLossAreVisible()
    {
        var report = RunAnalysis.BuildReport(Run(Steady()) with { IsIncomplete = true, DroppedDatagrams = 2, RejectedDatagrams = 1 });
        Assert.Contains("before it was complete", report.QualityNote);
        Assert.Contains("lost", report.QualityNote);
        Assert.Contains("could not be read", report.QualityNote);
    }

    [Fact]
    public void ComparisonShowsConditionDifferencesWithoutAutomaticWinner()
    {
        var a = Run(Steady());
        var b = Run(Steady(41).Select(s => s with
        {
            RearRadiusMeters = 0.35,
            State = s.State with { CarOrdinal = 200, TireTemperatureFahrenheit = new(120, 120, 130, 130), GroundSpeedMetersPerSecond = 15 }
        }).ToArray());
        var comparison = RunAnalysis.Compare(a, b, RunPurpose.Drifting);
        var conditions = comparison.Findings[0];
        Assert.Equal("Check the starting conditions", conditions.Title);
        Assert.Contains("same single car", conditions.Detail);
        Assert.Contains("calibration", conditions.Detail);
        Assert.Contains("different temperatures", conditions.Detail);
        Assert.Contains("different durations", conditions.Detail);
        Assert.DoesNotContain(comparison.Findings, f => f.Title.Contains("winner") || f.Title.Contains("better"));
        Assert.InRange(comparison.Findings.Length, 1, 3);
    }

    [Fact]
    public void ComparisonMeasuresSameSpeedRangeAndLabelsTheQuickestPass()
    {
        var a = Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20));
        var b = Run(Sample(0, 0), Sample(0.1, 20));
        var result = RunAnalysis.Compare(a, b, RunPurpose.Acceleration, speedFromMetersPerSecond: 5, speedToMetersPerSecond: 15);
        Assert.Equal(0.1, result.AccelerationA!.DurationSeconds, 8);
        Assert.Equal(0.05, result.AccelerationB!.DurationSeconds, 8);
        var finding = Assert.Single(result.Findings, f => f.Title.Contains("sooner"));
        Assert.Contains("quickest complete pass", finding.Detail);
        Assert.DoesNotContain("mph", finding.Detail);
    }

    [Fact]
    public void AccelerationComparisonKeepsWheelSpeedDifferenceAlongsideTimingAndTireConditions()
    {
        var a = Run(Enumerable.Range(0, 161).Select(i => Sample(i * 0.05, Math.Min(35, i * 0.25f),
            wheel: Math.Min(35, i * 0.25) + (i < 60 ? 10 : 0), throttle: 255)).ToArray());
        var b = Run(Enumerable.Range(0, 161).Select(i => Sample(i * 0.05, Math.Min(35, i * 0.3f),
            wheel: Math.Min(35, i * 0.3) + (i < 60 ? 3 : 0), throttle: 255) with
        { State = Sample(i * 0.05, Math.Min(35, i * 0.3f), throttle: 255).State with { TireTemperatureFahrenheit = new(150, 150, 160, 160) } }).ToArray());
        var result = RunAnalysis.Compare(a, b, RunPurpose.Acceleration);
        Assert.Equal(3, result.Findings.Length);
        Assert.Contains("different temperatures", result.Findings[0].Detail);
        Assert.Equal("Run B crossed the speed range sooner", result.Findings[1].Title);
        Assert.Equal("Run B had less wheel-speed excess", result.Findings[2].Title);
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("average"));
        Assert.InRange(result.AccelerationA!.DurationSeconds, 3.57, 3.59);
        Assert.InRange(result.AccelerationB!.DurationSeconds, 2.97, 2.99);
    }

    [Fact]
    public void FindingsOpenTheChannelsThatContainTheirEvidence()
    {
        var input = RunAnalysis.BuildReport(Run(Enumerable.Range(0, 21)
            .Select(i => Sample(i * 0.1, throttle: 255, brake: 30)).ToArray()));
        Assert.All(input.Findings, finding => Assert.Equal(RunEvidenceView.Inputs, finding.EvidenceView));
        Assert.Equal(2, input.Findings.Length);
        var tires = RunAnalysis.BuildReport(Run(Enumerable.Range(0, 21).Select(i => Sample(i * 0.1) with
        { State = Sample(i * 0.1).State with { TireTemperatureFahrenheit = new(100, 100, 110 + i, 110 + i) } }).ToArray()));
        Assert.Equal(RunEvidenceView.Tires, Assert.Single(tires.Findings).EvidenceView);
        var launch = RunAnalysis.BuildReport(Run(Steady()), RunPurpose.Acceleration);
        Assert.Equal(RunEvidenceView.Speed, Assert.Single(launch.Findings, f => f.Title.StartsWith("Wheel speed")).EvidenceView);
    }

    [Fact]
    public void RepeatedShiftPullShowsTotalThrottleTimeAsWellAsItsLongestStretch()
    {
        var samples = Enumerable.Range(0, 241).Select(i =>
        {
            var time = i * 0.05;
            var shift = i >= 40 && i % 40 < 3;
            return Sample(time, 10 + i * 0.15f, wheel: 10 + i * 0.15, throttle: shift ? (byte)0 : (byte)255) with
            { State = Sample(time, 10 + i * 0.15f, throttle: shift ? (byte)0 : (byte)255).State with { Gear = (TransmissionGear)(2 + i / 40) } };
        }).ToArray();
        var report = RunAnalysis.BuildReport(Run(samples), RunPurpose.Acceleration);
        var finding = Assert.Single(report.Findings, f => f.Title == "Longest full-throttle stretch");
        Assert.InRange(report.Statistics.FullThrottleSeconds, 10.9, 11.1);
        Assert.Equal(0, finding.Interval!.Value.StartSeconds, 8);
        Assert.Equal(1.95, finding.Interval.Value.EndSeconds, 8);
        Assert.StartsWith("Full throttle totaled ", finding.Detail);
        Assert.Contains("The longest uninterrupted stretch lasted 1.95 seconds.", finding.Detail);
        Assert.Equal(RunEvidenceView.Inputs, finding.EvidenceView);
    }

    [Theory]
    [InlineData(true, false, "Run A needs")]
    [InlineData(false, true, "Run B needs")]
    [InlineData(true, true, "Both Run A and Run B need")]
    public void MissingSpeedRangeIdentifiesTheSelectionThatNeedsAdjustment(bool missingA, bool missingB, string prefix)
    {
        var run = Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20));
        var result = RunAnalysis.Compare(run, run, RunPurpose.Acceleration,
            intervalA: missingA ? new RunInterval(0.1, 0.2) : null,
            intervalB: missingB ? new RunInterval(0.1, 0.2) : null,
            speedFromMetersPerSecond: 5, speedToMetersPerSecond: 15);
        var finding = Assert.Single(result.Findings, f => f.Title == "A matching acceleration interval is missing");
        Assert.StartsWith(prefix, finding.Detail);
        Assert.Equal(missingA, result.AccelerationA is null);
        Assert.Equal(missingB, result.AccelerationB is null);
    }

    [Fact]
    public void AccelerationComparisonDoesNotPromoteUncalibratedWheelSpeedDifferences()
    {
        var a = Run(Steady());
        var b = Run(Steady().Select(s => s with { WheelSpeedMetersPerSecond = 10, RearRadiusMeters = null }).ToArray());
        var result = RunAnalysis.Compare(a, b, RunPurpose.Acceleration);
        Assert.DoesNotContain(result.Findings, f => f.Title.Contains("less wheel-speed"));
        Assert.Contains(result.Findings, f => f.Detail.Contains("calibration"));
    }

    [Fact]
    public void SubsampleIntervalComparisonRetainsCarAndCalibrationContext()
    {
        var run = Run(Sample(0, 0), Sample(0.1, 10), Sample(0.2, 20));
        var result = RunAnalysis.Compare(run, run, intervalA: new(0.02, 0.08), intervalB: new(0.02, 0.08));
        Assert.DoesNotContain(result.Findings, f => f.Title == "Check the starting conditions");
        Assert.Equal(5, result.RunA.Statistics.AverageSpeedMetersPerSecond!.Value, 8);
    }

    [Fact]
    public void EmptyAndOutOfBoundsSelectionsHaveNoInventedMeasurements()
    {
        var empty = RunAnalysis.BuildReport(Run());
        Assert.Equal(0, empty.Statistics.SampleCount);
        Assert.Null(empty.Statistics.AverageSpeedMetersPerSecond);
        Assert.Null(RunAnalysis.MeasureAcceleration(Run(), 5, 15));
        var outside = RunAnalysis.BuildReport(Run(Sample(0), Sample(0.1)), interval: new(10, 20));
        Assert.Equal(0, outside.Statistics.RecordedSeconds);
        Assert.Null(outside.Statistics.AverageSpeedMetersPerSecond);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    [InlineData(double.NaN, 10)]
    [InlineData(0, double.PositiveInfinity)]
    public void InvalidSpeedRangesFailClearly(double from, double to) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RunAnalysis.MeasureAcceleration(Run(), from, to));

    [Fact]
    public void InvalidIntervalsFailClearly() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RunAnalysis.BuildReport(Run(Sample(0), Sample(0.1)), interval: new(1, 0)));

    [Fact]
    public void TenMinuteRecordingProducesStableFiniteStatisticsAndComparison()
    {
        var run = Run(Enumerable.Range(0, 60_001).Select(i => Sample(i * 0.01, 20, throttle: 255)).ToArray());
        var comparison = RunAnalysis.Compare(run, run, RunPurpose.Drifting);
        Assert.Equal(600, comparison.RunA.Statistics.RecordedSeconds, 6);
        Assert.Equal(12_000, comparison.RunA.Statistics.DistanceMeters, 5);
        Assert.Equal(20, comparison.RunA.Statistics.AverageSpeedMetersPerSecond!.Value, 7);
        Assert.Equal(comparison.RunA.Statistics, comparison.RunB.Statistics);
        Assert.Equal(0, comparison.RunA.Statistics.GapCount);
    }
}
