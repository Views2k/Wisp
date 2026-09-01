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
    private readonly NativeTachometerInterpolator _angle = new();
    private readonly NativeTachometerInterpolator _blur = new(allowNegativeValues: true);
    private long _lastExactObservationTimestamp;
    private bool _hasNativeState;

    public int? AcceptedCarOrdinal => _hasNativeState ? _angle.AcceptedCarOrdinal : null;

    public bool Observe(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double? nativeAngle,
        double? nativeBlur,
        long nowTimestamp,
        long? receivedTimestamp,
        bool sourceInvalidated,
        out NativeNeedleRenderState state)
    {
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
                return Sample(nowTimestamp, out state);
            }

            Reset();
            state = default;
            return false;
        }

        var receivedAt = receivedTimestamp ?? nowTimestamp;
        var acceptedObservation = receivedAt <= nowTimestamp &&
                                  (!_hasNativeState || receivedAt > _lastExactObservationTimestamp ||
                                   nowTimestamp < _lastExactObservationTimestamp);
        var angle = _angle.Observe(
            carOrdinal,
            gameTimestampMilliseconds,
            angleValue,
            nowTimestamp,
            receivedTimestamp);
        var blur = _blur.Observe(
            carOrdinal,
            gameTimestampMilliseconds,
            blurValue,
            nowTimestamp,
            receivedTimestamp);
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
