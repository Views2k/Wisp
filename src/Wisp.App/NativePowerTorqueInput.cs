namespace Wisp.App;

internal readonly record struct NativePowerTorqueInput(PowerTorqueDisplay Display, int CarOrdinal,
    uint GameTimestampMilliseconds, long ReceivedTimestamp, long ObservedTimestamp, long Revision);
