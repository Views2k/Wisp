using System.Diagnostics;

namespace Wisp.App;

// Use the native gauge's bounded receive-time playback without changing recorded values or peaks.
internal sealed class PowerTorqueNeedlePlayback
{
    private readonly NativeTachometerInterpolator _power = new(allowNegativeValues: true);
    private readonly NativeTachometerInterpolator _torque = new(allowNegativeValues: true);
    private PowerTorqueDisplay _display;
    private int _carOrdinal;
    private uint _gameTimestamp;
    private long _receivedTimestamp;
    private long _observedTimestamp;
    private long _pulseStart;
    private long _pulseEnd;
    private static readonly long PulsePeriodTicks = Math.Max(1, Stopwatch.Frequency * 8 / 10);
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
        UpdatePulse(display, carOrdinal, gameTimestamp, nowTimestamp, receivedTimestamp ?? nowTimestamp);
        _display = display;
        return display with
        {
            PowerBhp = _power.Observe(carOrdinal, gameTimestamp, display.PowerBhp, nowTimestamp, receivedTimestamp),
            TorqueNm = _torque.Observe(carOrdinal, gameTimestamp, display.TorqueNm, nowTimestamp, receivedTimestamp),
            DriftCutPulse = SamplePulse(nowTimestamp)
        };
    }

    internal PowerTorqueDisplay Sample(long timestamp) => _display.Available
        ? _display with
        {
            PowerBhp = _power.Sample(timestamp),
            TorqueNm = _torque.Sample(timestamp),
            DriftCutPulse = SamplePulse(timestamp)
        }
        : _display;

    private void UpdatePulse(PowerTorqueDisplay display, int carOrdinal, uint gameTimestamp,
        long nowTimestamp, long receivedTimestamp)
    {
        var gameElapsed = unchecked((int)(gameTimestamp - _gameTimestamp));
        var clockChanged = carOrdinal != _carOrdinal || nowTimestamp < _observedTimestamp ||
            gameElapsed < 0 || gameElapsed > PowerTorqueDriftHold.MaximumSampleGapMilliseconds ||
            (receivedTimestamp - _receivedTimestamp) * 1_000d / Stopwatch.Frequency >
            PowerTorqueDriftHold.MaximumSampleGapMilliseconds;
        if (clockChanged || !display.DriftPulseAllowed) _pulseStart = _pulseEnd = 0;

        // Complete one gentle cycle for a brief cut. Repeated packets and peak resets
        // cannot restart it, and rapid recovery/cut pairs share the same phase.
        var fresh = clockChanged || receivedTimestamp > _receivedTimestamp;
        if (fresh && display.IsDriftPowerCut && display.DriftPulseAllowed)
        {
            if (_pulseEnd <= nowTimestamp)
            {
                _pulseStart = nowTimestamp;
                _pulseEnd = nowTimestamp + PulsePeriodTicks;
            }
            else
            {
                var cycles = (nowTimestamp - _pulseStart) / PulsePeriodTicks + 1;
                _pulseEnd = _pulseStart + cycles * PulsePeriodTicks;
            }
        }
        if (fresh)
        {
            _carOrdinal = carOrdinal;
            _gameTimestamp = gameTimestamp;
            _receivedTimestamp = receivedTimestamp;
        }
        _observedTimestamp = nowTimestamp;
    }

    private double SamplePulse(long timestamp)
    {
        if (_pulseEnd == 0 || timestamp < _pulseStart || timestamp >= _pulseEnd) return 0;
        var phase = (timestamp - _pulseStart) % PulsePeriodTicks / (double)PulsePeriodTicks;
        return (1 - Math.Cos(phase * 2 * Math.PI)) / 2;
    }

    internal void Reset()
    {
        _power.Reset();
        _torque.Reset();
        _display = default;
        _carOrdinal = 0;
        _gameTimestamp = 0;
        _receivedTimestamp = 0;
        _observedTimestamp = 0;
        _pulseStart = _pulseEnd = 0;
    }
}
