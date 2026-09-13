using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.Drift;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class DriftGaugeZoneVisualTests
{
    private static readonly DriftZoneScoringProfile Profile = new(10, 110, (double)59.4f, 30, 40);

    internal static void AssertOnCurrentDispatcher()
    {
        ActualWindowContentStaysUnframedWhenLayoutIsUnlocked();
        VerifiedScaleUsesOneCachedColorRampWithoutFilledBands();
        OrdinaryDriftAnglesShowTheAvailableAngleBonus();
        FullBonusCeilingAndOverflowKeepTheirDistinctMeaning();
        AboveTheScoringLimitUsesWarningColorAndKeepsTheRealMagnitude();
        UnverifiedMalformedAndUnavailableDataNeverClaimABonus();
        MagnitudeWithoutLateralDirectionShowsUnsignedNumberWithoutAMarker();
        SaturationAndLimitReadoutsKeepOneDecimalWhileCustomReadoutsStaySignedIntegers();
        ZoneDarkModeUsesCachedArtworkAndLeavesTheGaugeBackgroundTransparent();
        BlackBackingOnlyAddsOneCachedBlackLayerAtTheChosenOpacity();
        MainReadoutsAreLargeAndSeparatedFromGuidance();
        BothModesKeepEveryCommandInsideTheBufferAndScaledViewport();
    }

    private static void ActualWindowContentStaysUnframedWhenLayoutIsUnlocked()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        var controller = new AppController(new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false
        }, _ => { }, new NoStartupRegistration());
        DriftGaugeWindow? window = null;
        try
        {
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new DriftGaugeWindow(controller);
            window.SetEnabled(true);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            // Render the actual content without inheriting the unshown Window's visibility.
            window.Content = null;
            foreach (var editMode in new[] { false, true, false })
            {
                window.SetEditMode(editMode);
                Assert.Same(editMode ? Cursors.SizeAll : Cursors.Arrow, window.Cursor);
                if (editMode) Assert.Equal(Visibility.Visible, content.Visibility);
                content.Measure(new Size(620, 108));
                content.Arrange(new Rect(0, 0, 620, 108));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(620, 108, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var pixels = new byte[620 * 108 * 4];
                bitmap.CopyPixels(pixels, 620 * 4, 0);
                Assert.DoesNotContain(pixels.Where((_, index) => index % 4 == 3), alpha => alpha != 0);
                Assert.False(window.IsVisible);
                Assert.False(window.ShowActivated);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle);
                Assert.Null(PresentationSource.FromVisual(content));
            }
        }
        finally
        {
            try
            {
                try { window?.Close(); }
                finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally { application.ShutdownMode = shutdownMode; }
        }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    private static void VerifiedScaleUsesOneCachedColorRampWithoutFilledBands()
    {
        var textures = DriftGaugeVisuals.LoadOnUiThread();
        Assert.Same(textures, DriftGaugeVisuals.LoadOnUiThread());
        var commands = Build(Reading(-40, 40, DriftGuidanceState.AngleBonusIncreasing));
        var scale = Assert.Single(commands, command => command.TextureId == 9);
        Assert.DoesNotContain(commands, command => command.TextureId == 7);
        AssertNoZoneRectangle(commands);
        var artwork = Assert.Single(textures, texture => texture.Id == scale.TextureId);
        foreach (var degrees in new[] { -90, -60, -30, -10, 0, 10, 30, 60, 90 })
            Assert.True(Alpha(artwork, X(degrees), 20) > 100, $"Missing tick at {degrees} degrees.");
        foreach (var sign in new[] { -1, 1 })
        {
            AssertPixelColor(artwork, X(0), 20, Color.FromRgb(255, 90, 95));
            AssertPixelColor(artwork, X(sign * 10), 20, Color.FromRgb(255, 205, 76));
            AssertPixelColor(artwork, X(sign * 30), 20, Color.FromRgb(202, 220, 127));
            AssertPixelColor(artwork, X(sign * 60), 20, Color.FromRgb(124, 242, 201));
            AssertPixelColor(artwork, X(sign * 90), 20, Color.FromRgb(255, 205, 76));
        }
        var image = Render(Reading(-40, 40, DriftGuidanceState.AngleBonusIncreasing), false);
        AssertPixelColor(image, X(30), 20, Color.FromRgb(202, 220, 127));
        AssertPixelColor(image, X(60), 20, Color.FromRgb(124, 242, 201));
    }

    private static void OrdinaryDriftAnglesShowTheAvailableAngleBonus()
    {
        foreach (var sample in new[]
        {
            (Angle: 20d, Percent: 80, Color: Color.FromRgb(228, 212, 101)),
            (Angle: 30d, Percent: 85, Color: Color.FromRgb(202, 220, 127)),
            (Angle: 40d, Percent: 90, Color: Color.FromRgb(175, 227, 152))
        })
            foreach (var sign in new[] { -1, 1 })
            {
                var commands = Build(Reading(sign * sample.Angle, sample.Angle, DriftGuidanceState.AngleBonusIncreasing));
                Assert.Equal(sample.Percent, DriftGaugeVisuals.AngleBonusPercent(sample.Angle, Profile));
                AssertBonus(commands, sample.Percent);
                Assert.Equal("SCORINGANGLE", TextAt(commands, 87));
                AssertTint(Assert.Single(commands, IsMarker), sample.Color);
                Assert.All(commands.Where(command => command.TextureId == 3 && command.OriginY == 50), command => AssertTint(command, sample.Color));
                Assert.Equal(X(sign * sample.Angle), Assert.Single(commands, IsMarker).OriginX + 1.5f, 3);
            }
        Assert.Equal(75, DriftGaugeVisuals.AngleBonusPercent(10, Profile));
        Assert.Equal(99, DriftGaugeVisuals.AngleBonusPercent(Profile.SaturationAngleDegrees - .0001, Profile));
        Assert.Equal(100, DriftGaugeVisuals.AngleBonusPercent(Profile.SaturationAngleDegrees, Profile));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1, 9.99, 110.01 })
            Assert.Null(DriftGaugeVisuals.AngleBonusPercent(invalid, Profile));
    }

    private static void FullBonusCeilingAndOverflowKeepTheirDistinctMeaning()
    {
        foreach (var sample in new[]
        {
            (Angle: Profile.SaturationAngleDegrees, Green: true), (Angle: 60d, Green: true),
            (Angle: 60.01d, Green: false), (Angle: 90d, Green: false),
            (Angle: 100d, Green: false), (Angle: 110d, Green: false)
        })
            foreach (var sign in new[] { -1, 1 })
            {
                var commands = Build(Reading(sign * sample.Angle, sample.Angle, DriftGuidanceState.MaximumAngleBonus));
                AssertBonus(commands, 100);
                Assert.Equal(sample.Green ? "FULLANGLEBONUS" : "NOEXTRABONUS", TextAt(commands, 87));
                AssertTint(Assert.Single(commands, IsMarker), sample.Green ? Color.FromRgb(124, 242, 201) : Color.FromRgb(255, 205, 76));
                Assert.Equal(X(Math.Clamp(sign * sample.Angle, -90, 90)), Assert.Single(commands, IsMarker).OriginX + 1.5f, 3);
                Assert.Equal(sample.Angle.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "°", NumericText(commands));
            }
    }

    private static void AboveTheScoringLimitUsesWarningColorAndKeepsTheRealMagnitude()
    {
        var commands = Build(Reading(135, 135, DriftGuidanceState.AboveScoringAngle));
        var marker = Assert.Single(commands, IsMarker);
        Assert.Equal(X(90), marker.OriginX + marker.AxisXX / 2, 3);
        AssertTint(marker, Color.FromRgb(255, 90, 95));
        Assert.Equal("135.0°", NumericText(commands));
        AssertBonus(commands, null);
        var below = Build(Reading(-5, 5, DriftGuidanceState.BelowScoringAngle));
        AssertTint(Assert.Single(below, IsMarker), Color.FromRgb(255, 90, 95));
        Assert.Equal("BELOWSCORINGANGLE", TextAt(below, 87));
        AssertBonus(below, null);
    }

    private static void UnverifiedMalformedAndUnavailableDataNeverClaimABonus()
    {
        var unavailable = Reading(62, 62, DriftGuidanceState.ProfileUnavailable);
        foreach (var profile in new DriftZoneScoringProfile?[] { null, new(110, 90, double.NaN, 30, 40), new(10, 110, 60, 30, 40) })
        {
            var commands = Build(unavailable, Presentation() with { ZoneProfile = profile });
            AssertNoZoneRectangle(commands);
            Assert.Single(commands, command => command.TextureId == 7);
            Assert.DoesNotContain(commands, command => command.TextureId == 9);
            Assert.Equal("62.0°", NumericText(commands));
            AssertBonus(commands, null);
            AssertTint(Assert.Single(commands, IsMarker), Colors.White);
        }
        foreach (var state in new[] { DriftGuidanceState.Unavailable, DriftGuidanceState.LowSpeed, DriftGuidanceState.Reversing })
        {
            var commands = Build(new(null, state) { IsZoneGuidance = true });
            Assert.DoesNotContain(commands, IsMarker);
            AssertBonus(commands, null);
            Assert.Equal("−−°", NumericText(commands));
        }
        foreach (var mode in new[] { DriftGaugeGuidanceMode.CustomTarget, DriftGaugeGuidanceMode.DriftZoneAngleBonus })
        {
            var commands = Build(Reading(62, 62, DriftGuidanceState.LowSpeed), Presentation() with { GuidanceMode = mode });
            Assert.DoesNotContain(commands, IsMarker);
            AssertBonus(commands, null);
            Assert.Equal("−−°", NumericText(commands));
            Assert.Equal("LOWSPEED", TextAt(commands, 87));
        }
    }

    private static void MagnitudeWithoutLateralDirectionShowsUnsignedNumberWithoutAMarker()
    {
        var vertical = Build(Reading(null, 62, DriftGuidanceState.MaximumAngleBonus));
        Assert.DoesNotContain(vertical, IsMarker);
        Assert.Equal("62.0°", NumericText(vertical));
        var number = vertical.Where(command => command.TextureId == 3 && command.OriginY == 50).ToArray();
        Assert.Equal(5, number.Length); // One decimal place and the degree glyph; no invented sign.
        foreach (var signed in new[] { -62d, 62d })
        {
            var withDirection = Build(Reading(signed, 62, DriftGuidanceState.MaximumAngleBonus));
            var otherNumber = withDirection.Where(command => command.TextureId == 3 && command.OriginY == 50).ToArray();
            Assert.Equal(number.Select(command => (command.UvLeft, command.UvTop)),
                otherNumber.Select(command => (command.UvLeft, command.UvTop)));
        }
    }

    private static void SaturationAndLimitReadoutsKeepOneDecimalWhileCustomReadoutsStaySignedIntegers()
    {
        Assert.Equal("59.4°", NumericText(Build(Reading(Profile.SaturationAngleDegrees, Profile.SaturationAngleDegrees,
            DriftGuidanceState.MaximumAngleBonus))));
        Assert.Equal("110.0°", NumericText(Build(Reading(110, 110, DriftGuidanceState.MaximumAngleBonus))));
        Assert.Equal("+59°", TextAt(Build(new(59.4, DriftGuidanceState.AboveTarget),
            Presentation() with { GuidanceMode = DriftGaugeGuidanceMode.CustomTarget }), 50));
    }

    private static void ZoneDarkModeUsesCachedArtworkAndLeavesTheGaugeBackgroundTransparent()
    {
        var textures = DriftGaugeVisuals.LoadOnUiThread();
        Assert.Same(textures, DriftGaugeVisuals.LoadOnUiThread());
        var commands = Build(Reading(100, 100, DriftGuidanceState.MaximumAngleBonus), Presentation() with { DarkMode = true });
        Assert.Single(commands, command => command.TextureId == 8);
        AssertNoZoneRectangle(commands);
        foreach (var id in new uint[] { 7, 8, 9 })
            Assert.Equal(0, Alpha(Assert.Single(textures, texture => texture.Id == id), 310, 105));
        foreach (var dark in new[] { false, true })
        {
            var bitmap = Render(Reading(100, 100, DriftGuidanceState.MaximumAngleBonus), dark);
            foreach (var point in new[] { new Point(X(-82.5), 33), new Point(X(62.5), 33), new Point(10, 54), new Point(610, 54), new Point(310, 2), new Point(450, 105) })
                Assert.Equal(0, Pixel(bitmap, point.X, point.Y)[3]);
            var captionPixels = new byte[116 * 15 * 4];
            bitmap.CopyPixels(new Int32Rect(480, 91, 116, 15), captionPixels, 116 * 4, 0);
            Assert.True(captionPixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 20) > 80,
                "The cached angle-factor caption is not visible in the composed image.");
        }
    }

    private static void BlackBackingOnlyAddsOneCachedBlackLayerAtTheChosenOpacity()
    {
        var reading = Reading(40, 40, DriftGuidanceState.AngleBonusIncreasing);
        foreach (var dark in new[] { false, true })
            foreach (var opacity in new[] { 0d, .25, .5, 1 })
            {
                var presentation = Presentation() with { DarkMode = dark };
                var transparent = Build(reading, presentation);
                var backed = Build(reading, presentation with { BackgroundEnabled = true, BackgroundOpacity = opacity });
                Assert.Equal(transparent, backed.Where(command => command.TextureId != 12));
                if (opacity == 0) Assert.DoesNotContain(backed, command => command.TextureId == 12);
                else
                {
                    var backing = Assert.Single(backed, command => command.TextureId == 12);
                    Assert.Equal(backed[0], backing);
                    Assert.Equal((float)opacity, backing.TintA);
                    AssertTint(backing, Colors.Black);
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        DriftGaugeVisuals.Draw(drawing, new Size(620, 108), reading, 40, 10, dark,
                            DriftGaugeGuidanceMode.DriftZoneAngleBonus, Profile, true, opacity);
                    var bitmap = new RenderTargetBitmap(620, 108, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var backgroundPixel = Pixel(bitmap, 450, 60);
                    Assert.Equal(new byte[] { 0, 0, 0 }, backgroundPixel.Take(3));
                    Assert.InRange(backgroundPixel[3], (byte)Math.Floor(opacity * 255), (byte)Math.Ceiling(opacity * 255));
                    foreach (var edge in new[] { new Point(0, 0), new Point(310, 0), new Point(619, 54), new Point(310, 107) })
                        Assert.Equal(0, Pixel(bitmap, edge.X, edge.Y)[3]);
                }
            }
    }

    private static void MainReadoutsAreLargeAndSeparatedFromGuidance()
    {
        foreach (var magnitude in new[] { 20d, 40, 59.4, 110 })
        {
            var commands = Build(Reading(magnitude, magnitude, magnitude < 59.4 ? DriftGuidanceState.AngleBonusIncreasing : DriftGuidanceState.MaximumAngleBonus));
            var angle = commands.Where(command => command.TextureId == 3 && command.OriginY == 50).ToArray();
            var percent = commands.Where(command => command.TextureId == 3 && command.OriginY == 58).ToArray();
            var guidance = commands.Where(command => command.TextureId == 3 && command.OriginY == 87).ToArray();
            Assert.NotEmpty(angle);
            Assert.NotEmpty(percent);
            Assert.All(angle, command => Assert.Equal(56, command.AxisYY));
            Assert.All(percent, command => Assert.Equal(39.2f, command.AxisYY, 3));
            Assert.True(guidance.Max(command => command.OriginX + command.AxisXX) < angle.Min(command => command.OriginX));
            Assert.True(angle.Max(command => command.OriginX + command.AxisXX) < percent.Min(command => command.OriginX));
        }
    }

    private static void BothModesKeepEveryCommandInsideTheBufferAndScaledViewport()
    {
        foreach (var mode in Enum.GetValues<DriftGaugeGuidanceMode>())
            foreach (var dark in new[] { false, true })
                foreach (var state in Enum.GetValues<DriftGuidanceState>())
                {
                    var presentation = Presentation() with { GuidanceMode = mode, DarkMode = dark, BackgroundEnabled = true, Width = 744, Height = 130 };
                    var commands = Build(Reading(-180, 180, state), presentation);
                    Assert.InRange(commands.Length, 1, DriftGaugeVisuals.MaximumCommands);
                    foreach (var command in commands)
                    {
                        Assert.True(float.IsFinite(command.OriginX) && float.IsFinite(command.OriginY));
                        Assert.InRange(command.OriginX, 0, presentation.Width);
                        Assert.InRange(command.OriginY, 0, presentation.Height);
                        Assert.InRange(command.OriginX + command.AxisXX, 0, presentation.Width + .001f);
                        Assert.InRange(command.OriginY + command.AxisYY, 0, presentation.Height + .001f);
                        Assert.InRange(command.UvLeft, 0, 1);
                        Assert.InRange(command.UvTop, 0, 1);
                        Assert.InRange(command.UvRight, command.UvLeft, 1);
                        Assert.InRange(command.UvBottom, command.UvTop, 1);
                    }
                }
    }

    private static DriftGaugePresentation Presentation() => new(620, 108, 1, 1, 1, true, 40, 10,
        GuidanceMode: DriftGaugeGuidanceMode.DriftZoneAngleBonus, ZoneProfile: Profile);

    private static DriftAngleReading Reading(double? signed, double magnitude, DriftGuidanceState state) =>
        new(signed, state) { IsZoneGuidance = true, MagnitudeDegrees = magnitude };

    private static DirectCompositionDrawCommand[] Build(DriftAngleReading reading, DriftGaugePresentation? presentation = null)
    {
        DriftGaugeVisuals.LoadOnUiThread();
        var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
        var count = DriftGaugeVisuals.Build(commands, presentation ?? Presentation(), reading);
        return commands.Take(count).ToArray();
    }

    private static void AssertNoZoneRectangle(DirectCompositionDrawCommand[] commands) =>
        Assert.All(commands.Where(command => command.TextureId == 1), command =>
            Assert.True(IsMarker(command) || command.OriginY == 5 && command.AxisXX == 8 && command.AxisYY == 3));

    private static bool IsMarker(DirectCompositionDrawCommand command) =>
        command.TextureId == 1 && command.OriginY == 7 && command.AxisXX == 3 && command.AxisYY == 34;

    private static string NumericText(DirectCompositionDrawCommand[] commands) => TextAt(commands, 50);

    private static void AssertBonus(DirectCompositionDrawCommand[] commands, int? percent)
    {
        Assert.Equal(percent is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : "", TextAt(commands, 58));
        if (percent is null)
        {
            Assert.DoesNotContain(commands, command => command.TextureId is 10 or 11);
            return;
        }
        // The numeric portion uses glyphs; the fixed "MAX ANGLE BONUS" caption is one cached sprite.
        var caption = Assert.Single(commands, command => command.TextureId == 10);
        Assert.Equal(480, caption.OriginX);
        Assert.Equal(91, caption.OriginY);
        Assert.Equal(116, caption.AxisXX);
        Assert.Equal(15, caption.AxisYY);
        var texture = Assert.Single(DriftGaugeVisuals.LoadOnUiThread(), texture => texture.Id == 10);
        Assert.True(texture.Pixels.ToArray().Where((_, index) => index % 4 == 3).Count(alpha => alpha > 20) > 200);
    }

    private static string TextAt(DirectCompositionDrawCommand[] commands, float y)
    {
        const string atlasGlyphs = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-−°.%";
        return new string(commands.Where(command => command.TextureId == 3 && command.OriginY == y)
            .Select(command => atlasGlyphs[(int)Math.Round(command.UvTop * 3) * 16 + (int)Math.Round(command.UvLeft * 16)]).ToArray());
    }

    private static float X(double degrees) => (float)(24 + (degrees + 90) / 180 * (596 - 24));

    private static void AssertTint(DirectCompositionDrawCommand command, Color color)
    {
        Assert.Equal(color.R / 255f, command.TintR, 6);
        Assert.Equal(color.G / 255f, command.TintG, 6);
        Assert.Equal(color.B / 255f, command.TintB, 6);
    }

    private static BitmapSource Render(DriftAngleReading reading, bool dark)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            DriftGaugeVisuals.Draw(drawing, new Size(620, 108), reading, 40, 10, dark,
                DriftGaugeGuidanceMode.DriftZoneAngleBonus, Profile);
        var bitmap = new RenderTargetBitmap(620, 108, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixel(BitmapSource bitmap, double x, double y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(x), (int)Math.Round(y), 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void AssertPixelColor(BitmapSource bitmap, double x, double y, Color expected) =>
        AssertPremultipliedColor(Pixel(bitmap, x, y), expected);

    private static void AssertPixelColor(AnalogHudTexture texture, double x, double y, Color expected)
    {
        var offset = (int)Math.Round(y * texture.Height / 108) * texture.Stride + (int)Math.Round(x * texture.Width / 620) * 4;
        AssertPremultipliedColor(texture.Pixels.Span.Slice(offset, 4).ToArray(), expected);
    }

    private static void AssertPremultipliedColor(byte[] pixel, Color expected)
    {
        Assert.True(pixel[3] > 50);
        Assert.InRange(pixel[0] * 255d / pixel[3], expected.B - 2, expected.B + 2);
        Assert.InRange(pixel[1] * 255d / pixel[3], expected.G - 2, expected.G + 2);
        Assert.InRange(pixel[2] * 255d / pixel[3], expected.R - 2, expected.R + 2);
    }

    private static byte Alpha(AnalogHudTexture texture, double x, double y)
    {
        var pixelX = (int)Math.Round(x * texture.Width / 620);
        var pixelY = (int)Math.Round(y * texture.Height / 108);
        return texture.Pixels.Span[pixelY * texture.Stride + pixelX * 4 + 3];
    }

}
