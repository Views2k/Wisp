using System.Globalization;
using Wisp.Telemetry;

namespace Wisp.App;

public static class UdpPortInput
{
    public static int Parse(string? text)
    {
        if (!int.TryParse(
                text?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port))
        {
            throw new ArgumentOutOfRangeException(
                nameof(text),
                "Enter a UDP port from 1024 to 65535.");
        }

        TelemetryUdpReceiver.ValidatePort(port);
        return port;
    }
}
