namespace Wisp.Core;

public enum ExactRedlineStatus
{
    Exact,
    Unavailable,
    GameNotRunning,
    UnsupportedBuild,
    AccessDenied,
    InvalidProvider,
    PlayerNotUnique,
    TelemetryMismatch,
    ReadFailure
}

public readonly record struct ExactRedlineResult(
    ExactRedlineStatus Status,
    double SimRedlineAngularVelocity,
    double Rpm,
    string Source)
{
    public const string NativeHudProviderSource = "FH6 ICarDynamics.SimRedlineAngVel";

    public bool IsExact => Status == ExactRedlineStatus.Exact;

    public static ExactRedlineResult Exact(double angularVelocity)
    {
        if (!double.IsFinite(angularVelocity) || angularVelocity <= 0)
        {
            return Unavailable(ExactRedlineStatus.InvalidProvider);
        }

        return new ExactRedlineResult(
            ExactRedlineStatus.Exact,
            angularVelocity,
            AngularVelocityToRpm(angularVelocity),
            NativeHudProviderSource);
    }

    public static ExactRedlineResult Unavailable(
        ExactRedlineStatus status = ExactRedlineStatus.Unavailable) =>
        new(status, 0, 0, NativeHudProviderSource);

    public static double AngularVelocityToRpm(double angularVelocity) =>
        angularVelocity * 60d / (2d * Math.PI);
}
