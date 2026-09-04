using System.Globalization;
using Wisp.Core;

namespace Wisp.App;

internal sealed class DashboardPowerDisplayModel
{
    private const double WattsPerHorsepower = 745.69987158227022;
    private const double PoundFeetPerNewtonMeter = 0.7375621492772656;
    private const double SmoothingTimeConstantMilliseconds = 250d;
    private const int PublishIntervalMilliseconds = 125;
    private const int MaximumContinuousSampleGapMilliseconds = 2_000;

    private bool _initialized;
    private int _carOrdinal;
    private uint _lastSampleTimestamp;
    private uint _lastPublishedTimestamp;
    private double _smoothedHorsepower;
    private double _smoothedTorque;
    private int _displayHorsepower;
    private int _displayTorque;
    private int _peakCarOrdinal;
    private double _peakHorsepower;
    private double _peakTorqueNm;
    private bool _hasPeakSample;
    private double _peakSpeedMetersPerSecond;
    private bool _hasPeakSpeedSample;

    internal DashboardPowertrainDisplay Observe(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double powerWatts,
        double torqueNm,
        TorqueUnit torqueUnit = TorqueUnit.NewtonMeters,
        double? speedMetersPerSecond = null,
        SpeedUnit speedUnit = SpeedUnit.MilesPerHour)
    {
        if (carOrdinal <= 0)
        {
            ResetCurrent();
            return FormatUnavailable(torqueUnit, speedUnit);
        }

        if (_peakCarOrdinal != carOrdinal)
        {
            _peakCarOrdinal = carOrdinal;
            _peakHorsepower = 0;
            _peakTorqueNm = 0;
            _hasPeakSample = false;
            _peakSpeedMetersPerSecond = 0;
            _hasPeakSpeedSample = false;
        }

        UpdateTopSpeed(speedMetersPerSecond);
        if (!double.IsFinite(powerWatts) || !double.IsFinite(torqueNm))
        {
            ResetCurrent();
            return FormatUnavailable(torqueUnit, speedUnit);
        }

        var horsepower = powerWatts / WattsPerHorsepower;

        if (!_initialized || _carOrdinal != carOrdinal)
        {
            return Initialize(
                carOrdinal,
                gameTimestampMilliseconds,
                horsepower,
                torqueNm,
                torqueUnit,
                speedUnit);
        }

        var sampleElapsedMilliseconds =
            unchecked((int)(gameTimestampMilliseconds - _lastSampleTimestamp));
        if (sampleElapsedMilliseconds < 0 ||
            sampleElapsedMilliseconds > MaximumContinuousSampleGapMilliseconds)
        {
            return Initialize(
                carOrdinal,
                gameTimestampMilliseconds,
                horsepower,
                torqueNm,
                torqueUnit,
                speedUnit);
        }

        if (sampleElapsedMilliseconds > 0)
        {
            var alpha = 1d - Math.Exp(
                -sampleElapsedMilliseconds / SmoothingTimeConstantMilliseconds);
            _smoothedHorsepower +=
                (horsepower - _smoothedHorsepower) * alpha;
            _smoothedTorque +=
                (torqueNm - _smoothedTorque) * alpha;
            _lastSampleTimestamp = gameTimestampMilliseconds;
        }

        var publishElapsedMilliseconds =
            unchecked((int)(gameTimestampMilliseconds - _lastPublishedTimestamp));
        if (publishElapsedMilliseconds >= PublishIntervalMilliseconds)
        {
            _displayHorsepower = RoundValue(_smoothedHorsepower);
            _displayTorque = RoundValue(_smoothedTorque);
            _lastPublishedTimestamp = gameTimestampMilliseconds;
            UpdatePowerPeaks();
        }

        return Format(_displayHorsepower, _displayTorque, torqueUnit, speedUnit);
    }

    internal void Reset()
    {
        ResetCurrent();
        _peakCarOrdinal = 0;
        _peakHorsepower = 0;
        _peakTorqueNm = 0;
        _hasPeakSample = false;
        _peakSpeedMetersPerSecond = 0;
        _hasPeakSpeedSample = false;
    }

    internal void ResetCurrent()
    {
        _initialized = false;
        _carOrdinal = 0;
        _lastSampleTimestamp = 0;
        _lastPublishedTimestamp = 0;
        _smoothedHorsepower = 0;
        _smoothedTorque = 0;
        _displayHorsepower = 0;
        _displayTorque = 0;
    }

    internal void ResetPeaks()
    {
        _peakHorsepower = 0;
        _peakTorqueNm = 0;
        _hasPeakSample = false;
        _peakSpeedMetersPerSecond = 0;
        _hasPeakSpeedSample = false;
    }

    internal DashboardPowertrainDisplay Current(
        TorqueUnit torqueUnit,
        SpeedUnit speedUnit = SpeedUnit.MilesPerHour)
    {
        return _initialized
            ? Format(_displayHorsepower, _displayTorque, torqueUnit, speedUnit)
            : FormatUnavailable(torqueUnit, speedUnit);
    }

