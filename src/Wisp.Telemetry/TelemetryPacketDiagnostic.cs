namespace Wisp.Telemetry;

public enum TelemetryPacketDiagnosticKind { Received, Drained, Accepted, Rejected }

public readonly record struct TelemetryPacketDiagnostic(
    TelemetryPacketDiagnosticKind Kind, long Timestamp, long ReceivedTimestamp, long Sequence,
    uint GameTimestampMilliseconds, float Rpm, float MaximumRpm, int CarOrdinal,
    bool RaceOn, PacketParseError ParseError);
