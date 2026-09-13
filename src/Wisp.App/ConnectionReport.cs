using Wisp.Core;

namespace Wisp.App;

public sealed record ConnectionReport(string Game, string Telemetry, string Hud, string Fix, bool ShowSetup);

internal readonly record struct ConnectionEvidence(
    bool SetupRequired, bool Suspended, bool GameRunning, bool WindowKnown,
    bool TelemetryFresh, bool ListenerRunning, bool ListenerFailed, bool ReceivedPackets,
    bool Driving, bool ManuallyHidden, bool HudRequested, double Opacity,
    NativeGameplayVisibility GameplayVisibility, bool VisibilityFresh,
    NativeAssistProviderStatus NativeStatus, int Port, string Shortcut);

internal static class ConnectionExplanation
{
    internal static ConnectionReport Describe(ConnectionEvidence e)
    {
        var game = e.GameRunning ? "Forza detected" : "Forza not detected";
        var telemetry = e.ListenerFailed ? "Connection could not start"
            : !e.ListenerRunning ? "Connection paused"
            : e.TelemetryFresh ? "Telemetry arriving"
            : e.ReceivedPackets ? "Telemetry stopped" : "Waiting for telemetry";
        ConnectionReport Result(string hud, string fix, bool setup = false) => new(game, telemetry, hud, fix, setup);

        if (e.HudRequested)
        {
            if (e.Opacity <= 0) return Result("Transparent · opacity is 0%", "Increase Opacity in Appearance → Layout to make the HUD visible.");
            var hud = e.TelemetryFresh ? "HUD is enabled" : "HUD is holding its last frame";
            if (e.ListenerFailed || !e.ListenerRunning)
                return Result(hud, $"Check port {e.Port} in Diagnostics to restore live telemetry. Match the same port in Forza Data Out.", true);
            if (e.NativeStatus is NativeAssistProviderStatus.UnsupportedBuild or NativeAssistProviderStatus.AccessDenied)
                return Result(hud, "Open Diagnostics → Compatibility to check the game's connection. Some native readings are unavailable.");
            return Result(hud, e.TelemetryFresh ? "If it is off-screen, use Reset HUD positions in Appearance → Layout."
                : "Return to driving to resume live readings.");
        }
        if (e.SetupRequired) return Result("Hidden · setup incomplete", "Finish the connection steps to start your HUD.", true);
        if (e.Suspended) return Result("Hidden · Wisp is waiting", "Open Forza to resume Wisp.");
        if (!e.GameRunning) return Result("Hidden · Forza is not running", "Start Forza Horizon 6 and enter a drive.");
        if (e.ListenerFailed || !e.ListenerRunning)
            return Result("Hidden · connection unavailable", $"Check port {e.Port} in Diagnostics. If another app uses it, choose a free port and match it in Forza Data Out.", true);
        if (!e.TelemetryFresh && !e.ReceivedPackets)
            return Result("Hidden · no telemetry yet", $"Turn on Data Out in Forza and use Wisp's port ({e.Port}). Enter free roam or an event to send telemetry.", true);
        if (e.ManuallyHidden)
            return Result("Hidden with the HUD shortcut", $"Press {e.Shortcut} to show the HUD again.");
        if (!e.WindowKnown)
            return Result("Hidden · game window not found", "Return to Forza once and use Fullscreen mode so Wisp can find its window.");
        if (!e.Driving)
            return Result("Hidden · driving is paused", "Return to a drive. Wisp hides the HUD in menus and loading screens.");
        if (e.NativeStatus == NativeAssistProviderStatus.UnsupportedBuild)
            return Result("Hidden · game update needs support", "Open Diagnostics → Compatibility and check for an update. Wisp needs a verified match for this game build.");
        if (e.NativeStatus == NativeAssistProviderStatus.AccessDenied)
            return Result("Hidden · game access was denied", "Restart Wisp and Forza with matching Windows permissions, then check Diagnostics → Compatibility.");
        if (e.GameplayVisibility == NativeGameplayVisibility.Hidden)
            return Result("Hidden · game HUD is hidden", "Return from menus or loading screens to a drive. Wisp follows the game's gameplay HUD visibility.");
        if (e.GameplayVisibility != NativeGameplayVisibility.Visible)
            return Result("Hidden · waiting for game HUD state", "Open Diagnostics → Compatibility to check game-build support. Telemetry can arrive before HUD visibility is verified.");
        if (!e.VisibilityFresh)
            return Result("Hidden · game HUD state is stale", "Return to driving. If this persists, open Diagnostics → Compatibility.");
        if (!e.TelemetryFresh)
            return Result("Hidden · telemetry stopped", "Return to a drive. If readings do not resume, check Data Out in Forza.", true);
        return Result("Hidden · waiting for the next game update", "Return to a drive. Open Diagnostics if the HUD remains hidden.");
    }
}
