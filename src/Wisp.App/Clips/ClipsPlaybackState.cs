using Wisp.App.DebugLogging;

namespace Wisp.App.Clips;

internal sealed class ClipsPlaybackState
{
    private enum Phase { Closed, Preparing, Ready, Failed }
    private Phase _phase;
    private double? _lastObservedPosition;
    private bool _viewed;
    private bool _hasStarted;
    private string _failure = "";
    private HealthPlayerState _healthState;

    public long Revision { get; private set; }
    public bool Preparing => _phase == Phase.Preparing;
    public bool PreparingPlaybackCopy { get; private set; }
    public double? PreparationProgress { get; private set; }
    public bool Ready => _phase == Phase.Ready;
    public bool Paused { get; private set; }
    public bool Ended { get; private set; }
    public bool Buffering { get; private set; }
    public bool Loading => Preparing || Buffering;
    public bool WaitingForInitialBuffer => Ready && !_hasStarted && Buffering;
    public bool CanTogglePlayback => Ready && (!Paused || !Buffering);
    public bool CanSeek => Ready && !Loading;
    public double DurationSeconds { get; private set; }
    public string Status => _phase switch
    {
        Phase.Preparing when PreparingPlaybackCopy => PreparationProgress is { } progress
            ? $"Preparing HDR playback copy… {progress:0}%" : "Preparing HDR playback copy…",
        Phase.Preparing => "Preparing clip…",
        Phase.Failed => _failure,
        Phase.Ready when Buffering && !_hasStarted => "Loading clip…",
        Phase.Ready when Paused && Buffering => "Paused · buffering clip…",
        Phase.Ready when Ended && Paused => "Clip finished.",
        Phase.Ready when Paused && !_hasStarted => "Ready · press Play.",
        Phase.Ready when Paused => "Paused",
        Phase.Ready when Buffering => "Buffering clip…",
        _ => ""
    };

    public void Reset()
    {
        Revision++;
        _phase = Phase.Closed;
        Paused = Ended = Buffering = _viewed = _hasStarted = false;
        PreparingPlaybackCopy = false; PreparationProgress = null;
        DurationSeconds = 0;
        _lastObservedPosition = null;
        _failure = "";
        RecordHealthState();
    }

    public void Prepare() { Reset(); Paused = true; _phase = Phase.Preparing; RecordHealthState(); }

    public void CopyPreparationChanged(long revision, bool preparing, double? progress)
    {
        if (revision != Revision || !Preparing) return;
        PreparingPlaybackCopy = preparing;
        PreparationProgress = preparing && progress is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) : null;
    }

    public bool TryOpen(long revision, bool hasVideo, int width, int height, double seconds)
    {
        if (revision != Revision || !Preparing || PreparingPlaybackCopy || !hasVideo || width <= 0 || height <= 0 ||
            !double.IsFinite(seconds) || seconds <= 0) return false;
        DurationSeconds = seconds;
        _lastObservedPosition = 0;
        _phase = Phase.Ready;
        RecordHealthState();
        return true;
    }

    public bool OpeningTimedOut(TimeSpan elapsed) => !PreparingPlaybackCopy &&
        (Preparing || WaitingForInitialBuffer) && elapsed >= TimeSpan.FromSeconds(15);

    public void BufferingChanged(long revision, bool buffering)
    {
        if (revision == Revision && (Preparing || Ready)) { Buffering = buffering; RecordHealthState(); }
    }

    public void SetPaused(bool paused)
    {
        if (!Ready || !paused && Buffering) return;
        Paused = paused;
        if (!paused) _hasStarted = true;
        if (!paused && Ended) { Ended = false; _lastObservedPosition = 0; }
        RecordHealthState();
    }

    public void ReachEnd(long revision)
    {
        if (revision != Revision || !Ready) return;
        Paused = Ended = true;
        Buffering = false;
        RecordHealthState();
    }

    public void SeekTo(double seconds)
    {
        if (!Ready || !double.IsFinite(seconds)) return;
        Ended = seconds >= DurationSeconds;
        // A seek jump is not evidence that playback advanced.
        _lastObservedPosition = null;
        RecordHealthState();
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
        RecordHealthState();
    }

    private void RecordHealthState()
    {
        var state = _phase switch
        {
            Phase.Closed => HealthPlayerState.Closed,
            Phase.Preparing => HealthPlayerState.Preparing,
            Phase.Failed => HealthPlayerState.Failed,
            _ when Buffering => HealthPlayerState.Buffering,
            _ when Ended => HealthPlayerState.Ended,
            _ when Paused => HealthPlayerState.ReadyPaused,
            _ => HealthPlayerState.Playing
        };
        if (state == _healthState) return;
        _healthState = state;
        HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.PlayerChanged, playerState: state);
    }
}
