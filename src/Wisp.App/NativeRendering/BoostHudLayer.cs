using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal static class BoostHudLayer
{
    private static readonly ConditionalWeakTable<FrameworkElement, Cache> Caches = new();

    internal static HudLayerSnapshot Capture(FrameworkElement control, DiagnosticsViewModel? vm)
    {
        control.Dispatcher.VerifyAccess();
        if (control is not BoostVisualBase visual)
            throw new ArgumentException("A boost visual is required.", nameof(control));
        var display = vm?.BoostDisplay ?? visual.Display;
        var frame = vm?.NativeGaugeFrame ?? default;
        var key = new Configuration(control is DigitalBoostRailView, control.ActualWidth, control.ActualHeight,
            SupplementaryHudArt.RasterScale(control), visual.PressureUnit, display.ScaleMinimumPsi < 0,
            visual.ColorNumber, control is DigitalBoostRailView digital && digital.UseStockColors,
            control is AnalogBoostGaugeView analogue && analogue.IsElectricMaterial,
            SupplementaryHudArt.Color(visual.LowBrush), SupplementaryHudArt.Color(visual.MidBrush),
            SupplementaryHudArt.Color(visual.HighBrush));
        var cache = Caches.GetValue(control, _ => new Cache());
        if (cache.Key != key || cache.Art is null)
        {
            cache.Key = key;
            cache.Art = BuildArt(key);
        }
        return new Snapshot(key, cache.Art, display, frame.CarOrdinal, frame.GameTimestampMilliseconds,
            frame.ReceivedTimestamp ?? 0);
    }

    internal sealed record Configuration(bool Digital, double Width, double Height, double RasterScale,
        BoostPressureUnit Unit, bool Vacuum, bool ColorNumber, bool StockMaterial, bool Electric,
        AnalogHudColor Low, AnalogHudColor Middle, AnalogHudColor High)
    {
        internal bool StockPalette => Low.R == Middle.R && Low.G == Middle.G && Low.B == Middle.B &&
            Middle.R == High.R && Middle.G == High.G && Middle.B == High.B;
    }

    internal sealed class Snapshot(Configuration config, Art art, BoostDisplay display, int car,
        uint gameTime, long received) : HudLayerSnapshot
    {
        internal Configuration Config { get; } = config;
        internal Art Artwork { get; } = art;
        internal BoostDisplay Display { get; } = display;
        internal int Car { get; } = car;
        internal uint GameTime { get; } = gameTime;
        internal long Received { get; } = received;
        internal override IReadOnlyList<AnalogHudTexture> Textures => Artwork.Textures;
        internal override object CompatibilityKey => (Config, Car, Display.IsAvailable);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }

    internal sealed record Art(IReadOnlyList<AnalogHudTexture> Textures, SupplementaryHudArt.GlyphSet? Glyphs);

    private sealed class Cache
    {
        internal Configuration? Key;
        internal Art? Art;
    }

    internal sealed class Playback : HudLayerPlayback
    {
        private readonly NativeTachometerInterpolator _pressure = new(allowNegativeValues: true);
        private readonly NativeTachometerInterpolator _fraction = new();
        private Snapshot? _snapshot;

        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            var next = (Snapshot)snapshot;
            if (_snapshot is null || !Equals(_snapshot.CompatibilityKey, next.CompatibilityKey))
            {
                _pressure.Reset();
                _fraction.Reset();
            }
            _snapshot = next;
            if (!next.Display.IsAvailable)
                return;
            var received = next.Received > 0 ? next.Received : timestamp;
            _pressure.Observe(next.Car, next.GameTime, next.Display.PressurePsi, timestamp, received);
            _fraction.Observe(next.Car, next.GameTime, next.Display.Fraction, timestamp, received);
        }

        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is not { Display.IsAvailable: true } snapshot)
                return [];
            var pressure = _pressure.Sample(timestamp);
            var fraction = Math.Clamp(_fraction.Sample(timestamp), 0, 1);
            return BoostHudLayer.Build(snapshot, pressure, fraction, timestamp);
        }
    }

    internal static DirectCompositionDrawCommand[] Build(Snapshot snapshot, double pressure, double fraction, long timestamp)
    {
        var c = snapshot.Config;
        if (!snapshot.Display.IsAvailable || c.Width <= 0 || c.Height <= 0 || !double.IsFinite(pressure) || !double.IsFinite(fraction))
            return [];
        var commands = new List<DirectCompositionDrawCommand>(110);
        var full = new AnalogHudRect(0, 0, c.Width, c.Height);
        commands.Add(AnalogHudScene.Quad(30000, full));
        // Numeric readouts retain their received display value; only the
        // moving gauge geometry uses the shared interpolation timeline.
        var numberPressure = snapshot.Display.PressurePsi;
        var numberColor = NumberColor(c, numberPressure, snapshot.Display.Fraction, timestamp);
        var gaugeFraction = BoostPressureUnits.GaugeFraction(pressure, c.Unit, c.Vacuum);
        if (c.Digital)
        {
            var railFraction = c.Vacuum ? gaugeFraction : fraction;
            if (!c.StockMaterial && railFraction > 0)
            {
                // Only the outside half of the moving end's 4.2-DIP stroke
                // survives the original track clip and opaque active fill.
                var glowWidth = Math.Min(2.1 * Math.Sqrt(1 + .2 * .2), 283 * (1 - railFraction));
                if (glowWidth > 0)
                {
                    var glow = AnalogHudScene.Quad(30003, new(9.05 + 283 * railFraction, 58.75, glowWidth, 6.5), opacity: 48 / 255d);
                    glow.AxisYX = -1.3f;
                    glow.UvLeft = (float)railFraction;
                    glow.UvRight = (float)(railFraction + glowWidth / 283);
                    commands.Add(glow);
                }
                var fill = AnalogHudScene.Quad(30003, new(9.05, 58.75, 283 * railFraction, 6.5));
                fill.AxisYX = -1.3f;
                fill.UvRight = (float)railFraction;
                commands.Add(fill);
                commands.Add(AnalogHudScene.Quad(30004, new(9.05 + 283 * railFraction - 5, 54.9, 10, 14)));
            }
            commands.Add(AnalogHudScene.Quad(30001, full));
            var glyphs = snapshot.Artwork.Glyphs!;
            var text = $"{BoostPressureUnits.FormatValue(numberPressure, c.Unit)} {BoostPressureUnits.Symbol(c.Unit)}";
            SupplementaryHudArt.AddText(commands, glyphs, text, 20, 31 + (58.75 - 31 - glyphs.Height) / 2,
                c.StockMaterial ? new(245, 245, 245) : numberColor);
            if (c.StockMaterial)
            {
                // The child material follows the parent's artwork/readout, as
                // in WPF, with its authored 302 x 24 box compressed vertically.
                var rail = AnalogHudScene.Quad(0, new(0, 55.75, 302, 12), shader: DirectCompositionShader.DigitalGauge);
                rail.ParameterX = (float)railFraction;
                rail.ParameterY = 1;
                commands.Add(rail);
            }
        }
        else
        {
            var zero = BoostPressureUnits.GaugeFraction(0, c.Unit, c.Vacuum);
            var start = Math.Min(gaugeFraction, zero);
            var length = Math.Abs(gaugeFraction - zero);
            var count = Math.Max(1, (int)Math.Ceiling(260 * length / 3));
            var minimum = BoostPressureUnits.AnalogMinimum(c.Unit, c.Vacuum);
            var maximum = BoostPressureUnits.AnalogMaximum(c.Unit);
            if (length > 0)
            {
                var diameter = Math.Min(c.Width, c.Height);
                var ringBounds = new AnalogHudRect((c.Width - diameter) / 2, (c.Height - diameter) / 2, diameter, diameter);
                for (var i = 0; i < count; i++)
                {
                    var from = start + length * i / count;
                    var to = start + length * (i + 1) / count;
                    var psi = minimum + (maximum - minimum) * to;
                    if (c.Unit == BoostPressureUnit.Bar) psi *= BoostPressureUnits.PsiPerBar;
                    var tint = Palette(c, Math.Clamp(psi / Math.Max(snapshot.Display.LearnedPeakPsi, 5), 0, 1));
                    var arc = AnalogHudScene.Quad(30002, ringBounds, color: tint, shader: DirectCompositionShader.ImageSector);
                    arc.ParameterX = (float)((110 + 260 * from) * Math.PI / 180);
                    arc.ParameterY = (float)((260 * (to - from) + .35) * Math.PI / 180);
                    commands.Add(arc);
                }
            }
            commands.Add(AnalogHudScene.Quad(30001, full));
            var displayed = c.Vacuum ? numberPressure : Math.Clamp(numberPressure, 0, BoostPressureUnits.AnalogMaximumPsi(c.Unit));
            AddDigits(commands, BoostPressureUnits.FormatValue(displayed, c.Unit, padPsi: true),
                c.Width / 2, c.Height / 2, numberColor);
            // This child is painted after its parent's OnRender in the original visual tree.
            var needle = AnalogHudScene.Quad(0,
                new(c.Width * 178.5 / 288, c.Height * 54 / 288, c.Width * 110 / 288, c.Height * 180 / 288),
                110 + 260 * gaugeFraction, new(c.Width / 2, c.Height / 2),
                shader: c.Electric ? DirectCompositionShader.ElectricNeedle : DirectCompositionShader.Needle);
            commands.Add(needle);
        }
        return commands.ToArray();
    }

    internal static AnalogHudColor NumberColor(Configuration c, double pressure, double fraction, long timestamp)
    {
        if (!c.ColorNumber || c.StockPalette || pressure <= 5)
            return new(245, 245, 245);
        var color = Palette(c, fraction);
        if (fraction < .88) return color;
        var milliseconds = timestamp * 1000d / Stopwatch.Frequency;
        var phase = milliseconds % 620 / 620 * Math.PI * 2;
        return color with { A = (byte)Math.Round(175 + 80 * ((Math.Sin(phase) + 1) / 2)) };
    }

    internal static AnalogHudColor Palette(Configuration c, double fraction)
    {
        const double middle = .56;
        fraction = Math.Clamp(fraction, 0, 1);
        return fraction <= middle
            ? SupplementaryHudArt.Lerp(c.Low, c.Middle, fraction / middle)
            : SupplementaryHudArt.Lerp(c.Middle, c.High, (fraction - middle) / (1 - middle));
    }

    private static void AddDigits(List<DirectCompositionDrawCommand> commands, string text,
        double cx, double cy, AnalogHudColor color)
    {
        var width = text.Sum(ch => ch == '.' ? 4 : ch == '-' ? 8 : 18) - (text.Length - 1);
        var scale = Math.Min(1, 44d / width);
        var left = cx - width * scale / 2;
        foreach (var ch in text)
        {
            var glyphWidth = ch == '.' ? 4 : ch == '-' ? 8 : 18;
            if (ch == '.')
                commands.Add(AnalogHudScene.Quad(30021, new(left + .65 * scale, cy + 10.65 * scale, 2.7 * scale, 2.7 * scale), color: color));
            else if (ch == '-')
                commands.Add(AnalogHudScene.Quad(30022, new(left + scale, cy - scale, 6 * scale, 2 * scale), color: color));
            else if (ch is >= '0' and <= '9')
                commands.Add(AnalogHudScene.Quad((uint)(30010 + ch - '0'), new(left, cy - 14 * scale, 18 * scale, 28 * scale), color: color));
            left += (glyphWidth - 1) * scale;
        }
    }

    private static Art BuildArt(Configuration c)
    {
        var textures = new List<AnalogHudTexture>();
        textures.Add(SupplementaryHudArt.Raster(30000, c.Width, c.Height, c.RasterScale, dc =>
        {
            if (c.Digital)
            {
                if (!c.StockMaterial)
                    dc.DrawGeometry(SupplementaryHudArt.Brush(236, 239, 244, 54), null,
                        SupplementaryHudArt.Rail(9.05, 292.05, 58.75, 65.25, 1.3));
            }
            else
            {
                dc.DrawGeometry(null, new Pen(SupplementaryHudArt.Brush(142, 147, 156, 145), 2.2),
                    SupplementaryHudArt.Arc(new(c.Width / 2, c.Height / 2), Math.Min(c.Width, c.Height) * .43, 110, 260));
            }
        }));
        textures.Add(SupplementaryHudArt.Raster(30001, c.Width, c.Height, c.RasterScale, dc =>
        {
            if (c.Digital) DrawDigitalStatic(dc, c);
            else DrawAnalogStatic(dc, c);
        }));
        if (c.Digital)
        {
            textures.Add(SupplementaryHudArt.Raster(30003, 283, 6.5, c.RasterScale, dc =>
            {
                var gradient = new LinearGradientBrush
                {
                    // Counter the quad's -0.2 rake so palette stops remain at
                    // the same absolute X coordinates as the WPF gradient.
                    MappingMode = BrushMappingMode.Absolute,
                    StartPoint = new(0, 0),
                    EndPoint = new(283 / 1.04, -56.6 / 1.04),
                    GradientStops = { new(SupplementaryHudArt.ToColor(c.Low with { A = 255 }), 0), new(SupplementaryHudArt.ToColor(c.Middle with { A = 255 }), .56),
                        new(SupplementaryHudArt.ToColor(c.High with { A = 255 }), 1) }
                };
                dc.DrawRectangle(gradient, null, new(0, 0, 283, 6.5));
            }));
            textures.Add(SupplementaryHudArt.Raster(30004, 10, 14, c.RasterScale, dc =>
            {
                var start = new Point(5, 3.6);
                var end = new Point(3.7, 10.6);
                dc.DrawLine(new Pen(SupplementaryHudArt.Brush(248, 250, 253, 28), 7.2), start, end);
                dc.DrawLine(new Pen(SupplementaryHudArt.Brush(248, 250, 253, 72), 3.6), start, end);
                dc.DrawLine(new Pen(SupplementaryHudArt.Brush(248, 250, 253, 242), 1.35), start, end);
            }));
            var glyphs = SupplementaryHudArt.Glyphs(textures, 30100, "0123456789.- PBARSI", 16, c.RasterScale,
                FontWeights.SemiBold, FontStyles.Italic);
            return new(textures.AsReadOnly(), glyphs);
        }
        var size = Math.Min(c.Width, c.Height);
        textures.Add(SupplementaryHudArt.Raster(30002, size, size, c.RasterScale, dc =>
        {
            var center = new Point(size / 2, size / 2);
            dc.DrawEllipse(null, new Pen(SupplementaryHudArt.Brush(255, 255, 255, 58), 8), center, size * .43, size * .43);
            dc.DrawEllipse(null, new Pen(Brushes.White, 3.2), center, size * .43, size * .43);
        }));
        SupplementaryHudArt.AddNativeDigits(textures, 30010);
        textures.Add(SupplementaryHudArt.Raster(30021, 2.7, 2.7, c.RasterScale,
            dc => dc.DrawEllipse(Brushes.White, null, new(1.35, 1.35), 1.35, 1.35)));
        textures.Add(SupplementaryHudArt.Pixel(30022));
        return new(textures.AsReadOnly(), null);
    }

    private static void DrawDigitalStatic(DrawingContext dc, Configuration c)
    {
        if (!c.StockMaterial)
            dc.DrawGeometry(null, new Pen(SupplementaryHudArt.Brush(238, 241, 246, 168), 1),
                SupplementaryHudArt.Rail(9.05, 292.05, 58.75, 65.25, 1.3));
        if (c.Vacuum)
        {
            var zero = 9.05 + 283 * BoostPressureUnits.GaugeFraction(0, c.Unit, true);
            dc.DrawLine(new(Brushes.WhiteSmoke, 1), new(zero, 55.75), new(zero, 57.75));
            dc.DrawLine(new(Brushes.WhiteSmoke, 1), new(zero - 1.3, 66.25), new(zero - 1.3, 68.25));
        }
        dc.DrawLine(new(SupplementaryHudArt.Brush(225, 229, 236, 38), 3.2), new(13.42, 26.5), new(7.38, 56.7));
        dc.DrawLine(new(SupplementaryHudArt.Brush(225, 229, 236, 204), 1.1), new(13.42, 26.5), new(7.38, 56.7));
        dc.DrawText(SupplementaryHudArt.Text("BOOST", 12, SupplementaryHudArt.Brush(224, 228, 236, 210),
            FontWeights.SemiBold, FontStyles.Italic), new(13.85, 69));
    }

    private static void DrawAnalogStatic(DrawingContext dc, Configuration c)
    {
        var center = new Point(c.Width / 2, c.Height / 2);
        var radius = Math.Min(c.Width, c.Height) * .43;
        var maximum = BoostPressureUnits.AnalogMaximum(c.Unit);
        var minimum = BoostPressureUnits.AnalogMinimum(c.Unit, c.Vacuum);
        var interval = c.Unit == BoostPressureUnit.Bar ? 1 : 10;
        var count = (int)((maximum - minimum) / interval);
        for (var i = 0; i <= count; i++)
        {
            var pressure = minimum + i * interval;
            var angle = 110 + 260 * (pressure - minimum) / (maximum - minimum);
            SupplementaryHudArt.Tick(dc, center, radius, angle, 7, 1.4, pressure.ToString("0", CultureInfo.InvariantCulture), 7, 15);
            if (i < count)
                SupplementaryHudArt.Tick(dc, center, radius, angle + 260 * (interval / 2d) / (maximum - minimum), 3.5, .8, null, 7, 15);
        }
        SupplementaryHudArt.GearRing(dc, center, 84, 29, 22.5);
        var unit = SupplementaryHudArt.Text(BoostPressureUnits.Symbol(c.Unit), 9,
            SupplementaryHudArt.Brush(185, 193, 207, 190), FontWeights.SemiBold);
        dc.DrawText(unit, new(center.X - unit.Width / 2, center.Y + 30));
        dc.DrawText(SupplementaryHudArt.Text("BOOST", 10.5, SupplementaryHudArt.Brush(210, 215, 225, 205),
            FontWeights.SemiBold, FontStyles.Italic), new(center.X + 27, center.Y + 40));
    }
}

