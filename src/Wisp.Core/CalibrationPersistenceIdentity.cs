namespace Wisp.Core;

public readonly record struct CalibrationPersistenceIdentity(
    DrivetrainType Drivetrain,
    int CalibrationRevision,
    RollingRadii Radii)
{
    private const double RelativeRadiusTolerance = 1e-9;

    public static Dictionary<int, CalibrationPersistenceIdentity> Capture(RollingRadiusEstimator estimator) =>
        estimator.ExportSnapshots().ToDictionary(
            snapshot => snapshot.CarOrdinal,
            snapshot => new CalibrationPersistenceIdentity(
                snapshot.Drivetrain!.Value,
                snapshot.CalibrationRevision,
                new RollingRadii(snapshot.FrontRadiusMeters!.Value, snapshot.RearRadiusMeters!.Value)));

    // Sample-count growth does not change the accepted calibration. Keep
    // the same radius tolerance used for the estimator's persisted transitions.
    public bool Matches(CalibrationPersistenceIdentity current) =>
        Drivetrain == current.Drivetrain &&
        CalibrationRevision == current.CalibrationRevision &&
        Math.Abs(current.Radii.FrontMeters - Radii.FrontMeters) / current.Radii.FrontMeters <=
            RelativeRadiusTolerance &&
        Math.Abs(current.Radii.RearMeters - Radii.RearMeters) / current.Radii.RearMeters <=
            RelativeRadiusTolerance;
}
