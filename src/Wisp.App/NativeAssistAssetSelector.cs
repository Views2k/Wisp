using Wisp.Core;

namespace Wisp.App;

internal static class NativeAssistAssetSelector
{
    public static string FileName(
        NativeGaugeMode mode,
        string name,
        bool active,
        NativeAssistSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var prefix = mode == NativeGaugeMode.Digital
            ? "HUD_Dial_Assist_Digital"
            : "HUD_Dial_Assist_Analogue";
        var state = active
            ? snapshot.HeadlightStateAvailable && snapshot.AreHeadlightsOn
                ? "On_glow"
                : "On"
            : "Off";
        return $"{prefix}_{name}_{state}.png";
    }
}
