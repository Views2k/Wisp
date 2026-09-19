using System.Diagnostics;

namespace Wisp.App.NativeRendering;

internal sealed class ElectricHudPlayback
{
    private readonly NativeNeedlePlayback _native = new();
    private readonly NativeTachometerInterpolator _speed = new();
    private readonly NativeElectricPowerGaugeModel _power = new();
    private NativeGaugeFrame _frame;
    private NativeElectricPowerGaugeDisplay _powerBar;
    private bool _hasFrame;
    private bool _usingNative;
    private double _previousAngle = double.NaN;
    private long _previousTimestamp;

    internal bool HasNativeNeedle(long timestamp) => _usingNative && _native.HasFreshState(timestamp);
    internal void Observe(NativeGaugeFrame frame, long timestamp) => ObserveQueued(frame, timestamp, timestamp);

    internal void ObserveQueued(NativeGaugeFrame frame, long publicationTimestamp, long consumptionTimestamp)
    {
        if (!frame.IsElectric) { Reset(); return; }
        _frame = frame;
        _hasFrame = true;
        var native = _native.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds,
            frame.NativeNeedleAngleDegrees, frame.NativeNeedleBlurAmount, consumptionTimestamp,
            frame.NativeGaugeObservedTimestamp > 0 ? frame.NativeGaugeObservedTimestamp : publicationTimestamp,
            frame.NativeGaugeSourceInvalidated, out _);
        if (native != _usingNative) { _usingNative = native; ResetBlur(); }
        var previousCar = _speed.AcceptedCarOrdinal;
        _speed.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds, frame.Speed,
            consumptionTimestamp, frame.ReceivedTimestamp ?? publicationTimestamp);
        if (previousCar is int previous && _speed.AcceptedCarOrdinal is int current && previous != current)
            ResetBlur();
        _powerBar = _power.Update(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio);
    }

    internal ElectricHudSample Sample(long timestamp)
    {
        if (!_hasFrame) return default;
        var digits = NativeElectricSpeedDisplaySelector.Resolve(_frame, timestamp);
        if (_usingNative && _native.Sample(timestamp, out var needle))
            return new(_frame, needle.Angle, needle.Blur, true, true, timestamp, digits, _powerBar, null,
                _native.PlaybackDelayMilliseconds(timestamp), _native.PlaybackTargetDelayMilliseconds,
                _native.BufferedSamples, _native.PlaybackAtNewest, _native.ReseedCount, _native.StarvationReseedCount);
        var speed = _speed.Sample(timestamp);
        var maximum = double.IsFinite(_frame.NativeElectricMaximumSpeed) && _frame.NativeElectricMaximumSpeed > 0
            ? _frame.NativeElectricMaximumSpeed : _frame.Unit == Wisp.Core.SpeedUnit.MilesPerHour ? 240d : 400d;
        var angle = NativeGaugeGeometry.ElectricAnalogNeedleAngle(speed, maximum);
        var elapsed = _previousTimestamp == 0 ? 0 : (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
        var delta = double.IsFinite(_previousAngle) && _frame.SpeedAvailable && elapsed <= .25 ? angle - _previousAngle : 0;
        var blur = NativeGaugeGeometry.AnalogNeedleBlurRadians(delta, elapsed);
        _previousAngle = _frame.SpeedAvailable ? angle : double.NaN;
        _previousTimestamp = timestamp;
        return new(_frame, angle, blur, _frame.SpeedAvailable, false, timestamp, digits, _powerBar, speed,
            _speed.PlaybackDelayMilliseconds(timestamp), _speed.PlaybackTargetDelayMilliseconds,
            _speed.BufferedSamples, _speed.PlaybackAtNewest, _speed.ReseedCount, _speed.StarvationReseedCount);
    }

    internal void Reset()
    {
        _native.Reset();
        _speed.Reset();
        _frame = default;
        _powerBar = default;
        _hasFrame = false;
        _usingNative = false;
        ResetBlur();
    }

    private void ResetBlur() { _previousAngle = double.NaN; _previousTimestamp = 0; }
}

internal readonly record struct ElectricHudSample(
    NativeGaugeFrame Frame, double Angle, double Blur, bool NeedleVisible, bool Native, long Timestamp,
    NativeElectricSpeedDisplay SpeedDisplay, NativeElectricPowerGaugeDisplay PowerBar, double? AppliedSpeed,
    double PlaybackDelayMilliseconds, double PlaybackTargetDelayMilliseconds, int BufferedSamples,
    bool PlaybackAtNewest, long ReseedCount, long StarvationReseedCount);
