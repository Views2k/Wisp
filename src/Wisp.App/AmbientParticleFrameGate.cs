namespace Wisp.App;

internal sealed class AmbientParticleFrameGate
{
    private const double FrameIntervalTicks = TimeSpan.TicksPerSecond / (double)AmbientBackdrop.ParticleFramesPerSecond;
    private const double JitterToleranceTicks = TimeSpan.TicksPerMillisecond;
    private double? _nextFrameTimestamp;
    private long _lastTimestamp = -1;
    private long _lastDrawTimestamp = -1;

    internal void Reset()
    {
        _nextFrameTimestamp = null;
        _lastTimestamp = -1;
        _lastDrawTimestamp = -1;
    }

    internal bool ShouldDraw(TimeSpan renderingTime)
    {
        var ticks = renderingTime.Ticks;
        if (ticks < 0 || ticks <= _lastTimestamp)
            return false;
        _lastTimestamp = ticks;
        if (_nextFrameTimestamp is { } deadline)
        {
            // RenderingTime is an estimate. Small timing variations around a
            // 60 Hz boundary must not discard every other animation update.
            if (ticks + JitterToleranceTicks < deadline ||
                ticks - _lastDrawTimestamp < FrameIntervalTicks / 2)
                return false;

            // Carry the fractional interval at higher refresh rates, but discard
            // missed deadlines after a stall instead of bursting to catch up.
            _nextFrameTimestamp = ticks - deadline >= FrameIntervalTicks
                ? ticks + FrameIntervalTicks
                : deadline + FrameIntervalTicks;
        }
        else
        {
            _nextFrameTimestamp = ticks + FrameIntervalTicks;
        }
        _lastDrawTimestamp = ticks;
        return true;
    }
}
