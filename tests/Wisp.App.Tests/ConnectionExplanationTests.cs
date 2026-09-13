using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ConnectionExplanationTests
{
    private static ConnectionEvidence Ready => new(false, false, true, true, true, true, false, true,
        true, false, true, 1, NativeGameplayVisibility.Visible, true, NativeAssistProviderStatus.Ready, 5602, "Ctrl + Shift + H");

    [Fact]
    public void LiveTelemetryDoesNotClaimTheOverlayIsActuallyDisplayed()
    {
        var report = ConnectionExplanation.Describe(Ready);
        Assert.Equal("Telemetry arriving", report.Telemetry);
        Assert.Equal("HUD is enabled", report.Hud);
        Assert.Contains("Reset HUD positions", report.Fix);
    }

    [Fact]
    public void NoGameAndNoPacketsOfferDifferentActions()
    {
        var noGame = ConnectionExplanation.Describe(Ready with { GameRunning = false, TelemetryFresh = false, HudRequested = false });
        Assert.Equal("Forza not detected", noGame.Game);
        Assert.Contains("Start Forza", noGame.Fix);
        var noPackets = ConnectionExplanation.Describe(Ready with { TelemetryFresh = false, ReceivedPackets = false, HudRequested = false });
        Assert.True(noPackets.ShowSetup);
        Assert.Contains("5602", noPackets.Fix);
        Assert.Contains("Data Out", noPackets.Fix);
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, "game update needs support", "Compatibility")]
    [InlineData(NativeAssistProviderStatus.AccessDenied, "access was denied", "permissions")]
    public void NativeFailuresExplainWhyPacketsAloneAreInsufficient(NativeAssistProviderStatus status, string reason, string fix)
    {
        var report = ConnectionExplanation.Describe(Ready with { HudRequested = false, NativeStatus = status, GameplayVisibility = NativeGameplayVisibility.Unknown });
        Assert.Equal("Telemetry arriving", report.Telemetry);
        Assert.Contains(reason, report.Hud);
        Assert.Contains(fix, report.Fix);
    }

    [Fact]
    public void ManualHideUsesTheConfiguredShortcut()
    {
        var report = ConnectionExplanation.Describe(Ready with { ManuallyHidden = true, HudRequested = false, Shortcut = "Alt + H" });
        Assert.Equal("Hidden with the HUD shortcut", report.Hud);
        Assert.Contains("Alt + H", report.Fix);
    }

    [Fact]
    public void UnknownAndHiddenGameHudAreNotConflated()
    {
        var hidden = ConnectionExplanation.Describe(Ready with { HudRequested = false, GameplayVisibility = NativeGameplayVisibility.Hidden });
        var unknown = ConnectionExplanation.Describe(Ready with { HudRequested = false, GameplayVisibility = NativeGameplayVisibility.Unknown });
        Assert.Contains("game HUD is hidden", hidden.Hud);
        Assert.Contains("waiting for game HUD state", unknown.Hud);
    }

    [Fact]
    public void RetainedBackgroundFrameDoesNotClaimLiveReadings()
    {
        var report = ConnectionExplanation.Describe(Ready with { TelemetryFresh = false, VisibilityFresh = false });
        Assert.Equal("Telemetry stopped", report.Telemetry);
        Assert.Equal("HUD is holding its last frame", report.Hud);
    }

    [Fact]
    public void ZeroOpacityOffersTheActualSetting()
    {
        var report = ConnectionExplanation.Describe(Ready with { Opacity = 0 });
        Assert.Contains("0%", report.Hud);
        Assert.Contains("Appearance → Layout", report.Fix);
    }

    [Fact]
    public void ListenerFailureOffersPortRecoveryWithoutEchoingRawError()
    {
        var report = ConnectionExplanation.Describe(Ready with { ListenerFailed = true, ListenerRunning = false, HudRequested = false });
        Assert.True(report.ShowSetup);
        Assert.Equal("Connection could not start", report.Telemetry);
        Assert.Contains("free port", report.Fix);
    }

    [Fact]
    public void ListenerFailureDoesNotPretendARetainedHudWasHidden()
    {
        var report = ConnectionExplanation.Describe(Ready with { ListenerFailed = true, ListenerRunning = false, TelemetryFresh = false });
        Assert.Equal("HUD is holding its last frame", report.Hud);
        Assert.Equal("Connection could not start", report.Telemetry);
        Assert.True(report.ShowSetup);
    }
}
