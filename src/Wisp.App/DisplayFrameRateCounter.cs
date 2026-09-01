namespace Wisp.App;

internal sealed class DisplayFrameRateCounter
{
    private TimeSpan _windowStartedAt;
    private TimeSpan _lastFrameAt;
    private int _framesInWindow;
    private bool _started;

    public double Rate { get; private set; }

    public double Observe(TimeSpan renderingTime)
    {
        if (!_started || renderingTime < _lastFrameAt)
        {
            StartWindow(renderingTime);
            return Rate;
        }

        if (renderingTime == _lastFrameAt)
        {
            return Rate;
        }

        _lastFrameAt = renderingTime;
        _framesInWindow++;
        var elapsed = renderingTime - _windowStartedAt;
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            Rate = _framesInWindow / elapsed.TotalSeconds;
            _windowStartedAt = renderingTime;
            _framesInWindow = 0;
        }

        return Rate;
    }

    public void Reset()
    {
        _windowStartedAt = default;
        _lastFrameAt = default;
        _framesInWindow = 0;
        _started = false;
        Rate = 0;
    }

    private void StartWindow(TimeSpan renderingTime)
    {
        _windowStartedAt = renderingTime;
        _lastFrameAt = renderingTime;
        _framesInWindow = 0;
        _started = true;
    }
}
