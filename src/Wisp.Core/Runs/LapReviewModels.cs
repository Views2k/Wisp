namespace Wisp.Core.Runs;

[Flags]
public enum LapReviewQuality
{
    None = 0,
    PartialStart = 1,
    PartialEnd = 2,
    TelemetryGap = 4,
    Rewind = 8,
    Discontinuity = 16,
    MissingPosition = 32,
    MissingTiming = 64,
    InferredTiming = 128
}

public sealed record LapReviewPoint(int SampleIndex, double RunSeconds, double LapSeconds,
    double DistanceMeters, LapPosition Position, bool BreakBefore, RunSample Sample);

public sealed record LapReviewLap
{
    public Guid RunId { get; init; }
    public string RunName { get; init; } = string.Empty;
    public string Tune { get; init; } = string.Empty;
    public int Number { get; init; }
    public int CarOrdinal { get; init; }
    public LapTimingMode TimingMode { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsComplete { get; init; }
    public LapReviewQuality Quality { get; init; }
    public LapReviewPoint[] Points { get; init; } = [];
    public double RecordedDistanceMeters => Points.Length == 0 ? 0 : Points[^1].DistanceMeters;
    public string Label => $"Lap {Number}" + (IsComplete && DurationSeconds is { } duration
        ? $" · {TimeSpan.FromSeconds(duration):m\\:ss\\.fff}" : " · partial") +
        ((Quality & (LapReviewQuality.TelemetryGap | LapReviewQuality.Rewind | LapReviewQuality.Discontinuity | LapReviewQuality.MissingPosition)) != 0 ? " · interrupted" : "");
}

public sealed record LapReviewResult(LapReviewLap[] Laps, string Message);

public sealed record LapValueSummary(double? Start, double? End, double? Minimum, double? Maximum, double? Mean);
public sealed record LapWheelSummaries(LapValueSummary FrontLeft, LapValueSummary FrontRight,
    LapValueSummary RearLeft, LapValueSummary RearRight);

public enum LapReviewEventKind { BrakeStart, BrakeEnd, ThrottlePickup, Upshift, Downshift }
public sealed record LapReviewEvent(LapReviewEventKind Kind, int PointIndex, double RunSeconds,
    double DistanceMeters, double Value, TransmissionGear? FromGear = null, TransmissionGear? ToGear = null);

public sealed record LapSectionStatistics
{
    public int FirstPointIndex { get; init; }
    public int LastPointIndex { get; init; }
    public int SampleCount { get; init; }
    public LapReviewQuality Quality { get; init; }
    public double DurationSeconds { get; init; }
    public double RecordedSeconds { get; init; }
    public double DistanceMeters { get; init; }
    public int GapCount { get; init; }
    public double? EntrySpeedMetersPerSecond { get; init; }
    public double? MinimumSpeedMetersPerSecond { get; init; }
    public double? ExitSpeedMetersPerSecond { get; init; }
    public double? AverageSpeedMetersPerSecond { get; init; }
    public double ThrottleSeconds { get; init; }
    public double FullThrottleSeconds { get; init; }
    public double BrakingSeconds { get; init; }
    public double CoastingSeconds { get; init; }
    public double? ThrottleFraction => RecordedSeconds > 0 ? ThrottleSeconds / RecordedSeconds : null;
    public double? FullThrottleFraction => RecordedSeconds > 0 ? FullThrottleSeconds / RecordedSeconds : null;
    public double? BrakingFraction => RecordedSeconds > 0 ? BrakingSeconds / RecordedSeconds : null;
    public double? CoastingFraction => RecordedSeconds > 0 ? CoastingSeconds / RecordedSeconds : null;
    public LapValueSummary ThrottlePercent { get; init; } = new(null, null, null, null, null);
    public LapValueSummary BrakePercent { get; init; } = new(null, null, null, null, null);
    public LapValueSummary SteeringRaw { get; init; } = new(null, null, null, null, null);
    public LapValueSummary LateralG { get; init; } = new(null, null, null, null, null);
    public LapValueSummary LongitudinalG { get; init; } = new(null, null, null, null, null);
    public LapValueSummary CombinedG { get; init; } = new(null, null, null, null, null);
    public LapValueSummary EngineRpm { get; init; } = new(null, null, null, null, null);
    public LapWheelSummaries TireTemperatureFahrenheit { get; init; } = EmptyWheels();
    public LapWheelSummaries TireSlipRatio { get; init; } = EmptyWheels();
    public LapWheelSummaries TireSlipAngle { get; init; } = EmptyWheels();
    public LapWheelSummaries NormalizedSuspensionTravel { get; init; } = EmptyWheels();
    public LapReviewEvent[] Events { get; init; } = [];
    public string QualityNote { get; init; } = string.Empty;

    private static LapWheelSummaries EmptyWheels() => new(new(null, null, null, null, null),
        new(null, null, null, null, null), new(null, null, null, null, null), new(null, null, null, null, null));
}

public sealed record LapReviewComparisonPoint(int PointIndex, double DistanceMeters,
    double? ReferenceLapSeconds, double? DeltaSeconds, int? ReferencePointIndex = null);
public sealed record LapReviewComparison(LapReviewComparisonPoint[] Points, double Coverage,
    bool CanCompare, string Message);
