namespace Wisp.App;

// Use the native gauge's bounded receive-time playback without changing recorded values or peaks.
internal sealed class PowerTorqueNeedlePlayback
{
    private readonly NativeTachometerInterpolator _power = new(allowNegativeValues: true);
    private readonly NativeTachometerInterpolator _torque = new(allowNegativeValues: true);
    private PowerTorqueDisplay _display;
    internal bool HasSamples => _display.Available;

    internal void UpdatePeaks(PowerTorqueDisplay display) => _display = _display with
    {
        PeakPowerBhp = display.PeakPowerBhp,
        PeakTorqueNm = display.PeakTorqueNm
    };

    internal PowerTorqueDisplay Observe(PowerTorqueDisplay display, int carOrdinal, uint gameTimestamp,
        long nowTimestamp, long? receivedTimestamp)
    {
        if (receivedTimestamp <= 0) receivedTimestamp = null;
        if (!display.Available)
        {
            Reset();
            return display;
        }
        _display = display;
        return display with
        {
            PowerBhp = _power.Observe(carOrdinal, gameTimestamp, display.PowerBhp, nowTimestamp, receivedTimestamp),
            TorqueNm = _torque.Observe(carOrdinal, gameTimestamp, display.TorqueNm, nowTimestamp, receivedTimestamp)
        };
    }

    internal PowerTorqueDisplay Sample(long timestamp) => _display.Available
        ? _display with { PowerBhp = _power.Sample(timestamp), TorqueNm = _torque.Sample(timestamp) }
        : _display;

    internal void Reset()
    {
        _power.Reset();
        _torque.Reset();
        _display = default;
    }
}
