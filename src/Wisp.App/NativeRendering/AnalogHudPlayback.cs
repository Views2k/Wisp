using System.Diagnostics;

namespace Wisp.App.NativeRendering;

// Owned by the native render thread. The existing playback math is shared
// with WPF; changing the renderer must not introduce another needle filter.
internal sealed class AnalogHudPlayback
{
    private static readonly long CompositorFallbackFreshnessTicks =
        (long)Math.Round(Stopwatch.Frequency * NativeNeedlePlayback.NativeSampleFreshnessMilliseconds / 1_000d);
    private readonly NativeNeedlePlayback _native = new();
    // Keep the live RPM needle on the same 20 ms minimum as native angles;
    // the shared interpolator still increases delay for measured arrival gaps.
    private readonly NativeTachometerInterpolator _rpm = new(minimumDelayMilliseconds: 20);
    private NativeGaugeFrame _frame;
    private bool _hasFrame;
    private bool _usingNative;
    private double _previousAngle = double.NaN;
    private long _previousTimestamp;
    private INativeNeedleHistorySource? _nativeSource;
    private NativeNeedleObservation[]? _nativeBatch;
    private NativeNeedleHistoryCursor _nativeCursor;
    private NativeNeedleObservation _latestNative;
    private bool _hasNativeObservation;
    private bool _sourceMatched;
    private ITelemetryNeedleHistorySource? _telemetrySource;
    private TelemetryNeedleObservation[]? _telemetryBatch;
    private NativeNeedleHistoryCursor _telemetryCursor;
    private TelemetryNeedleObservation _latestTelemetry;
    private bool _telemetryInitialized, _telemetryMatched, _hasTelemetryObservation;

    internal void SetNativeSource(INativeNeedleHistorySource? source)
    {
        _nativeSource = source;
        _nativeBatch = source is null ? null : new NativeNeedleObservation[NativeNeedleHistory.Capacity];
        _telemetrySource = source as ITelemetryNeedleHistorySource;
        _telemetryBatch = _telemetrySource is null ? null : new TelemetryNeedleObservation[TelemetryNeedleHistory.Capacity];
    }

    // Called after swapchain readiness, even when retrying previously drawn pixels.
    // The service supplies only accepted publications, without waiting for the UI.
    internal bool RefreshNativeHistory(long timestamp)
    {
        if (_nativeSource is null || !_hasFrame) return false;
        var read = _nativeSource.CopySince(_frame.CarOrdinal, _frame.NativeSourceIdentity,
            ref _nativeCursor, _nativeBatch!);
        // A publication may arrive while CopySince acquires the service lock.
        // Use its post-copy clock so that accepted sample is not lost as future.
        timestamp = Math.Max(timestamp, read.CopiedTimestamp);
        var invalidated = read.Reset && (read.MatchesSource || _sourceMatched);
        _sourceMatched = read.MatchesSource;
        if (read.Reset)
        {
            _native.Reset();
            _usingNative = false;
            _hasNativeObservation = false;
            ResetBlur();
        }
        for (var index = 0; index < read.Count; index++)
        {
            var observation = _nativeBatch![index];
            _latestNative = observation;
            _hasNativeObservation = true;
            invalidated |= observation.SourceInvalidated;
            var native = _native.ObserveQueued(observation.CarOrdinal, observation.GameTimestampMilliseconds,
                observation.Angle, observation.Blur, timestamp, observation.ObservedTimestamp,
                observation.SourceInvalidated);
            if (native != _usingNative) ResetBlur();
            _usingNative = native;
        }
        return RefreshTelemetryHistory(timestamp) || invalidated;
    }

    private bool RefreshTelemetryHistory(long timestamp)
    {
        if (_telemetrySource is null) return false;
        var read = _telemetrySource.CopyTelemetrySince(_frame.CarOrdinal, _frame.ReceivedTimestamp ?? 0,
            ref _telemetryCursor, _telemetryBatch!);
        if (!read.Initialized) return false;
        timestamp = Math.Max(timestamp, read.CopiedTimestamp);
        var reset = read.Reset || _telemetryMatched != read.MatchesFrame;
        _telemetryInitialized = true;
        _telemetryMatched = read.MatchesFrame;
        if (reset)
        {
            _rpm.Reset();
            _hasTelemetryObservation = false;
            ResetBlur();
        }
        for (var index = 0; index < read.Count; index++)
        {
            var observation = _telemetryBatch![index];
            _rpm.ObserveQueued(observation.CarOrdinal, observation.GameTimestampMilliseconds, observation.Rpm,
                timestamp, observation.ReceivedTimestamp);
            _latestTelemetry = observation;
            _hasTelemetryObservation = true;
        }
        return reset;
    }

    private bool TelemetryEligible => !_telemetryInitialized || _telemetryMatched;
    internal bool HasNativeNeedle(long timestamp) => TelemetryEligible && _usingNative && _native.HasFreshState(timestamp);

