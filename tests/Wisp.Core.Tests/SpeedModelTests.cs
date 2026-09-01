using Xunit;

namespace Wisp.Core.Tests;

public sealed class SpeedModelTests
{
    [Theory]
    [InlineData(SpeedUnit.MilesPerHour, 30, 67.108088761632)]
    [InlineData(SpeedUnit.MilesPerHour, -30, 67.108088761632)]
    [InlineData(SpeedUnit.KilometersPerHour, 30, 108.0)]
    public void Fh6VehicleSpeedUsesGroundSpeedDirectlyWithoutTireCalibration(
        SpeedUnit unit,
        float groundMetersPerSecond,
        double expected)
    {
        var state = TestVehicleState.Create(
            groundSpeed: groundMetersPerSecond,
            wheelSpeed: new WheelValues(900, 900, 900, 900));

        var speed = new SpeedModel().CalculateVehicleSpeed(state, unit);

        Assert.True(speed.IsAvailable);
        Assert.Equal(30, speed.MetersPerSecond, 12);
        Assert.Equal(expected, speed.DisplayValue, 10);
        Assert.Equal("FH6 vehicle speed", speed.SelectedWheels);
    }

    [Fact]
    public void Fh6VehicleSpeedDoesNotInheritWheelSmoothingState()
    {
        var model = new SpeedModel();
        _ = model.Calculate(
            TestVehicleState.Create(wheelSpeed: new WheelValues(400, 400, 400, 400)),
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            1,
            TimeSpan.FromMilliseconds(16));

        var ground = model.CalculateVehicleSpeed(
            TestVehicleState.Create(groundSpeed: 12),
            SpeedUnit.MilesPerHour);

        Assert.Equal(
            12 * SpeedModel.MetersPerSecondToMilesPerHour,
            ground.DisplayValue,
            12);
    }

    [Fact]
    public void SwitchingThroughFh6VehicleSpeedClearsWheelSmoothingState()
    {
        var model = new SpeedModel();
        var initial = TestVehicleState.Create(wheelSpeed: new WheelValues(100, 100, 100, 100));
        var changed = TestVehicleState.Create(wheelSpeed: new WheelValues(200, 200, 200, 200));

        _ = model.Calculate(
            initial,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            1,
            TimeSpan.FromMilliseconds(16));
        _ = model.CalculateVehicleSpeed(initial, SpeedUnit.MilesPerHour);
        var wheel = model.Calculate(
            changed,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            1,
            TimeSpan.FromMilliseconds(16));

        Assert.Equal(
            200 * 0.3 * SpeedModel.MetersPerSecondToMilesPerHour,
            wheel.DisplayValue,
            10);
    }

    [Theory]
    [InlineData(DrivetrainType.FrontWheelDrive, 0.305, 0.335, 15)]
    [InlineData(DrivetrainType.FrontWheelDrive, 0.305, 0.335, 155)]
    [InlineData(DrivetrainType.RearWheelDrive, 0.305, 0.335, 15)]
    [InlineData(DrivetrainType.RearWheelDrive, 0.305, 0.335, 155)]
    [InlineData(DrivetrainType.AllWheelDrive, 0.305, 0.335, 15)]
    [InlineData(DrivetrainType.AllWheelDrive, 0.305, 0.335, 155)]
    public void FullGripIndicatedSpeedStaysWithinTwoMphOfGroundSpeed(
        DrivetrainType drivetrain,
        double frontRadius,
        double rearRadius,
        double groundMph)
    {
        var groundMetersPerSecond = groundMph / SpeedModel.MetersPerSecondToMilesPerHour;
        var state = TestVehicleState.Create(
            drivetrain: drivetrain,
            groundSpeed: (float)groundMetersPerSecond,
            wheelSpeed: new WheelValues(
                (float)(groundMetersPerSecond / frontRadius),
                (float)(groundMetersPerSecond / frontRadius),
                (float)(groundMetersPerSecond / rearRadius),
                (float)(groundMetersPerSecond / rearRadius)),
            slipRatio: new WheelValues(0, 0, 0, 0));

        var indicated = new SpeedModel().CalculateWithRadii(
            state,
            new RollingRadii(frontRadius, rearRadius),
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            smoothing: 0,
            elapsed: TimeSpan.FromMilliseconds(16));

        Assert.True(indicated.IsAvailable);
        Assert.InRange(Math.Abs(indicated.DisplayValue - groundMph), 0, 2);
    }

    [Fact]
    public void ConvertsMetersPerSecondToMph()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(wheelSpeed: new WheelValues(10, 10, 10, 10));

        var speed = model.Calculate(state, 0.5, SpeedUnit.MilesPerHour, WheelAggregationMode.Robust, 0, TimeSpan.FromSeconds(1));

