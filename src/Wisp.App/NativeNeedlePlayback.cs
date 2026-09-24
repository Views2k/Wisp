using System.Diagnostics;

namespace Wisp.App;

/// <summary>
/// Plays the native needle angle and blur as one receive-time sample pair.
/// Partial or invalid native state is never combined with a derived fallback.
/// </summary>
internal sealed class NativeNeedlePlayback
{
    private const double MaximumNativeBlurAmount = 0.65;
    internal const int NativeSampleFreshnessMilliseconds =
        NativeTachometerInterpolator.MaximumPlaybackDelayMilliseconds;
    private static readonly long NativeSampleFreshnessTicks =
        (long)Math.Round(Stopwatch.Frequency * NativeSampleFreshnessMilliseconds / 1_000d);
    private readonly NativeTachometerInterpolator _angle = new(minimumDelayMilliseconds: 20);
    private readonly NativeTachometerInterpolator _blur = new(allowNegativeValues: true, minimumDelayMilliseconds: 20);
    private long _lastExactObservationTimestamp;
    private bool _hasNativeState;

    public int? AcceptedCarOrdinal => _hasNativeState ? _angle.AcceptedCarOrdinal : null;

    internal int BufferedSamples => _angle.BufferedSamples;
    internal bool PlaybackAtNewest => _angle.PlaybackAtNewest;
    internal double PlaybackTargetDelayMilliseconds => _angle.PlaybackTargetDelayMilliseconds;
    internal double PlaybackDelayMilliseconds(long timestamp) => _angle.PlaybackDelayMilliseconds(timestamp);
    internal long ReseedCount => _angle.ReseedCount;
    internal long StarvationReseedCount => _angle.StarvationReseedCount;
    internal bool HasFreshState(long timestamp) => _hasNativeState && IsFresh(timestamp);

    internal bool TryCopyCompositorCurve(long nowTimestamp, Span<CompositorNeedlePoint> points,
        out CompositorNeedleCurve curve)
    {
        curve = default;
        if (!_hasNativeState || !IsFresh(nowTimestamp)) return false;
        var freshUntil = _lastExactObservationTimestamp > long.MaxValue - NativeSampleFreshnessTicks
            ? long.MaxValue : _lastExactObservationTimestamp + NativeSampleFreshnessTicks;
        return CompositorNeedleCurveBuilder.TryCopy(_angle, _blur, nowTimestamp,
            _lastExactObservationTimestamp, freshUntil, points, out curve);
    }

    public bool Observe(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double? nativeAngle,
        double? nativeBlur,
        long nowTimestamp,
        long? receivedTimestamp,
        bool sourceInvalidated,
        out NativeNeedleRenderState state) =>
        ObserveCore(carOrdinal, gameTimestampMilliseconds, nativeAngle, nativeBlur, nowTimestamp,
            receivedTimestamp, sourceInvalidated, deferSample: false, out state);

    internal bool ObserveQueued(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double? nativeAngle,
        double? nativeBlur,
        long nowTimestamp,
        long? receivedTimestamp,
        bool sourceInvalidated) =>
        ObserveCore(carOrdinal, gameTimestampMilliseconds, nativeAngle, nativeBlur, nowTimestamp,
            receivedTimestamp, sourceInvalidated, deferSample: true, out _);

    private bool ObserveCore(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double? nativeAngle,
        double? nativeBlur,
        long nowTimestamp,
        long? receivedTimestamp,
        bool sourceInvalidated,
        bool deferSample,
        out NativeNeedleRenderState state)
    {
        if (deferSample)
        {
            // Rejected pairs still observe the consumer clock, but cannot
            // advance playback or refresh the last exact native observation.
            _angle.RecordQueuedConsumptionClock(nowTimestamp);
            _blur.RecordQueuedConsumptionClock(nowTimestamp);
        }
        if (sourceInvalidated || carOrdinal <= 0)
        {
            Reset();
            state = default;
            return false;
        }

        var carChanged = _hasNativeState && AcceptedCarOrdinal != carOrdinal;
        var angleValue = nativeAngle.GetValueOrDefault();
        var blurValue = nativeBlur.GetValueOrDefault();
        var hasExactPair = nativeAngle.HasValue && double.IsFinite(angleValue) && angleValue >= 0 &&
                           nativeBlur.HasValue && double.IsFinite(blurValue) &&
                           Math.Abs(blurValue) <= MaximumNativeBlurAmount;
        if (carChanged)
        {
            Reset();
        }

        if (!hasExactPair)
        {
            if (_hasNativeState && IsFresh(nowTimestamp))
            {
                if (deferSample) { state = default; return true; }
                return Sample(nowTimestamp, out state);
            }

            Reset();
            state = default;
            return false;
        }

        var receivedAt = receivedTimestamp ?? nowTimestamp;
        // Native gauge fields can remain on otherwise fresh telemetry frames
        // while the process-memory worker is delayed. Once that exact pair has
        // expired, do not repeatedly resurrect it as a new playback source.
        // A newer native observation can still switch playback back normally.
        if (receivedAt > nowTimestamp || nowTimestamp - receivedAt > NativeSampleFreshnessTicks)
        {
            if (_hasNativeState && IsFresh(nowTimestamp))
            {
                if (deferSample) { state = default; return true; }
                return Sample(nowTimestamp, out state);
            }

            Reset();
            state = default;
            return false;
        }

        var acceptedObservation = receivedAt <= nowTimestamp &&
                                  (!_hasNativeState || receivedAt > _lastExactObservationTimestamp ||
                                   nowTimestamp < _lastExactObservationTimestamp);
        var angle = deferSample
            ? _angle.ObserveQueued(carOrdinal, gameTimestampMilliseconds, angleValue, nowTimestamp, receivedTimestamp)
            : _angle.Observe(carOrdinal, gameTimestampMilliseconds, angleValue, nowTimestamp, receivedTimestamp);
        var blur = deferSample
            ? _blur.ObserveQueued(carOrdinal, gameTimestampMilliseconds, blurValue, nowTimestamp, receivedTimestamp)
            : _blur.Observe(carOrdinal, gameTimestampMilliseconds, blurValue, nowTimestamp, receivedTimestamp);
        _hasNativeState = _angle.AcceptedCarOrdinal is not null &&
                          _angle.AcceptedCarOrdinal == _blur.AcceptedCarOrdinal;
        if (_hasNativeState && acceptedObservation)
        {
            _lastExactObservationTimestamp = receivedAt;
        }
        state = _hasNativeState ? new NativeNeedleRenderState(angle, blur) : default;
        return _hasNativeState;
    }

    public bool Sample(long nowTimestamp, out NativeNeedleRenderState state)
    {
        if (!_hasNativeState || !IsFresh(nowTimestamp))
        {
            Reset();
            state = default;
            return false;
        }

        state = new NativeNeedleRenderState(
            _angle.Sample(nowTimestamp),
            _blur.Sample(nowTimestamp));
        return true;
    }

    public void Reset()
    {
        _angle.Reset();
        _blur.Reset();
        _lastExactObservationTimestamp = 0;
        _hasNativeState = false;
    }

    private bool IsFresh(long nowTimestamp)
    {
        var age = nowTimestamp - _lastExactObservationTimestamp;
        return age >= 0 && age <= NativeSampleFreshnessTicks;
    }
}

internal readonly record struct NativeNeedleRenderState(double Angle, double Blur);
