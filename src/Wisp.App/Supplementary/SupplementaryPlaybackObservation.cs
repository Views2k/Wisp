namespace Wisp.App.Supplementary;

// Observes one selected clip's open/progress lifecycle; it never reads media or drives player state.
internal sealed class SupplementaryPlaybackObservation(Func<string, SupplementaryOperation?>? begin = null)
{
    private SupplementaryOperation? _decode, _playback;

    internal void Prepare()
    {
        Reset();
        _decode = Begin("decode");
        _playback = Begin("playback");
    }

    internal void Decoded() => _decode?.Complete("success");
    internal void Progressed() => _playback?.Complete("success");
    internal void Failed()
    {
        _decode?.Complete("failure");
        _playback?.Complete("failure");
    }

    internal void Reset()
    {
        _decode?.Dispose();
        _playback?.Dispose();
        _decode = _playback = null;
    }

    private SupplementaryOperation? Begin(string stage) => begin is not null
        ? begin(stage) : SupplementaryObservations.Begin("clips", "clips", stage);
}
