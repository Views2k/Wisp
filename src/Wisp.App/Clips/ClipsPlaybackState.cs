namespace Wisp.App.Clips;

internal sealed class ClipsPlaybackState
{
    private enum Phase { Closed, Preparing, Ready, Failed }
    private Phase _phase;
    private double? _lastObservedPosition;
    private bool _viewed;
    private string _failure = "";

    public long Revision { get; private set; }
    public bool Preparing => _phase == Phase.Preparing;
    public bool Ready => _phase == Phase.Ready;
    public bool Paused { get; private set; }
    public bool Ended { get; private set; }
    public bool Buffering { get; private set; }
    public double DurationSeconds { get; private set; }
    public string Status => _phase switch
    {
        Phase.Preparing => "Preparing clip…",
        Phase.Failed => _failure,
        Phase.Ready when Ended && Paused => "Clip finished.",
        Phase.Ready when Paused && Buffering => "Paused · buffering clip…",
        Phase.Ready when Paused => "Paused",
        Phase.Ready when Buffering => "Buffering clip…",
        _ => ""
    };

    public void Reset()
    {
        Revision++;
        _phase = Phase.Closed;
        Paused = Ended = Buffering = _viewed = false;
        DurationSeconds = 0;
        _lastObservedPosition = null;
        _failure = "";
    }

    public void Prepare() { Reset(); _phase = Phase.Preparing; }

    public bool TryOpen(long revision, bool hasVideo, int width, int height, double seconds)
    {
        if (revision != Revision || !Preparing || !hasVideo || width <= 0 || height <= 0 ||
            !double.IsFinite(seconds) || seconds <= 0) return false;
        DurationSeconds = seconds;
        _lastObservedPosition = 0;
        _phase = Phase.Ready;
        return true;
    }

    public bool OpeningTimedOut(TimeSpan elapsed) => Preparing && elapsed >= TimeSpan.FromSeconds(15);

    public void BufferingChanged(long revision, bool buffering)
    {
        if (revision == Revision && (Preparing || Ready)) Buffering = buffering;
    }

    public void SetPaused(bool paused)
    {
        if (!Ready) return;
        Paused = paused;
        if (!paused && Ended) { Ended = false; _lastObservedPosition = 0; }
    }

    public void ReachEnd(long revision)
    {
        if (revision != Revision || !Ready) return;
        Paused = Ended = true;
        Buffering = false;
    }

    public void SeekTo(double seconds)
    {
        if (!Ready || !double.IsFinite(seconds)) return;
        Ended = seconds >= DurationSeconds;
        // A seek jump is not evidence that playback advanced.
        _lastObservedPosition = null;
    }

    public bool ObservePosition(long revision, double seconds)
    {
        if (revision != Revision || !Ready || Paused || Buffering || _viewed ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > DurationSeconds) return false;
        var advanced = _lastObservedPosition is { } previous && seconds > previous;
        _lastObservedPosition = seconds;
        if (advanced) _viewed = true;
        return advanced;
    }

    public void Fail(string message)
    {
        Reset();
        _phase = Phase.Failed;
        _failure = message;
    }
}
