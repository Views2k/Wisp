using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.NativeRendering;

internal static class PowerTorqueHudLayer
{
    private const double DesignSize = 140;
    private const int GlyphWidth = 16, GlyphHeight = 20, GlyphPadding = 2;
    private const string Glyphs = "0123456789PEAK —";
    private static readonly ConditionalWeakTable<PowerTorqueGaugeView, CaptureCache> Caches = new();
    private static readonly AnalogHudColor LabelColor = new(210, 215, 225, 215);
    private static readonly AnalogHudColor WhiteSmoke = new(245, 245, 245);

    internal static HudLayerSnapshot Capture(PowerTorqueGaugeView control, DiagnosticsViewModel? vm)
    {
        control.Dispatcher.VerifyAccess();
        var width = control.RenderSize.Width > 0 ? control.RenderSize.Width : DesignSize;
        var height = control.RenderSize.Height > 0 ? control.RenderSize.Height : DesignSize;
        var dpi = VisualTreeHelper.GetDpi(control);
        var rasterScale = Math.Max(2, Math.Ceiling(Math.Max(width * dpi.DpiScaleX, height * dpi.DpiScaleY) / DesignSize));
        if (Window.GetWindow(control) is { } window && !ReferenceEquals(control, window))
        {
            var transform = control.TransformToAncestor(window);
            var origin = transform.Transform(new Point());
            var x = transform.Transform(new Point(width, 0)) - origin;
            var y = transform.Transform(new Point(0, height)) - origin;
            rasterScale = Math.Max(rasterScale, Math.Ceiling(Math.Max(
                Math.Sqrt(x.X * x.X * dpi.DpiScaleX * dpi.DpiScaleX + x.Y * x.Y * dpi.DpiScaleY * dpi.DpiScaleY),
                Math.Sqrt(y.X * y.X * dpi.DpiScaleX * dpi.DpiScaleX + y.Y * y.Y * dpi.DpiScaleY * dpi.DpiScaleY)) / DesignSize));
        }
        var key = new ArtworkKey(control.NativeArtworkRevision, control.IsTorque, control.Maximum,
            control.TorqueUnit, dpi.PixelsPerDip, rasterScale);
        var cache = Caches.GetValue(control, static _ => new CaptureCache());
        if (cache.Artwork is null || cache.Key != key)
        {
            cache.Artwork = CaptureArtwork(control, rasterScale);
            cache.Key = key;
        }
        var input = vm is null
            ? new NativePowerTorqueInput(control.Display, 1, 0, 0, 0, 0)
            : vm.NativePowerTorqueInput;
        var options = new Options(width, height, control.IsTorque, control.TorqueUnit, control.Maximum,
            control.IsElectricMaterial, control.ColorNumber,
            Color(control.PaletteColor(0)), Color(control.PaletteColor(.56)), Color(control.PaletteColor(1)));
        if (cache.Snapshot is { } previous && previous.Input == input && previous.Options == options &&
            ReferenceEquals(previous.Artwork, cache.Artwork)) return previous;
        return cache.Snapshot = new Snapshot(cache.Artwork, input, options);
    }

    private static Artwork CaptureArtwork(PowerTorqueGaugeView control, double scale)
    {
        var firstId = control.IsTorque ? 25_000u : 20_000u;
        var textures = new List<AnalogHudTexture>();
        void Add(uint offset, BitmapSource image) => textures.Add(Texture(firstId + offset, image));
        Add(0, Rasterize(DesignSize, DesignSize, scale, dc => dc.DrawDrawing(control.CaptureNativeDial())));
        Add(1, Rasterize(DesignSize, DesignSize, scale, dc => dc.DrawDrawing(control.CaptureNativeColoredArc())));
        Add(2, Rasterize(16, 8, scale, dc => dc.DrawLine(new Pen(control.AccentBrush, 2), new Point(4, 4), new Point(10, 4))));
        var advances = new double[Glyphs.Length];
        Add(3, Rasterize(Glyphs.Length * GlyphWidth, GlyphHeight, scale, dc =>
        {
            for (var index = 0; index < Glyphs.Length; index++)
            {
                var text = control.CaptureNativeText(Glyphs[index].ToString(), 8.5, Brushes.White);
                advances[index] = text.WidthIncludingTrailingWhitespace;
                dc.DrawText(text, new Point(index * GlyphWidth + GlyphPadding, GlyphPadding));
            }
        }));
        var kerning = new double[Glyphs.Length * Glyphs.Length];
        for (var left = 0; left < Glyphs.Length; left++)
            for (var right = 0; right < Glyphs.Length; right++)
            {
                var text = control.CaptureNativeText(string.Concat(Glyphs[left], Glyphs[right]), 8.5, Brushes.White);
                kerning[left * Glyphs.Length + right] = text.WidthIncludingTrailingWhitespace - advances[left] - advances[right];
            }
        var unavailable = control.CaptureNativeText("—", 28, Brushes.WhiteSmoke);
        var unavailableSize = new AnalogHudPoint(Math.Ceiling(unavailable.Width + 4), Math.Ceiling(unavailable.Height + 4));
        Add(4, Rasterize(unavailableSize.X, unavailableSize.Y, scale, dc => dc.DrawText(unavailable, new Point(2, 2))));
        for (var digit = 0; digit < 10; digit++)
        {
            var source = NativeAssetCache.Get(NativeGaugeMode.Analogue, $"HUD_Dial_Speed_Analogue_{digit}.png");
            var original = Texture(firstId + 10 + (uint)digit, source);
            textures.Add(original);
            var mask = original.Pixels.ToArray();
            AnalogHudAssets.MakeWhiteAlphaMask(mask);
            textures.Add(new(firstId + 30 + (uint)digit, original.Width, original.Height, original.Stride, mask));
        }
        return new(firstId, textures.AsReadOnly(), advances, kerning, unavailable.Width, unavailableSize);
    }

