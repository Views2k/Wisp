namespace Wisp.Core.Runs;

public enum RunPurpose { General, Acceleration, Drifting }
public enum RunEvidenceView { Speed, Inputs, Tires }

public sealed record RunSample
{
    public double ElapsedSeconds { get; init; }
    public int Segment { get; init; }
    public bool IsDriving { get; init; }
    public required VehicleState State { get; init; }
    public double? WheelSpeedMetersPerSecond { get; init; }
    public double? FrontRadiusMeters { get; init; }
    public double? RearRadiusMeters { get; init; }
}

public sealed record RecordedRun
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Untitled run";
    public string Tune { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public string FinishReason { get; init; } = string.Empty;
    public bool IsIncomplete { get; init; }
    public long RejectedDatagrams { get; init; }
    public long DroppedDatagrams { get; init; }
    public RunMarker[] Markers { get; init; } = [];
    public RunSample[] Samples { get; init; } = [];
}

public sealed record RunMarker(double ElapsedSeconds, string Label);
public readonly record struct RunInterval(double StartSeconds, double EndSeconds);
public sealed record RunFinding(string Title, string Detail, RunInterval? Interval = null,
    RunEvidenceView EvidenceView = RunEvidenceView.Speed);

public sealed record RunStatistics
{
    public int SampleCount { get; init; }
    public double DurationSeconds { get; init; }
    public double RecordedSeconds { get; init; }
    public int GapCount { get; init; }
    public double? AverageSpeedMetersPerSecond { get; init; }
    public double? PeakSpeedMetersPerSecond { get; init; }
    public double DistanceMeters { get; init; }
    public double FullThrottleSeconds { get; init; }
    public double BrakingSeconds { get; init; }
    public double? PeakPowerWatts { get; init; }
    public double? PeakTorqueNm { get; init; }
    public double? PeakBoostPsi { get; init; }
    public double? PeakLateralG { get; init; }
    public double? PeakLongitudinalG { get; init; }
    public double? StartingFrontTemperatureFahrenheit { get; init; }
    public double? StartingRearTemperatureFahrenheit { get; init; }
    public double? EndingFrontTemperatureFahrenheit { get; init; }
    public double? EndingRearTemperatureFahrenheit { get; init; }
    public double? AverageWheelSpeedExcessMetersPerSecond { get; init; }
}

public sealed record RunReport(RunInterval Interval, RunStatistics Statistics, RunFinding[] Findings, string QualityNote);
public sealed record RunSpeedRange(double FromMetersPerSecond, double ToMetersPerSecond, double DurationSeconds, RunInterval Interval);
public sealed record RunComparison(RunReport RunA, RunReport RunB, RunFinding[] Findings, RunSpeedRange? AccelerationA, RunSpeedRange? AccelerationB);
