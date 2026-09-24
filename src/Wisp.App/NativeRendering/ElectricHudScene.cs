using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class ElectricHudScene
{
    internal const double Width = 345;
    internal const double Height = 345;
    private static readonly (double X, double Y)[] NumberOffsets =
    [
        (-108, 67), (-126, 2), (-108, -63), (-60, -110), (0, -126),
        (60, -110), (108, -63), (126, 2), (108, 67)
    ];
    private static readonly string[][] NumberFiles =
    [
        Enumerable.Range(0, 9).Select(index => $"HUD_EV_Dial_Speed{index * 30}.png").ToArray(),
        Enumerable.Range(0, 9).Select(index => $"HUD_EV_Dial_Speed{index * 50}.png").ToArray()
    ];
    private static readonly string[] DigitFiles = Enumerable.Range(0, 10).Select(digit => $"HUD_EV_Speed{digit}.png").ToArray();

    internal static DirectCompositionDrawCommand[] Build(ElectricHudSample sample,
        bool tractionActive, AnalogHudColor color, ElectricHudLayout? layout = null)
    {
        var commands = new List<DirectCompositionDrawCommand>(40);
        AppendCommands(commands, sample, tractionActive, color, layout);
        return commands.ToArray();
    }

    internal static void AppendCommands(List<DirectCompositionDrawCommand> commands, ElectricHudSample sample,
        bool tractionActive, AnalogHudColor color, ElectricHudLayout? layout = null)
    {
        var frame = sample.Frame;
        if (!frame.IsElectric) return;
        layout ??= ElectricHudLayout.Authored;
        commands.Add(Image("SpeedDial.png", layout.Dial));
        var gaugeAsset = NativeElectricGearModel.GaugeAsset(frame.ElectricGearState, digital: false);
        if (gaugeAsset is not null) commands.Add(Image(gaugeAsset, layout.Dial));
        var files = NumberFiles[frame.Unit == SpeedUnit.MilesPerHour ? 0 : 1];
        for (var index = 0; index < NumberOffsets.Length; index++)
        {
            var offset = NumberOffsets[index];
            commands.Add(AnalogHudScene.Quad(ElectricHudAssets.Id(NativeAssetFamily.Electric, files[index], ElectricHudAssets.NumberTint),
                layout.DialNumber with { X = layout.DialNumber.X + offset.X, Y = layout.DialNumber.Y + offset.Y }));
        }

        var assists = frame.NativeAssists;
        if (assists.Available)
        {
            AddAssist(commands, "ABS", assists.IsABSAvailable, assists.IsABSOn, assists.ABSAngle, assists, layout.Abs);
            AddAssist(commands, "TCR", assists.IsTCRAvailable, assists.IsTCROn, assists.TCRAngle, assists, layout.Tcr);
            AddAssist(commands, "LC", assists.IsLCAvailable, assists.IsLCOn, assists.LCAngle, assists, layout.Lc);
            AddAssist(commands, "STM", assists.IsSTMAvailable, assists.IsSTMOn, assists.STMAngle, assists, layout.Stm);
        }
        var previous = NativeElectricGearModel.AdjacentToken(frame.ElectricGearState, next: false);
        var next = NativeElectricGearModel.AdjacentToken(frame.ElectricGearState, next: true);
        if (previous is not null) commands.Add(Image($"HUD_EV_Gear_Small_{previous}.png", layout.PreviousGear, .4));
        if (next is not null) commands.Add(Image($"HUD_EV_Gear_Small_{next}.png", layout.NextGear, .4));
        commands.Add(Image("HUD_EV_Gear_Arc.png", layout.GearArc));
        var current = NativeElectricGearModel.CurrentToken(frame.ElectricGearState, NativeGaugeMode.Analogue, frame.Gear);
        if (current is not null) commands.Add(Image($"HUD_EV_Gear_{current}.png", layout.Gear));
        if (sample.NeedleVisible && double.IsFinite(sample.Angle) && double.IsFinite(sample.Blur))
        {
            var needle = AnalogHudScene.Quad(0, layout.Needle, sample.Angle, layout.NeedlePivot,
                shader: DirectCompositionShader.ElectricNeedle);
            needle.ParameterX = (float)sample.Blur;
            commands.Add(needle);
        }

        commands.Add(AnalogHudScene.Quad(ElectricHudAssets.Id(NativeAssetFamily.Analogue,
            frame.Unit == SpeedUnit.MilesPerHour ? "HUD_Dial_Unit_MPH.png" : "HUD_Dial_Unit_KPH.png"), layout.Unit, opacity: .5));
        var digits = sample.SpeedDisplay;
        var availableOpacity = frame.SpeedAvailable ? 1d : .20;
        AddDigit(commands, digits.Hundreds, layout.Hundreds, availableOpacity * (digits.SpeedLessHundred ? .16 : 1), tractionActive, color);
        AddDigit(commands, digits.Tens, layout.Tens, availableOpacity * (digits.SpeedLessTen ? .16 : 1), tractionActive, color);
        AddDigit(commands, digits.Ones, layout.Ones, availableOpacity * (digits.SpeedLessOrEqualOne ? .16 : 1), tractionActive, color);
        if (sample.PowerBar.Available)
        {
            commands.Add(Image("HUD_EV_RGN.png", layout.RegenLabel, .67));
            AddPowerBar(commands, layout, sample.PowerBar);
            commands.Add(Image("HUD_EV_PWR.png", layout.PowerLabel, .67));
        }
    }

    private static DirectCompositionDrawCommand Image(string file, AnalogHudRect bounds, double opacity = 1) =>
        AnalogHudScene.Quad(ElectricHudAssets.Id(NativeAssetFamily.Electric, file), bounds, opacity: opacity);

    private static void AddAssist(List<DirectCompositionDrawCommand> commands, string name, bool available,
        bool active, double angle, NativeAssistSnapshot snapshot, AnalogHudAssistLayout layout)
    {
        if (!available) return;
        var file = NativeAssistAssetSelector.FileName(NativeGaugeMode.Analogue, name, active, snapshot);
        commands.Add(AnalogHudScene.Quad(ElectricHudAssets.Id(NativeAssetFamily.Analogue, file), layout.Bounds, angle, layout.Pivot));
    }

    private static void AddDigit(List<DirectCompositionDrawCommand> commands, int digit, AnalogHudRect bounds,
        double opacity, bool tractionActive, AnalogHudColor color)
    {
        var file = DigitFiles[Math.Clamp(digit, 0, 9)];
        commands.Add(Image(file, bounds, opacity));
        if (tractionActive)
            commands.Add(AnalogHudScene.Quad(ElectricHudAssets.Id(NativeAssetFamily.Electric, file, alphaMask: true),
                bounds, opacity: opacity, color: color));
    }

    private static void AddPowerBar(List<DirectCompositionDrawCommand> commands, ElectricHudLayout layout, NativeElectricPowerGaugeDisplay display)
    {
        var bounds = layout.PowerBar;
        var rawRegenWidth = bounds.Width * display.RegenRatio;
        var rawPowerWidth = bounds.Width - rawRegenWidth;
        var regenWidth = layout.RoundX(rawRegenWidth);
        var powerWidth = bounds.Width - regenWidth;
        var regenFill = Math.Min(regenWidth, layout.RoundX(rawRegenWidth * display.RegenFill));
        var powerFill = Math.Min(powerWidth, layout.RoundX(rawPowerWidth * display.PowerFill));
        Add(0, regenWidth, new(255, 255, 255, 77));
        Add(regenWidth - regenFill, regenFill, new(255, 255, 255));
        Add(regenWidth, powerWidth, new(255, 255, 255, 77));
        Add(regenWidth, powerFill, new(66, 155, 165));
        void Add(double left, double width, AnalogHudColor tint)
        {
            if (width <= 0) return;
            var command = AnalogHudScene.Quad(ElectricHudAssets.WhiteTextureId,
                new(bounds.X + left, bounds.Y, width, bounds.Height), color: tint);
            command.AxisYX = (float)(Math.Tan(-8 * Math.PI / 180) * bounds.Height);
            commands.Add(command);
        }
    }
}
