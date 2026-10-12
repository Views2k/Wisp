namespace Wisp.App.Supplementary;

// Observes one selected clip's open/progress lifecycle; it never reads media or drives player state.
internal sealed class SupplementaryPlaybackObservation(Func<string, SupplementaryOperation?>? begin = null)
{
    private SupplementaryOperation? _decode, _playback;
    private bool _progressed, _failed;

    internal void Prepare()
    {
        Reset();
        _decode = Begin("decode");
        _playback = Begin("playback");
    }

    internal void Decoded() => _decode?.Complete("success");
    internal void Progressed() { _playback?.Complete("success"); _progressed = true; }
    internal void Failed(Exception? error = null, SupplementaryIncident? incident = null)
    {
        if (_failed) return;
        _failed = true;
        if (_progressed) _playback = Begin("playback");
        if (incident is not null) { _decode?.Complete("failure", incident); _playback?.Complete("failure", incident); }
        else if (error is not null) { _decode?.Fail(error); _playback?.Fail(error); }
        else
        {
            var evidence = SupplementaryIncidentSchema.Create("playback", "native-failure", "clips-playback");
            _decode?.Complete("failure", evidence); _playback?.Complete("failure", evidence);
        }
    }

    internal void Reset()
    {
        _decode?.Dispose();
        _playback?.Dispose();
        _decode = _playback = null;
        _progressed = _failed = false;
    }

    private SupplementaryOperation? Begin(string stage) => begin is not null
        ? begin(stage) : SupplementaryObservations.Begin("clips", "clips", stage);
}
