namespace Wisp.App;

public readonly record struct PowerTorqueDisplay(
    bool Available,
    double PowerBhp,
    double TorqueNm,
    double PeakPowerBhp,
    double PeakTorqueNm)
{
    public static PowerTorqueDisplay Unavailable => default;

    public static double ConvertTorque(double newtonMeters, TorqueUnit unit) =>
        unit == TorqueUnit.PoundFeet ? newtonMeters * 0.7375621492772656 : newtonMeters;
}

public sealed class PowerTorqueDisplayModel
{
    internal const double WattsPerHorsepower = 745.69987158227022;
    private const double SmoothingMilliseconds = 150;
    private const int MaximumContinuousGapMilliseconds = 2_000;
    private int _carOrdinal;
    private uint _timestamp;
    private bool _hasSample;
    private double _lastRawHorsepower;
    private double _lastRawTorqueNm;

    public PowerTorqueDisplay Current { get; private set; }

    public PowerTorqueDisplay Observe(int carOrdinal, uint timestamp, double powerWatts, double torqueNm)
    {
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

        var elapsed = unchecked((int)(timestamp - _timestamp));
        if (_hasSample && elapsed == 0)
        {
            return Current;
        }

        var horsepower = powerWatts / WattsPerHorsepower;
        _lastRawHorsepower = horsepower;
        _lastRawTorqueNm = torqueNm;
        if (_hasSample && elapsed > 0 && elapsed <= MaximumContinuousGapMilliseconds)
        {
            var blend = 1 - Math.Exp(-elapsed / SmoothingMilliseconds);
            // A convex blend remains finite even for opposite signed finite samples.
            horsepower = Current.PowerBhp * (1 - blend) + horsepower * blend;
            torqueNm = Current.TorqueNm * (1 - blend) + torqueNm * blend;
        }

        _timestamp = timestamp;
        _hasSample = true;
        Current = new PowerTorqueDisplay(true, horsepower, torqueNm,
            Math.Max(Current.PeakPowerBhp, Math.Max(0, _lastRawHorsepower)),
            Math.Max(Current.PeakTorqueNm, Math.Max(0, _lastRawTorqueNm)));
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
        _lastRawHorsepower = 0;
        _lastRawTorqueNm = 0;
        Current = Current with { Available = false, PowerBhp = 0, TorqueNm = 0 };
    }

    public void Reset()
    {
        _carOrdinal = 0;
        _timestamp = 0;
        _hasSample = false;
        _lastRawHorsepower = 0;
        _lastRawTorqueNm = 0;
        Current = PowerTorqueDisplay.Unavailable;
    }
}
