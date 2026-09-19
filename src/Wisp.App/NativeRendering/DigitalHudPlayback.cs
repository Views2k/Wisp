namespace Wisp.App.NativeRendering;

internal sealed class DigitalHudPlayback
{
    private readonly NativeTachometerInterpolator _rpm = new();
    private readonly NativeElectricPowerGaugeModel _power = new();
    private NativeGaugeFrame _frame;
    private NativeElectricPowerGaugeDisplay _powerBar;
    private bool _hasFrame;

    internal void Observe(NativeGaugeFrame frame, long timestamp) => ObserveQueued(frame, timestamp, timestamp);

    internal void ObserveQueued(NativeGaugeFrame frame, long publicationTimestamp, long consumptionTimestamp)
    {
        if (_hasFrame && _frame.IsElectric != frame.IsElectric) _rpm.Reset();
        _frame = frame;
        _hasFrame = true;
        if (!frame.IsElectric)
            _rpm.Observe(frame.CarOrdinal, frame.GameTimestampMilliseconds, frame.EngineRpm,
                consumptionTimestamp, frame.ReceivedTimestamp ?? publicationTimestamp);
        _powerBar = frame.IsElectric
            ? _power.Update(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio)
            : default;
    }

    internal DigitalHudSample Sample(long timestamp) => !_hasFrame ? default : new(
        _frame, _frame.IsElectric ? 0 : _rpm.Sample(timestamp),
        NativeElectricSpeedDisplaySelector.Resolve(_frame, timestamp), _powerBar, true);

    internal void Reset()
    {
        _rpm.Reset();
        _frame = default;
        _powerBar = default;
        _hasFrame = false;
    }
}

internal readonly record struct DigitalHudSample(NativeGaugeFrame Frame, double AppliedRpm,
    NativeElectricSpeedDisplay SpeedDisplay, NativeElectricPowerGaugeDisplay PowerBar, bool Available);