    private static BitmapSource Rasterize(double width, double height, double scale, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static AnalogHudTexture Texture(uint id, BitmapSource source)
    {
        var image = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        return new(id, image.PixelWidth, image.PixelHeight, stride, pixels);
    }

    private static AnalogHudColor Color(Color color) => new(color.R, color.G, color.B, color.A);
    private static double Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private sealed class CaptureCache
    {
        internal ArtworkKey Key;
        internal Artwork? Artwork;
        internal Snapshot? Snapshot;
    }

    private readonly record struct ArtworkKey(long Revision, bool IsTorque, double Maximum, TorqueUnit Unit,
        double PixelsPerDip, double RasterScale);
    private readonly record struct Options(double Width, double Height, bool IsTorque, TorqueUnit Unit,
        double Maximum, bool Electric, bool ColorNumber, AnalogHudColor Low, AnalogHudColor Mid, AnalogHudColor High);
    private sealed record Artwork(uint FirstId, IReadOnlyList<AnalogHudTexture> Textures,
        double[] Advances, double[] Kerning, double UnavailableAdvance, AnalogHudPoint UnavailableSize);
    private sealed record Compatibility(Artwork Artwork, Options Options, int CarOrdinal, bool Available, long Revision);

    private sealed class Snapshot(Artwork artwork, NativePowerTorqueInput input, Options options) : HudLayerSnapshot
    {
        internal Artwork Artwork { get; } = artwork;
        internal NativePowerTorqueInput Input { get; } = input;
        internal Options Options { get; } = options;
        internal override IReadOnlyList<AnalogHudTexture> Textures => Artwork.Textures;
        internal override object CompatibilityKey { get; } = new Compatibility(artwork, options, input.CarOrdinal,
            input.Display.Available, input.Revision);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }

    private sealed class Playback : HudLayerPlayback
    {
        private readonly PowerTorqueNeedlePlayback _playback = new();
        private Snapshot? _snapshot;
        private NativePowerTorqueInput _input;
        private bool _hasInput;

        internal override void Update(HudLayerSnapshot snapshot, long timestamp)
        {
            var next = (Snapshot)snapshot;
            var input = next.Input;
            if (!_hasInput || input.Revision != _input.Revision) _playback.Reset();
            if (!_hasInput || input != _input)
            {
                var received = input.ReceivedTimestamp > 0 ? input.ReceivedTimestamp :
                    input.ObservedTimestamp > 0 ? input.ObservedTimestamp : timestamp;
                _playback.Observe(input.Display, input.CarOrdinal, input.GameTimestampMilliseconds,
                    Math.Max(timestamp, received), received);
            }
            // A peak reset carries the original sample identity and must not
            // reset the needle timeline or resurrect the previous peak.
            _playback.UpdatePeaks(input.Display);
            _input = input;
            _hasInput = true;
            _snapshot = next;
        }

        internal override DirectCompositionDrawCommand[] Build(long timestamp)
        {
            if (_snapshot is not { } snapshot) return [];
            var options = snapshot.Options;
            var art = snapshot.Artwork;
            var display = _playback.Sample(timestamp);
            double Convert(double value) => options.IsTorque ? PowerTorqueDisplay.ConvertTorque(value, options.Unit) : value;
            var value = Convert(options.IsTorque ? display.TorqueNm : display.PowerBhp);
            var peak = Convert(options.IsTorque ? display.PeakTorqueNm : display.PeakPowerBhp);
            var readout = Convert(options.IsTorque ? display.ReadoutTorqueNm ?? display.TorqueNm : display.ReadoutPowerBhp ?? display.PowerBhp);
            var available = display.Available && double.IsFinite(value);
            var commands = new List<DirectCompositionDrawCommand>(24)
            {
                AnalogHudScene.Quad(art.FirstId, new(0, 0, DesignSize, DesignSize))
            };
            if (available && value > 0)
            {
                var fraction = Math.Clamp(value / options.Maximum, 0, 1);
                var arc = AnalogHudScene.Quad(art.FirstId + 1, new(0, 0, DesignSize, DesignSize),
                    shader: fraction >= 1 ? DirectCompositionShader.Image : DirectCompositionShader.ImageSector);
                arc.ParameterX = (float)(PowerTorqueGaugeView.StartAngle * Math.PI / 180);
                arc.ParameterY = (float)(PowerTorqueGaugeView.SweepAngle * fraction * Math.PI / 180);
                commands.Add(arc);
            }
            if (double.IsFinite(peak) && peak > 0)
                commands.Add(AnalogHudScene.Quad(art.FirstId + 2, new(124, 66, 16, 8),
                    PowerTorqueGaugeView.NeedleAngle(peak, options.Maximum), new(70, 70)));
            if (available) AddReadout(commands, art, options, readout, 1 - .45 * display.DriftCutPulse);
            else commands.Add(AnalogHudScene.Quad(art.FirstId + 4,
                new(70 - art.UnavailableAdvance / 2 - 2, 48, art.UnavailableSize.X, art.UnavailableSize.Y)));
            AddPeakText(commands, art, double.IsFinite(peak) && peak > 0
                ? $"PEAK {Whole(peak).ToString("0", CultureInfo.InvariantCulture)}" : "PEAK —");
            if (available)
            {
                var needle = AnalogHudScene.Quad(0, new(178.5, 54, 110, 180),
                    PowerTorqueGaugeView.NeedleAngle(value, options.Maximum), new(144, 144),
                    shader: options.Electric ? DirectCompositionShader.ElectricNeedle : DirectCompositionShader.Needle);
                const double needleScale = DesignSize / 288 * .8;
                needle.OriginX = (float)(70 + (needle.OriginX - 144) * needleScale);
                needle.OriginY = (float)(70 + (needle.OriginY - 144) * needleScale);
                needle.AxisXX *= (float)needleScale; needle.AxisXY *= (float)needleScale;
                needle.AxisYX *= (float)needleScale; needle.AxisYY *= (float)needleScale;
                commands.Add(needle);
            }
            var xScale = (float)(options.Width / DesignSize);
            var yScale = (float)(options.Height / DesignSize);
            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                command.OriginX *= xScale; command.OriginY *= yScale;
                command.AxisXX *= xScale; command.AxisXY *= yScale;
                command.AxisYX *= xScale; command.AxisYY *= yScale;
                commands[index] = command;
            }
            return commands.ToArray();
        }
    }

