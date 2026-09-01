using Wisp.Core;

namespace Wisp.Telemetry;

public static class Fh6GearDecoder
{
    public static TransmissionGear Decode(byte rawGear) => rawGear switch
    {
        0 => TransmissionGear.Reverse,
        >= 1 and <= 10 => (TransmissionGear)rawGear,
        11 => TransmissionGear.Neutral,
        _ => TransmissionGear.Unknown
    };
}
