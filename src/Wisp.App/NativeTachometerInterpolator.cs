using System.Diagnostics;

namespace Wisp.App;

/// <summary>
/// Plays accepted RPM samples on a short, continuous receive-time timeline.
/// Packet delivery does not restart motion, and RPM is never extrapolated.
/// </summary>
internal sealed class NativeTachometerInterpolator
{
    private const uint MaximumContinuousGapMilliseconds = 250;
    private const int SampleCapacity = 64;
    private const int ArrivalWindowSize = 8;
    internal const int MaximumPlaybackDelayMilliseconds = 75;
    private static readonly double MinimumDelayTicks = Stopwatch.Frequency * 0.040;
    private static readonly double MaximumDelayTicks =
        Stopwatch.Frequency * MaximumPlaybackDelayMilliseconds / 1_000d;

    private readonly RpmSample[] _samples = new RpmSample[SampleCapacity];
    private readonly long[] _arrivalIntervals = new long[ArrivalWindowSize];
    private int _sampleHead;
    private int _sampleCount;
    private int _arrivalCount;
    private int _arrivalIndex;
    private int _carOrdinal;
    private uint _lastGameTimestampMilliseconds;
    private long _lastReceivedTimestamp;
    private long _lastObservationTimestamp;
    private long _lastSampleTimestamp;
    private double _playbackTimestamp;
    private double _playbackDelayTicks = MinimumDelayTicks;
    private double _displayedRpm;
    private bool _allowInitialDelayReposition;
    private readonly bool _allowNegativeValues;

    public NativeTachometerInterpolator(bool allowNegativeValues = false)
    {
        _allowNegativeValues = allowNegativeValues;
    }

    public int? AcceptedCarOrdinal => _sampleCount > 0 ? _carOrdinal : null;

    public double Observe(
        int carOrdinal,
        uint gameTimestampMilliseconds,
        double engineRpm,
        long nowTimestamp,
        long? receivedTimestamp = null)
    {
        var receivedAt = receivedTimestamp ?? nowTimestamp;
        if (receivedAt > nowTimestamp)
        {
            return Sample(nowTimestamp);
        }

        // FH6 game time is quantized to 15.625 ms in captured packets. Equal
        // game timestamps can carry different RPM; only receive identity is
        // a duplicate. An old/rebound frame must not refresh sample freshness.
        var clockRewound = nowTimestamp < Math.Max(_lastObservationTimestamp, _lastSampleTimestamp);
        if (_sampleCount > 0 && !clockRewound && receivedAt <= _lastReceivedTimestamp)
        {
            return Sample(nowTimestamp);
        }

        if (!double.IsFinite(engineRpm) || (!_allowNegativeValues && engineRpm < 0))
        {
            Reset();
            return 0;
        }

        if (_sampleCount == 0 || carOrdinal != _carOrdinal || clockRewound)
        {
            return Snap(carOrdinal, gameTimestampMilliseconds, engineRpm, receivedAt, nowTimestamp);
        }

        var receivedGap = receivedAt - _lastReceivedTimestamp;
        var gameGap = unchecked(gameTimestampMilliseconds - _lastGameTimestampMilliseconds);
        // A delivery gap longer than the entire buffer is missing history,
        // not a slower RPM transition. Resume from the fresh sample directly.
        if (receivedGap > MaximumDelayTicks || gameGap > MaximumContinuousGapMilliseconds)
        {
            return Snap(carOrdinal, gameTimestampMilliseconds, engineRpm, receivedAt, nowTimestamp,
                recoverFromStarvation: gameGap <= MaximumContinuousGapMilliseconds);
        }

        // Advance against the old history first so callback order at the same
        // compositor timestamp cannot change the displayed position.
        Sample(nowTimestamp);
        Append(new RpmSample(receivedAt, engineRpm));
        UpdatePlaybackDelay(receivedGap, nowTimestamp);
        _lastReceivedTimestamp = receivedAt;
        _lastObservationTimestamp = nowTimestamp;
        _lastGameTimestampMilliseconds = gameTimestampMilliseconds;
        return _displayedRpm;
    }

