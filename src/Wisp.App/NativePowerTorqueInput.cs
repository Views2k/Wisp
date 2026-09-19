namespace Wisp.App;

internal readonly record struct NativePowerTorqueInput(PowerTorqueDisplay Display, int CarOrdinal,
    uint GameTimestampMilliseconds, long ReceivedTimestamp, long ObservedTimestamp, long Revision)
{
    internal double DriftFlashFrequencyHz { get; init; } = 1.25;
}
