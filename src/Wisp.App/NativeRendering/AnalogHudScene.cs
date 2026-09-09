using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class AnalogHudScene
{
    internal const double Width = 293;
    internal const double Height = 293.5;

    // All inputs are detached value snapshots. No WPF object, asset decode,
    // process read, or playback clock is accessed by the render worker here.
    internal static DirectCompositionDrawCommand[] Build(
        NativeGaugeFrame frame,
        double angle,
        double blur,
        bool needleVisible,
        bool tractionActive,
        AnalogHudColor color,
        AnalogHudLayout? layout = null,
        double? numberRpm = null)
    {
        if (frame.IsElectric)
            return [];
        layout ??= AnalogHudLayout.Authored;
        var commands = new List<DirectCompositionDrawCommand>(48);
        if (NativeGaugeGeometry.HasExactTachometerState(frame.ExactRedline, frame.TachometerMaximumRpm))
        {
            var material = Quad(0, layout.Material, shader: DirectCompositionShader.Dial);
            material.ParameterX = (float)NativeGaugeGeometry.RedlineStartNormalized(frame.ExactRedline, frame.TachometerMaximumRpm);
            material.ParameterY = (float)(1000 / NativeGaugeGeometry.ScaleMaximumRpm(frame.TachometerMaximumRpm));
            commands.Add(material);
            AddNumbers(commands, numberRpm is double sampledRpm ? frame with { EngineRpm = sampledRpm } : frame);
        }

        var assists = frame.NativeAssists;
        if (assists.Available)
        {
            AddAssist(commands, "ABS", assists.IsABSAvailable, assists.IsABSOn, assists.ABSAngle, assists, layout.Abs);
            AddAssist(commands, "TCR", assists.IsTCRAvailable, assists.IsTCROn, assists.TCRAngle, assists, layout.Tcr);
            AddAssist(commands, "LC", assists.IsLCAvailable, assists.IsLCOn, assists.LCAngle, assists, layout.Lc);
            AddAssist(commands, "STM", assists.IsSTMAvailable, assists.IsSTMOn, assists.STMAngle, assists, layout.Stm);
        }

        var gear = NativeGaugeGeometry.GearToken(frame.Gear, frame.GearDisplayMode);
        if (gear is not null)
        {
            var drive = gear == "Drive";
            var mode = drive ? NativeGaugeMode.Digital : NativeGaugeMode.Analogue;
            var file = NativeGearAssetSelector.FileName(mode, gear,
                NativeGaugeGeometry.IsShiftLightActive(frame.EngineRpm, frame.ExactRedline), assists);
            commands.Add(Quad(AnalogHudAssets.Id(drive ? NativeAssetFamily.Digital : NativeAssetFamily.Analogue, file), layout.Gear(drive)));
        }

        if (needleVisible && double.IsFinite(angle) && double.IsFinite(blur))
        {
            var needle = Quad(0, layout.Needle, angle, layout.NeedlePivot, shader: DirectCompositionShader.Needle);
            // Sampled native blur is already the game's shader parameter.
            needle.ParameterX = (float)blur;
            commands.Add(needle);
        }

        var mph = frame.Unit == SpeedUnit.MilesPerHour;
        commands.Add(Quad(AnalogHudAssets.Id(NativeAssetFamily.Analogue, mph ? "HUD_Dial_Unit_MPH.png" : "HUD_Dial_Unit_KPH.png"),
            layout.Unit(mph), opacity: 0.5));
        var digits = NativeGaugeGeometry.SpeedDigits(frame.Speed);
        var availableOpacity = frame.SpeedAvailable ? 1d : 0.2;
        AddDigit(commands, digits.Hundreds, layout.Hundreds, availableOpacity * (frame.Speed < 100 ? 0.16 : 1), tractionActive, color);
        AddDigit(commands, digits.Tens, layout.Tens, availableOpacity * (frame.Speed < 10 ? 0.16 : 1), tractionActive, color);
        AddDigit(commands, digits.Ones, layout.Ones, availableOpacity * (frame.Speed <= 1 ? 0.16 : 1), tractionActive, color);
        return commands.ToArray();
    }

    private static void AddNumbers(List<DirectCompositionDrawCommand> commands, NativeGaugeFrame frame)
    {
        var maximum = NativeGaugeGeometry.ScaleMaximumThousands(frame.TachometerMaximumRpm);
        for (var value = 0; value <= maximum; value++)
        {
            var radians = (120 + value * (240d / maximum)) * Math.PI / 180;
            var bounds = new AnalogHudRect(135 + Math.Cos(radians) * 126, 133.5 + Math.Sin(radians) * 126, 18, 18);
            var tint = !NativeGaugeGeometry.IsRedlineValue(value, frame.ExactRedline)
                ? AnalogHudAssets.NumberTint
                : NativeGaugeGeometry.IsAnalogRpmNumberLit(value, frame.EngineRpm)
                    ? AnalogHudAssets.LitRedlineNumberTint : AnalogHudAssets.RedlineNumberTint;
            commands.Add(Quad(AnalogHudAssets.Id(NativeAssetFamily.Analogue, $"HUD_Dial_RevNumbers_{value}.png", tint), bounds));
        }
    }

    private static void AddAssist(List<DirectCompositionDrawCommand> commands, string name, bool available,
        bool active, double angle, NativeAssistSnapshot snapshot, AnalogHudAssistLayout layout)
    {
        if (!available)
            return;
        var file = NativeAssistAssetSelector.FileName(NativeGaugeMode.Analogue, name, active, snapshot);
        commands.Add(Quad(AnalogHudAssets.Id(NativeAssetFamily.Analogue, file), layout.Bounds, angle, layout.Pivot));
    }

    private static void AddDigit(List<DirectCompositionDrawCommand> commands, int digit, AnalogHudRect bounds,
        double opacity, bool tractionActive, AnalogHudColor color)
    {
        var file = $"HUD_Dial_Speed_Analogue_{digit}.png";
        commands.Add(Quad(AnalogHudAssets.Id(NativeAssetFamily.Analogue, file), bounds, opacity: opacity));
        if (tractionActive)
            commands.Add(Quad(AnalogHudAssets.Id(NativeAssetFamily.Analogue, file, alphaMask: true), bounds, opacity: opacity, color: color));
    }

    internal static DirectCompositionDrawCommand Quad(uint textureId, AnalogHudRect bounds,
        double angle = 0, AnalogHudPoint pivot = default, double opacity = 1,
        AnalogHudColor? color = null, DirectCompositionShader shader = DirectCompositionShader.Image)
    {
        var radians = angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var x = bounds.X - pivot.X;
        var y = bounds.Y - pivot.Y;
        var tint = color ?? new AnalogHudColor(255, 255, 255);
        return new DirectCompositionDrawCommand
        {
            TextureId = textureId,
            Shader = shader,
            OriginX = (float)(pivot.X + cosine * x - sine * y),
            OriginY = (float)(pivot.Y + sine * x + cosine * y),
            AxisXX = (float)(cosine * bounds.Width),
            AxisXY = (float)(sine * bounds.Width),
            AxisYX = (float)(-sine * bounds.Height),
            AxisYY = (float)(cosine * bounds.Height),
            UvRight = 1,
            UvBottom = 1,
            TintR = tint.R / 255f,
            TintG = tint.G / 255f,
            TintB = tint.B / 255f,
            TintA = (float)(tint.A / 255d * opacity)
        };
    }
}
