using Xunit;

namespace Wisp.Core.Tests;

public sealed class RollingRadiusEstimatorTests
{
    [Fact]
    public void DefaultCalibrationConvergesInTwelveCleanFrames()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
            if (index < CalibrationOptions.DefaultMinimumSamples - 1)
            {
                Assert.Null(result.RadiusMeters);
            }
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
        var persisted = Assert.Single(estimator.ExportSnapshots());
        Assert.Equal(CalibrationOptions.DefaultMinimumSamples, persisted.SampleCount);
        Assert.Equal(0.3, persisted.RadiusMeters, 6);
    }

    [Fact]
    public void ConvergesOnLowSlipSamples()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < 40; index++)
        {
            var jitter = index % 3 == 0 ? 0.2f : -0.1f;
            result = estimator.Observe(TestVehicleState.Create(
                wheelSpeed: new WheelValues(100 + jitter, 100 - jitter, 100 + jitter, 100 - jitter)));
        }

        Assert.True(result.IsCalibrated);
        Assert.InRange(result.RadiusMeters!.Value, 0.299, 0.301);
    }

    [Fact]
    public void NormalizedBelowGripNoiseConvergesInOneShortWindow()
    {
        const float trueRadius = 0.318f;
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            var angularSpeed = 86f + ((index % 4) * 0.35f);
            var radiusNoise = 1f + (((index % 5) - 2) * 0.0015f);
            var slipRatio = 0.05f + ((index % 4) * 0.015f);
            var slipAngle = 0.08f + ((index % 3) * 0.035f);
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: angularSpeed * trueRadius * radiusNoise,
                wheelSpeed: new WheelValues(
                    angularSpeed - 0.08f,
                    angularSpeed + 0.08f,
                    angularSpeed - 0.08f,
                    angularSpeed + 0.08f),
                slipRatio: new WheelValues(slipRatio, slipRatio, slipRatio, slipRatio),
                slipAngle: new WheelValues(slipAngle, slipAngle, slipAngle, slipAngle),
                acceleration: 2.4f) with
            {
                Steering = 6
            });
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(CalibrationOptions.DefaultMinimumSamples, result.AcceptedSamples);
        Assert.InRange(result.RadiusMeters!.Value, 0.317, 0.319);
    }

    [Fact]
    public void RejectsCalibrationDuringWheelspin()
    {
        var estimator = new RollingRadiusEstimator();
        var spinning = TestVehicleState.Create(
            wheelSpeed: new WheelValues(180, 180, 180, 180),
            slipRatio: new WheelValues(1.2f, 1.2f, 1.2f, 1.2f));

        for (var index = 0; index < 100; index++)
        {
            estimator.Observe(spinning);
        }

        var result = estimator.Get(spinning.CarOrdinal);
        Assert.False(result.IsCalibrated);
        Assert.Equal(0, result.AcceptedSamples);
    }

    [Fact]
    public void SlipBeyondTheConservativeGripWindowIsRejectedForRadiusLearning()
    {
        var estimator = new RollingRadiusEstimator();
        var result = estimator.Observe(TestVehicleState.Create(
            slipRatio: new WheelValues(0.13f, 0.13f, 0.13f, 0.13f)));

        Assert.False(result.SampleAccepted);
        Assert.Equal("Tire slip ratio", result.RejectionReason);
        Assert.Null(result.ProvisionalRadiusMeters);
    }

    [Fact]
    public void LongDriftCannotCreateACalibrationProfile()
    {
        var estimator = new RollingRadiusEstimator();
        var drifting = TestVehicleState.Create(
            groundSpeed: 22,
            wheelSpeed: new WheelValues(75, 75, 210, 205),
            slipRatio: new WheelValues(0.03f, 0.03f, 0.72f, 0.68f),
            slipAngle: new WheelValues(0.35f, 0.35f, 0.82f, 0.79f)) with
        {
            Steering = 62,
            LateralAccelerationMetersPerSecondSquared = 6.2f
        };

        for (var index = 0; index < 600; index++)
        {
            estimator.Observe(drifting);
        }

        var result = estimator.Get(drifting.CarOrdinal);
        Assert.False(result.IsTrusted);
        Assert.Equal(0, result.AcceptedSamples);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void StaticHighSlipAngleFromAggressiveAlignmentDoesNotBlockCleanRollingCalibration()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100),
                slipRatio: new WheelValues(0.03f, 0.03f, 0.04f, 0.04f),
                slipAngle: new WheelValues(0.75f, 0.78f, 0.92f, 0.95f)));
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void HighSlipAngleCorneringCannotRatchetATrustedRadiusIntoATuneChange()
    {
        var estimator = new RollingRadiusEstimator();
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            estimator.Observe(TestVehicleState.Create());
        }

        CalibrationResult result = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples * 4; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 36,
                wheelSpeed: new WheelValues(100, 100, 100, 100),
                slipRatio: new WheelValues(0.03f, 0.03f, 0.04f, 0.04f),
                slipAngle: new WheelValues(0.7f, 0.7f, 0.9f, 0.9f)) with
            {
                Steering = 45,
                LateralAccelerationMetersPerSecondSquared = 4.5f
            });
        }

        Assert.True(result.IsTrusted);
        Assert.False(result.SampleAccepted);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
        Assert.Equal(0.3, estimator.ExportSnapshots().Single().RadiusMeters, 6);
    }

    [Theory]
    [InlineData(155.0)]
    [InlineData(250.0)]
    public void UltraCleanReplacementRemovesMaterialHighSpeedBiasAfterNinetySixSamples(
        double groundMilesPerHour)
    {
        const double trueRadius = 0.3;
        const double staleRadius = trueRadius * 1.024;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var state = UltraCleanRollingState(groundMilesPerHour, trueRadius);

        CalibrationResult result = default;
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples;
             index++)
        {
            result = estimator.Observe(state);
            Assert.True(result.IsTrusted);
            if (index < CalibrationOptions.DefaultReplacementMinimumSamples - 1)
            {
                Assert.Equal(staleRadius, result.RadiusMeters!.Value, 8);
                Assert.Equal("Trusted replacement consensus pending", result.RejectionReason);
            }
        }

        Assert.Equal(trueRadius, result.RadiusMeters!.Value, 6);
        var corrected = Assert.Single(estimator.ExportSnapshots());
        Assert.Equal(trueRadius, corrected.RadiusMeters, 6);

        var indicated = new SpeedModel().CalculateWithRadii(
            state,
            result.TrustedRadii,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            smoothing: 0,
            elapsed: TimeSpan.FromMilliseconds(16));
        Assert.True(indicated.IsAvailable);
        Assert.InRange(Math.Abs(indicated.DisplayValue - groundMilesPerHour), 0, 0.01);
    }

    [Fact]
    public void DriftFramesDoNotCountOrPoisonAcceptedReplacementSamples()
    {
        const double trueRadius = 0.3;
        const double staleRadius = trueRadius * 1.024;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var clean = UltraCleanRollingState(250, trueRadius);

        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples - 1;
             index++)
        {
            var refining = estimator.Observe(clean);
            Assert.True(refining.IsTrusted);
            Assert.Equal(staleRadius, refining.RadiusMeters!.Value, 8);
        }

        var drift = clean with
        {
            TireSlipRatio = new WheelValues(0.8f, 0.8f, 0.8f, 0.8f),
            Steering = 55,
            LateralAccelerationMetersPerSecondSquared = 5
        };
        for (var index = 0; index < 200; index++)
        {
            var ignored = estimator.Observe(drift);
            Assert.True(ignored.IsTrusted);
            Assert.False(ignored.SampleAccepted);
            Assert.Equal(staleRadius, ignored.RadiusMeters!.Value, 8);
        }

        var result = estimator.Observe(clean);
        Assert.Equal(trueRadius, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void TelemetrySessionBoundaryClearsPartialReplacementEvidence()
    {
        const double trueRadius = 0.3;
        const double staleRadius = trueRadius * 1.024;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var clean = UltraCleanRollingState(250, trueRadius);

        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples - 1;
             index++)
        {
            estimator.Observe(clean);
        }

        estimator.EndTelemetrySession();
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples - 1;
             index++)
        {
            var restarted = estimator.Observe(clean);
            Assert.Equal(staleRadius, restarted.RadiusMeters!.Value, 8);
        }

        var corrected = estimator.Observe(clean);
        Assert.Equal(trueRadius, corrected.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void StaleTelemetryGapClearsPartialReplacementEvidence()
    {
        const double trueRadius = 0.3;
        const double staleRadius = trueRadius * 1.024;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var clean = UltraCleanRollingState(250, trueRadius);

        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples - 1;
             index++)
        {
            estimator.Observe(clean);
        }

        var gap = estimator.Observe(clean, isFresh: false);
        Assert.Equal("Stale telemetry", gap.RejectionReason);
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples - 1;
             index++)
        {
            var restarted = estimator.Observe(clean);
            Assert.Equal(staleRadius, restarted.RadiusMeters!.Value, 8);
        }

        var corrected = estimator.Observe(clean);
        Assert.Equal(trueRadius, corrected.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ConfirmedBaselineClosesReplacementForRestOfSession()
    {
        const double radius = 0.3;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, radius) });
        var baseline = UltraCleanRollingState(155, radius);

        for (var index = 0;
             index < CalibrationOptions.DefaultBaselineConfirmationSamples;
             index++)
        {
            estimator.Observe(baseline);
        }

        var biased = UltraCleanRollingState(250, radius * 1.024);
        CalibrationResult result = default;
        for (var index = 0; index < 10_000; index++)
        {
            result = estimator.Observe(biased);
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(radius, result.RadiusMeters!.Value, 8);
        Assert.Equal(radius, estimator.ExportSnapshots().Single().RadiusMeters, 8);
    }

    [Fact]
    public void SubHalfPercentReplacementRoundTripsThroughSnapshot()
    {
        const double trueRadius = 0.3;
        const double staleRadius = trueRadius * 1.003;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var clean = UltraCleanRollingState(250, trueRadius);

        CalibrationResult corrected = default;
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples;
             index++)
        {
            corrected = estimator.Observe(clean);
        }

        Assert.Equal(trueRadius, corrected.RadiusMeters!.Value, 6);
        var snapshot = Assert.Single(estimator.ExportSnapshots());
        Assert.Equal(RollingRadiusEstimator.CurrentCalibrationRevision, snapshot.CalibrationRevision);

        var restored = new RollingRadiusEstimator();
        restored.ImportSnapshots(new[] { snapshot });
        Assert.Equal(trueRadius, restored.Get(100).RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ExhaustedSplitReplacementWindowCannotRatchetUntilNextSession()
    {
        const double firstCandidateRadius = 0.3;
        const double secondCandidateRadius = 0.3024;
        const double staleRadius = firstCandidateRadius * 1.024;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var first = UltraCleanRollingState(250, firstCandidateRadius);
        var second = UltraCleanRollingState(250, secondCandidateRadius);

        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementWindowSamples;
             index++)
        {
            var result = estimator.Observe(index % 2 == 0 ? first : second);
            Assert.True(result.IsTrusted);
            Assert.Equal(staleRadius, result.RadiusMeters!.Value, 8);
        }

        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples * 2;
             index++)
        {
            var ineligible = estimator.Observe(first);
            Assert.Equal(staleRadius, ineligible.RadiusMeters!.Value, 8);
        }

        Assert.Equal(staleRadius, estimator.ExportSnapshots().Single().RadiusMeters, 8);

        estimator.EndTelemetrySession();
        CalibrationResult rearmed = default;
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples;
             index++)
        {
            rearmed = estimator.Observe(first);
        }

        Assert.Equal(firstCandidateRadius, rearmed.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ReplacementRequiresNinetyPercentConsensusInAcceptedWindow()
    {
        const double trueRadius = 0.3;
        const double outlierRadius = 0.31;
        const double staleRadius = 0.33;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, staleRadius) });
        var outlier = UltraCleanRollingState(250, outlierRadius);
        var clean = UltraCleanRollingState(250, trueRadius);

        for (var index = 0; index < 4; index++)
        {
            estimator.Observe(outlier);
        }

        CalibrationResult result = default;
        for (var index = 0; index < 96; index++)
        {
            result = estimator.Observe(clean);
            if (index < 95)
            {
                Assert.Equal(staleRadius, result.RadiusMeters!.Value, 8);
            }
        }

        Assert.Equal(trueRadius, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void FullAwdReplacementWindowDoesNotAllocatePerFrame()
    {
        const double staleFrontRadius = 0.33;
        const double staleRearRadius = 0.38;
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[]
        {
            new CalibrationSnapshot(
                100,
                (staleFrontRadius + staleRearRadius) * 0.5,
                121,
                DrivetrainType.AllWheelDrive,
                RollingRadiusEstimator.CurrentCalibrationRevision,
                staleFrontRadius,
                staleRearRadius)
        });
        var first = UltraCleanAwdRollingState(155, 0.3, 0.35);
        var second = UltraCleanAwdRollingState(155, 0.304, 0.344);

        for (var index = 0; index < 220; index++)
        {
            estimator.Observe(index % 2 == 0 ? first : second);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
        {
            estimator.Observe(index % 2 == 0 ? first : second);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.InRange(allocated, 0, 4_096);
        var retained = estimator.Get(100);
        Assert.Equal(staleFrontRadius, retained.TrustedRadii!.Value.FrontMeters, 8);
        Assert.Equal(staleRearRadius, retained.TrustedRadii.Value.RearMeters, 8);
    }

    [Fact]
    public void BriefCleanRollingSegmentAfterDriftCompletesCalibration()
    {
        const float trueRadius = 0.325f;
        var estimator = new RollingRadiusEstimator();

        for (var index = 0; index < 300; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 18,
                wheelSpeed: new WheelValues(60, 60, 190, 185),
                slipRatio: new WheelValues(0.04f, 0.04f, 0.85f, 0.78f),
                slipAngle: new WheelValues(0.3f, 0.3f, 0.9f, 0.86f)) with
            {
                Steering = 55,
                LateralAccelerationMetersPerSecondSquared = 5.5f
            });
        }

        CalibrationResult result = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            var angularSpeed = 78f + ((index % 3) * 0.25f);
            var radiusNoise = 1f + (((index % 4) - 1.5f) * 0.001f);
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: angularSpeed * trueRadius * radiusNoise,
                wheelSpeed: new WheelValues(
                    angularSpeed,
                    angularSpeed,
                    angularSpeed - 0.05f,
                    angularSpeed + 0.05f),
                slipRatio: new WheelValues(0.04f, 0.04f, 0.08f, 0.09f),
                slipAngle: new WheelValues(0.06f, 0.06f, 0.11f, 0.12f),
                acceleration: 1.2f));
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(CalibrationOptions.DefaultMinimumSamples, result.AcceptedSamples);
        Assert.InRange(result.RadiusMeters!.Value, 0.324, 0.326);
    }

    [Fact]
    public void InconsistentCandidateRadiiCannotReachTrustQuorum()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < 120; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: index % 2 == 0 ? 27 : 34,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        Assert.False(result.IsTrusted);
        Assert.Equal(12, result.AcceptedSamples);
        Assert.Equal("Candidate radius outlier", result.RejectionReason);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void InterleavedTelemetryOutliersDoNotEraseAStableConsensus()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var cycle = 0; cycle < 4; cycle++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 33,
                wheelSpeed: new WheelValues(100, 100, 100, 100),
                slipRatio: new WheelValues(0.1f, 0.1f, 0.1f, 0.1f)));
            for (var clean = 0; clean < 3; clean++)
            {
                result = estimator.Observe(TestVehicleState.Create(
                    groundSpeed: 30,
                    wheelSpeed: new WheelValues(100, 100, 100, 100)));
            }
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(12, result.AcceptedSamples);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ConsistentButSlippingCandidatesCannotBiasCalibration()
    {
        var estimator = new RollingRadiusEstimator();

        for (var index = 0; index < 120; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(150, 150, 150, 150),
                slipRatio: new WheelValues(0.26f, 0.26f, 0.26f, 0.26f)));
        }

        var result = estimator.Get(100);
        Assert.False(result.IsTrusted);
        Assert.Equal(0, result.AcceptedSamples);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void KeepsCalibrationIndependentAcrossCars()
    {
        var options = new CalibrationOptions { MinimumSamples = 5 };
        var estimator = new RollingRadiusEstimator(options);

        var secondCarBefore = estimator.Get(20);
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                carOrdinal: 10,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(carOrdinal: 20, groundSpeed: 35, wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        CalibrationResult firstCarVerified = default;
        CalibrationResult secondCarVerified = default;
        for (var index = 0; index < 3; index++)
        {
            firstCarVerified = estimator.Observe(TestVehicleState.Create(
                carOrdinal: 10,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        for (var index = 0; index < 3; index++)
        {
            secondCarVerified = estimator.Observe(TestVehicleState.Create(
                carOrdinal: 20,
                groundSpeed: 35,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        Assert.False(secondCarBefore.IsCalibrated);
        Assert.Equal(0.3, firstCarVerified.RadiusMeters!.Value, 6);
        Assert.Equal(0.35, secondCarVerified.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void LargeTrustedRadiusChangeRequiresNewSessionAndFullReplacementQuorum()
    {
        var options = new CalibrationOptions
        {
            MinimumSamples = 5,
            MaximumSamples = 20
        };
        var estimator = new RollingRadiusEstimator(options);
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(groundSpeed: 30, wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        CalibrationResult sameSession = default;
        for (var index = 0; index < 200; index++)
        {
            sameSession = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 36,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        Assert.True(sameSession.IsTrusted);
        Assert.Equal(0.3, sameSession.RadiusMeters!.Value, 6);

        estimator.EndTelemetrySession();
        CalibrationResult changed = default;
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples;
             index++)
        {
            changed = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 36,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
            if (index < CalibrationOptions.DefaultReplacementMinimumSamples - 1)
            {
                Assert.Equal(0.3, changed.RadiusMeters!.Value, 6);
            }
        }

        Assert.True(changed.SampleAccepted);
        Assert.True(changed.IsTrusted);
        Assert.Equal(0.36, changed.RadiusMeters!.Value, 6);
        Assert.Equal(string.Empty, changed.RejectionReason);
        Assert.Equal(0.36, estimator.ExportSnapshots().Single().RadiusMeters, 6);
    }

    [Fact]
    public void TrustedRadiusCannotRatchetThroughIndividuallySmallSessionChanges()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions
        {
            MinimumSamples = 3,
            MaximumSamples = 20
        });
        for (var index = 0; index < 3; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var withinTolerance = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 30.7f,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));
        var beyondStoredTolerance = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 31.4f,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));

        Assert.True(withinTolerance.IsTrusted);
        Assert.Equal(0.3, withinTolerance.RadiusMeters!.Value, 6);
        Assert.True(beyondStoredTolerance.SampleAccepted);
        Assert.Equal(string.Empty, beyondStoredTolerance.RejectionReason);
        Assert.True(beyondStoredTolerance.IsTrusted);
        Assert.Equal(0.3, beyondStoredTolerance.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ProductionDefaultsKeepTrustedRadiusImmutableDuringLongBiasedSequence()
    {
        var estimator = new RollingRadiusEstimator();
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var initiallyTrusted = Assert.Single(estimator.ExportSnapshots());

        CalibrationResult result = default;
        for (var index = 0; index < 5_000; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30.72f,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        Assert.True(result.IsTrusted);
        Assert.True(result.SampleAccepted);
        Assert.Equal(1, result.Confidence);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
        Assert.Equal(initiallyTrusted.SampleCount, result.AcceptedSamples);
        Assert.Equal(initiallyTrusted, Assert.Single(estimator.ExportSnapshots()));
    }

    [Fact]
    public void RejectsZeroSpeed()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 0,
            wheelSpeed: new WheelValues(0, 0, 0, 0)));

        Assert.False(result.SampleAccepted);
        Assert.False(result.IsCalibrated);
    }

    [Fact]
    public void ReturningToKnownCarRetainsTrustedRadiusDuringWheelspin()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 5 });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(carOrdinal: 10, groundSpeed: 30));
        }

        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                carOrdinal: 20,
                groundSpeed: 35,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var returnedCarDuringWheelspin = estimator.Observe(TestVehicleState.Create(
            carOrdinal: 10,
            groundSpeed: 30,
            slipRatio: new WheelValues(1.2f, 1.2f, 1.2f, 1.2f)));
        Assert.False(returnedCarDuringWheelspin.SampleAccepted);
        Assert.True(returnedCarDuringWheelspin.IsCalibrated);
        Assert.Equal(0.3, returnedCarDuringWheelspin.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ExportedCarProfileIsImmediatelyTrustedAfterRestart()
    {
        var options = new CalibrationOptions { MinimumSamples = 5 };
        var firstSession = new RollingRadiusEstimator(options);
        for (var index = 0; index < 5; index++)
        {
            firstSession.Observe(TestVehicleState.Create(carOrdinal: 10, groundSpeed: 30));
        }

        var nextSession = new RollingRadiusEstimator(options);
        nextSession.ImportSnapshots(firstSession.ExportSnapshots());

        var restoredDuringWheelspin = nextSession.Observe(TestVehicleState.Create(
            carOrdinal: 10,
            groundSpeed: 30,
            slipRatio: new WheelValues(1.2f, 1.2f, 1.2f, 1.2f)));

        Assert.True(restoredDuringWheelspin.IsCalibrated);
        Assert.Equal(0.3, restoredDuringWheelspin.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void FirstCleanSampleProvidesAProvisionalRadius()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));

        Assert.True(result.SampleAccepted);
        Assert.False(result.IsCalibrated);
        Assert.Null(result.RadiusMeters);
        Assert.Equal(0.3, result.ProvisionalRadiusMeters!.Value, 6);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void RwdCalibrationUsesRearWheelsWhenTireSizesAreStaggered()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < 40; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.RearWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(80, 80, 100, 100)));
        }

        Assert.True(result.IsCalibrated);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void StrongAccelerationIsRejectedForRadiusLearning()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 30,
            wheelSpeed: new WheelValues(120, 120, 120, 120),
            acceleration: 7));

        Assert.False(result.SampleAccepted);
        Assert.Null(result.RadiusMeters);
        Assert.Null(result.ProvisionalRadiusMeters);
        Assert.Equal("Longitudinal acceleration", result.RejectionReason);
    }

    [Fact]
    public void ModerateCleanAccelerationCanContributeToCalibration()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult result = default;

        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(acceleration: 3));
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void MeaningfulSteeringIsRejectedForRadiusLearning()
    {
        var estimator = new RollingRadiusEstimator();
        var result = estimator.Observe(TestVehicleState.Create() with { Steering = 17 });

        Assert.False(result.SampleAccepted);
        Assert.Equal("Steering input", result.RejectionReason);
    }

    [Fact]
    public void CorneringAccelerationIsRejectedForRadiusLearning()
    {
        var estimator = new RollingRadiusEstimator();
        var result = estimator.Observe(TestVehicleState.Create() with
        {
            LateralAccelerationMetersPerSecondSquared = 2.0f
        });

        Assert.False(result.SampleAccepted);
        Assert.Equal("Cornering acceleration", result.RejectionReason);
    }

    [Fact]
    public void StrongBrakingIsRejectedForRadiusLearning()
    {
        var estimator = new RollingRadiusEstimator();
        var decelerating = estimator.Observe(TestVehicleState.Create(acceleration: -5));
        var brakeApplied = estimator.Observe(TestVehicleState.Create(brake: 96));

        Assert.False(decelerating.SampleAccepted);
        Assert.Equal("Longitudinal acceleration", decelerating.RejectionReason);
        Assert.False(brakeApplied.SampleAccepted);
        Assert.Equal("Braking input", brakeApplied.RejectionReason);
    }

    [Fact]
    public void LightBrakingCannotBakeAPositiveSpeedBiasIntoTheTireProfile()
    {
        const double trueRadius = 0.3;
        const float groundSpeed = 30;
        var estimator = new RollingRadiusEstimator();
        var brakedWheelSpeed = (float)(groundSpeed / trueRadius * 0.98);
        CalibrationResult biased = default;

        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            biased = estimator.Observe(TestVehicleState.Create(
                groundSpeed: groundSpeed,
                wheelSpeed: new WheelValues(
                    brakedWheelSpeed,
                    brakedWheelSpeed,
                    brakedWheelSpeed,
                    brakedWheelSpeed),
                acceleration: -1.5f,
                brake: 16));
        }

        Assert.False(biased.IsTrusted);
        Assert.Equal("Longitudinal deceleration", biased.RejectionReason);
        Assert.Empty(estimator.ExportSnapshots());

        var cleanWheelSpeed = (float)(groundSpeed / trueRadius);
        CalibrationResult clean = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            clean = estimator.Observe(TestVehicleState.Create(
                groundSpeed: groundSpeed,
                wheelSpeed: new WheelValues(
                    cleanWheelSpeed,
                    cleanWheelSpeed,
                    cleanWheelSpeed,
                    cleanWheelSpeed)));
        }

        Assert.True(clean.IsTrusted);
        Assert.Equal(trueRadius, clean.RadiusMeters!.Value, 6);
        var indicated = new SpeedModel().CalculateWithRadii(
            TestVehicleState.Create(
                groundSpeed: groundSpeed,
                wheelSpeed: new WheelValues(
                    cleanWheelSpeed,
                    cleanWheelSpeed,
                    cleanWheelSpeed,
                    cleanWheelSpeed)),
            clean.TrustedRadii,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromMilliseconds(16));
        Assert.Equal(
            groundSpeed * SpeedModel.MetersPerSecondToMilesPerHour,
            indicated.DisplayValue,
            4);
    }

    [Fact]
    public void UnloadedDrivenAxleIsRejectedEvenWhenTheOtherAxleIsLoaded()
    {
        var estimator = new RollingRadiusEstimator();
        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive) with
        {
            NormalizedSuspensionTravel = new WheelValues(0.5f, 0.5f, 0.01f, 0.01f)
        });

        Assert.False(result.SampleAccepted);
        Assert.Equal("Driven axle unloaded", result.RejectionReason);
    }

    [Fact]
    public void RwdWheelspinDoesNotSubstituteStaggeredFrontRadiusForRearRadius()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 30,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        var incorrectFrontProxyMph = 200 * (30.0 / 80.0) *
                                     SpeedModel.MetersPerSecondToMilesPerHour;
        var correctRearMph = 200 * 0.3 * SpeedModel.MetersPerSecondToMilesPerHour;

        Assert.False(result.SampleAccepted);
        Assert.False(result.IsCalibrated);
        Assert.Null(result.RadiusMeters);
        Assert.Equal(1.25, incorrectFrontProxyMph / correctRearMph, 6);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void NonDrivenAxleCannotOverrideASavedDrivenRadius()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, 0.36) });

        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 220, 225),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        Assert.True(result.IsCalibrated);
        Assert.Equal(0.36, result.RadiusMeters!.Value, 6);
        Assert.Equal(0.36, estimator.ExportSnapshots().Single().RadiusMeters, 6);
    }

    [Fact]
    public void SameIdCleanMismatchWaitsForConsensusBeforeReplacingSavedProfile()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, 0.36) });

        CalibrationResult result = default;
        for (var index = 0; index < 3; index++)
        {
            result = estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.RearWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(120, 120, 120, 120)));

        }

        Assert.True(result.SampleAccepted);
        Assert.True(result.IsCalibrated);
        Assert.Equal(0.36, result.RadiusMeters!.Value, 6);
        Assert.Equal("Trusted replacement consensus pending", result.RejectionReason);
    }

    [Fact]
    public void NewCarWheelspinIsUnavailableInsteadOfDisplayingTheFormer152MphFallback()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)) with
        {
            Gear = TransmissionGear.Third
        };
        var estimator = new RollingRadiusEstimator();
        var calibration = estimator.Observe(state);
        var speed = new SpeedModel().Calculate(
            state,
            calibration.RadiusMeters,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.Robust,
            0,
            TimeSpan.FromSeconds(1));
        var oldFallbackSpeed = new SpeedModel().Calculate(
            state,
            null,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.Robust,
            0,
            TimeSpan.FromSeconds(1));

        const double formerFallbackRadiusMeters = 0.34;
        var formerFallbackMph = 200 * formerFallbackRadiusMeters *
                                SpeedModel.MetersPerSecondToMilesPerHour;

        Assert.Null(calibration.RadiusMeters);
        Assert.False(speed.IsAvailable);
        Assert.False(oldFallbackSpeed.IsAvailable);
        Assert.Equal(0, speed.DisplayValue);
        Assert.Equal(0, oldFallbackSpeed.DisplayValue);
        Assert.InRange(formerFallbackMph, 151, 153);
    }

    [Fact]
    public void AwdWheelspinWithoutAPreviouslyVerifiedRadiusIsUnavailable()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        Assert.False(result.IsCalibrated);
        Assert.Null(result.RadiusMeters);
    }

    [Fact]
    public void FortyFourPercentCrossAxleErrorIsRefused()
    {
        var estimator = new RollingRadiusEstimator();
        var result = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 18,
            wheelSpeed: new WheelValues(50, 50, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        var nonDrivenRadius = 18.0 / 50.0;
        const double actualDrivenRadius = 0.25;

        Assert.Equal(1.44, nonDrivenRadius / actualDrivenRadius, 6);
        Assert.Null(result.RadiusMeters);
        Assert.False(result.IsCalibrated);
    }

    [Fact]
    public void TrustedReplacementDoesNotReuseInitialCalibrationQuorum()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions
        {
            MinimumSamples = 5,
            MaximumSamples = 20
        });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        estimator.EndTelemetrySession();
        CalibrationResult confirmedChange = default;
        for (var index = 0;
             index < CalibrationOptions.DefaultReplacementMinimumSamples;
             index++)
        {
            confirmedChange = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 36,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
            if (index < CalibrationOptions.DefaultReplacementMinimumSamples - 1)
            {
                Assert.True(confirmedChange.IsCalibrated);
                Assert.Equal(0.3, confirmedChange.RadiusMeters!.Value, 6);
            }
        }

        Assert.Equal(0.36, confirmedChange.RadiusMeters!.Value, 6);
        Assert.True(confirmedChange.IsCalibrated);
        Assert.Equal(string.Empty, confirmedChange.RejectionReason);
    }

    [Fact]
    public void TelemetrySessionBoundaryPreservesTrustedRadiusDuringWheelspin()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 5 });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        estimator.EndTelemetrySession();
        var immediateWheelspin = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        Assert.True(immediateWheelspin.IsCalibrated);
        Assert.Equal(0.3, immediateWheelspin.RadiusMeters!.Value, 6);
        Assert.Null(immediateWheelspin.ProvisionalRadiusMeters);
    }

    [Fact]
    public void MenuCyclePreservesPartialInitialCalibrationProgress()
    {
        var estimator = new RollingRadiusEstimator();
        var firstHalf = CalibrationOptions.DefaultMinimumSamples / 2;
        for (var index = 0; index < firstHalf; index++)
        {
            var groundSpeed = 30 + (index % 2 == 0 ? 0.6f : -0.6f);
            var angularSpeed = groundSpeed / 0.3f;
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: groundSpeed,
                wheelSpeed: new WheelValues(
                    angularSpeed,
                    angularSpeed,
                    angularSpeed,
                    angularSpeed)));
        }

        estimator.EndTelemetrySession();
        CalibrationResult result = default;
        for (var index = firstHalf; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            var groundSpeed = 30 + (index % 2 == 0 ? 0.6f : -0.6f);
            var angularSpeed = groundSpeed / 0.3f;
            result = estimator.Observe(TestVehicleState.Create(
                groundSpeed: groundSpeed,
                wheelSpeed: new WheelValues(
                    angularSpeed,
                    angularSpeed,
                    angularSpeed,
                    angularSpeed)));
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(CalibrationOptions.DefaultMinimumSamples, result.AcceptedSamples);
        Assert.InRange(result.RadiusMeters!.Value, 0.299, 0.301);
    }

    [Fact]
    public void MenuAndStalePacketsDoNotErasePartialCalibrationProgress()
    {
        var estimator = new RollingRadiusEstimator();
        var firstHalf = CalibrationOptions.DefaultMinimumSamples / 2;
        for (var index = 0; index < firstHalf; index++)
        {
            estimator.Observe(TestVehicleState.Create());
        }

        var menuPacket = estimator.Observe(TestVehicleState.Create() with { IsRaceOn = false });
        var stalePacket = estimator.Observe(TestVehicleState.Create(), isFresh: false);
        estimator.EndTelemetrySession();

        CalibrationResult result = default;
        for (var index = firstHalf; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(TestVehicleState.Create());
        }

        Assert.Equal("Not driving", menuPacket.RejectionReason);
        Assert.Equal("Stale telemetry", stalePacket.RejectionReason);
        Assert.True(result.IsTrusted);
        Assert.Equal(CalibrationOptions.DefaultMinimumSamples, result.AcceptedSamples);
        Assert.Equal(0.3, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void RestartedProfileRemainsTrustedAcrossMenuCycle()
    {
        var learned = new RollingRadiusEstimator();
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            learned.Observe(TestVehicleState.Create());
        }

        var restored = new RollingRadiusEstimator();
        restored.ImportSnapshots(learned.ExportSnapshots());
        var first = restored.Observe(TestVehicleState.Create(
            wheelSpeed: new WheelValues(200, 200, 200, 200),
            slipRatio: new WheelValues(1, 1, 1, 1)));
        restored.EndTelemetrySession();
        var resumed = restored.Observe(TestVehicleState.Create(
            wheelSpeed: new WheelValues(200, 200, 200, 200),
            slipRatio: new WheelValues(1, 1, 1, 1)));

        Assert.True(first.IsTrusted);
        Assert.True(resumed.IsTrusted);
        Assert.Equal(0.3, resumed.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void RelearnConvergesInDefaultCleanFrameQuorum()
    {
        var estimator = new RollingRadiusEstimator();
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            estimator.Observe(TestVehicleState.Create());
        }

        Assert.True(estimator.ResetProfile(100));
        CalibrationResult relearned = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            relearned = estimator.Observe(TestVehicleState.Create(
                groundSpeed: 32,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        Assert.True(relearned.IsTrusted);
        Assert.Equal(0.32, relearned.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void RepeatedTabOutGapsNeverWithdrawTrustedProfile()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 5 });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        estimator.EndTelemetrySession();
        var firstResume = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));
        estimator.EndTelemetrySession();
        var secondResume = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 20,
            wheelSpeed: new WheelValues(80, 80, 200, 200),
            slipRatio: new WheelValues(0.02f, 0.02f, 1.1f, 1.2f)));

        Assert.True(firstResume.IsCalibrated);
        Assert.True(secondResume.IsCalibrated);
        Assert.Equal(0.3, firstResume.RadiusMeters!.Value, 6);
        Assert.Equal(0.3, secondResume.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void PendingAwdTuneChangeKeepsTrustedProfileAcrossSessionBoundary()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 5 });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.AllWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var mismatch = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 80, 80)));
        estimator.EndTelemetrySession();
        var wheelspinAfterGap = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(200, 200, 200, 200),
            slipRatio: new WheelValues(1.1f, 1.1f, 1.1f, 1.1f)));

        Assert.Equal(string.Empty, mismatch.RejectionReason);
        Assert.True(mismatch.IsCalibrated);
        Assert.Equal(0.3, mismatch.RadiusMeters!.Value, 6);
        Assert.True(wheelspinAfterGap.IsCalibrated);
        Assert.Equal(0.3, wheelspinAfterGap.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void StaggeredAwdLearnsIndependentFrontAndRearRadii()
    {
        var estimator = new RollingRadiusEstimator();
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 80, 80));

        CalibrationResult result = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(state);
        }

        Assert.True(result.IsTrusted);
        Assert.Equal(0.3, result.TrustedRadii!.Value.FrontMeters, 6);
        Assert.Equal(0.375, result.TrustedRadii.Value.RearMeters, 6);
        Assert.Equal(0.3375, result.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void CapturedStaggeredAwdTelemetryCompletesCalibration()
    {
        var estimator = new RollingRadiusEstimator();
        var state = TestVehicleState.Create(
            carOrdinal: 3766,
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: (float)(16.2 / SpeedModel.MetersPerSecondToMilesPerHour),
            wheelSpeed: new WheelValues(21.4f, 21.6f, 20.4f, 20.6f),
            slipRatio: new WheelValues(0.01f, -0.10f, 0.01f, -0.10f));

        CalibrationResult result = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            result = estimator.Observe(state);
        }

        Assert.True(result.IsTrusted);
        Assert.InRange(result.TrustedRadii!.Value.FrontMeters, 0.336, 0.338);
        Assert.InRange(result.TrustedRadii.Value.RearMeters, 0.352, 0.354);
        var indicated = new SpeedModel().CalculateWithRadii(
            state,
            result.TrustedRadii,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromMilliseconds(16));
        Assert.Equal(16.2, indicated.DisplayValue, 3);
    }

    [Fact]
    public void StaggeredAwdProfileRoundTripsWithBothAxleRadii()
    {
        var learned = new RollingRadiusEstimator();
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            learned.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.AllWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 80, 80)));
        }

        var restored = new RollingRadiusEstimator();
        restored.ImportSnapshots(learned.ExportSnapshots());
        var result = restored.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(200, 200, 120, 120),
            slipRatio: new WheelValues(1, 1, 1, 1)));

        Assert.True(result.IsTrusted);
        Assert.Equal(0.3, result.TrustedRadii!.Value.FrontMeters, 6);
        Assert.Equal(0.375, result.TrustedRadii.Value.RearMeters, 6);
    }

    [Fact]
    public void DrivetrainConversionInvalidatesTheOldDrivenAxleProfile()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 3 });
        for (var index = 0; index < 3; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.RearWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(80, 80, 100, 100)));
        }

        var convertedAndSpinning = estimator.Observe(TestVehicleState.Create(
            drivetrain: DrivetrainType.FrontWheelDrive,
            groundSpeed: 30,
            wheelSpeed: new WheelValues(200, 200, 100, 100),
            slipRatio: new WheelValues(1.1f, 1.2f, 0.02f, 0.02f)));

        Assert.Null(convertedAndSpinning.RadiusMeters);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void ManualProfileResetRemovesTrustedRadiusForATireChange()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 3 });
        for (var index = 0; index < 3; index++)
        {
            estimator.Observe(TestVehicleState.Create(carOrdinal: 100));
        }

        Assert.True(estimator.Get(100).IsCalibrated);
        Assert.True(estimator.ResetProfile(100));
        Assert.False(estimator.Get(100).IsCalibrated);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void LegacyUnversionedSnapshotIsNotTrusted()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[]
        {
            new CalibrationSnapshot(100, 0.36, 121, DrivetrainType.RearWheelDrive)
        });

        Assert.False(estimator.Get(100).IsTrusted);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void PreviousCalibrationRevisionIsInvalidatedForOneTimeSafeRelearning()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[]
        {
            new CalibrationSnapshot(
                100,
                0.36,
                121,
                DrivetrainType.RearWheelDrive,
                RollingRadiusEstimator.CurrentCalibrationRevision - 1)
        });

        Assert.False(estimator.Get(100).IsTrusted);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void CurrentSnapshotWithoutDrivetrainIdentityIsNotTrusted()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[]
        {
            new CalibrationSnapshot(
                100,
                0.36,
                121,
                null,
                RollingRadiusEstimator.CurrentCalibrationRevision)
        });

        Assert.False(estimator.Get(100).IsTrusted);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void SnapshotWithoutEnoughAcceptedSamplesIsNotTrusted()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[]
        {
            new CalibrationSnapshot(
                100,
                0.36,
                CalibrationOptions.DefaultMinimumSamples - 1,
                DrivetrainType.RearWheelDrive,
                RollingRadiusEstimator.CurrentCalibrationRevision)
        });

        Assert.False(estimator.Get(100).IsTrusted);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Theory]
    [InlineData(0.14)]
    [InlineData(0.76)]
    [InlineData(1.2)]
    public void ImplausibleImportedRadiusIsNotTrusted(double radiusMeters)
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { TrustedSnapshot(100, radiusMeters) });

        Assert.False(estimator.Get(100).IsTrusted);
        Assert.Empty(estimator.ExportSnapshots());
    }

    [Fact]
    public void ImplausiblyLargeCandidateRadiusIsRejected()
    {
        var estimator = new RollingRadiusEstimator();

        var result = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 80,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));

        Assert.False(result.SampleAccepted);
        Assert.Equal("Implausible radius", result.RejectionReason);
        Assert.Null(result.ProvisionalRadiusMeters);
    }

    [Fact]
    public void ImplausibleCleanCandidateCannotHideOrReplaceATrustedProfile()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 3 });
        for (var index = 0; index < 3; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var anomalous = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 80,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));
        var recovered = estimator.Observe(TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 100, 100)));

        Assert.False(anomalous.SampleAccepted);
        Assert.Equal("Implausible radius", anomalous.RejectionReason);
        Assert.True(anomalous.IsTrusted);
        Assert.Equal(0.3, anomalous.RadiusMeters!.Value, 6);
        Assert.Equal(0.3, estimator.ExportSnapshots().Single().RadiusMeters, 6);
        Assert.True(recovered.IsTrusted);
        Assert.Equal(0.3, recovered.RadiusMeters!.Value, 6);
    }

    [Fact]
    public void ExportedSnapshotCarriesPhysicsRevisionAndDrivetrain()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 3 });
        for (var index = 0; index < 3; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.RearWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 100, 100)));
        }

        var snapshot = Assert.Single(estimator.ExportSnapshots());
        Assert.Equal(RollingRadiusEstimator.CurrentCalibrationRevision, snapshot.CalibrationRevision);
        Assert.Equal(DrivetrainType.RearWheelDrive, snapshot.Drivetrain);
    }

    private static VehicleState UltraCleanRollingState(
        double groundMilesPerHour,
        double effectiveRadiusMeters)
    {
        var groundMetersPerSecond = groundMilesPerHour /
                                    SpeedModel.MetersPerSecondToMilesPerHour;
        var wheelRadiansPerSecond = groundMetersPerSecond / effectiveRadiusMeters;
        return TestVehicleState.Create(
            groundSpeed: (float)groundMetersPerSecond,
            wheelSpeed: new WheelValues(
                (float)wheelRadiansPerSecond,
                (float)wheelRadiansPerSecond,
                (float)wheelRadiansPerSecond,
                (float)wheelRadiansPerSecond),
            slipRatio: new WheelValues(0.01f, 0.01f, 0.01f, 0.01f),
            slipAngle: new WheelValues(0.9f, 0.9f, 0.9f, 0.9f),
            acceleration: 0,
            brake: 0);
    }

    private static VehicleState UltraCleanAwdRollingState(
        double groundMilesPerHour,
        double frontRadiusMeters,
        double rearRadiusMeters)
    {
        var groundMetersPerSecond = groundMilesPerHour /
                                    SpeedModel.MetersPerSecondToMilesPerHour;
        var frontRadiansPerSecond = groundMetersPerSecond / frontRadiusMeters;
        var rearRadiansPerSecond = groundMetersPerSecond / rearRadiusMeters;
        return TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: (float)groundMetersPerSecond,
            wheelSpeed: new WheelValues(
                (float)frontRadiansPerSecond,
                (float)frontRadiansPerSecond,
                (float)rearRadiansPerSecond,
                (float)rearRadiansPerSecond),
            slipRatio: new WheelValues(0.01f, 0.01f, 0.01f, 0.01f),
            slipAngle: new WheelValues(0.9f, 0.9f, 0.9f, 0.9f),
            acceleration: 0,
            brake: 0);
    }

    private static CalibrationSnapshot TrustedSnapshot(int carOrdinal, double radiusMeters) =>
        new(
            carOrdinal,
            radiusMeters,
            121,
            DrivetrainType.RearWheelDrive,
            RollingRadiusEstimator.CurrentCalibrationRevision);
}
