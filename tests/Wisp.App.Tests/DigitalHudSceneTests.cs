using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DigitalHudSceneTests
{
    [Theory]
    [InlineData(false, "HUD_Dial_Digital_Gear_Redline_glow_3.png")]
    [InlineData(true, "HUD_Dial_Digital_Gear_Redline_glow_Drive.png")]
    public void TachMaterialUsesSampledRpmAndGearUsesCurrentShiftState(bool automatic, string gearFile)
    {
        var frame = Frame() with { EngineRpm = 8300, GearDisplayMode = automatic ? GearDisplayMode.Automatic : GearDisplayMode.Manual };
        var sample = Sample(frame) with { AppliedRpm = 4000 };
        var commands = DigitalHudScene.Build(sample, false, default);
        Assert.Contains(commands, command => Name(command) == gearFile);
        var material = Assert.Single(commands, command => command.Shader == DirectCompositionShader.DigitalGauge);
        Assert.Equal((float)NativeGaugeGeometry.NormalizedRpm(4000, 9000), material.ParameterX);
        Assert.Equal((float)NativeGaugeGeometry.RedlineStartNormalized(frame.ExactRedline, 9000), material.ParameterY);
        Assert.Equal(DirectCompositionShader.DigitalGauge, commands[^1].Shader);
    }

    [Fact]
    public void DigitsKeepDigitalOpacityAndPerDigitTractionMaskOrder()
    {
        var commands = DigitalHudScene.Build(Sample(Frame() with { Speed = 7, SpeedAvailable = false }), true, new(85, 230, 193));
        var digits = commands.Where(command => Name(command).Contains("Speed_Digital", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, digits.Length);
        for (var index = 0; index < digits.Length; index += 2)
        {
            Assert.False(Definition(digits[index]).AlphaMask);
            Assert.True(Definition(digits[index + 1]).AlphaMask);
            Assert.Equal(digits[index].OriginX, digits[index + 1].OriginX);
            Assert.Equal(digits[index].TintA, digits[index + 1].TintA);
            Assert.Equal(85 / 255f, digits[index + 1].TintR);
        }
        Assert.Equal(.09f, digits[0].TintA);
        Assert.Equal(.09f, digits[2].TintA);
        Assert.Equal(.24f, digits[4].TintA);
    }

    [Fact]
    public void ElectricKeepsGearNeighborsNativeBarAndNoTachOrShiftLight()
    {
        var frame = Frame() with { IsElectric = true, EngineRpm = 8300, ElectricGearState = new(true, 2, 3, 1, 2, false) };
        var commands = DigitalHudScene.Build(Sample(frame), false, default);
        Assert.Equal("HUD_EV_Digital_Bar_2bar.png", Name(commands[0]));
        Assert.Equal("HUD_EV_Gear_3.png", Name(commands[1]));
        Assert.Equal("HUD_Dial_Digital_Gear_2.png", Name(commands[2]));
        Assert.Equal("HUD_EV_PWR.png", Name(commands[^1]));
        Assert.DoesNotContain(commands, command => command.Shader == DirectCompositionShader.DigitalGauge);
        var noPower = DigitalHudScene.Build(Sample(frame) with { PowerBar = default }, false, default);
        Assert.DoesNotContain(noPower, command => command.TextureId == DigitalHudAssets.WhiteTextureId);
        Assert.DoesNotContain(noPower, command => Name(command) is "HUD_EV_RGN.png" or "HUD_EV_PWR.png");
        Assert.Empty(DigitalHudScene.Build(default, false, default));
    }

    [Fact]
    public void PowerFillRoundsItsAuthoredWidthBeforeTheArrangedTrackClipsIt()
    {
        var frame = Frame() with { IsElectric = true, NativePowerFillAmount = .6007 };
        var layout = DigitalHudLayout.Authored(frame);
        layout = layout with
        {
            PowerBar = layout.PowerBar with { XAxis = new(322d / 1.5, 0) },
            DpiScaleX = 1.5,
            LayoutRounding = true
        };
        var commands = DigitalHudScene.Build(Sample(frame), false, default, layout);
        var bar = commands.Where(command => command.TextureId == DigitalHudAssets.WhiteTextureId).ToArray();
        Assert.Equal(4, bar.Length);
        // At 150%, the original 150.5-DIP power column times .6007 rounds to 136 pixels.
        // Reconstructing it from the rounded 214.6667-DIP track incorrectly yields 135 pixels.
        Assert.Equal((float)(136d / 1.5), bar[3].AxisXX);
        Assert.Equal(layout.PowerBar.XAxis.X, bar[0].AxisXX + bar[2].AxisXX, 4);
    }

    [Fact]
    public void EveryCatalogAssetExistsAndUsesAnIsolatedId()
    {
        var definitions = DigitalHudAssets.Definitions;
        Assert.Equal(definitions.Count, definitions.Select(asset => asset.Id).Distinct().Count());
        var root = RepositoryRoot();
        foreach (var asset in definitions)
        {
            Assert.InRange(asset.Id, 50_001u, 59_999u);
            Assert.Equal(asset.Id, DigitalHudAssets.Id(asset.Family, asset.FileName, asset.Tint, asset.AlphaMask));
            Assert.True(File.Exists(Path.Combine(root, "src", "Wisp.App", "Assets", "Native", asset.Family.ToString(), asset.FileName)), asset.FileName);
        }
    }

    internal static void AssertOnCurrentDispatcher()
    {
        foreach (var dpi in new[] { 96, 120, 144, 192 }) AssertAtDpi(dpi);
    }

    private static void AssertAtDpi(int dpi)
    {
        var combustion = new NativeDigitalSpeedometer();
        var electric = new NativeElectricDigitalSpeedometer();
        BindingOperations.ClearBinding(combustion, NativeDigitalSpeedometer.FrameProperty);
        BindingOperations.ClearBinding(electric, NativeElectricDigitalSpeedometer.FrameProperty);
        foreach (var isElectric in new[] { false, true })
            foreach (var multiGear in new[] { false, true })
                foreach (var mask in new[] { 0, 1, 5, 10, 15 })
                    foreach (var unit in new[] { SpeedUnit.MilesPerHour, SpeedUnit.KilometersPerHour })
                    {
                        var frame = Frame() with
                        {
                            IsElectric = isElectric,
                            Unit = unit,
                            Assists = Frame().NativeAssists with
                            {
                                IsSTMAvailable = (mask & 1) != 0,
                                IsABSAvailable = (mask & 2) != 0,
                                IsLCAvailable = (mask & 4) != 0,
                                IsTCRAvailable = (mask & 8) != 0
                            },
                            ElectricGearState = multiGear ? new(true, 2, 3, 1, 2, false) : default
                        };
                        UserControl control;
                        if (isElectric) { electric.Frame = frame; control = electric; }
                        else { combustion.Frame = frame; control = combustion; }
                        ArrangeAtDpi(control, dpi);
                        var layout = isElectric ? DigitalHudLayout.Capture(electric) : DigitalHudLayout.Capture(combustion);
                        var commands = DigitalHudScene.Build(Sample(frame), false, default, layout);
                        AssertQuad(control, "GearImage", Assert.Single(commands, command => Name(command).StartsWith("HUD_Dial_Digital_Gear_", StringComparison.Ordinal)));
                        AssertQuad(control, "UnitImage", Assert.Single(commands, command => Name(command).Contains("Unit_Digital", StringComparison.Ordinal)));
                        var digits = commands.Where(command => Name(command).Contains("Speed_Digital", StringComparison.Ordinal)).ToArray();
                        AssertQuad(control, "HundredsImage", digits[0]);
                        AssertQuad(control, "TensImage", digits[1]);
                        AssertQuad(control, "OnesImage", digits[2]);
                        foreach (var (element, token, bit) in new[] { ("StmImage", "STM", 1), ("AbsImage", "ABS", 2), ("LcImage", "LC", 4), ("TcrImage", "TCR", 8) })
                        {
                            var matching = commands.Where(command => Name(command).Contains($"_{token}_", StringComparison.Ordinal)).ToArray();
                            if ((mask & bit) != 0) AssertQuad(control, element, Assert.Single(matching));
                            else Assert.Empty(matching);
                        }
                        if (!isElectric) AssertQuad(control, "GaugeVisual", Assert.Single(commands, command => command.Shader == DirectCompositionShader.DigitalGauge));
                        else
                        {
                            if (multiGear)
                            {
                                AssertQuad(control, "GearGaugeImage", commands[0]);
                                AssertQuad(control, "NextGearImage", commands[1]);
                            }
                            AssertQuad(control, "RegenLabelImage", Assert.Single(commands, command => Name(command) == "HUD_EV_RGN.png"));
                            AssertQuad(control, "PowerLabelImage", Assert.Single(commands, command => Name(command) == "HUD_EV_PWR.png"));
                            var bar = commands.Where(command => command.TextureId == DigitalHudAssets.WhiteTextureId).ToArray();
                            Assert.Equal(4, bar.Length);
                            AssertQuad(control, "RegenIndicator", bar[1]);
                            AssertQuad(control, "PowerIndicator", bar[3]);
                            var powerBar = (FrameworkElement)control.FindName("PowerBarGrid");
                            Assert.Equal(multiGear ? 234 : 215, powerBar.Width);
                            Assert.Equal(powerBar.RenderSize.Width, bar[0].AxisXX + bar[2].AxisXX, 4);
                        }
                    }
        electric.Frame = Frame() with { IsElectric = true, NativePowerFillAmount = .6007 };
        ArrangeAtDpi(electric, dpi);
        var boundaryCommands = DigitalHudScene.Build(Sample(electric.Frame), false, default, DigitalHudLayout.Capture(electric));
        var boundaryBar = boundaryCommands.Where(command => command.TextureId == DigitalHudAssets.WhiteTextureId).ToArray();
        AssertQuad(electric, "PowerIndicator", boundaryBar[3]);
        var textures = DigitalHudAssets.LoadOnUiThread();
        Assert.Same(textures, DigitalHudAssets.LoadOnUiThread());
        Assert.Equal(DigitalHudAssets.Definitions.Count + 1, textures.Count);
        Assert.All(textures, texture => Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length));
    }

    internal static void ArrangeAtDpi(FrameworkElement control, int dpi)
    {
        var size = new Size(control.Width, control.Height);
        // Materialize the detached control's templates before propagating DPI.
        Arrange();
        var scale = new DpiScale(dpi / 96d, dpi / 96d);
        for (var pass = 0; pass < 2; pass++)
        {
            VisualTreeHelper.SetRootDpi(control, scale);
            var elements = VisualElements();
            // SetRootDpi does not invalidate cached descendant measurements.
            for (var index = elements.Count - 1; index >= 0; index--) elements[index].InvalidateMeasure();
            Arrange();
        }
        foreach (var element in VisualElements())
        {
            var actual = VisualTreeHelper.GetDpi(element);
            var name = element is FrameworkElement framework ? framework.Name : string.Empty;
            Assert.True(actual.DpiScaleX == scale.DpiScaleX && actual.DpiScaleY == scale.DpiScaleY,
                $"{control.GetType().Name}/{element.GetType().Name}.{name}: expected {dpi}x{dpi} DPI, " +
                $"actual {actual.PixelsPerInchX}x{actual.PixelsPerInchY} DPI.");
        }

        void Arrange()
        {
            control.Measure(size);
            control.Arrange(new Rect(size));
            control.UpdateLayout();
        }

        List<UIElement> VisualElements()
        {
            var result = new List<UIElement>();
            var pending = new Queue<DependencyObject>();
            pending.Enqueue(control);
            var visited = 0;
            while (pending.Count > 0)
            {
                Assert.True(++visited <= 4096, "The detached digital gauge exceeds the bounded visual-tree limit.");
                var current = pending.Dequeue();
                if (current is UIElement element) result.Add(element);
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
            return result;
        }
    }

    private static void AssertQuad(UserControl control, string name, DirectCompositionDrawCommand command)
    {
        var element = (FrameworkElement)control.FindName(name);
        var transform = element.TransformToAncestor(control);
        var origin = transform.Transform(new Point());
        var x = transform.Transform(new Point(element.RenderSize.Width, 0));
        var y = transform.Transform(new Point(0, element.RenderSize.Height));
        try
        {
            Assert.Equal(VisualTreeHelper.GetDpi(control), VisualTreeHelper.GetDpi(element));
            Assert.InRange(Math.Abs(origin.X - command.OriginX), 0, .0001);
            Assert.InRange(Math.Abs(origin.Y - command.OriginY), 0, .0001);
            Assert.InRange(Math.Abs(x.X - origin.X - command.AxisXX), 0, .0001);
            Assert.InRange(Math.Abs(x.Y - origin.Y - command.AxisXY), 0, .0001);
            Assert.InRange(Math.Abs(y.X - origin.X - command.AxisYX), 0, .0001);
            Assert.InRange(Math.Abs(y.Y - origin.Y - command.AxisYY), 0, .0001);
            Assert.Equal((float)element.Opacity, command.TintA);
        }
        catch (Exception error)
        {
            var dpi = VisualTreeHelper.GetDpi(control);
            var bar = control.FindName("PowerBarGrid") as Grid;
            throw new InvalidOperationException(
                $"{control.GetType().Name}.{name} at {dpi.PixelsPerInchX}x{dpi.PixelsPerInchY} DPI: " +
                $"WPF origin=({origin.X},{origin.Y}), x=({x.X - origin.X},{x.Y - origin.Y}), y=({y.X - origin.X},{y.Y - origin.Y}); " +
                $"native origin=({command.OriginX},{command.OriginY}), x=({command.AxisXX},{command.AxisXY}), y=({command.AxisYX},{command.AxisYY}); " +
                $"bar width={bar?.RenderSize.Width}, regen column={bar?.ColumnDefinitions[0].ActualWidth}, power column={bar?.ColumnDefinitions[1].ActualWidth}.", error);
        }
    }

    private static DigitalHudSample Sample(NativeGaugeFrame frame) => new(frame, frame.EngineRpm,
        NativeElectricSpeedDisplaySelector.Resolve(frame, 0),
        new NativeElectricPowerGaugeModel().Update(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio), true);
    private static NativeGaugeFrame Frame() => new(true, 137, 5000, 9000, TransmissionGear.Third, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(8000 * 2 * Math.PI / 60), NativeAssistSnapshot.Unavailable() with
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
            HeadlightStateAvailable = true,
            AreHeadlightsOn = true
        }, NativeRegenFillAmount: .1, NativePowerFillAmount: .6, NativeRegenPowerRatio: .3);
    private static AnalogHudAsset Definition(DirectCompositionDrawCommand command) =>
        DigitalHudAssets.Definitions.Single(asset => asset.Id == command.TextureId);
    private static string Name(DirectCompositionDrawCommand command) => command.TextureId == 0
        ? command.Shader.ToString() : command.TextureId == DigitalHudAssets.WhiteTextureId ? "White" : Definition(command).FileName;
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Wisp.sln.");
    }
}
