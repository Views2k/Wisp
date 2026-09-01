namespace Wisp.Core;

public static class DrivingTransitionPolicy
{
    public static DrivingTransitionDecision Evaluate(
        bool wasDriving,
        DrivingTelemetrySignal telemetrySignal,
        bool autoMinimizeOnTelemetry)
    {
        var isDriving = telemetrySignal switch
        {
            DrivingTelemetrySignal.Driving => true,
            DrivingTelemetrySignal.NotDriving => false,
            _ => wasDriving
        };
        var startedDriving = telemetrySignal == DrivingTelemetrySignal.Driving && !wasDriving;
        return new DrivingTransitionDecision(
            isDriving,
            startedDriving,
            startedDriving && autoMinimizeOnTelemetry);
    }
}

public enum DrivingTelemetrySignal
{
    Unavailable = 0,
    NotDriving = 1,
    Driving = 2
}

public readonly record struct DrivingTransitionDecision(
    bool IsDriving,
    bool StartedDriving,
    bool ShouldMinimizeControlPanel);
