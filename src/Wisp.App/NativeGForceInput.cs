namespace Wisp.App;

internal readonly record struct NativeGForceInput(bool Active, double XG, double YG, double FullScaleG,
    int CarOrdinal, uint GameTimestampMilliseconds, long ReceivedTimestamp);
