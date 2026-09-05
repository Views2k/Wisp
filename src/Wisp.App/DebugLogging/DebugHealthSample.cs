namespace Wisp.App.DebugLogging;

internal enum DebugFocus { Unknown, Game, Wisp, Other, None }

internal sealed record DebugHealthSample
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }
    public double CollectionGapMilliseconds { get; init; }
    public long ReceivedDatagrams { get; init; }
    public long DrainedDatagrams { get; init; }
    public long AcceptedPackets { get; init; }
    public long RejectedPackets { get; init; }
    public long ProcessedPackets { get; init; }
    public double IncomingHz { get; init; }
    public double ProcessedHz { get; init; }
    public double? PacketAgeMilliseconds { get; init; }
    public bool ListenerRunning { get; init; }
    public bool ListenerError { get; init; }
    public bool RaceOn { get; init; }
    public bool GameTimestampAdvancing { get; init; }
    public DebugFocus Focus { get; init; }
    public bool UiContextFresh { get; init; }
    public bool OverlayExpectedVisible { get; init; }
    public bool NativeExpected { get; init; }
    public double? UiHeartbeatAgeMilliseconds { get; init; }
    public double? DispatcherDelayMilliseconds { get; init; }
    public bool DispatcherProbePending { get; init; }
    public double CompositionHz { get; init; }
    public double? CompositionAgeMilliseconds { get; init; }
    public double CompositionMaximumGapMilliseconds { get; init; }
    public bool NativeAvailable { get; init; }
    public long NativeReadAttempts { get; init; }
    public long NativeReadFailures { get; init; }
    public string NativeStatus { get; init; } = "Unknown";
    public double? NativeAgeMilliseconds { get; init; }
    public double? NativeVisibilityAgeMilliseconds { get; init; }
    public string GameplayVisibility { get; init; } = "Unknown";
    public double? WispCpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public int Gen2Collections { get; init; }
    public long DroppedRecords { get; init; }
    public long CollectorFailures { get; init; }
}