    public double Sample(long nowTimestamp)
    {
        if (_sampleCount == 0 || nowTimestamp <= _lastSampleTimestamp)
        {
            return _displayedRpm;
        }

        var elapsed = nowTimestamp - _lastSampleTimestamp;
        var playback = _playbackTimestamp + elapsed;
        var desiredPlayback = nowTimestamp - _playbackDelayTicks;

        // The normal 40 ms timeline is fixed. If delivery genuinely slows,
        // adjust its delay gradually without rewinding or jumping the needle.
        var blend = 1 - Math.Exp(-elapsed / (Stopwatch.Frequency * 0.100));
        var correction = (desiredPlayback - playback) * blend;
        playback += Math.Clamp(correction, -elapsed * 0.25, elapsed * 0.25);
        _playbackTimestamp = Math.Min(_lastReceivedTimestamp, Math.Max(_playbackTimestamp, playback));
        _lastSampleTimestamp = nowTimestamp;

        while (_sampleCount > 1 && At(1).Timestamp <= _playbackTimestamp)
        {
            _sampleHead = (_sampleHead + 1) % SampleCapacity;
            _sampleCount--;
        }

        var left = At(0);
        if (_sampleCount == 1 || _playbackTimestamp <= left.Timestamp)
        {
            return _displayedRpm = left.Rpm;
        }

        var right = At(1);
        var fraction = (_playbackTimestamp - left.Timestamp) / (right.Timestamp - left.Timestamp);
        return _displayedRpm = left.Rpm + (right.Rpm - left.Rpm) * fraction;
    }

    public void Reset()
    {
        _sampleHead = 0;
        _sampleCount = 0;
        _arrivalCount = 0;
        _arrivalIndex = 0;
        _carOrdinal = 0;
        _lastGameTimestampMilliseconds = 0;
        _lastReceivedTimestamp = 0;
        _lastObservationTimestamp = 0;
        _lastSampleTimestamp = 0;
        _playbackTimestamp = 0;
        _playbackDelayTicks = MinimumDelayTicks;
        _displayedRpm = 0;
        _allowInitialDelayReposition = false;
    }

    private double Snap(int carOrdinal, uint gameTimestampMilliseconds, double engineRpm,
        long receivedTimestamp, long nowTimestamp, bool recoverFromStarvation = false)
    {
        Reset();
        _carOrdinal = carOrdinal;
        _lastGameTimestampMilliseconds = gameTimestampMilliseconds;
        _lastReceivedTimestamp = receivedTimestamp;
        _lastObservationTimestamp = nowTimestamp;
        _lastSampleTimestamp = nowTimestamp;
        // A recovery snap already displays this sample. Starting behind it would
        // freeze the needle for another 40 ms despite new samples arriving.
        // The normal bounded phase correction rebuilds delay as data resumes.
        _playbackTimestamp = recoverFromStarvation ? receivedTimestamp : nowTimestamp - MinimumDelayTicks;
        _allowInitialDelayReposition = !recoverFromStarvation;
        _displayedRpm = engineRpm;
        Append(new RpmSample(receivedTimestamp, engineRpm));
        return engineRpm;
    }

    private void UpdatePlaybackDelay(long interval, long nowTimestamp)
    {
        _arrivalIntervals[_arrivalIndex] = interval;
        _arrivalIndex = (_arrivalIndex + 1) % ArrivalWindowSize;
        _arrivalCount = Math.Min(_arrivalCount + 1, ArrivalWindowSize);

        var longestInterval = 0L;
        for (var index = 0; index < _arrivalCount; index++)
        {
            longestInterval = Math.Max(longestInterval, _arrivalIntervals[index]);
        }

        _playbackDelayTicks = Math.Clamp(longestInterval * 1.25, MinimumDelayTicks, MaximumDelayTicks);
        if (_allowInitialDelayReposition && _arrivalCount == 1 && _playbackTimestamp <= At(0).Timestamp)
        {
            // Establish a slower stream's delay while still holding its first
            // sample. Subsequent cadence changes never reposition playback.
            _playbackTimestamp = nowTimestamp - _playbackDelayTicks;
        }
        _allowInitialDelayReposition = false;
    }

    private void Append(RpmSample sample)
    {
        if (_sampleCount == SampleCapacity)
        {
            _sampleHead = (_sampleHead + 1) % SampleCapacity;
            _sampleCount--;
        }

        _samples[(_sampleHead + _sampleCount) % SampleCapacity] = sample;
        _sampleCount++;
    }

    private RpmSample At(int index) => _samples[(_sampleHead + index) % SampleCapacity];
    private readonly record struct RpmSample(long Timestamp, double Rpm);
}
