using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class TireHudLayer
{
    private static readonly ConditionalWeakTable<FrameworkElement, Cache> Caches = new();

    internal static HudLayerSnapshot Capture(FrameworkElement control, DiagnosticsViewModel? vm)
    {
        control.Dispatcher.VerifyAccess();
        if (control is not TireTemperatureVisualBase visual)
            throw new ArgumentException("A tire temperature visual is required.", nameof(control));
        var display = vm?.TireTemperatureDisplay ?? visual.Display;
        var frame = vm?.NativeGaugeFrame ?? default;
        var config = new Configuration(control is DigitalTireTemperatureGaugeView,
            control.ActualWidth, control.ActualHeight, SupplementaryHudArt.RasterScale(control),
            visual.TemperatureUnit, visual.ReactiveColors, visual.IsAttached,
            SupplementaryHudArt.Color(visual.LowBrush), SupplementaryHudArt.Color(visual.MidBrush),
            SupplementaryHudArt.Color(visual.HighBrush));
        var cache = Caches.GetValue(control, _ => new Cache());
        if (cache.Key != config || cache.Art is null)
        {
            cache.Key = config;
            cache.Art = BuildArt(config);
        }
        return new Snapshot(config, cache.Art, display, frame.CarOrdinal, frame.GameTimestampMilliseconds,
            frame.ReceivedTimestamp ?? 0);
    }

    internal sealed record Configuration(bool Digital, double Width, double Height, double RasterScale,
        TireTemperatureUnit Unit, bool ReactiveColors, bool Attached,
        AnalogHudColor Low, AnalogHudColor Middle, AnalogHudColor High)
    {
        internal bool StockPalette => Low.R == Middle.R && Low.G == Middle.G && Low.B == Middle.B &&
            Middle.R == High.R && Middle.G == High.G && Middle.B == High.B;
    }

    internal sealed record Art(IReadOnlyList<AnalogHudTexture> Textures,
        SupplementaryHudArt.GlyphSet Labels, SupplementaryHudArt.GlyphSet? Readout = null);

    internal sealed class Snapshot(Configuration config, Art art, TireTemperatureDisplay display,
        int car, uint gameTime, long received) : HudLayerSnapshot
    {
        internal Configuration Config { get; } = config;
        internal Art Artwork { get; } = art;
        internal TireTemperatureDisplay Display { get; } = display;
        internal int Car { get; } = car;
        internal uint GameTime { get; } = gameTime;
        internal long Received { get; } = received;
        internal override IReadOnlyList<AnalogHudTexture> Textures => Artwork.Textures;
        internal override object CompatibilityKey => (Config, Car, Display.IsAvailable);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }

    private sealed class Cache
    {
        internal Configuration? Key;
        internal Art? Art;
    }

    internal sealed class Playback : HudLayerPlayback
    {
        private readonly NativeTachometerInterpolator _front = new();
        private readonly NativeTachometerInterpolator _rear = new();
        private Snapshot? _snapshot;

        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            var next = (Snapshot)snapshot;
            if (_snapshot is null || !Equals(_snapshot.CompatibilityKey, next.CompatibilityKey))
            {
                _front.Reset();
                _rear.Reset();
            }
            _snapshot = next;
            if (!next.Display.IsAvailable) return;
            var received = next.Received > 0 ? next.Received : timestamp;
            _front.Observe(next.Car, next.GameTime, next.Display.FrontFraction, timestamp, received);
            _rear.Observe(next.Car, next.GameTime, next.Display.RearFraction, timestamp, received);
        }

        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is not { Display.IsAvailable: true } snapshot) return [];
            return TireHudLayer.Build(snapshot, _front.Sample(timestamp), _rear.Sample(timestamp));
        }
    }

    internal static DirectCompositionDrawCommand[] Build(Snapshot snapshot, double front, double rear)
    {
        var c = snapshot.Config;
        if (!snapshot.Display.IsAvailable || c.Width <= 0 || c.Height <= 0 ||
            !double.IsFinite(front) || !double.IsFinite(rear)) return [];
        front = Math.Clamp(front, 0, 1);
        rear = Math.Clamp(rear, 0, 1);
        var commands = new List<DirectCompositionDrawCommand>(32);
        var full = new AnalogHudRect(0, 0, c.Width, c.Height);
        commands.Add(AnalogHudScene.Quad(40000, full));
        var frontColor = NeedleColor(c, c.Low, c.Digital ? (byte)255 : (byte)242);
        var rearColor = NeedleColor(c, c.Middle, c.Digital ? (byte)255 : (byte)226);
        if (c.Digital)
        {
            commands.Add(AnalogHudScene.Quad(40003, new(9.05 + 283 * rear - 5, 55.05, 10, 14), color: rearColor));
            commands.Add(AnalogHudScene.Quad(40002, new(9.05 + 283 * front - 5, 55.05, 10, 14), color: frontColor));
            commands.Add(AnalogHudScene.Quad(40001, full));
            var frontText = $"F  {Temperature(snapshot.Display.FrontFahrenheit, c.Unit)}°";
            var rearText = $"REAR  {Temperature(snapshot.Display.RearFahrenheit, c.Unit)}°";
            SupplementaryHudArt.AddText(commands, snapshot.Artwork.Labels, frontText, 78, 34, new(244, 246, 250, 238));
            var rearWidth = SupplementaryHudArt.TextWidth(snapshot.Artwork.Labels, rearText);
            SupplementaryHudArt.AddText(commands, snapshot.Artwork.Labels, rearText, 291.05 - rearWidth, 34, new(224, 228, 236, 220));
        }
        else
        {
            var center = new AnalogHudPoint(c.Width / 2, c.Height / 2);
            var radius = Math.Min(c.Width, c.Height) * .43;
            var rearAngle = 110 + 260 * rear;
            var frontAngle = 110 + 260 * front;
            var needleBounds = NeedleBounds(c);
            // The slim tapered tire profile is deliberately distinct from the engine needle.
            commands.Add(AnalogHudScene.Quad(40002, needleBounds, rearAngle, center,
                color: rearColor with { A = (byte)Math.Round(rearColor.A * .86) }));
            commands.Add(AnalogHudScene.Quad(40002, needleBounds, frontAngle, center, color: frontColor));
            AddNeedleLabel(commands, snapshot.Artwork.Labels, "F", center, radius, frontAngle, frontColor);
            AddNeedleLabel(commands, snapshot.Artwork.Labels, "R", center, radius, rearAngle, rearColor);
            commands.Add(AnalogHudScene.Quad(40001, full));
            AddDigits(commands, Temperature(snapshot.Display.FrontFahrenheit, c.Unit), center.X + 8, center.Y - 11);
            AddDigits(commands, Temperature(snapshot.Display.RearFahrenheit, c.Unit), center.X + 8, center.Y + 12);
        }
        return commands.ToArray();
    }

    internal static AnalogHudColor NeedleColor(Configuration c, AnalogHudColor palette, byte alpha)
    {
        var color = !c.ReactiveColors || c.StockPalette
            ? c.Digital ? new AnalogHudColor(248, 250, 253) : new AnalogHudColor(244, 246, 250)
            : palette;
        return color with { A = alpha };
    }

    private static string Temperature(double fahrenheit, TireTemperatureUnit unit) =>
        Math.Round(TireTemperatureDisplay.ConvertForReadout(fahrenheit, unit), MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);

    private static void AddNeedleLabel(List<DirectCompositionDrawCommand> commands, SupplementaryHudArt.GlyphSet glyphs,
        string label, AnalogHudPoint center, double radius, double angle, AnalogHudColor color)
    {
        var point = SupplementaryHudArt.Polar(new(center.X, center.Y), radius - 19, angle);
        SupplementaryHudArt.AddText(commands, glyphs, label,
            point.X - SupplementaryHudArt.TextWidth(glyphs, label) / 2, point.Y - glyphs.Height / 2, color);
    }

    private static void AddDigits(List<DirectCompositionDrawCommand> commands, string text, double cx, double cy)
    {
        const double width = 10.3;
        const double gap = -.5;
        var left = cx - (width * text.Length + gap * (text.Length - 1)) / 2;
        var color = new AnalogHudColor(248, 250, 253, 242);
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9')
                commands.Add(AnalogHudScene.Quad((uint)(40010 + ch - '0'), new(left, cy - 8, width, 16), color: color));
            else if (ch == '-')
                commands.Add(AnalogHudScene.Quad(40022, new(left + 2, cy - .75, width - 4, 1.5), color: color));
            left += width + gap;
        }
    }

    private static Art BuildArt(Configuration c)
    {
        var textures = new List<AnalogHudTexture>();
        textures.Add(SupplementaryHudArt.Raster(40000, c.Width, c.Height, c.RasterScale, dc =>
        {
            if (c.Digital)
            {
                var housing = SupplementaryHudArt.Rail(9.05, 292.05, 58.75, 65.25, 1.3);
                dc.DrawGeometry(SupplementaryHudArt.Brush(236, 239, 244, 34),
                    new Pen(SupplementaryHudArt.Brush(238, 241, 246, 168), 1), housing);
                if (c.Attached)
                {
                    dc.DrawLine(new(SupplementaryHudArt.Brush(225, 229, 236, 36), 3.2), new(12.28, 32.5), new(7.38, 56.7));
                    dc.DrawLine(new(SupplementaryHudArt.Brush(225, 229, 236, 204), 1.1), new(12.28, 32.5), new(7.38, 56.7));
                }
                dc.DrawText(SupplementaryHudArt.Text($"TIRE TEMP  {TireTemperatureDisplay.UnitSymbol(c.Unit)}", 11.5,
                    SupplementaryHudArt.Brush(224, 228, 236, 210), FontWeights.SemiBold, FontStyles.Italic), new(13.85, 66));
            }
            else DrawAnalogBackground(dc, c);
        }));
        textures.Add(SupplementaryHudArt.Raster(40001, c.Width, c.Height, c.RasterScale, dc =>
        {
            if (c.Digital)
                dc.DrawGeometry(null, new Pen(SupplementaryHudArt.Brush(238, 241, 246, 168), 1),
                    SupplementaryHudArt.Rail(9.05, 292.05, 58.75, 65.25, 1.3));
            else DrawAnalogForeground(dc, c);
        }));
        if (c.Digital)
        {
            foreach (var (id, strength) in new[] { (40002u, 1d), (40003u, .76) })
            {
                textures.Add(SupplementaryHudArt.Raster(id, 10, 14, c.RasterScale, dc =>
                {
                    var start = new Point(5, 3.5);
                    var end = new Point(3.7, 10.4);
                    dc.DrawLine(new(SupplementaryHudArt.Brush(255, 255, 255, (byte)Math.Round(28 * strength)), 7), start, end);
                    dc.DrawLine(new(SupplementaryHudArt.Brush(255, 255, 255, (byte)Math.Round(72 * strength)), 3.5), start, end);
                    dc.DrawLine(new(SupplementaryHudArt.Brush(255, 255, 255, (byte)Math.Round(242 * strength)), 1.3), start, end);
                }));
            }
            var readout = SupplementaryHudArt.Glyphs(textures, 40100, "0123456789.-° FREAR", 14, c.RasterScale,
                FontWeights.SemiBold, FontStyles.Italic);
            return new(textures.AsReadOnly(), readout);
        }
        var needleBounds = NeedleBounds(c);
        textures.Add(SupplementaryHudArt.Raster(40002, needleBounds.Width, needleBounds.Height, Math.Max(4, c.RasterScale), dc =>
        {
            var inner = new Point(1, 2);
            var outer = new Point(needleBounds.Width - 1, 2);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(inner, true, true);
                context.LineTo(new(inner.X + 2.8, inner.Y + .95), true, false);
                context.LineTo(new(outer.X, outer.Y + .7), true, false);
                context.LineTo(new(outer.X, outer.Y - .7), true, false);
                context.LineTo(new(inner.X + 2.8, inner.Y - .95), true, false);
            }
            dc.DrawGeometry(Brushes.White, null, geometry);
        }));
        SupplementaryHudArt.AddNativeDigits(textures, 40010);
        textures.Add(SupplementaryHudArt.Pixel(40022));
        var labels = SupplementaryHudArt.Glyphs(textures, 40100, "FR", 8, c.RasterScale, FontWeights.Bold);
        return new(textures.AsReadOnly(), labels);
    }

    private static AnalogHudRect NeedleBounds(Configuration c)
    {
        var radius = Math.Min(c.Width, c.Height) * .43;
        var innerX = c.Width / 2 + radius * .57;
        var outerX = c.Width / 2 + radius - 4.5;
        return new(innerX - 1, c.Height / 2 - 2, Math.Max(1, outerX - innerX + 2), 4);
    }

    private static void DrawAnalogBackground(DrawingContext dc, Configuration c)
    {
        var center = new Point(c.Width / 2, c.Height / 2);
        var radius = Math.Min(c.Width, c.Height) * .43;
        dc.DrawGeometry(null, new Pen(SupplementaryHudArt.Brush(142, 147, 156, 145), 2.2),
            SupplementaryHudArt.Arc(center, radius, 110, 260));
        for (var i = 0; i <= 6; i++)
        {
            var fahrenheit = TireTemperatureDisplay.MinimumFahrenheit +
                (TireTemperatureDisplay.MaximumFahrenheit - TireTemperatureDisplay.MinimumFahrenheit) * i / 6;
            var label = Math.Round(TireTemperatureDisplay.Convert(fahrenheit, c.Unit), MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture);
            var angle = 110 + 260 * i / 6d;
            SupplementaryHudArt.Tick(dc, center, radius, angle, 7, 1.4, label, 6.2, 14.5);
            if (i < 6) SupplementaryHudArt.Tick(dc, center, radius, angle + 260d / 12, 3.5, .8, null, 6.2, 14.5);
        }
    }

    private static void DrawAnalogForeground(DrawingContext dc, Configuration c)
    {
        var center = new Point(c.Width / 2, c.Height / 2);
        SupplementaryHudArt.GearRing(dc, center, 92, 34, 28);
        var brush = SupplementaryHudArt.Brush(210, 215, 225, 205);
        dc.DrawText(SupplementaryHudArt.Text("FRONT", 5.8, brush, FontWeights.Bold), new(center.X - 21, center.Y - 18));
        dc.DrawText(SupplementaryHudArt.Text("REAR", 5.8, brush, FontWeights.Bold), new(center.X - 21, center.Y + 5));
        dc.DrawLine(new(SupplementaryHudArt.Brush(232, 235, 241, 92), .8), new(center.X - 27, center.Y), new(center.X + 28, center.Y));
        var unit = SupplementaryHudArt.Text(TireTemperatureDisplay.UnitSymbol(c.Unit), 8.5,
            SupplementaryHudArt.Brush(185, 193, 207, 190), FontWeights.SemiBold);
        dc.DrawText(unit, new(center.X - unit.Width / 2, center.Y + 36));
        dc.DrawText(SupplementaryHudArt.Text("TIRE TEMP", 9.5, brush, FontWeights.SemiBold, FontStyles.Italic),
            new(center.X + 23, center.Y + 43));
    }
}
