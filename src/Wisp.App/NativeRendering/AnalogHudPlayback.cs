using System.Diagnostics;

namespace Wisp.App.NativeRendering;

// Owned by the native render thread. The existing playback math is shared
// with WPF; changing the renderer must not introduce another needle filter.
internal sealed class AnalogHudPlayback
{
    private readonly NativeNeedlePlayback _native = new();
    private readonly NativeTachometerInterpolator _rpm = new();
    private NativeGaugeFrame _frame;
    private bool _hasFrame;
    private bool _usingNative;
    private double _previousAngle = double.NaN;
    private long _previousTimestamp;

    internal bool HasNativeNeedle(long timestamp) => _usingNative && _native.HasFreshState(timestamp);

    internal void Observe(NativeGaugeFrame frame, long timestamp) => ObserveQueued(frame, timestamp, timestamp);

    internal void ObserveQueued(NativeGaugeFrame frame, long publicationTimestamp, long consumptionTimestamp)
    {
        _frame = frame;
        _hasFrame = true;
        // A queued frame may predate the previous render sample without the
        // clock rewinding. Check freshness at consumption, retaining source time.
        var native = _native.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds,
            frame.NativeNeedleAngleDegrees, frame.NativeNeedleBlurAmount, consumptionTimestamp,
            frame.NativeGaugeObservedTimestamp > 0 ? frame.NativeGaugeObservedTimestamp : publicationTimestamp,
            frame.NativeGaugeSourceInvalidated, out _);
        if (native != _usingNative)
        {
            _usingNative = native;
            ResetBlur();
        }

        var previousCar = _rpm.AcceptedCarOrdinal;
        _rpm.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds, frame.EngineRpm,
            consumptionTimestamp, frame.ReceivedTimestamp ?? publicationTimestamp);
        if (previousCar is int previous && _rpm.AcceptedCarOrdinal is int current && previous != current)
            ResetBlur();
    }

    internal AnalogHudSample Sample(long timestamp)
    {
        if (!_hasFrame)
            return default;

        if (_usingNative && _native.Sample(timestamp, out var needle))
        {
            return new(_frame, needle.Angle, needle.Blur, true, true, timestamp, null,
                _native.PlaybackDelayMilliseconds(timestamp), _native.PlaybackTargetDelayMilliseconds,
                _native.BufferedSamples, _native.PlaybackAtNewest, _native.ReseedCount,
                _native.StarvationReseedCount);
        }

        var rpm = _rpm.Sample(timestamp);
        var visible = NativeGaugeGeometry.HasExactTachometerState(_frame.ExactRedline, _frame.TachometerMaximumRpm);
        var angle = NativeGaugeGeometry.AnalogNeedleAngle(rpm, _frame.TachometerMaximumRpm);
        var elapsed = _previousTimestamp == 0 ? 0 : (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
        var delta = double.IsFinite(_previousAngle) && visible && elapsed <= .25 ? angle - _previousAngle : 0;
        var blur = NativeGaugeGeometry.CombustionNeedleBlurRadians(delta, elapsed);
        _previousAngle = visible ? angle : double.NaN;
        _previousTimestamp = timestamp;
        return new(_frame, angle, blur, visible, false, timestamp, rpm,
            _rpm.PlaybackDelayMilliseconds(timestamp), _rpm.PlaybackTargetDelayMilliseconds,
            _rpm.BufferedSamples, _rpm.PlaybackAtNewest, _rpm.ReseedCount, _rpm.StarvationReseedCount);
    }

    internal void Reset()
    {
        _native.Reset();
        _rpm.Reset();
        _hasFrame = false;
        _usingNative = false;
        ResetBlur();
    }

    private void ResetBlur()
    {
        _previousAngle = double.NaN;
        _previousTimestamp = 0;
    }
}

internal readonly record struct AnalogHudSample(
    NativeGaugeFrame Frame, double Angle, double Blur, bool NeedleVisible, bool Native,
    long Timestamp, double? AppliedRpm, double PlaybackDelayMilliseconds,
    double PlaybackTargetDelayMilliseconds, int BufferedSamples, bool PlaybackAtNewest,
    long ReseedCount, long StarvationReseedCount);