    internal NativeGaugeFrame CurrentFrame
    {
        get
        {
            var frame = _hasTelemetryObservation && _telemetryMatched ? _frame with
            {
                EngineRpm = _latestTelemetry.Rpm,
                GameTimestampMilliseconds = _latestTelemetry.GameTimestampMilliseconds,
                ReceivedTimestamp = _latestTelemetry.ReceivedTimestamp
            } : _frame;
            return _nativeSource is null ? frame : frame with
            {
                NativeNeedleAngleDegrees = _hasNativeObservation ? _latestNative.Angle : double.NaN,
                NativeNeedleBlurAmount = _hasNativeObservation ? _latestNative.Blur : double.NaN,
                NativeGaugeObservedTimestamp = _hasNativeObservation ? _latestNative.ObservedTimestamp : 0,
                NativeGaugeSourceInvalidated = !_sourceMatched || (_hasNativeObservation
                    ? _latestNative.SourceInvalidated : _frame.NativeGaugeSourceInvalidated)
            };
        }
    }

    internal void Observe(NativeGaugeFrame frame, long timestamp) => ObserveCore(frame, timestamp, timestamp, queued: false);

    internal void ObserveQueued(NativeGaugeFrame frame, long publicationTimestamp, long consumptionTimestamp) =>
        ObserveCore(frame, publicationTimestamp, consumptionTimestamp, queued: true);

    private void ObserveCore(NativeGaugeFrame frame, long publicationTimestamp, long consumptionTimestamp, bool queued)
    {
        _frame = frame;
        _hasFrame = true;
        // A queued frame may predate the previous render sample without the
        // clock rewinding. Check freshness at consumption, retaining source time.
        if (_nativeSource is null)
        {
            var nativeTimestamp = frame.NativeGaugeObservedTimestamp > 0 ? frame.NativeGaugeObservedTimestamp : publicationTimestamp;
            var native = queued
            ? _native.ObserveQueued(frame.CarOrdinal, frame.GameTimestampMilliseconds,
                frame.NativeNeedleAngleDegrees, frame.NativeNeedleBlurAmount, consumptionTimestamp,
                nativeTimestamp, frame.NativeGaugeSourceInvalidated)
            : _native.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds,
                frame.NativeNeedleAngleDegrees, frame.NativeNeedleBlurAmount, consumptionTimestamp,
                nativeTimestamp, frame.NativeGaugeSourceInvalidated, out _);
            if (native != _usingNative)
            {
                _usingNative = native;
                ResetBlur();
            }
        }

        // After the direct feed starts, rebinding an older UI frame must not
        // reseed it or retimestamp an accepted telemetry sample.
        if (_telemetryInitialized) return;
        var previousCar = _rpm.AcceptedCarOrdinal;
        if (queued)
            _rpm.ObserveQueued(frame.CarOrdinal, frame.GameTimestampMilliseconds, frame.EngineRpm,
                consumptionTimestamp, frame.ReceivedTimestamp ?? publicationTimestamp);
        else
            _rpm.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds, frame.EngineRpm,
                consumptionTimestamp, frame.ReceivedTimestamp ?? publicationTimestamp);
        if (previousCar is int previous && _rpm.AcceptedCarOrdinal is int current && previous != current)
            ResetBlur();
    }

    internal AnalogHudSample Sample(long timestamp)
    {
        if (!_hasFrame)
            return default;

        var frame = CurrentFrame;
        if (!TelemetryEligible)
            return new(frame, 0, 0, false, false, timestamp, null, 0, 0, 0, false, 0, 0);

        if (_usingNative && _native.Sample(timestamp, out var needle))
        {
            return new(frame, needle.Angle, needle.Blur, true, true, timestamp, null,
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
        return new(frame, angle, blur, visible, false, timestamp, rpm,
            _rpm.PlaybackDelayMilliseconds(timestamp), _rpm.PlaybackTargetDelayMilliseconds,
            _rpm.BufferedSamples, _rpm.PlaybackAtNewest, _rpm.ReseedCount, _rpm.StarvationReseedCount);
    }

    internal bool TryCopyCompositorCurve(long timestamp, Span<CompositorNeedlePoint> points,
        out CompositorNeedleCurve curve)
    {
        curve = default;
        if (!_hasFrame || _frame.IsElectric || !TelemetryEligible) return false;
        // Advance only the real consumer clock. The curve builder projects a
        // frozen copy; it must never Sample a future timestamp on live playback.
        if (_usingNative && _native.HasFreshState(timestamp))
        {
            if (!_native.Sample(timestamp, out _)) return false;
            return _native.TryCopyCompositorCurve(timestamp, points, out curve);
        }
        if (!NativeGaugeGeometry.HasExactTachometerState(_frame.ExactRedline, _frame.TachometerMaximumRpm) ||
            _rpm.AcceptedCarOrdinal != _frame.CarOrdinal) return false;
        var latestObservation = _rpm.LastAcceptedReceivedTimestamp;
        var freshUntil = latestObservation > long.MaxValue - CompositorFallbackFreshnessTicks
            ? long.MaxValue : latestObservation + CompositorFallbackFreshnessTicks;
        if (timestamp < latestObservation || timestamp >= freshUntil) return false;
        var sample = Sample(timestamp);
        return CompositorNeedleCurveBuilder.TryCopyRpm(_rpm, _frame.TachometerMaximumRpm, sample.Blur,
            timestamp, latestObservation, freshUntil, points, out curve);
    }

    internal void Reset()
    {
        _native.Reset();
        _rpm.Reset();
        _hasFrame = false;
        _usingNative = false;
        _nativeCursor = default;
        _hasNativeObservation = false;
        _sourceMatched = false;
        _telemetryCursor = default;
        _telemetryInitialized = _telemetryMatched = _hasTelemetryObservation = false;
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
