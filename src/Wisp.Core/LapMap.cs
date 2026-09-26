namespace Wisp.Core;

public sealed record LapTrackOutline(IReadOnlyList<LapPosition> Points, bool Complete);
public sealed record LapMapReading(LapTrackOutline Outline, LapPosition Position, long ReceivedTimestamp, bool IsRecording = true);

// Shared world-to-map coordinates keep the outline and live marker aligned.
// Include the car in the bounds when it leaves the recorded route.
public readonly record struct LapMapBounds(double Left, double Top, double Size)
{
    public static LapMapBounds From(LapTrackOutline outline)
    {
        if (outline.Points.Count == 0) return new(-25, -25, 50);
        var left = outline.Points.Min(p => (double)p.X);
        var right = outline.Points.Max(p => (double)p.X);
        var top = outline.Points.Min(p => (double)p.Z);
        var bottom = outline.Points.Max(p => (double)p.Z);
        var size = Math.Max(50, Math.Max(right - left, bottom - top));
        return new((left + right - size) / 2, (top + bottom - size) / 2, size);
    }

    public LapMapBounds Include(LapPosition position)
    {
        var left = Math.Min(Left, position.X);
        var right = Math.Max(Left + Size, position.X);
        var top = Math.Min(Top, position.Z);
        var bottom = Math.Max(Top + Size, position.Z);
        var size = Math.Max(right - left, bottom - top);
        return new((left + right - size) / 2, (top + bottom - size) / 2, size);
    }
    public (double X, double Y) Project(LapPosition position) => ((position.X - Left) / Size, (Top + Size - position.Z) / Size);
}
