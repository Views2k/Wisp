namespace Wisp.App;

internal sealed class AmbientParticleFrameGate
{
    private long? _origin;
    private long _lastBucket = -1;
    private long _lastTimestamp = -1;

    internal void Reset()
    {
        _origin = null;
        _lastBucket = -1;
        _lastTimestamp = -1;
    }

    internal bool ShouldDraw(TimeSpan renderingTime)
    {
        var ticks = renderingTime.Ticks;
        if (ticks < 0 || ticks <= _lastTimestamp)
            return false;
        _origin ??= ticks;
        _lastTimestamp = ticks;
        // Absolute buckets carry fractional refresh intervals across callbacks.
        // One TimeSpan tick of tolerance avoids rounding a 60 Hz boundary down.
        var bucket = (long)Math.Floor((ticks - _origin.Value + 1d) *
            AmbientBackdrop.ParticleFramesPerSecond / TimeSpan.TicksPerSecond);
        if (bucket <= _lastBucket)
            return false;
        _lastBucket = bucket;
        return true;
    }
}