    private DashboardPowertrainDisplay Initialize(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double horsepower,
        double torqueNm,
        TorqueUnit torqueUnit,
        SpeedUnit speedUnit)
    {
        _initialized = true;
        _carOrdinal = carOrdinal;
        _lastSampleTimestamp = gameTimestampMilliseconds;
        _lastPublishedTimestamp = gameTimestampMilliseconds;
        _smoothedHorsepower = horsepower;
        _smoothedTorque = torqueNm;
        _displayHorsepower = RoundValue(horsepower);
        _displayTorque = RoundValue(torqueNm);
        UpdatePowerPeaks();
        return Format(_displayHorsepower, _displayTorque, torqueUnit, speedUnit);
    }

    private void UpdatePowerPeaks()
    {
        _peakHorsepower = Math.Max(_peakHorsepower, Math.Max(0, _smoothedHorsepower));
        _peakTorqueNm = Math.Max(_peakTorqueNm, Math.Max(0, _smoothedTorque));
        _hasPeakSample = true;
    }

    private void UpdateTopSpeed(double? speedMetersPerSecond)
    {
        if (speedMetersPerSecond is not { } speed || !double.IsFinite(speed))
        {
            return;
        }

        _peakSpeedMetersPerSecond = Math.Max(
            _peakSpeedMetersPerSecond,
            Math.Max(0, speed));
        _hasPeakSpeedSample = true;
    }

    private static int RoundValue(double value) =>
        (int)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue);

    private DashboardPowertrainDisplay Format(
        int horsepower,
        int torqueNm,
        TorqueUnit torqueUnit,
        SpeedUnit speedUnit) =>
        new(
            FormatHorsepower(horsepower),
            FormatTorque(torqueNm, torqueUnit),
            _hasPeakSample ? FormatPeakHorsepower(_peakHorsepower) : "—",
            _hasPeakSample ? FormatPeakTorque(_peakTorqueNm, torqueUnit) : "—",
            _hasPeakSpeedSample ? FormatTopSpeed(_peakSpeedMetersPerSecond, speedUnit) : "—");

    private DashboardPowertrainDisplay FormatUnavailable(
        TorqueUnit torqueUnit,
        SpeedUnit speedUnit) =>
        new(
            "—",
            "—",
            _hasPeakSample ? FormatPeakHorsepower(_peakHorsepower) : "—",
            _hasPeakSample ? FormatPeakTorque(_peakTorqueNm, torqueUnit) : "—",
            _hasPeakSpeedSample ? FormatTopSpeed(_peakSpeedMetersPerSecond, speedUnit) : "—");

    private static string FormatHorsepower(int horsepower) =>
        $"{horsepower.ToString("+0;-0;0", CultureInfo.InvariantCulture)} HP";

    private static string FormatPeakHorsepower(double horsepower) =>
        $"{RoundValue(horsepower).ToString(CultureInfo.InvariantCulture)} HP";

    private static string FormatTorque(int torqueNm, TorqueUnit torqueUnit)
    {
        var converted = torqueUnit == TorqueUnit.PoundFeet
            ? RoundValue(torqueNm * PoundFeetPerNewtonMeter)
            : torqueNm;
        var suffix = torqueUnit == TorqueUnit.PoundFeet ? "lb-ft" : "Nm";
        return $"{converted.ToString("+0;-0;0", CultureInfo.InvariantCulture)} {suffix}";
    }

    private static string FormatPeakTorque(double torqueNm, TorqueUnit torqueUnit)
    {
        var converted = torqueUnit == TorqueUnit.PoundFeet
            ? torqueNm * PoundFeetPerNewtonMeter
            : torqueNm;
        var suffix = torqueUnit == TorqueUnit.PoundFeet ? "lb-ft" : "Nm";
        return $"{RoundValue(converted).ToString(CultureInfo.InvariantCulture)} {suffix}";
    }

    private static string FormatTopSpeed(double speedMetersPerSecond, SpeedUnit speedUnit)
    {
        var multiplier = speedUnit == SpeedUnit.MilesPerHour
            ? SpeedModel.MetersPerSecondToMilesPerHour
            : SpeedModel.MetersPerSecondToKilometersPerHour;
        var speed = NativeGaugeGeometry.ClampSpeed(
            (int)Math.Floor(speedMetersPerSecond * multiplier));
        var suffix = speedUnit == SpeedUnit.MilesPerHour ? "MPH" : "KM/H";
        return $"{speed.ToString(CultureInfo.InvariantCulture)} {suffix}";
    }
}

internal readonly record struct DashboardPowertrainDisplay(
    string Power,
    string Torque,
    string PeakPower,
    string PeakTorque,
    string TopSpeed)
{
    internal static DashboardPowertrainDisplay Unavailable { get; } = new("—", "—", "—", "—", "—");
}
