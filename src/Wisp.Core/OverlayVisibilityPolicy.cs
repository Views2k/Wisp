namespace Wisp.Core;

public static class OverlayVisibilityPolicy
{
    public static bool ShouldShow(
        bool nativeHudTelemetryActive,
        bool telemetryFresh,
        bool gameAwareVisibility,
        bool forzaForeground,
        bool forzaWindowKnown,
        bool editMode,
        bool forzaRunning,
        bool overlayForeground,
        NativeGameplayVisibility nativeGameplayVisibility = NativeGameplayVisibility.Unknown,
        bool nativeVisibilityFresh = false)
    {
        // This is a Data Out activity gate, not an authoritative native HUD
        // visibility flag. Buy & Sell can keep IsRaceOn and game time active.
        if (!nativeHudTelemetryActive)
        {
            return false;
        }

        // An overlay without a live FH6 owner can leak over unrelated apps.
        // This ownership boundary is absolute; the game-aware option never
        // turns Wisp into a global desktop overlay.
        if (!forzaRunning || !forzaWindowKnown)
        {
            return false;
        }

        // Native gameplay visibility is independent of optional gauge state.
        // Unknown and hidden cannot be bypassed by edit mode or preferences.
        if (nativeGameplayVisibility != NativeGameplayVisibility.Visible ||
            (forzaForeground && !nativeVisibilityFresh))
        {
            return false;
        }

        if (!gameAwareVisibility)
        {
            return true;
        }

        // Hide foreground surfaces that stop simulation activity. Freshness
        // alone cannot hide menus that continue sending active telemetry.
        // Retain the last frame across a background gap for the established
        // alt-tab behavior while the owning FH6 window remains known.
        if (!telemetryFresh && forzaForeground)
        {
            return false;
        }

        _ = editMode;
        _ = overlayForeground;
        return true;
    }
}
