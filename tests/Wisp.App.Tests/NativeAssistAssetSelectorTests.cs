using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAssistAssetSelectorTests
{
    [Theory]
    [InlineData(NativeGaugeMode.Digital, false, false, false, "HUD_Dial_Assist_Digital_ABS_Off.png")]
    [InlineData(NativeGaugeMode.Digital, true, true, false, "HUD_Dial_Assist_Digital_ABS_On.png")]
    [InlineData(NativeGaugeMode.Digital, true, true, true, "HUD_Dial_Assist_Digital_ABS_On_glow.png")]
    [InlineData(NativeGaugeMode.Analogue, false, false, false, "HUD_Dial_Assist_Analogue_ABS_Off.png")]
    [InlineData(NativeGaugeMode.Analogue, true, true, false, "HUD_Dial_Assist_Analogue_ABS_On.png")]
    [InlineData(NativeGaugeMode.Analogue, true, true, true, "HUD_Dial_Assist_Analogue_ABS_On_glow.png")]
    public void SelectsTheNativeOffOnAndHeadlightGlowTextures(
        NativeGaugeMode mode,
        bool active,
        bool headlightStateAvailable,
        bool headlightsOn,
        string expected)
    {
        var snapshot = NativeAssistSnapshot.Unavailable() with
        {
            HeadlightStateAvailable = headlightStateAvailable,
            AreHeadlightsOn = headlightsOn
        };

        Assert.Equal(expected, NativeAssistAssetSelector.FileName(mode, "ABS", active, snapshot));
    }

    [Theory]
    [InlineData(NativeGaugeMode.Digital, "HUD_Dial_Assist_Digital_LC_On.png")]
    [InlineData(NativeGaugeMode.Analogue, "HUD_Dial_Assist_Analogue_LC_On.png")]
    public void DoesNotClaimGlowWithoutAnExactHeadlightState(
        NativeGaugeMode mode,
        string expected)
    {
        var snapshot = NativeAssistSnapshot.Unavailable() with
        {
            HeadlightStateAvailable = false,
            AreHeadlightsOn = true
        };

        Assert.Equal(expected, NativeAssistAssetSelector.FileName(mode, "LC", true, snapshot));
    }
}
