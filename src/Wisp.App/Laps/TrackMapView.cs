using System.Windows;
using Wisp.App.NativeRendering;

namespace Wisp.App.Laps;

internal sealed class TrackMapView : FrameworkElement
{
    internal required LapDeltaService Service { get; init; }
    internal string? TrackColor { get; set; }
    internal string? CarColor { get; set; }
    internal string? BackgroundColor { get; set; }
    internal TrackMapHudLayer.Artwork? Artwork { get; set; }
}
