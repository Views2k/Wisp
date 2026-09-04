using System.Globalization;

namespace Wisp.App;

internal sealed class DashboardPowerDisplayModel
{
    private const double WattsPerHorsepower = 745.69987158227022;
    private const double SmoothingTimeConstantMilliseconds = 250d;
    private const int PublishIntervalMilliseconds = 125;
    private const int MaximumContinuousSampleGapMilliseconds = 2_000;

    private bool _initialized;
    private int _carOrdinal;
    private uint _lastSampleTimestamp;
    private uint _lastPublishedTimestamp;
    private double _smoothedHorsepower;
    private int _displayHorsepower;

    internal string Observe(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double powerWatts)
    {
        if (carOrdinal <= 0 || !double.IsFinite(powerWatts))
        {
            Reset();
            return "—";
        }

        var horsepower = powerWatts / WattsPerHorsepower;
        if (!_initialized || _carOrdinal != carOrdinal)
        {
            return Initialize(carOrdinal, gameTimestampMilliseconds, horsepower);
        }

        var sampleElapsedMilliseconds =
            unchecked((int)(gameTimestampMilliseconds - _lastSampleTimestamp));
        if (sampleElapsedMilliseconds < 0 ||
            sampleElapsedMilliseconds > MaximumContinuousSampleGapMilliseconds)
        {
            return Initialize(carOrdinal, gameTimestampMilliseconds, horsepower);
        }

        if (sampleElapsedMilliseconds > 0)
        {
            var alpha = 1d - Math.Exp(
                -sampleElapsedMilliseconds / SmoothingTimeConstantMilliseconds);
            _smoothedHorsepower +=
                (horsepower - _smoothedHorsepower) * alpha;
            _lastSampleTimestamp = gameTimestampMilliseconds;
        }

        var publishElapsedMilliseconds =
            unchecked((int)(gameTimestampMilliseconds - _lastPublishedTimestamp));
        if (publishElapsedMilliseconds >= PublishIntervalMilliseconds)
        {
            _displayHorsepower = RoundHorsepower(_smoothedHorsepower);
            _lastPublishedTimestamp = gameTimestampMilliseconds;
        }

        return Format(_displayHorsepower);
    }

    internal void Reset()
    {
        _initialized = false;
        _carOrdinal = 0;
        _lastSampleTimestamp = 0;
        _lastPublishedTimestamp = 0;
        _smoothedHorsepower = 0;
        _displayHorsepower = 0;
    }

    private string Initialize(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double horsepower)
    {
        _initialized = true;
        _carOrdinal = carOrdinal;
        _lastSampleTimestamp = gameTimestampMilliseconds;
        _lastPublishedTimestamp = gameTimestampMilliseconds;
        _smoothedHorsepower = horsepower;
        _displayHorsepower = RoundHorsepower(horsepower);
        return Format(_displayHorsepower);
    }

    private static int RoundHorsepower(double horsepower) =>
        (int)Math.Clamp(
            Math.Round(horsepower, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue);

    private static string Format(int horsepower) =>
        $"{horsepower.ToString("+0;-0;0", CultureInfo.InvariantCulture)} HP";
}
