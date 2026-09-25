using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.Laps;
using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class LapDeltaHudLayer
{
    internal const double Width = 320, Height = 112;
    private const uint Base = 90_000;
    private const string Glyphs = "0123456789+−.";
    private const int CellWidth = 42, CellHeight = 64;
    private static IReadOnlyList<AnalogHudTexture>? _textures;
    private static readonly double[] Advances = new double[Glyphs.Length];
    private static readonly Typeface Face = new(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
        FontStyles.Italic, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly AnalogHudColor White = new(245, 247, 250), Muted = new(190, 199, 212);
    private static readonly AnalogHudColor Ahead = new(124, 242, 201), Behind = new(255, 100, 124);

    internal static HudLayerSnapshot Capture(LapDeltaView view) => new Snapshot(LoadTextures(), view.Service,
        view.Reference, view.ShowBar, LapColors.Tint(view.AheadColor, LapColors.Ahead), LapColors.Tint(view.BehindColor, LapColors.Behind));

    internal static IReadOnlyList<AnalogHudTexture> LoadTextures()
    {
        if (_textures is not null) return _textures;
        var textures = new List<AnalogHudTexture>
        {
            Raster(Base, 320, 112, dc => dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(175, 8, 12, 17)),
                new Pen(new SolidColorBrush(Color.FromArgb(70, 210, 218, 230)), 1), new Rect(1, 1, 318, 110), 12, 12)),
            Raster(Base + 1, 1, 1, dc => dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1, 1)))
        };
        var labels = new[] { "SESSION BEST", "PREVIOUS LAP", "START A LAP", "SETTING REFERENCE", "REJOIN YOUR LAP" };
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            textures.Add(Raster(Base + 2 + (uint)i, 280, 28, dc =>
            {
                var text = Format(label, i < 2 ? 14 : 19);
                dc.DrawText(text, new Point((280 - text.Width) / 2, 0));
            }));
        }
        for (var i = 0; i < Glyphs.Length; i++)
        {
            var text = Format(Glyphs[i].ToString(), 52);
            Advances[i] = text.WidthIncludingTrailingWhitespace + 2;
            textures.Add(Raster(Base + 10 + (uint)i, CellWidth, CellHeight,
                dc => dc.DrawText(text, new Point((CellWidth - text.Width) / 2, 0))));
        }
        _textures = textures.AsReadOnly();
        return _textures;
    }

    private static FormattedText Format(string text, double size) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Face, size, Brushes.White, 2);

    internal static AnalogHudTexture Raster(uint id, int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return new(id, bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels);
    }

    private sealed class Snapshot(IReadOnlyList<AnalogHudTexture> textures, LapDeltaService service,
        LapDeltaReference reference, bool bar, AnalogHudColor ahead, AnalogHudColor behind) : HudLayerSnapshot
    {
        internal LapDeltaService Service { get; } = service;
        internal LapDeltaReference Reference { get; } = reference;
        internal bool Bar { get; } = bar;
        internal AnalogHudColor Ahead { get; } = ahead;
        internal AnalogHudColor Behind { get; } = behind;
        internal override IReadOnlyList<AnalogHudTexture> Textures => textures;
        internal override object CompatibilityKey => (Reference, Bar, Ahead, Behind);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }

    private sealed class Playback : HudLayerPlayback
    {
        private Snapshot? _snapshot;
        private int _drawnGeneration;
        private LapDeltaReading _reading = LapDeltaReading.Waiting;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) => _snapshot = (Snapshot)snapshot;
        internal override bool CanReuse(long timestamp) => _snapshot is { } s && _drawnGeneration == s.Service.Generation;
        internal override bool RefreshNativeHistory(long timestamp)
        {
            var next = _snapshot?.Service.Latest ?? LapDeltaReading.Waiting;
            var changed = next.Seconds.HasValue != _reading.Seconds.HasValue;
            _reading = next;
            return changed;
        }

        internal override void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp)
        {
            if (_snapshot is not { } s) return;
            _drawnGeneration = s.Service.Generation;
            _reading = s.Service.Latest;
            Append(commands, _reading, s.Reference, s.Bar, s.Ahead, s.Behind);
        }
    }

    internal static void Append(List<DirectCompositionDrawCommand> commands, LapDeltaReading reading,
        LapDeltaReference reference, bool bar, AnalogHudColor? ahead = null, AnalogHudColor? behind = null)
    {
        commands.Add(AnalogHudScene.Quad(Base, new(0, 0, Width, Height)));
        commands.Add(AnalogHudScene.Quad(Base + (reference == LapDeltaReference.SessionBest ? 2u : 3u), new(20, 9, 280, 28), color: Muted));
        if (reading.Seconds is not { } delta)
        {
            var label = reading.Status switch { LapDeltaStatus.RecordingLap => 5u, LapDeltaStatus.RejoinReference => 6u, _ => 4u };
            commands.Add(AnalogHudScene.Quad(Base + label, new(20, 47, 280, 28), color: White));
            return;
        }
        var rounded = Math.Round(delta, 2);
        var text = (rounded < 0 ? "−" : "+") + Math.Abs(rounded).ToString("0.00", CultureInfo.InvariantCulture);
        var color = rounded < 0 ? ahead ?? Ahead : rounded > 0 ? behind ?? Behind : White;
        var naturalWidth = text.Sum(character => Advances[Glyphs.IndexOf(character)]);
        var scale = Math.Min(1, 276 / naturalWidth);
        var x = (Width - naturalWidth * scale) / 2;
        foreach (var character in text)
        {
            var index = Glyphs.IndexOf(character);
            if (index >= 0) commands.Add(AnalogHudScene.Quad(Base + 10 + (uint)index, new(x - (CellWidth - Advances[index]) * scale / 2, 29, CellWidth * scale, 64), color: color));
            x += Advances[index] * scale;
        }
        if (!bar) return;
        commands.Add(AnalogHudScene.Quad(Base + 1, new(20, 96, 280, 3), color: new(70, 79, 92)));
        var length = Math.Clamp(Math.Abs(delta) / 2, 0, 1) * 140;
        if (length > 0) commands.Add(AnalogHudScene.Quad(Base + 1, new(delta < 0 ? 160 - length : 160, 95, length, 5), color: color));
        commands.Add(AnalogHudScene.Quad(Base + 1, new(159, 92, 2, 11), color: White));
    }
}
