using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AnalogHudSceneTests
{
    [Fact]
    public void StockNeedleStatePassesThroughWithoutRpmReconstructionOrBlurScaling()
    {
        var commands = Build(Frame() with { EngineRpm = 1000 }, 327.125, -0.4875);
        var needle = Assert.Single(commands, command => command.Shader == DirectCompositionShader.Needle);
        var expected = AnalogHudScene.Quad(0, AnalogHudLayout.Authored.Needle, 327.125,
            AnalogHudLayout.Authored.NeedlePivot, shader: DirectCompositionShader.Needle);
        Assert.Equal(expected.OriginX, needle.OriginX);
        Assert.Equal(expected.OriginY, needle.OriginY);
        Assert.Equal(expected.AxisXX, needle.AxisXX);
        Assert.Equal(expected.AxisXY, needle.AxisXY);
        Assert.Equal((float)-0.4875, needle.ParameterX);
        Assert.NotEqual(327.125, NativeGaugeGeometry.AnalogNeedleAngle(1000, 9000));
    }

    [Fact]
    public void UnavailableScaleDoesNotRemoveAnAvailableNativeNeedleAndElectricIsOutOfScope()
    {
        var frame = Frame() with { ExactRedline = ExactRedlineResult.Unavailable(), TachometerMaximumRpm = 0 };
        var commands = Build(frame);
        Assert.DoesNotContain(commands, command => command.Shader == DirectCompositionShader.Dial);
        Assert.Single(commands, command => command.Shader == DirectCompositionShader.Needle);
        Assert.DoesNotContain(commands, command => Name(command).Contains("RevNumbers", StringComparison.Ordinal));
        Assert.Empty(Build(frame with { IsElectric = true }));
        Assert.DoesNotContain(AnalogHudScene.Build(frame, double.NaN, 0, false, false, default),
            command => command.Shader == DirectCompositionShader.Needle);
    }

    [Theory]
    [InlineData(false, "HUD_Dial_Analog_Gear_Redline_glow_3.png", 100)]
    [InlineData(true, "HUD_Dial_Digital_Gear_Redline_glow_Drive.png", 68)]
    public void GearKeepsStockShiftGlowAndAutomaticAsset(bool automatic, string expectedFile, int expectedWidth)
    {
        var frame = Frame() with
        {
            EngineRpm = 8300,
            GearDisplayMode = automatic ? GearDisplayMode.Automatic : GearDisplayMode.Manual
        };
        var command = Assert.Single(Build(frame), command => Name(command) == expectedFile);
        Assert.Equal(expectedWidth, command.AxisXX);
        Assert.Equal(expectedWidth, command.AxisYY);
        Assert.Equal(144f, command.OriginX + command.AxisXX / 2);
    }

    [Fact]
    public void FallbackNumbersUseSampledRpmWhileShiftGearUsesTheLatestRpm()
    {
        var commands = AnalogHudScene.Build(Frame() with { EngineRpm = 8300 }, 330, -0.1,
            true, false, default, numberRpm: 7900);
        Assert.Single(commands, command => Name(command) == "HUD_Dial_Analog_Gear_Redline_glow_3.png");
        var number = Assert.Single(commands, command => Name(command) == "HUD_Dial_RevNumbers_8.png");
        Assert.Equal(AnalogHudAssets.RedlineNumberTint, Definition(number).Tint);
    }

    [Fact]
    public void DrawOrderPreservesOrbitAssistsGearNeedleUnitAndPerDigitTractionOverlay()
    {
        var commands = AnalogHudScene.Build(Frame() with { Speed = 7 }, 250, -0.3, true, true, new(85, 230, 193));
        var names = commands.Select(Name).ToArray();
        var abs = Array.FindIndex(names, name => name.Contains("_ABS_", StringComparison.Ordinal));
        Assert.True(abs > 1);
        Assert.Contains("_TCR_", names[abs + 1], StringComparison.Ordinal);
        Assert.Contains("_LC_", names[abs + 2], StringComparison.Ordinal);
        Assert.Contains("_STM_", names[abs + 3], StringComparison.Ordinal);
        Assert.Contains("Gear", names[abs + 4], StringComparison.Ordinal);
        Assert.Equal(DirectCompositionShader.Needle, commands[abs + 5].Shader);
        Assert.Equal("HUD_Dial_Unit_MPH.png", names[abs + 6]);
        var digits = commands.Skip(abs + 7).ToArray();
        Assert.Equal(6, digits.Length);
        for (var index = 0; index < 6; index += 2)
        {
            Assert.False(Definition(digits[index]).AlphaMask);
            Assert.True(Definition(digits[index + 1]).AlphaMask);
            Assert.Equal(digits[index].OriginX, digits[index + 1].OriginX);
            Assert.Equal(digits[index].TintA, digits[index + 1].TintA);
            Assert.Equal(85 / 255f, digits[index + 1].TintR);
        }
        Assert.Equal(0.16f, digits[0].TintA);
        Assert.Equal(0.16f, digits[2].TintA);
        Assert.Equal(1f, digits[4].TintA);
    }

    [Fact]
    public void AlphaMaskPreservesCoverageWithoutMultiplyingByDigitRgb()
    {
        byte[] pixels = [0, 0, 0, 0, 2, 50, 100, 128, 10, 20, 30, 255];
        AnalogHudAssets.MakeWhiteAlphaMask(pixels);
        Assert.Equal<byte>([0, 0, 0, 0, 128, 128, 128, 128, 255, 255, 255, 255], pixels);
    }

    [Fact]
    public void EveryPreloadedAssetExistsAndHasOneStableId()
    {
        var assets = AnalogHudAssets.Definitions;
        Assert.Equal(162, assets.Count);
        Assert.Equal(assets.Count, assets.Select(asset => asset.Id).Distinct().Count());
        var root = RepositoryRoot();
        foreach (var asset in assets)
        {
            Assert.Equal(asset.Id, AnalogHudAssets.Id(asset.Family, asset.FileName, asset.Tint, asset.AlphaMask));
            Assert.True(File.Exists(Path.Combine(root, "src", "Wisp.App", "Assets", "Native", asset.Family.ToString(), asset.FileName)), asset.FileName);
        }
    }

    // Runs on the existing resource-only dispatcher, with no live window or
    // second WPF application. Compare actual arranged/transformed UI geometry.
    internal static void AssertOnCurrentDispatcher()
    {
        var control = new NativeAnalogSpeedometer();
        BindingOperations.ClearBinding(control, NativeAnalogSpeedometer.FrameProperty);
        var frame = Frame() with { NativeNeedleAngleDegrees = 214.75, NativeNeedleBlurAmount = -0.31 };
        foreach (var automatic in new[] { false, true })
        {
            frame = frame with
            {
                GearDisplayMode = automatic ? GearDisplayMode.Automatic : GearDisplayMode.Manual,
                Unit = automatic ? SpeedUnit.KilometersPerHour : SpeedUnit.MilesPerHour,
                GameTimestampMilliseconds = automatic ? 20u : 0u
            };
            control.Frame = frame;
            control.Measure(new Size(293, 293.5));
            control.Arrange(new Rect(0, 0, 293, 293.5));
            control.UpdateLayout();
            var angle = ((RotateTransform)control.FindName("NeedleRotation")).Angle;
            var layout = AnalogHudLayout.Capture(control);
            var commands = AnalogHudScene.Build(frame, angle, -0.31, true, false, default, layout);
            AssertQuadMatches(control, "NeedleMaterial", Assert.Single(commands, command => command.Shader == DirectCompositionShader.Needle));
            AssertQuadMatches(control, "MaterialVisual", Assert.Single(commands, command => command.Shader == DirectCompositionShader.Dial));
            AssertQuadMatches(control, "GearImage", Assert.Single(commands, command => Name(command).Contains("Gear", StringComparison.Ordinal)));
            AssertQuadMatches(control, "UnitImage", Assert.Single(commands, command => Name(command).Contains("Unit", StringComparison.Ordinal)));
            foreach (var name in new[] { "ABS", "TCR", "LC", "STM" })
            {
                var elementName = name == "ABS" ? "AbsImage" : name == "TCR" ? "TcrImage" : name == "LC" ? "LcImage" : "StmImage";
                AssertQuadMatches(control, elementName, Assert.Single(commands, command => Name(command).Contains($"_{name}_", StringComparison.Ordinal)));
            }
            var digits = commands.Where(command => Name(command).Contains("Speed_Analogue", StringComparison.Ordinal)).ToArray();
            AssertQuadMatches(control, "HundredsImage", digits[0]);
            AssertQuadMatches(control, "TensImage", digits[1]);
            AssertQuadMatches(control, "OnesImage", digits[2]);
            Assert.Equal(angle, ((RotateTransform)control.FindName("NeedleRotation")).Angle);
        }

        var textures = AnalogHudAssets.LoadOnUiThread();
        Assert.Same(textures, AnalogHudAssets.LoadOnUiThread());
        Assert.Equal(AnalogHudAssets.Definitions.Count, textures.Count);
        Assert.All(textures, texture => Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length));

        var startup = new NativeAnalogSpeedometer();
        BindingOperations.ClearBinding(startup, NativeAnalogSpeedometer.FrameProperty);
        startup.Frame = Frame() with
        {
            Assists = NativeAssistSnapshot.Unavailable(),
            Gear = (TransmissionGear)99,
            ExactRedline = ExactRedlineResult.Unavailable(),
            TachometerMaximumRpm = 0
        };
        startup.Measure(new Size(293, 293.5));
        startup.Arrange(new Rect(0, 0, 293, 293.5));
        startup.UpdateLayout();
        var startupLayout = AnalogHudLayout.Capture(startup);
        startup.Frame = Frame() with { NativeNeedleAngleDegrees = 214.75, NativeNeedleBlurAmount = -0.31 };
        startup.Measure(new Size(293, 293.5));
        startup.Arrange(new Rect(0, 0, 293, 293.5));
        startup.UpdateLayout();
        var startupAngle = ((RotateTransform)startup.FindName("NeedleRotation")).Angle;
        var startupCommands = AnalogHudScene.Build(startup.Frame, startupAngle, -0.31, true, false, default, startupLayout);
        AssertQuadMatches(startup, "NeedleMaterial", Assert.Single(startupCommands, command => command.Shader == DirectCompositionShader.Needle));
        AssertQuadMatches(startup, "MaterialVisual", Assert.Single(startupCommands, command => command.Shader == DirectCompositionShader.Dial));
        AssertQuadMatches(startup, "AbsImage", Assert.Single(startupCommands, command => Name(command).Contains("_ABS_", StringComparison.Ordinal)));
        AssertQuadMatches(startup, "GearImage", Assert.Single(startupCommands, command => Name(command).Contains("Gear", StringComparison.Ordinal)));
    }

    private static void AssertQuadMatches(NativeAnalogSpeedometer control, string name, DirectCompositionDrawCommand command)
    {
        var element = (FrameworkElement)control.FindName(name);
        var transform = element.TransformToAncestor(control);
        var origin = transform.Transform(new Point(0, 0));
        var right = transform.Transform(new Point(element.RenderSize.Width, 0));
        var bottom = transform.Transform(new Point(0, element.RenderSize.Height));
        Assert.InRange(Math.Abs(origin.X - command.OriginX), 0, 0.0001);
        Assert.InRange(Math.Abs(origin.Y - command.OriginY), 0, 0.0001);
        Assert.InRange(Math.Abs(right.X - origin.X - command.AxisXX), 0, 0.0001);
        Assert.InRange(Math.Abs(right.Y - origin.Y - command.AxisXY), 0, 0.0001);
        Assert.InRange(Math.Abs(bottom.X - origin.X - command.AxisYX), 0, 0.0001);
        Assert.InRange(Math.Abs(bottom.Y - origin.Y - command.AxisYY), 0, 0.0001);
    }

    private static NativeGaugeFrame Frame() => new(true, 137, 5000, 9000, TransmissionGear.Third, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(8000 * 2 * Math.PI / 60),
        NativeAssistSnapshot.Unavailable() with
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
        });

    private static DirectCompositionDrawCommand[] Build(NativeGaugeFrame frame, double angle = 210, double blur = 0) =>
        AnalogHudScene.Build(frame, angle, blur, true, false, default);

    private static AnalogHudAsset Definition(DirectCompositionDrawCommand command) =>
        AnalogHudAssets.Definitions.Single(asset => asset.Id == command.TextureId);

    private static string Name(DirectCompositionDrawCommand command) => command.TextureId == 0 ? command.Shader.ToString() : Definition(command).FileName;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Wisp.sln.");
    }
}
