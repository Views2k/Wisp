using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Wisp.App.Laps;
using Wisp.Core;

namespace Wisp.App.NativeRendering;

internal static class TrackMapHudLayer
{
    internal const double Size = 360;
    private const uint Base = 95_000;
    private const double Margin = 28, Plot = 304, Top = 40;
    private static IReadOnlyList<AnalogHudTexture>? _staticTextures;
    internal sealed record Artwork(LapTrackOutline? Outline, LapMapBounds Bounds, IReadOnlyList<AnalogHudTexture> Textures);

    internal static HudLayerSnapshot Capture(TrackMapView view)
    {
        var outline = view.Service.LatestMap?.Outline;
        if (view.Artwork is not { } previous || !ReferenceEquals(previous.Outline, outline))
            view.Artwork = CreateArtwork(outline);
        return new Snapshot(view.Artwork, view.Service, LapColors.Tint(view.TrackColor, LapColors.Track),
            LapColors.Tint(view.CarColor, LapColors.Car), LapColors.Tint(view.BackgroundColor, LapColors.Background));
    }

    internal static Artwork CreateArtwork(LapTrackOutline? outline)
    {
        _staticTextures ??= BuildStatic();
        var bounds = outline is null ? new LapMapBounds(-25, -25, 50) : LapMapBounds.From(outline);
        var track = LapDeltaHudLayer.Raster(Base + 5, 512, 512, dc =>
        {
            if (outline is null || outline.Points.Count < 2) return;
            var path = new StreamGeometry();
            using (var geometry = path.Open())
            {
                var first = bounds.Project(outline.Points[0]);
                geometry.BeginFigure(new Point(8 + first.X * 496, 8 + first.Y * 496), false, false);
                for (var i = 1; i < outline.Points.Count; i++)
                {
                    var point = bounds.Project(outline.Points[i]);
                    geometry.LineTo(new Point(8 + point.X * 496, 8 + point.Y * 496), true, false);
                }
            }
            path.Freeze();
            dc.DrawGeometry(null, new Pen(Brushes.White, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, path);
        });
        return new(outline, bounds, _staticTextures.Append(track).ToArray());
    }

    private static IReadOnlyList<AnalogHudTexture> BuildStatic()
    {
        var textures = new List<AnalogHudTexture>
        {
            LapDeltaHudLayer.Raster(Base, 360, 360, dc => dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, 360, 360), 14, 14)),
            LapDeltaHudLayer.Raster(Base + 1, 20, 20, dc => dc.DrawEllipse(Brushes.White, null, new Point(10, 10), 7, 7))
        };
        foreach (var (text, id) in new[] { ("LIVE LAP", 2u), ("LEARNING TRACK", 3u), ("START A LAP", 4u) })
            textures.Add(LapDeltaHudLayer.Raster(Base + id, 304, 24, dc =>
            {
                var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Bahnschrift"), 13, Brushes.White, 2);
                dc.DrawText(label, new Point((304 - label.Width) / 2, 0));
            }));
        return textures.AsReadOnly();
    }

    internal static void Append(List<DirectCompositionDrawCommand> commands, Artwork artwork, LapMapReading? map,
        AnalogHudColor track, AnalogHudColor car, AnalogHudColor background)
    {
        commands.Add(AnalogHudScene.Quad(Base, new(0, 0, Size, Size), color: background));
        commands.Add(AnalogHudScene.Quad(Base + (map is null || !map.IsRecording ? 4u : artwork.Outline?.Complete == true ? 2u : 3u),
            new(Margin, 12, Plot, 24), color: new(220, 228, 239)));
        if (map is null || !ReferenceEquals(artwork.Outline, map.Outline)) return;
        var bounds = artwork.Bounds.Include(map.Position);
        var scale = artwork.Bounds.Size / bounds.Size;
        var left = Margin + (artwork.Bounds.Left - bounds.Left) / bounds.Size * Plot;
        var top = Top + (bounds.Top + bounds.Size - artwork.Bounds.Top - artwork.Bounds.Size) / bounds.Size * Plot;
        // Texture has eight pixels of padding; compensate exactly so both the
        // track and marker use the same world-to-screen transform.
        var textureSize = Plot * scale * 512 / 496;
        commands.Add(AnalogHudScene.Quad(Base + 5, new(left - textureSize / 64, top - textureSize / 64, textureSize, textureSize), color: track));
        if (map.Outline.Points.Count > 0)
        {
            var start = bounds.Project(map.Outline.Points[0]);
            commands.Add(AnalogHudScene.Quad(Base + 1, new(Margin + start.X * Plot - 4, Top + start.Y * Plot - 4, 8, 8), color: new(255, 255, 255)));
        }
        var point = bounds.Project(map.Position);
        commands.Add(AnalogHudScene.Quad(Base + 1, new(Margin + point.X * Plot - 12, Top + point.Y * Plot - 12, 24, 24), color: new(8, 12, 17)));
        commands.Add(AnalogHudScene.Quad(Base + 1, new(Margin + point.X * Plot - 9, Top + point.Y * Plot - 9, 18, 18), color: car));
    }

    private sealed class Snapshot(Artwork artwork, LapDeltaService service, AnalogHudColor track, AnalogHudColor car,
        AnalogHudColor background) : HudLayerSnapshot
    {
        internal Artwork Artwork => artwork;
        internal LapDeltaService Service => service;
        internal AnalogHudColor Track => track;
        internal AnalogHudColor Car => car;
        internal AnalogHudColor Background => background;
        internal override IReadOnlyList<AnalogHudTexture> Textures => artwork.Textures;
        internal override object CompatibilityKey => (artwork.Outline, track, car, background);
        internal override HudLayerPlayback CreatePlayback() => new Playback();
    }
    private sealed class Playback : HudLayerPlayback
    {
        private Snapshot? _snapshot;
        private LapTrackOutline? _drawnOutline;
        private int _drawnGeneration;
        private bool _drawnRecording;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) => _snapshot = (Snapshot)snapshot;
        internal override bool CanReuse(long timestamp)
        {
            if (_snapshot is not { } s) return false;
            var map = s.Service.LatestMap;
            return _drawnGeneration == s.Service.Generation && ReferenceEquals(_drawnOutline, map?.Outline) &&
                _drawnRecording == (map?.IsRecording == true);
        }
        internal override void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp)
        {
            if (_snapshot is not { } s) return;
            _drawnGeneration = s.Service.Generation;
            var map = s.Service.LatestMap;
            _drawnOutline = map?.Outline;
            _drawnRecording = map?.IsRecording == true;
            Append(commands, s.Artwork, map, s.Track, s.Car, s.Background);
        }
    }
}
