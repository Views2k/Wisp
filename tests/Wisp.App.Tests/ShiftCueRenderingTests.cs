using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueRenderingTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public void SupportedPerformanceGuidanceDoesNotMixInTheEarlierPinkRedlineCue(int stage, bool flash)
    {
        var frame = Frame() with { EngineRpm = 8200 };
        var active = frame with { ShiftCue = new(true, stage, 0xFF65D99C, flash, 8400) };
        var analogNormal = AnalogHudAssets.Id(NativeAssetFamily.Analogue,
            NativeGearAssetSelector.FileName(NativeGaugeMode.Analogue, "3", false, frame.NativeAssists));
        var analogRedline = AnalogHudAssets.Id(NativeAssetFamily.Analogue,
            NativeGearAssetSelector.FileName(NativeGaugeMode.Analogue, "3", true, frame.NativeAssists));
        var digitalNormal = DigitalHudAssets.Id(NativeAssetFamily.Digital,
            NativeGearAssetSelector.FileName(NativeGaugeMode.Digital, "3", false, frame.NativeAssists));
        var digitalRedline = DigitalHudAssets.Id(NativeAssetFamily.Digital,
            NativeGearAssetSelector.FileName(NativeGaugeMode.Digital, "3", true, frame.NativeAssists));
        Assert.Contains(Analog(frame), c => c.TextureId == analogRedline);
        Assert.Contains(Analog(active), c => c.TextureId == analogNormal);
        Assert.DoesNotContain(Analog(active), c => c.TextureId == analogRedline);
        Assert.Contains(Digital(frame), c => c.TextureId == digitalRedline);
        Assert.Contains(Digital(active), c => c.TextureId == digitalNormal);
        Assert.DoesNotContain(Digital(active), c => c.TextureId == digitalRedline);
    }

    [Theory]
    [InlineData(1, 0xFF65D99Cu)]
    [InlineData(2, 0xFFFFD166u)]
    [InlineData(3, 0xFFFF526Du)]
    public void BothNativePathsAddOnlyTheConfiguredGearRing(int stage, uint color)
    {
        var frame = Frame();
        var active = frame with { ShiftCue = new(true, stage, color, true, 8_400) };
        var analog = Analog(active);
        var ring = Assert.Single(analog, command => command.TextureId == ShiftCueArtwork.AnalogTextureId);
        Assert.Equal(Analog(frame), analog.Where(command => command.TextureId != ShiftCueArtwork.AnalogTextureId));
        Assert.Equal(144f, ring.OriginX + ring.AxisXX / 2);
        Assert.Equal(142.5f, ring.OriginY + ring.AxisYY / 2);
        AssertColor(ring, color);

        var digital = Digital(active);
        var digitalRing = Assert.Single(digital, command => command.TextureId == ShiftCueArtwork.DigitalTextureId);
        Assert.Equal(Digital(frame), digital.Where(command => command.TextureId != ShiftCueArtwork.DigitalTextureId));
        var gear = DigitalHudLayout.Authored(active).Gear;
        Assert.Equal((float)gear.Origin.X, digitalRing.OriginX);
        Assert.Equal((float)gear.Origin.Y, digitalRing.OriginY);
        AssertColor(digitalRing, color);
    }

    [Theory]
    [InlineData(false, 3, true, 8400)]
    [InlineData(true, 0, true, 8400)]
    [InlineData(true, 4, true, 8400)]
    [InlineData(true, 3, false, 8400)]
    [InlineData(true, 3, true, 0)]
    [InlineData(true, 3, true, double.NaN)]
    public void DisabledInvalidAndFlashOffHaveExactlyTheOriginalCommands(bool enabled, int stage, bool flash, double target)
    {
        var frame = Frame();
        var hidden = frame with { ShiftCue = new(enabled, stage, 0xFFFF526D, flash, target) };
        Assert.Equal(Analog(frame), Analog(hidden));
        Assert.Equal(Digital(frame), Digital(hidden));
    }

    [Theory]
    [InlineData(TransmissionGear.Reverse)]
    [InlineData(TransmissionGear.Neutral)]
    [InlineData(TransmissionGear.Unknown)]
    public void InvalidDrivingGearNeverShowsACue(TransmissionGear gear)
    {
        var frame = Frame() with { Gear = gear, ShiftCue = new(true, 3, 0xFFFF526D, true, 8400) };
        Assert.DoesNotContain(Analog(frame), command => command.TextureId == ShiftCueArtwork.AnalogTextureId);
        Assert.DoesNotContain(Digital(frame), command => command.TextureId == ShiftCueArtwork.DigitalTextureId);
        var electric = Frame() with { IsElectric = true, ShiftCue = frame.ShiftCue };
        Assert.Empty(Analog(electric));
        Assert.DoesNotContain(Digital(electric), command => command.TextureId == ShiftCueArtwork.DigitalTextureId);
    }

    [Fact]
    public void PendingLegacyNativePixelsAreInvalidatedWhenTheFlashOrColorChanges()
    {
        var frame = Frame() with { ShiftCue = new(true, 3, 0xFFFF526D, true, 8400) };
        var sample = default(AnalogHudSample) with { Frame = frame };
        var presentation = new AnalogHudPresentation(293, 294, 0, 0, 1, 0, 0, 1, 1, true, false,
            default, AnalogHudLayout.Authored);
        var pending = new AnalogHudPendingFrame(sample, presentation, 1, 1);
        Assert.True(pending.CanReuse(presentation, frame, false));
        Assert.False(pending.CanReuse(presentation, frame with { ShiftCue = frame.ShiftCue with { FlashOn = false } }, false));
        Assert.False(pending.CanReuse(presentation, frame with { ShiftCue = frame.ShiftCue with { ColorArgb = 0xFF123456 } }, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WpfRingUsesTheSameMaskAndClearsCompletelyWhenDisabled(bool digital)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new ShiftCueVisual { Digital = digital, Width = 256, Height = 256 };
                visual.Measure(new Size(256, 256));
                visual.Arrange(new Rect(0, 0, 256, 256));
                visual.Update(new(true, 1, 0xFFFFFFFF, false, 8400));
                var actual = Render(visual);
                var expected = ShiftCueArtwork.Texture(digital).Pixels.ToArray();
                Assert.Equal(expected, actual);
                Assert.Contains(actual, alpha => alpha is > 0 and < 255);
                visual.Update(default);
                Assert.All(Render(visual), value => Assert.Equal((byte)0, value));
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static byte[] Render(FrameworkElement visual)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[256 * 256 * 4];
        bitmap.CopyPixels(pixels, 256 * 4, 0);
        return pixels;
    }

    private static void AssertColor(DirectCompositionDrawCommand command, uint color)
    {
        Assert.Equal((byte)(color >> 16) / 255f, command.TintR);
        Assert.Equal((byte)(color >> 8) / 255f, command.TintG);
        Assert.Equal((byte)color / 255f, command.TintB);
        Assert.Equal((byte)(color >> 24) / 255f, command.TintA);
    }

    private static NativeGaugeFrame Frame() => new(true, 137, 7000, 9000, TransmissionGear.Third,
        SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(8000 * Math.PI / 30));

    private static DirectCompositionDrawCommand[] Analog(NativeGaugeFrame frame) =>
        AnalogHudScene.Build(frame, 280, -.2, true, false, default);

    private static DirectCompositionDrawCommand[] Digital(NativeGaugeFrame frame) =>
        DigitalHudScene.Build(new(frame, frame.EngineRpm,
            NativeElectricSpeedDisplaySelector.Resolve(frame, 0), default, true), false, default);
}
