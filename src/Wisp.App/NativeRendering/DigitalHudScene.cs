using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class DigitalHudScene
{
    private static readonly string[] DigitFiles = Enumerable.Range(0, 10).Select(digit => $"HUD_Dial_Speed_Digital_{digit}.png").ToArray();

    internal static DirectCompositionDrawCommand[] Build(DigitalHudSample sample,
        bool tractionActive, AnalogHudColor color, DigitalHudLayout? layout = null)
    {
        if (!sample.Available) return [];
        var frame = sample.Frame;
        layout ??= DigitalHudLayout.Authored(frame);
        var commands = new List<DirectCompositionDrawCommand>(24);
        if (frame.IsElectric)
        {
            var gauge = NativeElectricGearModel.GaugeAsset(frame.ElectricGearState, digital: true);
            if (gauge is not null) commands.Add(Image(NativeAssetFamily.Electric, gauge, layout.GearGauge));
            var next = NativeElectricGearModel.AdjacentToken(frame.ElectricGearState, next: true);
            if (next is not null) commands.Add(Image(NativeAssetFamily.Electric, $"HUD_EV_Gear_{next}.png", layout.NextGear, .4));
        }
        var assists = frame.NativeAssists;
        var gear = frame.IsElectric
            ? NativeElectricGearModel.CurrentToken(frame.ElectricGearState, NativeGaugeMode.Digital, frame.Gear)
            : NativeGaugeGeometry.GearToken(frame.Gear, frame.GearDisplayMode);
        if (gear is not null)
        {
            commands.Add(Image(NativeAssetFamily.Digital, NativeGearAssetSelector.FileName(NativeGaugeMode.Digital, gear,
                !frame.IsElectric && !frame.ShiftCue.ReplacesRedlineCue(frame) &&
                NativeGaugeGeometry.IsShiftLightActive(frame.EngineRpm, frame.ExactRedline), assists), layout.Gear));
            if (frame.ShiftCue.CanRender(frame))
                commands.Add(layout.Gear.Command(ShiftCueArtwork.DigitalTextureId,
                    color: ShiftCueArtwork.Tint(frame.ShiftCue.ColorArgb)));
        }
        var digits = sample.SpeedDisplay;
        var opacity = frame.SpeedAvailable ? 1d : .30;
        AddDigit(commands, digits.Hundreds, layout.Hundreds, opacity * (digits.SpeedLessHundred ? .30 : .80), tractionActive, color);
        AddDigit(commands, digits.Tens, layout.Tens, opacity * (digits.SpeedLessTen ? .30 : .80), tractionActive, color);
        AddDigit(commands, digits.Ones, layout.Ones, opacity * (digits.SpeedLessOrEqualOne ? .30 : .80), tractionActive, color);
        commands.Add(Image(NativeAssetFamily.Digital, frame.Unit == SpeedUnit.MilesPerHour
            ? "HUD_Dial_Unit_Digital_MPH.png" : "HUD_Dial_Unit_Digital_KPH.png", layout.Unit, .30));
        if (assists.Available)
        {
            AddAssist(commands, "STM", assists.IsSTMAvailable, assists.IsSTMOn, assists, layout.Stm);
            AddAssist(commands, "ABS", assists.IsABSAvailable, assists.IsABSOn, assists, layout.Abs);
            AddAssist(commands, "LC", assists.IsLCAvailable, assists.IsLCOn, assists, layout.Lc);
            AddAssist(commands, "TCR", assists.IsTCRAvailable, assists.IsTCROn, assists, layout.Tcr);
        }
        if (!frame.IsElectric)
        {
            var parameters = NativeDigitalGaugeVisual.GaugeParametersFor(frame with { EngineRpm = sample.AppliedRpm });
            var gauge = layout.Gauge.Command(0, shader: DirectCompositionShader.DigitalGauge);
            gauge.ParameterX = (float)parameters.X;
            gauge.ParameterY = (float)parameters.Y;
            commands.Add(gauge);
        }
        else if (sample.PowerBar.Available)
        {
            commands.Add(Image(NativeAssetFamily.Electric, "HUD_EV_RGN.png", layout.RegenLabel, .67));
            AddPowerBar(commands, layout, sample.PowerBar,
                NativeElectricGearModel.IsMultiGear(frame.ElectricGearState) ? 234 : 215);
            commands.Add(Image(NativeAssetFamily.Electric, "HUD_EV_PWR.png", layout.PowerLabel, .67));
        }
        return commands.ToArray();
    }

    private static DirectCompositionDrawCommand Image(NativeAssetFamily family, string file, DigitalHudQuad quad, double opacity = 1) =>
        quad.Command(DigitalHudAssets.Id(family, file), opacity);

    private static void AddDigit(List<DirectCompositionDrawCommand> commands, int digit, DigitalHudQuad quad,
        double opacity, bool traction, AnalogHudColor color)
    {
        var file = DigitFiles[Math.Clamp(digit, 0, 9)];
        commands.Add(Image(NativeAssetFamily.Digital, file, quad, opacity));
        if (traction) commands.Add(quad.Command(DigitalHudAssets.Id(NativeAssetFamily.Digital, file, alphaMask: true), opacity, color));
    }

    private static void AddAssist(List<DirectCompositionDrawCommand> commands, string name, bool available,
        bool active, NativeAssistSnapshot state, DigitalHudQuad quad)
    {
        if (available) commands.Add(Image(NativeAssetFamily.Digital,
            NativeAssistAssetSelector.FileName(NativeGaugeMode.Digital, name, active, state), quad, active ? 1 : 77d / 255d));
    }

    private static void AddPowerBar(List<DirectCompositionDrawCommand> commands, DigitalHudLayout layout, NativeElectricPowerGaugeDisplay display, double authoredWidth)
    {
        var bar = layout.PowerBar;
        var width = bar.XAxis.X;
        if (width <= 0) return;
        // WPF assigns indicator widths before rounding the enclosing grid.
        // Derive those widths from the authored 215/234-DIP bar, then clip to its arranged columns.
        var rawRegenWidth = authoredWidth * display.RegenRatio;
        var rawPowerWidth = authoredWidth - rawRegenWidth;
        var authoredBoundary = layout.RoundX(rawRegenWidth);
        // Grid redistributes column rounding, then rounds each child during arrangement.
        // Capture both track quads so the native bar matches those final visible bounds.
        var regenTrack = layout.RegenTrack ?? bar with { XAxis = new(authoredBoundary, 0) };
        var powerTrack = layout.PowerTrack ?? bar with
        {
            Origin = new(bar.Origin.X + authoredBoundary, bar.Origin.Y),
            XAxis = new(width - authoredBoundary, 0)
        };
        var regenWidth = regenTrack.XAxis.X;
        var powerWidth = powerTrack.XAxis.X;
        var regenFill = Math.Min(regenWidth, layout.RoundX(rawRegenWidth * display.RegenFill));
        var powerFill = Math.Min(powerWidth, layout.RoundX(rawPowerWidth * display.PowerFill));
        Add(regenTrack, 0, regenWidth, new(255, 255, 255, 77));
        Add(regenTrack, regenWidth - regenFill, regenFill, new(255, 255, 255));
        Add(powerTrack, 0, powerWidth, new(255, 255, 255, 77));
        Add(powerTrack, 0, powerFill, new(66, 155, 165));
        void Add(DigitalHudQuad track, double left, double length, AnalogHudColor tint)
        {
            if (length <= 0 || track.XAxis.X <= 0) return;
            var quad = track with
            {
                Origin = new(track.Origin.X + left, track.Origin.Y),
                XAxis = new(length, 0)
            };
            commands.Add(quad.Command(DigitalHudAssets.WhiteTextureId, color: tint));
        }
    }
}
