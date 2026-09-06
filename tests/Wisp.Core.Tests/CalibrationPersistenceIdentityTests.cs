using Xunit;

namespace Wisp.Core.Tests;

public sealed class CalibrationPersistenceIdentityTests
{
    [Fact]
    public void CapturesOnlySnapshotsAcceptedByTheEstimator()
    {
        var estimator = new RollingRadiusEstimator();
        var accepted = Snapshot();
        estimator.ImportSnapshots(new[]
        {
            accepted,
            accepted with { CarOrdinal = 101, CalibrationRevision = 0 },
            accepted with { CarOrdinal = 102, SampleCount = CalibrationOptions.DefaultMinimumSamples - 1 },
            accepted with { CarOrdinal = 103, Drivetrain = (DrivetrainType)99 },
            accepted with { CarOrdinal = 104, FrontRadiusMeters = double.NaN },
            accepted with { CarOrdinal = 0 }
        });

        var captured = CalibrationPersistenceIdentity.Capture(estimator);

        Assert.Equal(accepted.CarOrdinal, Assert.Single(captured).Key);
        Assert.True(captured[accepted.CarOrdinal].Matches(Identity()));
    }

    [Fact]
    public void RejectedDuplicateCannotReplaceTheLastAcceptedIdentity()
    {
        var estimator = new RollingRadiusEstimator();
        var accepted = Snapshot();
        estimator.ImportSnapshots(new[]
        {
            accepted with { Drivetrain = DrivetrainType.FrontWheelDrive },
            accepted,
            accepted with { CalibrationRevision = 0, RearRadiusMeters = 0.4 }
        });

        var captured = CalibrationPersistenceIdentity.Capture(estimator);

        Assert.True(Assert.Single(captured).Value.Matches(Identity()));
    }

    [Fact]
    public void ChangedDrivetrainIsNotHiddenByEqualRadii()
    {
        var saved = Identity();
        var current = saved with { Drivetrain = DrivetrainType.AllWheelDrive };

        Assert.False(saved.Matches(current));
    }

    [Fact]
    public void ChangedRevisionIsNotHiddenByEqualRadii()
    {
        var saved = Identity() with { CalibrationRevision = 0 };

        Assert.False(saved.Matches(Identity()));
    }

    [Fact]
    public void SampleCountGrowthDoesNotRequestAnotherSave()
    {
        var first = new RollingRadiusEstimator();
        first.ImportSnapshots(new[] { Snapshot() });
        var second = new RollingRadiusEstimator();
        second.ImportSnapshots(new[] { Snapshot() with { SampleCount = 100 } });

        var saved = Assert.Single(CalibrationPersistenceIdentity.Capture(first)).Value;
        var current = Assert.Single(CalibrationPersistenceIdentity.Capture(second)).Value;

        Assert.True(saved.Matches(current));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherAxleRetainsTheExistingRelativeRadiusTolerance(bool frontAxle)
    {
        var saved = Identity();
        var belowTolerance = saved with
        {
            Radii = frontAxle
                ? new RollingRadii(0.3 * (1 + 0.5e-9), 0.3)
                : new RollingRadii(0.3, 0.3 * (1 + 0.5e-9))
        };
        var aboveTolerance = saved with
        {
            Radii = frontAxle
                ? new RollingRadii(0.3 * (1 + 2e-9), 0.3)
                : new RollingRadii(0.3, 0.3 * (1 + 2e-9))
        };

        Assert.True(saved.Matches(belowTolerance));
        Assert.False(saved.Matches(aboveTolerance));
    }

    [Fact]
    public void RejectedLoadedRevisionCannotSuppressEqualRadiusRelearning()
    {
        var estimator = new RollingRadiusEstimator();
        estimator.ImportSnapshots(new[] { Snapshot() with { CalibrationRevision = 0 } });
        var saved = CalibrationPersistenceIdentity.Capture(estimator);
        for (var index = 0; index < CalibrationOptions.DefaultMinimumSamples; index++)
        {
            estimator.Observe(TestVehicleState.Create());
        }

        var relearned = Assert.Single(CalibrationPersistenceIdentity.Capture(estimator));

        Assert.False(saved.ContainsKey(relearned.Key));
        Assert.True(relearned.Value.Matches(Identity()));
    }

    private static CalibrationPersistenceIdentity Identity() => new(
        DrivetrainType.RearWheelDrive,
        RollingRadiusEstimator.CurrentCalibrationRevision,
        new RollingRadii(0.3, 0.3));

    private static CalibrationSnapshot Snapshot() => new(
        100, 0.3, CalibrationOptions.DefaultMinimumSamples, DrivetrainType.RearWheelDrive,
        RollingRadiusEstimator.CurrentCalibrationRevision, 0.3, 0.3);
}
