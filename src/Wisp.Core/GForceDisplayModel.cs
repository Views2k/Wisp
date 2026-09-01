namespace Wisp.Core;

public readonly record struct GForceDisplay(
    double LateralG,
    double LongitudinalG,
    double FullScaleG,
    double NormalizedX,
    double NormalizedY,
    bool IsOverRange);

public sealed class GForceDisplayModel
{
    public const double StandardGravity = 9.80665;
    public const double MinimumFullScaleG = 1.0;
    public const double MaximumFullScaleG = 5.0;
    public const double ScaleHeadroomFactor = 1.15;
    public const double ScaleStepG = 0.25;
    public const double MaximumScaleReductionPerSecond = 0.5;
    public const double CenterDeadbandG = 0.035;
    public const double CenterFullResponseG = 0.075;
    public static readonly TimeSpan PeakWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeakBucketDuration = TimeSpan.FromMilliseconds(100);
    private const int PeakCompactionThreshold = 256;

    private readonly List<PeakSample> _windowPeaks = new(256);
    private int _peakHead;
    private DateTimeOffset? _currentBucketStartedAtUtc;
    private DateTimeOffset _currentBucketPeakAtUtc;
    private double _currentBucketPeakG;
    private double _fullScaleG = MinimumFullScaleG;
    private DateTimeOffset? _lastUpdateAtUtc;

    public GForceDisplay Calculate(
        double lateralMetersPerSecondSquared,
        double longitudinalMetersPerSecondSquared,
        DateTimeOffset nowUtc)
    {
        if (_lastUpdateAtUtc is { } lastUpdate && nowUtc < lastUpdate)
        {
            Reset();
        }

        var lateralG = double.IsFinite(lateralMetersPerSecondSquared)
            ? lateralMetersPerSecondSquared / StandardGravity
            : 0;
        var longitudinalG = double.IsFinite(longitudinalMetersPerSecondSquared)
            ? longitudinalMetersPerSecondSquared / StandardGravity
            : 0;
        var resultantG = Magnitude(lateralG, longitudinalG);
        var windowPeakG = ObservePeak(nowUtc, resultantG);
        var desiredScaleG = Math.Clamp(
            NiceStep(windowPeakG * ScaleHeadroomFactor),
            MinimumFullScaleG,
            MaximumFullScaleG);

        if (desiredScaleG >= _fullScaleG || _lastUpdateAtUtc is null)
        {
            _fullScaleG = desiredScaleG;
        }
        else
        {
            var elapsedSeconds = Math.Max(0, (nowUtc - _lastUpdateAtUtc.Value).TotalSeconds);
            _fullScaleG = Math.Max(
                desiredScaleG,
                _fullScaleG - (MaximumScaleReductionPerSecond * elapsedSeconds));
        }

        _lastUpdateAtUtc = nowUtc;
        var centerResponse = CenterResponse(resultantG);
        var normalizedX = (lateralG * centerResponse) / _fullScaleG;
        var normalizedY = (longitudinalG * centerResponse) / _fullScaleG;
        PinToUnitCircle(ref normalizedX, ref normalizedY);

        return new GForceDisplay(
            lateralG,
            longitudinalG,
            _fullScaleG,
            normalizedX,
            normalizedY,
            windowPeakG > MaximumFullScaleG);
    }

    public void Reset()
    {
        _windowPeaks.Clear();
        _peakHead = 0;
        _currentBucketStartedAtUtc = null;
        _currentBucketPeakAtUtc = default;
        _currentBucketPeakG = 0;
        _fullScaleG = MinimumFullScaleG;
        _lastUpdateAtUtc = null;
    }

    private double ObservePeak(DateTimeOffset nowUtc, double magnitudeG)
    {
        if (_currentBucketStartedAtUtc is not { } bucketStartedAtUtc)
        {
            _currentBucketStartedAtUtc = nowUtc;
            _currentBucketPeakAtUtc = nowUtc;
            _currentBucketPeakG = magnitudeG;
        }
        else if (nowUtc - bucketStartedAtUtc < PeakBucketDuration)
        {
            if (magnitudeG >= _currentBucketPeakG)
            {
                _currentBucketPeakG = magnitudeG;
                _currentBucketPeakAtUtc = nowUtc;
            }
        }
        else
        {
            AddFinalizedPeak(new PeakSample(_currentBucketPeakAtUtc, _currentBucketPeakG));
            _currentBucketStartedAtUtc = nowUtc;
            _currentBucketPeakAtUtc = nowUtc;
            _currentBucketPeakG = magnitudeG;
        }

        var cutoff = nowUtc - PeakWindow;
        while (_peakHead < _windowPeaks.Count && _windowPeaks[_peakHead].AtUtc < cutoff)
        {
            _peakHead++;
        }

        if (_peakHead > PeakCompactionThreshold && _peakHead * 2 > _windowPeaks.Count)
        {
            _windowPeaks.RemoveRange(0, _peakHead);
            _peakHead = 0;
        }

        var finalizedPeakG = _peakHead < _windowPeaks.Count
            ? _windowPeaks[_peakHead].MagnitudeG
            : 0;
        return Math.Max(finalizedPeakG, _currentBucketPeakG);
    }

    private void AddFinalizedPeak(PeakSample sample)
    {
        while (_windowPeaks.Count > _peakHead && _windowPeaks[^1].MagnitudeG <= sample.MagnitudeG)
        {
            _windowPeaks.RemoveAt(_windowPeaks.Count - 1);
        }

        _windowPeaks.Add(sample);
    }

    private static double NiceStep(double value) =>
        Math.Ceiling(value / ScaleStepG) * ScaleStepG;

    private static double CenterResponse(double magnitudeG)
    {
        if (magnitudeG <= CenterDeadbandG)
        {
            return 0;
        }

        if (magnitudeG >= CenterFullResponseG)
        {
            return 1;
        }

        var progress = (magnitudeG - CenterDeadbandG) /
                       (CenterFullResponseG - CenterDeadbandG);
        return progress * progress * (3 - (2 * progress));
    }

    private static double Magnitude(double x, double y)
    {
        var maximum = Math.Max(Math.Abs(x), Math.Abs(y));
        if (maximum == 0)
        {
            return 0;
        }

        var normalizedX = x / maximum;
        var normalizedY = y / maximum;
        return maximum * Math.Sqrt((normalizedX * normalizedX) + (normalizedY * normalizedY));
    }

    private static void PinToUnitCircle(ref double x, ref double y)
    {
        var maximum = Math.Max(Math.Abs(x), Math.Abs(y));
        if (maximum <= 1 && Magnitude(x, y) <= 1)
        {
            return;
        }

        var scaledX = x / maximum;
        var scaledY = y / maximum;
        var scaledMagnitude = Math.Sqrt((scaledX * scaledX) + (scaledY * scaledY));
        x = scaledX / scaledMagnitude;
        y = scaledY / scaledMagnitude;
    }

    private readonly record struct PeakSample(DateTimeOffset AtUtc, double MagnitudeG);
}
