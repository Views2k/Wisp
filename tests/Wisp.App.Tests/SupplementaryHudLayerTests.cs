using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryHudLayerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoostKeepsNativeNeedleProfileAndItsOriginalLastPaintOrder(bool electric)
    {
        var snapshot = Boost(config: BoostConfig() with { Electric = electric });
        var commands = BoostHudLayer.Build(snapshot, 35, .5, 0);
        var needle = commands[^1];
        Assert.Equal(electric ? DirectCompositionShader.ElectricNeedle : DirectCompositionShader.Needle, needle.Shader);
        var expected = AnalogHudScene.Quad(0, new(136 * 178.5 / 288, 136 * 54d / 288, 136 * 110d / 288, 136 * 180d / 288),
            240, new(68, 68), shader: needle.Shader);
        Assert.Equal(expected.OriginX, needle.OriginX);
        Assert.Equal(expected.OriginY, needle.OriginY);
        Assert.Equal(expected.AxisXX, needle.AxisXX);
        Assert.Equal(expected.AxisXY, needle.AxisXY);
        Assert.Equal(0, needle.ParameterX);
    }

    [Theory]
    [InlineData(BoostPressureUnit.Psi, -20, 70)]
    [InlineData(BoostPressureUnit.Bar, -14.503773773, 72.518868865)]
    public void VacuumArcRunsBetweenPressureAndZeroAndKeepsFullUnitRange(BoostPressureUnit unit, double minimum, double maximum)
    {
        var snapshot = Boost(config: BoostConfig() with { Unit = unit, Vacuum = true });
        var belowZero = BoostHudLayer.Build(snapshot, minimum, 0, 0);
        var arcs = belowZero.Where(c => c.Shader == DirectCompositionShader.ImageSector).ToArray();
        Assert.NotEmpty(arcs);
        Assert.InRange(arcs[0].ParameterX, (float)(110 * Math.PI / 180) - .0001, (float)(110 * Math.PI / 180) + .0001);
        Assert.DoesNotContain(BoostHudLayer.Build(snapshot, 0, 0, 0), c => c.Shader == DirectCompositionShader.ImageSector);
        var full = BoostHudLayer.Build(snapshot, maximum, 1, 0);
        Assert.InRange(Angle(full[^1]), 9.99, 10.01);
    }

    [Fact]
    public void DigitalStockBoostUsesTheOriginalMaterialAndTheHalvedAuthoredHeight()
    {
        var config = BoostConfig() with { Digital = true, StockMaterial = true, Width = 302, Height = 88 };
        var commands = BoostHudLayer.Build(Boost(config: config), 28, .4, 0);
        var rail = commands[^1];
        Assert.Equal(DirectCompositionShader.DigitalGauge, rail.Shader);
        Assert.Equal(302, rail.AxisXX);
        Assert.Equal(12, rail.AxisYY);
        Assert.Equal(55.75f, rail.OriginY);
        Assert.Equal(.4f, rail.ParameterX);
        Assert.Equal(1, rail.ParameterY);
        Assert.DoesNotContain(commands, c => c.TextureId == 30003);
    }

    [Fact]
    public void CustomDigitalBoostKeepsSlantedFillGradientClippingAndNeutralMovingMarker()
    {
        var config = BoostConfig() with { Digital = true, Width = 302, Height = 88 };
        var commands = BoostHudLayer.Build(Boost(config: config), 35, .5, 0);
        var fill = Assert.Single(commands, c => c.TextureId == 30003 && c.TintA == 1);
        var glow = Assert.Single(commands, c => c.TextureId == 30003 && c.TintA < 1);
        Assert.Equal(141.5f, fill.AxisXX);
        Assert.Equal(-1.3f, fill.AxisYX);
        Assert.Equal(.5f, fill.UvRight);
        Assert.Equal(.5f, glow.UvLeft);
        Assert.True(glow.UvRight > glow.UvLeft);
        var marker = Assert.Single(commands, c => c.TextureId == 30004);
        Assert.Equal(1, marker.TintR);
        Assert.Equal(1, marker.TintG);
        Assert.Equal(1, marker.TintB);
    }

    [Fact]
    public void NumberPulsePreservesThresholdPeriodAndNeutralStockPalette()
    {
        var config = BoostConfig() with { ColorNumber = true };
        Assert.Equal(new AnalogHudColor(245, 245, 245), BoostHudLayer.NumberColor(config, 5, 1, 0));
        var a = BoostHudLayer.NumberColor(config, 40, .95, Ticks(155));
        var b = BoostHudLayer.NumberColor(config, 40, .95, Ticks(465));
        Assert.InRange(a.A, 254, 255);
        Assert.InRange(b.A, 175, 176);
        Assert.Equal(a.R, b.R);
        Assert.Equal(a.G, b.G);
        Assert.Equal(a.B, b.B);
        var stock = config with { Middle = config.Low, High = config.Low };
        Assert.Equal(new AnalogHudColor(245, 245, 245), BoostHudLayer.NumberColor(stock, 70, 1, 0));
    }

    [Fact]
    public void BoostMotionContinuesBetweenPacketsAndResetsForDifferentCarOrUnits()
    {
        var origin = Ticks(1000);
        var now = origin;
        var playback = new BoostHudLayer.Playback(() => now);
        for (var i = 0; i <= 5; i++)
        {
            var time = origin + Ticks(i * 16);
            now = time;
            playback.Update(Boost(pressure: i * 10, received: time, game: (uint)(i * 16)), time);
        }
        var first = Angle(playback.Build(origin + Ticks(80))[^1]);
        var between = Angle(playback.Build(origin + Ticks(88))[^1]);
        Assert.True(between > first);
        Assert.InRange(between - first, 1, 30);
        now = origin + Ticks(96);
        playback.Update(Boost(pressure: 0, car: 2, received: now), now);
        Assert.InRange(Angle(playback.Build(origin + Ticks(96))[^1]), 109.999, 110.001);
        now = origin + Ticks(112);
        playback.Update(Boost(pressure: 0, config: BoostConfig() with { Unit = BoostPressureUnit.Bar, Vacuum = true },
            received: now), now);
        Assert.InRange(Angle(playback.Build(origin + Ticks(112))[^1]), 153.32, 153.35);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedBoostSnapshotKeepsItsTimelineWhenPublishedBeforeThePreviousRender(bool hasReceivedTimestamp)
    {
        var origin = Ticks(1000);
        var now = origin;
        var playback = new BoostHudLayer.Playback(() => now);
        for (var ms = 0; ms <= 40; ms += 10)
        {
            now = origin + Ticks(ms);
            playback.Update(Boost(pressure: 10 + ms * .5, game: (uint)ms, received: now), now);
        }
        AssertNeedleAngle(playback.Build(origin + Ticks(50))[^1], 110 + 260 * 15 / 70d);

        var published = origin + Ticks(49);
        now = origin + Ticks(52);
        playback.Update(Boost(pressure: 34.5, game: 49, received: hasReceivedTimestamp ? published : 0), published);

        AssertNeedleAngle(playback.Build(now)[^1], 110 + 260 * 16 / 70d);
        AssertNeedleAngle(playback.Build(origin + Ticks(85))[^1], 110 + 260 * 32.5 / 70d);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedTireSnapshotKeepsBothTimelinesWhenPublishedBeforeThePreviousRender(bool hasReceivedTimestamp)
    {
        var origin = Ticks(1000);
        var now = origin;
        var playback = new TireHudLayer.Playback(() => now);
        for (var ms = 0; ms <= 40; ms += 10)
        {
            now = origin + Ticks(ms);
            playback.Update(Snapshot(ms, now), now);
        }
        AssertTireAngles(playback.Build(origin + Ticks(50)), .2, .7);

        var published = origin + Ticks(49);
        now = origin + Ticks(52);
        playback.Update(Snapshot(49, hasReceivedTimestamp ? published : 0), published);

        AssertTireAngles(playback.Build(now), .22, .68);
        AssertTireAngles(playback.Build(origin + Ticks(85)), .55, .35);

        static TireHudLayer.Snapshot Snapshot(int ms, long received) => new(TireConfig(),
            new(Array.Empty<AnalogHudTexture>(), DummyGlyphs()),
            new(true, 200, 220, .1 + ms * .01, .8 - ms * .01), 1, (uint)ms, received);
    }

    private static void AssertTireAngles(DirectCompositionDrawCommand[] commands, double front, double rear)
    {
        var needles = commands.Where(command => command.TextureId == 40002).ToArray();
        Assert.Equal(2, needles.Length);
        AssertNeedleAngle(needles[0], 110 + 260 * rear);
        AssertNeedleAngle(needles[1], 110 + 260 * front);
    }

    private static void AssertNeedleAngle(DirectCompositionDrawCommand command, double degrees) =>
        Assert.InRange(Angle(command), degrees - .001, degrees + .001);

    [Fact]
    public void CompatibilityIgnoresLiveValuesButInvalidatesThePendingSceneForConfigurationAndAvailability()
    {
        Assert.Equal(Boost(pressure: 10).CompatibilityKey, Boost(pressure: 50).CompatibilityKey);
        Assert.NotEqual(Boost().CompatibilityKey, Boost(config: BoostConfig() with { Unit = BoostPressureUnit.Bar }).CompatibilityKey);
        Assert.NotEqual(Boost().CompatibilityKey, Boost(available: false).CompatibilityKey);
        Assert.NotEqual(Boost().CompatibilityKey, Boost(car: 3).CompatibilityKey);
        Assert.Equal(Tire(front: .1).CompatibilityKey, Tire(front: .9).CompatibilityKey);
        Assert.NotEqual(Tire().CompatibilityKey, Tire(config: TireConfig() with { Unit = TireTemperatureUnit.Celsius }).CompatibilityKey);
    }

    [Fact]
    public void BoostDigitsKeepTheReceivedPressureWhileTheNeedlePlaysBetweenPackets()
    {
        var commands = BoostHudLayer.Build(Boost(pressure: 35), 0, 0, 0);
        Assert.Equal(new uint[] { 30013, 30015 }, commands.Where(c => c.TextureId is >= 30010 and <= 30019)
            .Select(c => c.TextureId).ToArray());
        Assert.InRange(Angle(commands[^1]), 109.999, 110.001);
    }

    [Fact]
    public void TireRetainsSeparateFrontAndRearColorsLabelsAndReadoutsAboveTheTaperedNeedles()
    {
        var snapshot = Tire(config: TireConfig() with { ReactiveColors = true });
        var commands = TireHudLayer.Build(snapshot, .8, .2);
        var needles = commands.Where(c => c.TextureId == 40002).ToArray();
        Assert.Equal(2, needles.Length);
        Assert.Equal(snapshot.Config.Middle.R / 255f, needles[0].TintR);
        Assert.Equal(snapshot.Config.Low.R / 255f, needles[1].TintR);
        Assert.Equal((float)(Math.Round(226 * .86) / 255), needles[0].TintA);
        Assert.Equal(242 / 255f, needles[1].TintA);
        var faceIndex = Array.FindIndex(commands, c => c.TextureId == 40001);
        Assert.True(faceIndex > Array.FindLastIndex(commands, c => c.TextureId == 40002));
        Assert.All(commands.Skip(faceIndex + 1), c => Assert.InRange(c.TextureId, 40010u, 40022u));
    }

    [Fact]
    public void TireStockPaletteAndDisabledReactiveColorsKeepTheNeutralNeedleMaterial()
    {
        var c = TireConfig();
        Assert.Equal(new AnalogHudColor(244, 246, 250, 242), TireHudLayer.NeedleColor(c, c.Low, 242));
        c = c with { ReactiveColors = true, Middle = c.Low, High = c.Low };
        Assert.Equal(new AnalogHudColor(244, 246, 250, 242), TireHudLayer.NeedleColor(c, c.Low, 242));
        c = c with { Digital = true };
        Assert.Equal(new AnalogHudColor(248, 250, 253), TireHudLayer.NeedleColor(c, c.Low, 255));
    }

    [Fact]
    public void UnavailableAndNonfiniteMotionNeverSubmitGaugePixels()
    {
        Assert.Empty(BoostHudLayer.Build(Boost(available: false), 10, .5, 0));
        Assert.Empty(BoostHudLayer.Build(Boost(), double.NaN, .5, 0));
        Assert.Empty(TireHudLayer.Build(Tire(available: false), .4, .5));
        Assert.Empty(TireHudLayer.Build(Tire(), double.NaN, .5));
        var now = Ticks(1000);
        var playback = new TireHudLayer.Playback(() => now);
        playback.Update(Tire(), now);
        Assert.NotEmpty(playback.Build(now));
        now = Ticks(1016);
        playback.Update(Tire(available: false), now);
        Assert.Empty(playback.Build(now));
    }

    internal static void AssertOnCurrentDispatcher()
    {
        var boost = new AnalogBoostGaugeView
        {
            Width = 136,
            Height = 136,
            Display = new(true, 20, 50, .4, 70),
            LowBrush = Brushes.DeepSkyBlue,
            MidBrush = Brushes.RoyalBlue,
            HighBrush = Brushes.MediumPurple
        };
        Arrange(boost, 136, 136);
        var initial = BoostHudLayer.Capture(boost, null);
        var copied = initial.Textures[0].Pixels.ToArray();
        for (var i = 0; i < 120; i++)
        {
            boost.Display = boost.Display with { PressurePsi = i / 2d, Fraction = i / 120d };
            Assert.Same(initial.Textures, BoostHudLayer.Capture(boost, null).Textures);
        }
        boost.LowBrush = Brushes.Orange;
        var changed = BoostHudLayer.Capture(boost, null);
        Assert.NotSame(initial.Textures, changed.Textures);
        Assert.Equal(copied, initial.Textures[0].Pixels.ToArray());
        AssertNativeDigitPixels(initial.Textures, 30010);
        AssertTextures(initial.Textures, 30000, 39999);

        var tire = new AnalogTireTemperatureGaugeView
        {
            Width = 136,
            Height = 136,
            Display = new(true, 200, 180, .5, .4)
        };
        Arrange(tire, 136, 136);
        var tireInitial = TireHudLayer.Capture(tire, null);
        tire.Display = new(true, 220, 250, .56, .66);
        Assert.Same(tireInitial.Textures, TireHudLayer.Capture(tire, null).Textures);
        tire.TemperatureUnit = TireTemperatureUnit.Celsius;
        Assert.NotSame(tireInitial.Textures, TireHudLayer.Capture(tire, null).Textures);
        AssertNativeDigitPixels(tireInitial.Textures, 40010);
        AssertTextures(tireInitial.Textures, 40000, 49999);

        var digitalBoost = new DigitalBoostRailView { Width = 302, Height = 88, Display = boost.Display };
        Arrange(digitalBoost, 302, 88);
        var digital = (BoostHudLayer.Snapshot)BoostHudLayer.Capture(digitalBoost, null);
        AssertTextures(digital.Textures, 30000, 39999);
        var text = "-1.2 BAR";
        var actual = SupplementaryHudArt.TextWidth(digital.Artwork.Glyphs!, text);
        var expected = SupplementaryHudArt.Text(text, 16, Brushes.White, FontWeights.SemiBold, FontStyles.Italic).WidthIncludingTrailingWhitespace;
        Assert.InRange(Math.Abs(actual - expected), 0, .01);
        var digitalTire = new DigitalTireTemperatureGaugeView { Width = 302, Height = 84, Display = tire.Display };
        Arrange(digitalTire, 302, 84);
        var digitalTireSnapshot = TireHudLayer.Capture(digitalTire, null);
        AssertTextures(digitalTireSnapshot.Textures, 40000, 49999);
        Assert.NotEqual(digitalTireSnapshot.Textures.Single(t => t.Id == 40002).Pixels.ToArray(),
            digitalTireSnapshot.Textures.Single(t => t.Id == 40003).Pixels.ToArray());
        AssertFallbackTransitions(boost, digitalBoost, tire, digitalTire);
    }

    private static void AssertFallbackTransitions(AnalogBoostGaugeView analog, DigitalBoostRailView digital,
        AnalogTireTemperatureGaugeView analogTire, DigitalTireTemperatureGaugeView digitalTire)
    {
        var marker = (DependencyProperty)typeof(HudNativeHost)
            .GetField("PresentedProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var canvas = (Canvas)((Viewbox)analog.Children[0]).Child;
        var material = (NativeAnalogNeedleVisual)canvas.Children[0];
        var rotation = (RotateTransform)canvas.RenderTransform;
        Assert.True(Render(analog) > 0);
        var previousAngle = rotation.Angle;
        analog.SetValue(marker, true);
        analog.Display = new(true, 17, 70, 17 / 70d, 70);
        analog.IsElectricMaterial = true;
        Assert.Equal(0, Render(analog));
        Assert.Equal(Visibility.Hidden, material.Visibility);
        Assert.Equal(previousAngle, rotation.Angle);
        Assert.Equal(17, ((BoostHudLayer.Snapshot)BoostHudLayer.Capture(analog, null)).Display.PressurePsi);
        analog.SetValue(marker, false);
        Assert.True(Render(analog) > 0);
        Assert.Equal(Visibility.Visible, material.Visibility);
        Assert.True(material.IsElectricMaterial);
        Assert.Equal(110 + 260 * 17 / 70d, rotation.Angle, 8);

        digital.UseStockColors = true;
        Render(digital);
        var stockMaterial = (NativeDigitalGaugeVisual)digital.Children[0];
        Assert.Equal(Visibility.Visible, stockMaterial.Visibility);
        digital.SetValue(marker, true);
        Assert.Equal(0, Render(digital));
        Assert.Equal(Visibility.Hidden, stockMaterial.Visibility);
        digital.Display = BoostDisplay.Unavailable;
        digital.SetValue(marker, false);
        Render(digital);
        Assert.Equal(Visibility.Collapsed, stockMaterial.Visibility);
        digital.Display = new(true, 30, 50, .6, 70);
        Render(digital);
        Assert.Equal(Visibility.Visible, stockMaterial.Visibility);

        foreach (var tire in new TireTemperatureVisualBase[] { analogTire, digitalTire })
        {
            Assert.True(Render(tire) > 0);
            tire.SetValue(marker, true);
            tire.Display = new(true, 180, 200, .43, .5);
            Assert.Equal(0, Render(tire));
            Assert.Equal(180, ((TireHudLayer.Snapshot)TireHudLayer.Capture(tire, null)).Display.FrontFahrenheit);
            tire.SetValue(marker, false);
            Assert.True(Render(tire) > 0);
        }
    }

    private static int Render(FrameworkElement control)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            control.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [context]);
        return visual.Drawing?.Children.Count ?? 0;
    }

    private static void AssertNativeDigitPixels(IReadOnlyList<AnalogHudTexture> textures, uint first)
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            var source = new FormatConvertedBitmap(NativeAssetCache.Get(NativeGaugeMode.Analogue,
                $"HUD_Dial_Speed_Analogue_{digit}.png"), PixelFormats.Pbgra32, null, 0);
            var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
            source.CopyPixels(bytes, source.PixelWidth * 4, 0);
            Assert.Equal(bytes, textures.Single(t => t.Id == first + digit).Pixels.ToArray());
        }
    }

    private static void AssertTextures(IReadOnlyList<AnalogHudTexture> textures, uint minimum, uint maximum)
    {
        Assert.Equal(textures.Count, textures.Select(t => t.Id).Distinct().Count());
        Assert.All(textures, t =>
        {
            Assert.InRange(t.Id, minimum, maximum);
            Assert.Equal(t.Stride * t.Height, t.Pixels.Length);
        });
    }

    private static void Arrange(FrameworkElement control, double width, double height)
    {
        control.Measure(new(width, height));
        control.Arrange(new(0, 0, width, height));
        control.UpdateLayout();
    }

    private static double Angle(DirectCompositionDrawCommand command) =>
        (Math.Atan2(command.AxisXY, command.AxisXX) * 180 / Math.PI + 360) % 360;
    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);
    private static BoostHudLayer.Configuration BoostConfig() => new(false, 136, 136, 1,
        BoostPressureUnit.Psi, false, true, false, false, new(72, 217, 241), new(57, 127, 247), new(164, 92, 255));
    private static TireHudLayer.Configuration TireConfig() => new(false, 136, 136, 1,
        TireTemperatureUnit.Fahrenheit, false, true, new(72, 217, 241), new(57, 127, 247), new(164, 92, 255));
    private static SupplementaryHudArt.GlyphSet DummyGlyphs() => new(new Dictionary<char, SupplementaryHudArt.Glyph>(), 20);
    private static BoostHudLayer.Snapshot Boost(double pressure = 35, bool available = true,
        BoostHudLayer.Configuration? config = null, int car = 1, uint game = 0, long received = 0) =>
        new(config ?? BoostConfig(), new(Array.Empty<AnalogHudTexture>(), DummyGlyphs()),
            new(available, pressure, 70, pressure / 70, 70), car, game, received);
    private static TireHudLayer.Snapshot Tire(double front = .5, bool available = true,
        TireHudLayer.Configuration? config = null) => new(config ?? TireConfig(),
            new(Array.Empty<AnalogHudTexture>(), DummyGlyphs()), new(available, 200, 220, front, .7), 1, 0, 0);
}