// Raster work is limited to artwork/configuration changes. Live values, clipping,
// needle transforms and color pulses are submitted as native draw commands.
internal static class SupplementaryHudArt
{
    internal sealed record Glyph(uint Texture, double Width, double Height, double Advance);
    internal sealed record GlyphSet(IReadOnlyDictionary<char, Glyph> Characters, double Height,
        IReadOnlyDictionary<int, double>? PairAdjustments = null);

    internal static double RasterScale(FrameworkElement control)
    {
        var dpi = VisualTreeHelper.GetDpi(control).DpiScaleX;
        var window = Window.GetWindow(control);
        if (window is not null && control.IsDescendantOf(window))
        {
            var transform = control.TransformToAncestor(window);
            var zero = transform.Transform(new(0, 0));
            var x = transform.Transform(new(1, 0));
            var y = transform.Transform(new(0, 1));
            dpi *= Math.Max((x - zero).Length, (y - zero).Length);
        }
        // Round upward, retaining physical resolution while avoiding a new
        // artwork allocation for every tiny step of a live scale adjustment.
        return Math.Clamp(Math.Ceiling(dpi * 4) / 4, 1, 8);
    }

    internal static AnalogHudColor Color(Brush brush)
    {
        var c = brush is SolidColorBrush solid ? solid.Color : Colors.DodgerBlue;
        return new(c.R, c.G, c.B, c.A);
    }

