using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ElectricHudSceneTests
{
    [Fact]
    public void FullScenePreservesAuthoredLayerOrderAndExactNeedleMaterial()
    {
        var commands = ElectricHudScene.Build(Sample(Frame()), true, new(85, 230, 193));
        var names = commands.Select(Name).ToArray();
        Assert.Equal("SpeedDial.png", names[0]);
        Assert.Equal("GearGauge2.png", names[1]);
        var needleIndex = Array.FindIndex(commands, command => command.Shader == DirectCompositionShader.ElectricNeedle);
        Assert.True(Array.IndexOf(names, "HUD_EV_Gear_Small_1.png") < needleIndex);
        Assert.True(Array.IndexOf(names, "HUD_EV_Gear_2.png") < needleIndex);
        Assert.Equal("HUD_Dial_Unit_MPH.png", names[needleIndex + 1]);
        Assert.Equal((float)-.3, commands[needleIndex].ParameterX);
        for (var index = needleIndex + 2; index < needleIndex + 8; index += 2)
        {
            Assert.False(Definition(commands[index]).AlphaMask);
            Assert.True(Definition(commands[index + 1]).AlphaMask);
            Assert.Equal(commands[index].OriginX, commands[index + 1].OriginX);
            Assert.Equal(commands[index].TintA, commands[index + 1].TintA);
            Assert.Equal(85 / 255f, commands[index + 1].TintR);
        }
        Assert.Equal("HUD_EV_RGN.png", names[needleIndex + 8]);
        Assert.Equal("HUD_EV_PWR.png", names[^1]);
        Assert.DoesNotContain(commands, command => command.Shader == DirectCompositionShader.Needle);
    }

    [Theory]
    [InlineData(SpeedUnit.MilesPerHour, "HUD_EV_Dial_Speed240.png")]
    [InlineData(SpeedUnit.KilometersPerHour, "HUD_EV_Dial_Speed400.png")]
    public void UnitsUseOriginalDialLabelsWithoutChangingNativeAngle(SpeedUnit unit, string lastNumber)
    {
        var commands = ElectricHudScene.Build(Sample(Frame() with { Unit = unit }), false, default);
        Assert.Contains(commands, command => Name(command) == lastNumber);
        var needle = Assert.Single(commands, command => command.Shader == DirectCompositionShader.ElectricNeedle);
        var expected = AnalogHudScene.Quad(0, ElectricHudLayout.Authored.Needle, 214.75,
            ElectricHudLayout.Authored.NeedlePivot, shader: DirectCompositionShader.ElectricNeedle);
        Assert.Equal(expected.OriginX, needle.OriginX);
        Assert.Equal(expected.AxisXY, needle.AxisXY);
    }

    [Fact]
    public void MissingNativePowerTripletHidesEntireBarAndCombustionHasNoElectricScene()
    {
        var sample = Sample(Frame()) with { PowerBar = default };
        var commands = ElectricHudScene.Build(sample, false, default);
        Assert.DoesNotContain(commands, command => command.TextureId == ElectricHudAssets.WhiteTextureId);
        Assert.DoesNotContain(commands, command => Name(command) is "HUD_EV_RGN.png" or "HUD_EV_PWR.png");
        Assert.Empty(ElectricHudScene.Build(sample with { Frame = sample.Frame with { IsElectric = false } }, false, default));
    }

    [Fact]
    public void TextureIdsAreSeparateAndEveryOriginalAssetExists()
    {
        var definitions = ElectricHudAssets.Definitions;
        Assert.Equal(definitions.Count, definitions.Select(asset => asset.Id).Distinct().Count());
        var root = RepositoryRoot();
        foreach (var asset in definitions)
        {
            Assert.InRange(asset.Id, 10_001u, 19_999u);
            Assert.Equal(asset.Id, ElectricHudAssets.Id(asset.Family, asset.FileName, asset.Tint, asset.AlphaMask));
            Assert.True(File.Exists(Path.Combine(root, "src", "Wisp.App", "Assets", "Native", asset.Family.ToString(), asset.FileName)), asset.FileName);
        }
    }

    internal static void AssertOnCurrentDispatcher()
    {
        var control = new NativeElectricAnalogSpeedometer();
        BindingOperations.ClearBinding(control, NativeElectricAnalogSpeedometer.FrameProperty);
        foreach (var unit in new[] { SpeedUnit.MilesPerHour, SpeedUnit.KilometersPerHour })
        {
            control.Frame = Frame() with { Unit = unit, NativeNeedleAngleDegrees = 214.75, NativeNeedleBlurAmount = -.3 };
            control.Measure(new Size(345, 345));
            control.Arrange(new Rect(0, 0, 345, 345));
            control.UpdateLayout();
            var layout = ElectricHudLayout.Capture(control);
            var angle = ((RotateTransform)control.FindName("NeedleRotation")).Angle;
            var sample = Sample(control.Frame) with { Angle = angle };
            var commands = ElectricHudScene.Build(sample, false, default, layout);
            foreach (var (element, file) in new[]
            {
                ("DialImage", "SpeedDial.png"), ("GearGaugeImage", "GearGauge2.png"),
                ("GearImage", "HUD_EV_Gear_2.png"), ("GearArcImage", "HUD_EV_Gear_Arc.png"),
                ("PreviousGearImage", "HUD_EV_Gear_Small_1.png"), ("NextGearImage", "HUD_EV_Gear_Small_3.png"),
                ("RegenLabelImage", "HUD_EV_RGN.png"), ("PowerLabelImage", "HUD_EV_PWR.png")
            }) AssertQuad(control, (FrameworkElement)control.FindName(element), Assert.Single(commands, command => Name(command) == file));
            AssertQuad(control, (FrameworkElement)control.FindName("NeedleMaterial"),
                Assert.Single(commands, command => command.Shader == DirectCompositionShader.ElectricNeedle));
            AssertQuad(control, (FrameworkElement)control.FindName("UnitImage"),
                Assert.Single(commands, command => Name(command).Contains("Unit", StringComparison.Ordinal)));
            var digits = commands.Where(command => Name(command).StartsWith("HUD_EV_Speed", StringComparison.Ordinal)).ToArray();
            foreach (var (element, index) in new[] { ("HundredsImage", 0), ("TensImage", 1), ("OnesImage", 2) })
                AssertQuad(control, (FrameworkElement)control.FindName(element), digits[index]);
            var assistNames = new[] { ("AbsImage", "_ABS_"), ("TcrImage", "_TCR_"), ("LcImage", "_LC_"), ("StmImage", "_STM_") };
            foreach (var (element, token) in assistNames)
                AssertQuad(control, (FrameworkElement)control.FindName(element),
                    Assert.Single(commands, command => Name(command).Contains(token, StringComparison.Ordinal)));
            var bar = (FrameworkElement)((StackPanel)control.FindName("PowerBarPanel")).Children[1];
            var track = commands.Where(command => command.TextureId == ElectricHudAssets.WhiteTextureId).ToArray();
            Assert.Equal(4, track.Length);
            AssertQuad(control, (FrameworkElement)control.FindName("RegenIndicator"), track[1]);
            AssertQuad(control, (FrameworkElement)control.FindName("PowerIndicator"), track[3]);
            Assert.Equal(bar.RenderSize.Width, track[0].AxisXX + track[2].AxisXX, 4);
        }
        var textures = ElectricHudAssets.LoadOnUiThread();
        Assert.Same(textures, ElectricHudAssets.LoadOnUiThread());
        Assert.Equal(ElectricHudAssets.Definitions.Count + 1, textures.Count);
        Assert.All(textures, texture => Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length));
    }

    private static void AssertQuad(Visual control, FrameworkElement element, DirectCompositionDrawCommand command)
    {
        var transform = element.TransformToAncestor(control);
        var origin = transform.Transform(new Point());
        var right = transform.Transform(new Point(element.RenderSize.Width, 0));
        var bottom = transform.Transform(new Point(0, element.RenderSize.Height));
        Assert.InRange(Math.Abs(origin.X - command.OriginX), 0, .0001);
        Assert.InRange(Math.Abs(origin.Y - command.OriginY), 0, .0001);
        Assert.InRange(Math.Abs(right.X - origin.X - command.AxisXX), 0, .0001);
        Assert.InRange(Math.Abs(right.Y - origin.Y - command.AxisXY), 0, .0001);
        Assert.InRange(Math.Abs(bottom.X - origin.X - command.AxisYX), 0, .0001);
        Assert.InRange(Math.Abs(bottom.Y - origin.Y - command.AxisYY), 0, .0001);
    }

    private static ElectricHudSample Sample(NativeGaugeFrame frame) => new(frame, 214.75, -.3, true, true, 0,
        NativeElectricSpeedDisplaySelector.Resolve(frame, 0),
        new NativeElectricPowerGaugeModel().Update(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio),
        null, 0, 40, 1, true, 1, 0);
    private static NativeGaugeFrame Frame() => new(true, 137, 0, 0, TransmissionGear.Second, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Unavailable(), NativeAssistSnapshot.Unavailable() with
        {
            Available = true,
            IsABSAvailable = true,
            IsABSOn = true,
            IsTCRAvailable = true,
            IsTCROn = false,
            IsLCAvailable = true,
            IsLCOn = true,
            IsSTMAvailable = true,
            IsSTMOn = false,
            ABSAngle = -34,
            TCRAngle = 23,
            LCAngle = -12,
            STMAngle = 48,
            HeadlightStateAvailable = true,
            AreHeadlightsOn = true
        }, IsElectric: true, CarOrdinal: 1, NativeRegenFillAmount: .1, NativePowerFillAmount: .6,
        NativeRegenPowerRatio: .3, ElectricGearState: new(true, 2, 3, 1, 2, false));
    private static AnalogHudAsset Definition(DirectCompositionDrawCommand command) =>
        ElectricHudAssets.Definitions.Single(asset => asset.Id == command.TextureId);
    private static string Name(DirectCompositionDrawCommand command) => command.TextureId == 0
        ? command.Shader.ToString() : command.TextureId == ElectricHudAssets.WhiteTextureId ? "White" : Definition(command).FileName;
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Wisp.sln.");
    }
}
