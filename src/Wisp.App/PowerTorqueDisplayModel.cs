using System.Diagnostics;

namespace Wisp.App;

public readonly record struct PowerTorqueDisplay(
    bool Available,
    double PowerBhp,
    double TorqueNm,
    double PeakPowerBhp,
    double PeakTorqueNm)
{
    public static PowerTorqueDisplay Unavailable => default;
    public double? ReadoutPowerBhp { get; init; }
    public double? ReadoutTorqueNm { get; init; }

    public static double ConvertTorque(double newtonMeters, TorqueUnit unit) =>
        unit == TorqueUnit.PoundFeet ? newtonMeters * 0.7375621492772656 : newtonMeters;
}

public sealed class PowerTorqueDisplayModel
{
    internal const double WattsPerHorsepower = 745.69987158227022;
    private const int ReadoutIntervalMilliseconds = 100;
    private const int MaximumContinuousGapMilliseconds = 2_000;
    private double _smoothingMilliseconds = 250;
    private bool _showNegative;
    private int _carOrdinal;
    private uint _timestamp;
    private long? _receivedTimestamp;
    private double _readoutElapsedMilliseconds;
    private bool _hasSample;
    private double _lastRawHorsepower;
    private double _lastRawTorqueNm;

    public PowerTorqueDisplay Current { get; private set; }

    public double SmoothingMilliseconds
    {
        get => _smoothingMilliseconds;
        set => _smoothingMilliseconds = double.IsFinite(value) ? Math.Clamp(value, 0, 1_500) : 250;
    }

    public bool ShowNegative
    {
        get => _showNegative;
        set
        {
            if (_showNegative == value) return;
            _showNegative = value;
            if (!value)
            {
                Current = Current with
                {
                    PowerBhp = Math.Max(0, Current.PowerBhp),
                    TorqueNm = Math.Max(0, Current.TorqueNm),
                    ReadoutPowerBhp = Current.ReadoutPowerBhp is { } power ? Math.Max(0, power) : null,
                    ReadoutTorqueNm = Current.ReadoutTorqueNm is { } torque ? Math.Max(0, torque) : null
                };
            }
        }
    }

    public PowerTorqueDisplay Observe(int carOrdinal, uint timestamp, double powerWatts, double torqueNm,
        long? receivedTimestamp = null)
    {
        if (receivedTimestamp <= 0) receivedTimestamp = null;
        if (carOrdinal <= 0)
        {
            ResetCurrent();
            return Current;
        }

        if (carOrdinal != _carOrdinal)
        {
            Reset();
            _carOrdinal = carOrdinal;
        }

        if (!double.IsFinite(powerWatts) || !double.IsFinite(torqueNm))
        {
            ResetCurrent();
            return Current;
        }

        var gameElapsed = unchecked((int)(timestamp - _timestamp));
        var hasReceiveClock = receivedTimestamp.HasValue && _receivedTimestamp.HasValue;
        // Distinct FH6 packets can share one quantized game timestamp.
        var elapsed = hasReceiveClock
            ? (receivedTimestamp!.Value - _receivedTimestamp!.Value) * 1_000d / Stopwatch.Frequency
            : gameElapsed;
        if (_hasSample && (hasReceiveClock ? elapsed <= 0 : elapsed == 0))
        {
            return Current;
        }

        var horsepower = powerWatts / WattsPerHorsepower;
        _lastRawHorsepower = horsepower;
        _lastRawTorqueNm = torqueNm;
        if (!ShowNegative)
        {
            horsepower = Math.Max(0, horsepower);
            torqueNm = Math.Max(0, torqueNm);
        }
        var continuous = _hasSample && elapsed > 0 && elapsed <= MaximumContinuousGapMilliseconds &&
            gameElapsed >= 0 && gameElapsed <= MaximumContinuousGapMilliseconds;
        if (continuous && SmoothingMilliseconds > 0)
        {
            var blend = 1 - Math.Exp(-elapsed / SmoothingMilliseconds);
            // A convex blend remains finite even for opposite signed finite samples.
            horsepower = Current.PowerBhp * (1 - blend) + horsepower * blend;
            torqueNm = Current.TorqueNm * (1 - blend) + torqueNm * blend;
        }

        var readoutPower = Current.ReadoutPowerBhp;
        var readoutTorque = Current.ReadoutTorqueNm;
        _readoutElapsedMilliseconds = continuous ? _readoutElapsedMilliseconds + elapsed : 0;
        if (!continuous || readoutPower is null || readoutTorque is null ||
            _readoutElapsedMilliseconds >= ReadoutIntervalMilliseconds)
        {
            readoutPower = horsepower;
            readoutTorque = torqueNm;
            _readoutElapsedMilliseconds %= ReadoutIntervalMilliseconds;
        }

        _timestamp = timestamp;
        _receivedTimestamp = receivedTimestamp;
        _hasSample = true;
        Current = new PowerTorqueDisplay(true, horsepower, torqueNm,
            Math.Max(Current.PeakPowerBhp, Math.Max(0, _lastRawHorsepower)),
            Math.Max(Current.PeakTorqueNm, Math.Max(0, _lastRawTorqueNm)))
        {
            ReadoutPowerBhp = readoutPower,
            ReadoutTorqueNm = readoutTorque
        };
        return Current;
    }

    public void ResetPeaks() => Current = Current with
    {
        PeakPowerBhp = Current.Available ? Math.Max(0, _lastRawHorsepower) : 0,
        PeakTorqueNm = Current.Available ? Math.Max(0, _lastRawTorqueNm) : 0
    };

    public void ResetCurrent()
    {
        _hasSample = false;
        _receivedTimestamp = null;
        _readoutElapsedMilliseconds = 0;
        _lastRawHorsepower = 0;
        _lastRawTorqueNm = 0;
        Current = Current with
        {
            Available = false,
            PowerBhp = 0,
            TorqueNm = 0,
            ReadoutPowerBhp = null,
            ReadoutTorqueNm = null
        };
    }

    public void Reset()
    {
        _carOrdinal = 0;
        _timestamp = 0;
        _receivedTimestamp = null;
        _readoutElapsedMilliseconds = 0;
        _hasSample = false;
        _lastRawHorsepower = 0;
        _lastRawTorqueNm = 0;
        Current = PowerTorqueDisplay.Unavailable;
    }
}