    private static void AddReadout(List<DirectCompositionDrawCommand> commands, Artwork art, Options options, double value, double opacity)
    {
        var number = Whole(value).ToString("0", CultureInfo.InvariantCulture);
        var tint = options.ColorNumber ? Palette(options, Math.Clamp(value / options.Maximum, 0, 1)) : WhiteSmoke;
        var width = number.Sum(character => character == '-' ? 8d : 18) - (number.Length - 1);
        var scale = Math.Min(1, 48 / width);
        var left = 70 - width / 2;
        foreach (var character in number)
        {
            var rectangle = character == '-' ? new AnalogHudRect(left + 1, 69, 6, 2) : new AnalogHudRect(left, 56, 18, 28);
            rectangle = new(70 + (rectangle.X - 70) * scale, 70 + (rectangle.Y - 70) * scale,
                rectangle.Width * scale, rectangle.Height * scale);
            if (character == '-')
                commands.Add(AnalogHudScene.Quad(0, rectangle, opacity: opacity, color: tint));
            else
                commands.Add(AnalogHudScene.Quad(art.FirstId + (options.ColorNumber ? 30u : 10u) + (uint)(character - '0'),
                    rectangle, opacity: opacity, color: options.ColorNumber ? tint : null));
            left += character == '-' ? 7 : 17;
        }
    }

    private static void AddPeakText(List<DirectCompositionDrawCommand> commands, Artwork art, string text)
    {
        var width = 0d;
        var previous = -1;
        foreach (var character in text)
        {
            var index = Glyphs.IndexOf(character);
            if (index < 0) continue;
            if (previous >= 0) width += art.Kerning[previous * Glyphs.Length + index];
            width += art.Advances[index];
            previous = index;
        }
        var x = 70 - width / 2;
        previous = -1;
        foreach (var character in text)
        {
            var index = Glyphs.IndexOf(character);
            if (index < 0) continue;
            if (previous >= 0) x += art.Kerning[previous * Glyphs.Length + index];
            if (character != ' ')
            {
                var command = AnalogHudScene.Quad(art.FirstId + 3,
                    new(x - GlyphPadding, 126 - GlyphPadding, GlyphWidth, GlyphHeight), color: LabelColor);
                command.UvLeft = index / (float)Glyphs.Length;
                command.UvRight = (index + 1) / (float)Glyphs.Length;
                commands.Add(command);
            }
            x += art.Advances[index];
            previous = index;
        }
    }

    private static AnalogHudColor Palette(Options options, double fraction)
    {
        var lower = fraction <= .56;
        var start = lower ? options.Low : options.Mid;
        var end = lower ? options.Mid : options.High;
        var blend = lower ? fraction / .56 : (fraction - .56) / .44;
        byte Channel(byte left, byte right) => (byte)Math.Round(left + (right - left) * blend);
        return new(Channel(start.R, end.R), Channel(start.G, end.G), Channel(start.B, end.B), Channel(start.A, end.A));
    }
}