        Assert.True(speed.IsAvailable);
        Assert.Equal(11.185, speed.DisplayValue, 3);
    }

    [Fact]
    public void ConvertsMetersPerSecondToKph()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(wheelSpeed: new WheelValues(10, 10, 10, 10));

        var speed = model.Calculate(state, 0.5, SpeedUnit.KilometersPerHour, WheelAggregationMode.Robust, 0, TimeSpan.FromSeconds(1));

        Assert.Equal(18, speed.DisplayValue, 6);
    }

    [Fact]
    public void ReverseWheelRotationDisplaysPositiveSpeed()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(wheelSpeed: new WheelValues(-20, -20, -20, -20));

        var speed = model.Calculate(state, 0.3, SpeedUnit.MilesPerHour, WheelAggregationMode.Robust, 0, TimeSpan.FromSeconds(1));

        Assert.True(speed.DisplayValue > 0);
    }

    [Fact]
    public void OppositeDrivenWheelDirectionsProduceZeroCarrierSpeed()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(20, 20, -100, 100));

        var speed = model.Calculate(
            state,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromSeconds(1));

        Assert.True(speed.IsAvailable);
        Assert.Equal(0, speed.DisplayValue);
    }

    [Fact]
    public void ZeroSpeedRemainsZero()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(groundSpeed: 0, wheelSpeed: new WheelValues(0, 0, 0, 0));

        var speed = model.Calculate(state, 0.3, SpeedUnit.MilesPerHour, WheelAggregationMode.Robust, 0, TimeSpan.FromSeconds(1));

        Assert.Equal(0, speed.DisplayValue);
    }

    [Fact]
    public void ImplausiblyHighWheelSpeedIsUnavailableInsteadOfSilentlyClamped()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(wheelSpeed: new WheelValues(100_000, 100_000, 100_000, 100_000));

        var speed = model.Calculate(state, 1.0, SpeedUnit.KilometersPerHour, WheelAggregationMode.Robust, 0, TimeSpan.FromSeconds(1));

        Assert.False(speed.IsAvailable);
        Assert.Equal(0, speed.DisplayValue);
    }

    [Fact]
    public void ImplausibleImpactSpikeKeepsTheLastValidDisplayedSpeed()
    {
        var model = new SpeedModel();
        var normal = TestVehicleState.Create(
            wheelSpeed: new WheelValues(100, 100, 100, 100));
        var impactSpike = TestVehicleState.Create(
            wheelSpeed: new WheelValues(10_000, 10_000, 10_000, 10_000));

        var beforeImpact = model.Calculate(
            normal,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromMilliseconds(16));
        var duringImpact = model.Calculate(
            impactSpike,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromMilliseconds(16));

        Assert.True(duringImpact.IsAvailable);
        Assert.Equal(beforeImpact.DisplayValue, duringImpact.DisplayValue, 12);
    }

    [Fact]
    public void ImplausiblyLargeRollingRadiusIsUnavailable()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(wheelSpeed: new WheelValues(100, 100, 100, 100));

        var speed = model.Calculate(
            state,
            0.8,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromMilliseconds(16));

        Assert.False(speed.IsAvailable);
        Assert.Equal(0, speed.DisplayValue);
    }

    [Fact]
    public void SmoothingCannotOvershootThePreviousAndCurrentRawSpeeds()
    {
        var model = new SpeedModel();
        var fast = TestVehicleState.Create(wheelSpeed: new WheelValues(400, 400, 400, 400));
        var slow = TestVehicleState.Create(wheelSpeed: new WheelValues(100, 100, 100, 100));

        var previous = model.Calculate(
            fast,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            1,
            TimeSpan.FromMilliseconds(16));
        var filtered = model.Calculate(
            slow,
            0.3,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            1,
            TimeSpan.FromMilliseconds(16));
        var currentRaw = 100 * 0.3 * SpeedModel.MetersPerSecondToMilesPerHour;

        Assert.InRange(filtered.DisplayValue, currentRaw, previous.DisplayValue);
    }

    [Theory]
    [InlineData(100, 400)]
    [InlineData(400, 100)]
    public void MaximumSmoothingStaysWithinOnePointFiveMphOfLiveWheelSpeed(
        float firstWheelSpeed,
        float currentWheelSpeed)
    {
        const double radiusMeters = 0.3;
        var model = new SpeedModel();
        model.Calculate(
            TestVehicleState.Create(wheelSpeed: new WheelValues(
                firstWheelSpeed,
                firstWheelSpeed,
                firstWheelSpeed,
                firstWheelSpeed)),
            radiusMeters,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            smoothing: 1,
            elapsed: TimeSpan.FromMilliseconds(16));

        var current = model.Calculate(
            TestVehicleState.Create(wheelSpeed: new WheelValues(
                currentWheelSpeed,
                currentWheelSpeed,
                currentWheelSpeed,
                currentWheelSpeed)),
            radiusMeters,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            smoothing: 1,
            elapsed: TimeSpan.FromMilliseconds(16));
        var rawMph = currentWheelSpeed * radiusMeters *
                     SpeedModel.MetersPerSecondToMilesPerHour;

        Assert.InRange(Math.Abs(current.DisplayValue - rawMph), 0, 1.500001);
    }

    [Fact]
    public void MissingRadiusReturnsExplicitlyUnavailableInsteadOfPoint34Fallback()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(200, 200, 200, 200));

        var speed = model.Calculate(
            state,
            null,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.Robust,
            0,
            TimeSpan.FromSeconds(1));

        const double formerFallbackRadiusMeters = 0.34;
        var formerFallbackMph = 200 * formerFallbackRadiusMeters *
                                SpeedModel.MetersPerSecondToMilesPerHour;

        Assert.InRange(formerFallbackMph, 151, 153);
        Assert.False(speed.IsAvailable);
        Assert.False(speed.UsesEstimatedRadius);
        Assert.Equal(0, speed.MetersPerSecond);
        Assert.Equal(0, speed.DisplayValue);
    }

    [Fact]
    public void StoppedWheelsNeverMirrorNonzeroGroundSpeed()
    {
        var model = new SpeedModel();
        var state = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(0, 0, 0, 0));

        var speed = model.Calculate(
            state,
            null,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.Robust,
            0,
            TimeSpan.FromSeconds(1));

        Assert.Equal(0, speed.DisplayValue);
        Assert.False(speed.IsAvailable);
        Assert.False(speed.UsesEstimatedRadius);
    }

    [Fact]
    public void LegacyRobustModePreservesTheLiteralDrivenWheelMean()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            wheelSpeed: new WheelValues(100, 100, 110, 500));
        var robustModel = new SpeedModel();
        var rawModel = new SpeedModel();

        var robust = robustModel.Calculate(
            state,
            0.33,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.Robust,
            0,
            TimeSpan.FromSeconds(1));
        var raw = rawModel.Calculate(
            state,
            0.33,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromSeconds(1));

        Assert.True(robust.IsAvailable);
        Assert.Equal(raw.MetersPerSecond, robust.MetersPerSecond, 12);
        Assert.Equal(raw.DisplayValue, robust.DisplayValue, 12);
        Assert.True(raw.DisplayValue > 200);
    }

    [Fact]
    public void CalibratedRwdWheelspinUsesRearRotationAndNotGroundSpeed()
    {
        var estimator = new RollingRadiusEstimator(new CalibrationOptions { MinimumSamples = 5 });
        for (var index = 0; index < 5; index++)
        {
            estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.RearWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(80, 80, 100, 100)));
        }

        var spinning = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(53.333333f, 53.333333f, 200, 200),
            slipRatio: new WheelValues(0.01f, 0.01f, 1.1f, 1.1f));
        var calibration = estimator.Observe(spinning);
        var indicated = new SpeedModel().Calculate(
            spinning,
            calibration.RadiusMeters,
            SpeedUnit.MilesPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromSeconds(1));

        var expectedWheelMph = 200 * 0.3 * SpeedModel.MetersPerSecondToMilesPerHour;
        var groundMph = 20 * SpeedModel.MetersPerSecondToMilesPerHour;
        Assert.True(calibration.IsTrusted);
        Assert.Equal(expectedWheelMph, indicated.DisplayValue, 8);
        Assert.NotEqual(groundMph, indicated.DisplayValue);
    }

    [Fact]
    public void RwdDifferentialCarrierUsesSignedRearWheelMean()
    {
        var state = TestVehicleState.Create(
            drivetrain: DrivetrainType.RearWheelDrive,
            groundSpeed: 5,
            wheelSpeed: new WheelValues(1_000, 1_000, 100, 300));

        var indicated = new SpeedModel().Calculate(
            state,
            0.3,
            SpeedUnit.KilometersPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromSeconds(1));

        Assert.Equal(216, indicated.DisplayValue, 8);
    }

    [Fact]
    public void StaggeredAwdUsesEachAxlesOwnRollingRadiusDuringWheelspin()
    {
        var estimator = new RollingRadiusEstimator();
        CalibrationResult calibration = default;
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            calibration = estimator.Observe(TestVehicleState.Create(
                drivetrain: DrivetrainType.AllWheelDrive,
                groundSpeed: 30,
                wheelSpeed: new WheelValues(100, 100, 80, 80)));
        }

        var spinning = TestVehicleState.Create(
            drivetrain: DrivetrainType.AllWheelDrive,
            groundSpeed: 20,
            wheelSpeed: new WheelValues(200, 200, 120, 120),
            slipRatio: new WheelValues(1, 1, 1, 1));
        var indicated = new SpeedModel().CalculateWithRadii(
            spinning,
            calibration.TrustedRadii,
            SpeedUnit.KilometersPerHour,
            WheelAggregationMode.RawDrivenWheels,
            0,
            TimeSpan.FromSeconds(1));

        Assert.True(calibration.IsTrusted);
        Assert.Equal(0.3, calibration.TrustedRadii!.Value.FrontMeters, 6);
        Assert.Equal(0.375, calibration.TrustedRadii.Value.RearMeters, 6);
        Assert.Equal(189, indicated.DisplayValue, 8);
    }
}
