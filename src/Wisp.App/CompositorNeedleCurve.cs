using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wisp.App;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct CompositorNeedlePoint(double offsetSeconds, double angle, double blur)
{
    public readonly double OffsetSeconds = offsetSeconds;
    public readonly double Angle = angle;
    public readonly double Blur = blur;
}

internal readonly record struct CompositorNeedleCurve(
    int Count,
    long StartTimestamp,
    long EndTimestamp,
    long FreshUntilTimestamp,
    long LatestObservationTimestamp,
    int AcceptedCarOrdinal,
    long ReseedCount,
    double PlaybackDelayMilliseconds,
    double PlaybackTargetDelayMilliseconds,
    bool NativeSource = true)
{
    // At most 75 one-millisecond boundaries, 64 source knots and two endpoints.
    internal const int MaximumPoints = 144;
    internal double DurationSeconds => (EndTimestamp - StartTimestamp) / (double)Stopwatch.Frequency;
}

internal static class CompositorNeedleCurveBuilder
{
    internal static bool TryCopy(
        NativeTachometerInterpolator angle, NativeTachometerInterpolator blur,
        long timestamp, long latestObservation, long freshUntil,
        Span<CompositorNeedlePoint> destination, out CompositorNeedleCurve curve) =>
        TryCopyCore(angle, blur, timestamp, latestObservation, freshUntil, 0, 0, destination, out curve);

    internal static bool TryCopyRpm(
        NativeTachometerInterpolator rpm, double maximumRpm, double currentBlur,
        long timestamp, long latestObservation, long freshUntil,
        Span<CompositorNeedlePoint> destination, out CompositorNeedleCurve curve) =>
        TryCopyCore(rpm, null, timestamp, latestObservation, freshUntil, maximumRpm, currentBlur, destination, out curve);

    private static bool TryCopyCore(
        NativeTachometerInterpolator angle, NativeTachometerInterpolator? blur,
        long timestamp, long latestObservation, long freshUntil, double maximumRpm, double currentBlur,
        Span<CompositorNeedlePoint> destination, out CompositorNeedleCurve curve)
    {
        curve = default;
        var startBlur = currentBlur;
        if (destination.IsEmpty || timestamp >= freshUntil ||
            !angle.TryProjectCompositorValue(timestamp, out var startPlayback, out var startAngle))
            return false;
        if (blur is not null &&
            (!blur.TryProjectCompositorValue(timestamp, out var blurPlayback, out startBlur) ||
            startPlayback != blurPlayback || angle.AcceptedCarOrdinal != blur.AcceptedCarOrdinal))
            return false;
        if (blur is null && (!double.IsFinite(maximumRpm) || maximumRpm <= 0 || !double.IsFinite(currentBlur)))
            return false;

        Span<long> sources = stackalloc long[64];
        var sourceCount = angle.CopyCompositorSourceTimestamps(sources);
        if (sourceCount == 0 || !angle.TryProjectCompositorValue(freshUntil, out var lastPlayback, out _))
            return false;
        var end = lastPlayback >= sources[sourceCount - 1]
            ? angle.FindCompositorSourceCrossing(sources[sourceCount - 1], timestamp, freshUntil)
            : freshUntil;

        Span<long> crossings = stackalloc long[64];
        var crossingCount = 0;
        for (var index = 0; index < sourceCount; index++)
        {
            if (sources[index] <= startPlayback || sources[index] > lastPlayback) continue;
            crossings[crossingCount++] = angle.FindCompositorSourceCrossing(sources[index], timestamp, end);
        }

        destination[0] = new(0, blur is null ? NativeGaugeGeometry.AnalogNeedleAngle(startAngle, maximumRpm) : startAngle, startBlur);
        var count = 1;
        var crossingIndex = 0;
        var step = Math.Max(1, Stopwatch.Frequency / 1_000);
        var nextGrid = timestamp > long.MaxValue - step ? long.MaxValue : timestamp + step;
        var current = timestamp;
        while (current < end)
        {
            while (crossingIndex < crossingCount && crossings[crossingIndex] <= current) crossingIndex++;
            var next = Math.Min(end, nextGrid);
            if (crossingIndex < crossingCount) next = Math.Min(next, crossings[crossingIndex]);
            if (count == destination.Length ||
                !angle.TryProjectCompositorValue(next, out var angleClock, out var angleValue))
                return false;
            var blurValue = currentBlur;
            if (blur is not null &&
                (!blur.TryProjectCompositorValue(next, out var blurClock, out blurValue) || angleClock != blurClock))
                return false;
            if (blur is null) angleValue = NativeGaugeGeometry.AnalogNeedleAngle(angleValue, maximumRpm);
            destination[count++] = new((next - timestamp) / (double)Stopwatch.Frequency, angleValue, blurValue);
            current = next;
            if (current == nextGrid)
                nextGrid = nextGrid > long.MaxValue - step ? long.MaxValue : nextGrid + step;
        }

        curve = new(count, timestamp, end, freshUntil, latestObservation,
            angle.AcceptedCarOrdinal!.Value, angle.ReseedCount,
            (timestamp - startPlayback) * 1_000d / Stopwatch.Frequency,
            angle.PlaybackTargetDelayMilliseconds, NativeSource: blur is not null);
        return true;
    }
}