    internal static Color ToColor(AnalogHudColor c) => System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
    internal static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(System.Windows.Media.Color.FromArgb(a, r, g, b));
    internal static AnalogHudColor Lerp(AnalogHudColor a, AnalogHudColor b, double value) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * value), (byte)Math.Round(a.G + (b.G - a.G) * value),
        (byte)Math.Round(a.B + (b.B - a.B) * value));

    internal static FormattedText Text(string text, double size, Brush brush, FontWeight weight = default, FontStyle style = default) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
                style == default ? FontStyles.Normal : style, weight == default ? FontWeights.Normal : weight,
                FontStretches.Condensed), size, brush, 1);

    internal static AnalogHudTexture Raster(uint id, double width, double height, double scale, Action<DrawingContext> draw)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        var pixelWidth = (int)Math.Ceiling(width * scale);
        var pixelHeight = (int)Math.Ceiling(height * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Cover exactly the command's logical rectangle even when its
            // dimensions or display scale land between whole pixels.
            dc.PushTransform(new ScaleTransform(pixelWidth / width, pixelHeight / height));
            draw(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new(id, bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels);
    }

    internal static AnalogHudTexture Pixel(uint id) => new(id, 1, 1, 4, new byte[] { 255, 255, 255, 255 });

    internal static void AddNativeDigits(List<AnalogHudTexture> textures, uint firstId)
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            var source = NativeAssetCache.Get(NativeGaugeMode.Analogue, $"HUD_Dial_Speed_Analogue_{digit}.png");
            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            textures.Add(new(firstId + (uint)digit, bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels));
        }
    }

    internal static GlyphSet Glyphs(List<AnalogHudTexture> textures, uint firstId, string characters,
        double size, double scale, FontWeight weight, FontStyle style = default)
    {
        var glyphs = new Dictionary<char, Glyph>();
        double height = 0;
        foreach (var ch in characters.Distinct())
        {
            var text = Text(ch.ToString(), size, Brushes.White, weight, style);
            height = Math.Max(height, text.Height);
            // Padding retains italic overhang and antialias pixels; advancement
            // remains the original font's metric, independent of bitmap bounds.
            var width = Math.Ceiling(text.WidthIncludingTrailingWhitespace + Math.Max(0, text.OverhangTrailing) + 4);
            var id = firstId++;
            textures.Add(Raster(id, width, text.Height + 4, scale, dc => dc.DrawText(text, new(2, 2))));
            glyphs.Add(ch, new(id, width, text.Height + 4, text.WidthIncludingTrailingWhitespace));
        }
        var pairs = new Dictionary<int, double>();
        foreach (var first in glyphs.Keys)
            foreach (var second in glyphs.Keys)
            {
                var pair = Text(string.Concat(first, second), size, Brushes.White, weight, style);
                var adjustment = pair.WidthIncludingTrailingWhitespace - glyphs[first].Advance - glyphs[second].Advance;
                if (Math.Abs(adjustment) > .00001) pairs.Add((first << 16) | second, adjustment);
            }
        return new(new System.Collections.ObjectModel.ReadOnlyDictionary<char, Glyph>(glyphs), height,
            new System.Collections.ObjectModel.ReadOnlyDictionary<int, double>(pairs));
    }

    internal static double TextWidth(GlyphSet glyphs, string text)
    {
        var width = 0d;
        for (var i = 0; i < text.Length; i++)
            if (glyphs.Characters.TryGetValue(text[i], out var glyph))
                width += glyph.Advance + PairAdjustment(glyphs, text, i);
        return width;
    }

    private static double PairAdjustment(GlyphSet glyphs, string text, int index) =>
        index + 1 < text.Length && glyphs.PairAdjustments is { } pairs &&
        pairs.TryGetValue((text[index] << 16) | text[index + 1], out var adjustment) ? adjustment : 0;

    internal static void AddText(List<DirectCompositionDrawCommand> commands, GlyphSet glyphs,
        string text, double x, double y, AnalogHudColor color)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (!glyphs.Characters.TryGetValue(ch, out var glyph)) continue;
            commands.Add(AnalogHudScene.Quad(glyph.Texture, new(x - 2, y - 2, glyph.Width, glyph.Height), color: color));
            x += glyph.Advance + PairAdjustment(glyphs, text, index);
        }
    }

    internal static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    internal static Geometry Arc(Point center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Polar(center, radius, start), false, false);
            context.ArcTo(Polar(center, radius, start + sweep), new(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    internal static Geometry Rail(double left, double right, double top, double bottom, double rake)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new(left, top), true, true);
            context.LineTo(new(right, top), true, false);
            context.LineTo(new(right - rake, bottom), true, false);
            context.LineTo(new(left - rake, bottom), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    internal static void Tick(DrawingContext dc, Point center, double radius, double angle,
        double length, double thickness, string? label, double fontSize, double labelOffset)
    {
        var brush = Brush(190, 195, 205, 185);
        dc.DrawLine(new(brush, thickness), Polar(center, radius - length, angle), Polar(center, radius, angle));
        if (label is null) return;
        var text = Text(label, fontSize, brush, FontWeights.SemiBold);
        var point = Polar(center, radius - labelOffset, angle);
        dc.DrawText(text, new(point.X - text.Width / 2, point.Y - text.Height / 2));
    }

    internal static void GearRing(DrawingContext dc, Point center, double size, double outer, double inner)
    {
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(center, outer, outer), new EllipseGeometry(center, inner, inner)));
        dc.DrawImage(NativeAssetCache.Get(NativeGaugeMode.Analogue, "HUD_Dial_Analog_Gear_1.png"),
            new(center.X - size / 2, center.Y - size / 2, size, size));
        dc.Pop();
    }
}
