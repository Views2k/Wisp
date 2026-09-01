using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGearAssetSelectorTests
{
    [Theory]
    [InlineData(NativeGaugeMode.Digital, "1", false, false, false, "HUD_Dial_Digital_Gear_1.png")]
    [InlineData(NativeGaugeMode.Digital, "1", true, false, false, "HUD_Dial_Digital_Gear_Redline_1.png")]
    [InlineData(NativeGaugeMode.Digital, "1", true, true, false, "HUD_Dial_Digital_Gear_Redline_1.png")]
    [InlineData(NativeGaugeMode.Digital, "1", true, true, true, "HUD_Dial_Digital_Gear_Redline_glow_1.png")]
    [InlineData(NativeGaugeMode.Digital, "Drive", true, true, true, "HUD_Dial_Digital_Gear_Redline_glow_Drive.png")]
    [InlineData(NativeGaugeMode.Analogue, "10", false, false, false, "HUD_Dial_Analog_Gear_10.png")]
    [InlineData(NativeGaugeMode.Analogue, "10", true, false, false, "HUD_Dial_Analog_Gear_Redline_10.png")]
    [InlineData(NativeGaugeMode.Analogue, "10", true, true, true, "HUD_Dial_Analog_Gear_Redline_glow_10.png")]
    public void SelectsNativeNormalPlainRedlineAndHeadlightGlowTextures(
        NativeGaugeMode mode,
        string gear,
        bool shiftLightOn,
        bool headlightStateAvailable,
        bool headlightsOn,
        string expected)
    {
        var snapshot = NativeAssistSnapshot.Unavailable() with
        {
            HeadlightStateAvailable = headlightStateAvailable,
            AreHeadlightsOn = headlightsOn
        };

        Assert.Equal(
            expected,
            NativeGearAssetSelector.FileName(mode, gear, shiftLightOn, snapshot));
    }

    [Theory]
    [InlineData(NativeGaugeMode.Digital, "R", "HUD_Dial_Digital_Gear_R.png")]
    [InlineData(NativeGaugeMode.Digital, "N", "HUD_Dial_Digital_Gear_N.png")]
    [InlineData(NativeGaugeMode.Analogue, "R", "HUD_Dial_Analog_Gear_R.png")]
    [InlineData(NativeGaugeMode.Analogue, "N", "HUD_Dial_Analog_Gear_N.png")]
    public void ReverseAndNeutralNeverUseShiftLightAssets(
        NativeGaugeMode mode,
        string gear,
        string expected)
    {
        var snapshot = NativeAssistSnapshot.Unavailable() with
        {
            HeadlightStateAvailable = true,
            AreHeadlightsOn = true
        };

        Assert.Equal(expected, NativeGearAssetSelector.FileName(mode, gear, true, snapshot));
    }
}
