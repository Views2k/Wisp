using Xunit;

namespace Wisp.Core.Tests;

public sealed class OverlayVisibilityPolicyTests
{
    [Fact]
    public void AltTabRetainsAConnectedHudWhenItsForzaOwnerIsStillKnown()
    {
        Assert.True(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: false,
            gameAwareVisibility: true,
            forzaForeground: false,
            forzaWindowKnown: true,
            editMode: false,
            forzaRunning: true,
            overlayForeground: false,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: false));
    }

    [Theory]
    [InlineData("loading")]
    [InlineData("pause/menu")]
    [InlineData("cinematic")]
    public void NativeHudInactiveTelemetryHidesTheOverlay(string state)
    {
        _ = state;
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: false,
            telemetryFresh: true,
            gameAwareVisibility: true,
            forzaForeground: true,
            forzaWindowKnown: true,
            editMode: false,
            forzaRunning: true,
            overlayForeground: false,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: true));
    }

    [Fact]
    public void StaleSimulationHidesTheHudWhileForzaMenuIsForeground()
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: false,
            gameAwareVisibility: true,
            forzaForeground: true,
            forzaWindowKnown: true,
            editMode: false,
            forzaRunning: true,
            overlayForeground: false,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: true));
    }

    [Fact]
    public void DisablingGameAwareVisibilityNeverCreatesAnUnownedDesktopOverlay()
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility: false,
            forzaForeground: false,
            forzaWindowKnown: false,
            editMode: false,
            forzaRunning: true,
            overlayForeground: false,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ActiveMenuHidesDespiteFreshRaceOnTelemetryAndUserOptions(bool gameAwareVisibility, bool editMode)
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility,
            forzaForeground: true,
            forzaWindowKnown: true,
            editMode,
            forzaRunning: true,
            overlayForeground: editMode,
            nativeGameplayVisibility: NativeGameplayVisibility.Hidden,
            nativeVisibilityFresh: true));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AltTabCannotResurrectAKnownHiddenHud(bool nativeVisibilityFresh, bool gameAwareVisibility)
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: false,
            gameAwareVisibility,
            forzaForeground: false,
            forzaWindowKnown: true,
            editMode: true,
            forzaRunning: true,
            overlayForeground: true,
            nativeGameplayVisibility: NativeGameplayVisibility.Hidden,
            nativeVisibilityFresh));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void UnknownVisibilityCannotBeBypassedByFocusOrPreferences(bool forzaForeground, bool gameAwareVisibility)
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility,
            forzaForeground,
            forzaWindowKnown: true,
            editMode: true,
            forzaRunning: true,
            overlayForeground: true,
            nativeGameplayVisibility: NativeGameplayVisibility.Unknown,
            nativeVisibilityFresh: true));
    }

    [Fact]
    public void MissingVisibilityArgumentsFailClosed()
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility: false,
            forzaForeground: false,
            forzaWindowKnown: true,
            editMode: true,
            forzaRunning: true,
            overlayForeground: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForegroundRequiresAFreshNativeObservationEvenWithFreshTelemetry(bool gameAwareVisibility)
    {
        Assert.False(OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive: true,
            telemetryFresh: true,
            gameAwareVisibility,
            forzaForeground: true,
            forzaWindowKnown: true,
            editMode: true,
            forzaRunning: true,
            overlayForeground: true,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: false));
    }

    [Theory]
    [InlineData(true, true, true, true, true, false, true, false, true)]
    [InlineData(true, true, true, false, true, false, true, false, true)]
    [InlineData(true, true, false, true, true, true, true, true, true)]
    [InlineData(true, false, false, true, true, false, true, false, true)]
    [InlineData(true, false, true, true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, false, true, false, false)]
    [InlineData(true, true, true, false, true, false, false, false, false)]
    [InlineData(true, true, false, false, false, false, true, false, false)]
    [InlineData(false, true, true, false, true, true, true, true, false)]
    [InlineData(true, true, true, false, false, true, true, true, false)]
    [InlineData(true, true, true, false, false, true, true, false, false)]
    [InlineData(true, true, true, false, false, true, false, true, false)]
    [InlineData(false, true, true, true, true, false, true, false, false)]
    public void ShowsOnlyForAllowedForegroundContext(
        bool nativeHudTelemetryActive,
        bool telemetryFresh,
        bool gameAwareVisibility,
        bool forzaForeground,
        bool forzaWindowKnown,
        bool editMode,
        bool forzaRunning,
        bool overlayForeground,
        bool expected)
    {
        var actual = OverlayVisibilityPolicy.ShouldShow(
            nativeHudTelemetryActive,
            telemetryFresh,
            gameAwareVisibility,
            forzaForeground,
            forzaWindowKnown,
            editMode,
            forzaRunning,
            overlayForeground,
            nativeGameplayVisibility: NativeGameplayVisibility.Visible,
            nativeVisibilityFresh: true);

        Assert.Equal(expected, actual);
    }
}
