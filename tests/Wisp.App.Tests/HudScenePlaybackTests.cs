using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudScenePlaybackTests
{
    [Fact]
    public void SharedScenePreservesZOrderAndAppliesEachPlacementOnce()
    {
        var first = new ProbeSnapshot(11, "first");
        var second = new ProbeSnapshot(22, "second");
        var placement = new HudLayerPlacement(1, first, 100, 200, 0, 2, -3, 0, .5f);
        var scene = new HudScenePlayback();
        scene.Update(new(800, 600, true, [placement, Place(2, second)]), 10);
        var commands = scene.Build(20);
        Assert.Equal(new uint[] { 11, 22 }, commands.Select(command => command.TextureId));
        // Local origin (4,5), basis (6,0)/(0,7), at a rotated 2x/3x placement.
        Assert.Equal(85, commands[0].OriginX);
        Assert.Equal(208, commands[0].OriginY);
        Assert.Equal(0, commands[0].AxisXX);
        Assert.Equal(12, commands[0].AxisXY);
        Assert.Equal(-21, commands[0].AxisYX);
        Assert.Equal(0, commands[0].AxisYY);
        Assert.Equal(.25f, commands[0].TintA);
        Assert.Equal(4, commands[1].OriginX);
        Assert.Equal(.5f, commands[1].TintA);
        var again = scene.Build(21);
        Assert.Equal(commands[0].OriginX, again[0].OriginX);
        Assert.Equal(commands[0].TintA, again[0].TintA);
        Assert.Equal(4, first.LastPlayback!.Original.OriginX);
    }

    [Fact]
    public void NewFramesRetainPlaybackButRemovedOrReplacedLayersDoNotReuseState()
    {
        var first = new ProbeSnapshot(11, "one");
        var scene = new HudScenePlayback();
        scene.Update(Window(Place(1, first)), 10);
        var retained = first.LastPlayback!;
        var update = new ProbeSnapshot(11, "two", first.Textures);
        scene.Update(Window(Place(1, update)), 20);
        Assert.Equal(2, retained.Updates);
        Assert.Null(update.LastPlayback);
        var otherType = new OtherSnapshot();
        scene.Update(Window(Place(1, otherType)), 30);
        Assert.Equal(1, otherType.Created);
        Assert.Equal(2, retained.Updates);
        scene.Update(Window(), 40);
        Assert.Empty(scene.Build(41));
        scene.Update(Window(Place(1, first)), 50);
        Assert.NotSame(retained, first.LastPlayback);
        Assert.Equal(1, first.LastPlayback!.Updates);
        scene.Reset();
        Assert.Empty(scene.Build(60));
        Assert.Empty(scene.Textures);
    }

    [Fact]
    public void TextureChangesTrackArtworkReplacementAndLayerRemovalNotEveryTelemetryUpdate()
    {
        var first = new ProbeSnapshot(11, "one");
        var scene = new HudScenePlayback();
        scene.Update(Window(Place(1, first)), 10);
        Assert.True(scene.ConsumeTextureChanges());
        Assert.False(scene.ConsumeTextureChanges());
        scene.Update(Window(Place(1, new ProbeSnapshot(11, "two", first.Textures))), 20);
        Assert.False(scene.ConsumeTextureChanges());
        var replacement = new ProbeSnapshot(11, "theme");
        scene.Update(Window(Place(1, replacement)), 30);
        Assert.True(scene.ConsumeTextureChanges());
        Assert.Same(replacement.Textures[0], Assert.Single(scene.Textures));
        scene.Update(Window(), 40);
        Assert.True(scene.ConsumeTextureChanges());
        Assert.Empty(scene.Textures);
    }

    [Fact]
    public void PendingPixelsCannotSurviveWindowLayoutOpacityOrMembershipChanges()
    {
        var layer = Place(1, new ProbeSnapshot(11, "one"));
        var snapshot = Window(layer);
        Assert.True(snapshot.CompatibleWith(Window(layer with { Snapshot = new ProbeSnapshot(11, "two") })));
        Assert.False(snapshot.CompatibleWith(snapshot with { Active = false }));
        Assert.False(snapshot.CompatibleWith(snapshot with { Width = 801 }));
        Assert.False(snapshot.CompatibleWith(Window(layer with { AxisXX = 1.5f, AxisYY = 1.5f })));
        Assert.False(snapshot.CompatibleWith(Window(layer with { OriginX = 1 })));
        Assert.False(snapshot.CompatibleWith(Window(layer with { Opacity = .4f })));
        Assert.False(snapshot.CompatibleWith(Window()));
    }

    internal static void AssertOnCurrentDispatcher()
    {
        AssertNativeFreshness(electric: false);
        AssertNativeFreshness(electric: true);
        AssertDigitalPowerBarLayoutRefreshesAfterRatioChanges();
        var control = new NativeElectricDigitalSpeedometer();
        BindingOperations.ClearBinding(control, NativeElectricDigitalSpeedometer.FrameProperty);
        foreach (var multi in new[] { false, true, false })
            foreach (var assists in new[] { false, true, false })
            {
                control.Frame = Frame(true) with
                {
                    ElectricGearState = multi ? new(true, 2, 3, 1, 2, false) : default,
                    Assists = NativeAssistSnapshot.Unavailable() with { Available = assists, IsABSAvailable = assists }
                };
                Arrange(control);
                var snapshot = MainHudLayer.Capture(control, null);
                var playback = snapshot.CreatePlayback();
                playback.Update(snapshot, Stopwatch.GetTimestamp());
                var commands = playback.Build(Stopwatch.GetTimestamp());
                AssertImageQuad(control, "UnitImage", Assert.Single(commands, command => AssetName(command).Contains("Unit_Digital", StringComparison.Ordinal)));
                AssertImageQuad(control, "GearImage", Assert.Single(commands, command => AssetName(command).StartsWith("HUD_Dial_Digital_Gear_", StringComparison.Ordinal)));
                if (assists) AssertImageQuad(control, "AbsImage", Assert.Single(commands, command => AssetName(command).Contains("_ABS_", StringComparison.Ordinal)));
                var bar = commands.Where(command => command.TextureId == DigitalHudAssets.WhiteTextureId).ToArray();
                var powerBar = (FrameworkElement)control.FindName("PowerBarGrid");
                Assert.Equal(multi ? 234 : 215, powerBar.Width);
                Assert.Equal(powerBar.RenderSize.Width, bar[0].AxisXX + bar[2].AxisXX, 4);
                // Invalidated layout must keep the last arranged geometry until layout completes.
                control.InvalidateArrange();
                Assert.False(control.IsArrangeValid);
                var interim = MainHudLayer.Capture(control, null);
                Assert.Equal(snapshot.CompatibilityKey, interim.CompatibilityKey);
            }
    }

    private static void AssertDigitalPowerBarLayoutRefreshesAfterRatioChanges()
    {
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            var control = new NativeElectricDigitalSpeedometer();
            BindingOperations.ClearBinding(control, NativeElectricDigitalSpeedometer.FrameProperty);
            object? previousLayout = null;
            foreach (var ratio in new[] { .3, .37, .7, .3 })
            {
                control.Frame = Frame(true) with { NativeRegenPowerRatio = ratio };
                DigitalHudSceneTests.ArrangeAtDpi(control, dpi);
                var snapshot = MainHudLayer.Capture(control, null);
                if (previousLayout is not null) Assert.NotEqual(previousLayout, snapshot.CompatibilityKey);
                previousLayout = snapshot.CompatibilityKey;
                var playback = snapshot.CreatePlayback();
                var now = Stopwatch.GetTimestamp();
                playback.Update(snapshot, now);
                var commands = playback.Build(now);
                var bar = commands.Where(command => command.TextureId == DigitalHudAssets.WhiteTextureId).ToArray();
                Assert.Equal(4, bar.Length);
                var powerBar = (Grid)control.FindName("PowerBarGrid");
                Assert.Equal(powerBar.ColumnDefinitions[0].ActualWidth, bar[0].AxisXX, 4);
                AssertImageQuad(control, "RegenIndicator", bar[1]);
                AssertImageQuad(control, "PowerIndicator", bar[3]);
            }
        }
    }

    private static void AssertNativeFreshness(bool electric)
    {
        if (electric) ElectricHudAssets.LoadOnUiThread(); else AnalogHudAssets.LoadOnUiThread();
        UserControl control = electric ? new NativeElectricAnalogSpeedometer() : new NativeAnalogSpeedometer();
        var property = electric ? NativeElectricAnalogSpeedometer.FrameProperty : NativeAnalogSpeedometer.FrameProperty;
        BindingOperations.ClearBinding(control, property);
        var now = Stopwatch.GetTimestamp();
        var frame = Frame(electric) with
        {
            NativeNeedleAngleDegrees = 214.75,
            NativeNeedleBlurAmount = -.3,
            NativeGaugeObservedTimestamp = now,
            ReceivedTimestamp = now
        };
        control.SetValue(property, frame);
        Arrange(control);
        // Re-stamp after any first-use asset decode/layout work.
        now = Stopwatch.GetTimestamp();
        control.SetValue(property, frame with { NativeGaugeObservedTimestamp = now, ReceivedTimestamp = now });
        Arrange(control);
        var snapshot = electric ? MainHudLayer.Capture((NativeElectricAnalogSpeedometer)control, null) : MainHudLayer.Capture((NativeAnalogSpeedometer)control, null);
        var scene = new HudScenePlayback();
        var observed = Stopwatch.GetTimestamp();
        // The native observation remains fresh for this initial sample.
        scene.Update(Window(Place(1, snapshot)), observed);
        var commands = scene.Build(observed);
        var shader = electric ? DirectCompositionShader.ElectricNeedle : DirectCompositionShader.Needle;
        Assert.Equal(-.3f, Assert.Single(commands, command => command.Shader == shader).ParameterX);
        Assert.True(scene.CanReuse(observed));
        var expired = observed + Stopwatch.Frequency;
        Assert.False(scene.CanReuse(expired));
        Assert.Single(scene.Build(expired), command => command.Shader == shader);
        Assert.True(scene.CanReuse(expired));
    }

    private static NativeGaugeFrame Frame(bool electric) => new(true, 137, 5000, 9000, TransmissionGear.Third,
        SpeedUnit.MilesPerHour, ExactRedlineResult.Exact(8000 * Math.PI / 30), IsElectric: electric,
        CarOrdinal: 1, NativeRegenFillAmount: .1, NativePowerFillAmount: .6, NativeRegenPowerRatio: .3);
    private static void Arrange(FrameworkElement control)
    {
        control.Measure(new Size(control.Width, control.Height));
        control.Arrange(new Rect(0, 0, control.Width, control.Height));
        control.UpdateLayout();
    }
    private static string AssetName(DirectCompositionDrawCommand command) =>
        DigitalHudAssets.Definitions.FirstOrDefault(asset => asset.Id == command.TextureId)?.FileName ?? string.Empty;
    private static void AssertImageQuad(UserControl control, string name, DirectCompositionDrawCommand command)
    {
        var element = (FrameworkElement)control.FindName(name);
        var transform = element.TransformToAncestor(control);
        var origin = transform.Transform(new Point());
        Assert.InRange(Math.Abs(origin.X - command.OriginX), 0, .0001);
        Assert.InRange(Math.Abs(origin.Y - command.OriginY), 0, .0001);
    }
    private static HudWindowSnapshot Window(params HudLayerPlacement[] layers) => new(800, 600, true, layers);
    private static HudLayerPlacement Place(int id, HudLayerSnapshot snapshot) => new(id, snapshot, 0, 0, 1, 0, 0, 1, 1);

    private sealed class ProbeSnapshot(uint textureId, string value, IReadOnlyList<AnalogHudTexture>? textures = null) : HudLayerSnapshot
    {
        internal string Value { get; } = value;
        internal ProbePlayback? LastPlayback { get; private set; }
        internal override IReadOnlyList<AnalogHudTexture> Textures { get; } = textures ?? [new(textureId, 1, 1, 4, new byte[] { 255, 255, 255, 255 })];
        internal override HudLayerPlayback CreatePlayback() => LastPlayback = new(textureId);
    }
    private sealed class ProbePlayback(uint textureId) : HudLayerPlayback
    {
        internal DirectCompositionDrawCommand Original { get; } = AnalogHudScene.Quad(textureId, new(4, 5, 6, 7), opacity: .5);
        internal int Updates { get; private set; }
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) => Updates++;
        internal override DirectCompositionDrawCommand[] Build(long timestamp) => [Original];
    }
    private sealed class OtherSnapshot : HudLayerSnapshot
    {
        internal int Created { get; private set; }
        internal override IReadOnlyList<AnalogHudTexture> Textures => [];
        internal override HudLayerPlayback CreatePlayback() { Created++; return new ProbePlayback(33); }
    }
}
