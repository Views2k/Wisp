using Wisp.Core;

namespace Wisp.App;

internal static class NativeGearAssetSelector
{
    public static string FileName(
        NativeGaugeMode mode,
        string gear,
        bool shiftLightOn,
        NativeAssistSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gear);
        ArgumentNullException.ThrowIfNull(snapshot);

        var prefix = mode == NativeGaugeMode.Digital
            ? "HUD_Dial_Digital_Gear"
            : "HUD_Dial_Analog_Gear";
        if (!shiftLightOn || gear is "N" or "R")
        {
            return $"{prefix}_{gear}.png";
        }

        // FH6 uses the plain redline texture with headlights off and the glow
        // texture with headlights on.  If that exact state is unavailable,
        // fail closed to the plain texture instead of inventing a glow state.
        var state = snapshot.HeadlightStateAvailable && snapshot.AreHeadlightsOn
            ? "Redline_glow"
            : "Redline";
        return $"{prefix}_{state}_{gear}.png";
    }
}
