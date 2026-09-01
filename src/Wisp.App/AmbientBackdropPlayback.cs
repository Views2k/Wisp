namespace Wisp.App;

internal readonly record struct AmbientBackdropPlaybackState
{
    internal bool IsLoaded { get; init; }
    internal bool IsVisible { get; init; }
    internal bool HasHost { get; init; }
    internal bool HostIsVisible { get; init; }
    internal bool HostIsActive { get; init; }
    internal bool HostIsMinimized { get; init; }
    internal bool IsAnimationEnabled { get; init; }
    internal bool ClientAreaAnimation { get; init; }
    internal bool HighContrast { get; init; }
    internal int RenderingTier { get; init; }
    internal bool HasViewport { get; init; }
    internal bool IsDesignMode { get; init; }
    internal double Intensity { get; init; }

    internal bool CanAnimate =>
        IsLoaded && IsVisible && HasHost && HostIsVisible && HostIsActive && !HostIsMinimized &&
        IsAnimationEnabled && ClientAreaAnimation && !HighContrast && RenderingTier >= 2 &&
        HasViewport && !IsDesignMode && double.IsFinite(Intensity) && Intensity > 0;
}

internal sealed class AmbientBackdropClock
{
    internal const int FramesPerSecond = 24;
    internal const double MaximumStepSeconds = 0.1;
    private double? _lastTimestamp;

    internal bool IsRunning { get; private set; }
    internal double Seconds { get; private set; }
    internal double LastStepSeconds { get; private set; }

    internal void SetRunning(bool running, double timestamp)
    {
        if (running == IsRunning)
            return;
        IsRunning = running;
        LastStepSeconds = 0;
        _lastTimestamp = running && double.IsFinite(timestamp) ? timestamp : null;
    }

    internal bool Advance(double timestamp)
    {
        LastStepSeconds = 0;
        if (!IsRunning || !double.IsFinite(timestamp))
            return false;
        if (_lastTimestamp is not { } previous)
        {
            _lastTimestamp = timestamp;
            return false;
        }
        if (timestamp <= previous)
            return false;
        _lastTimestamp = timestamp;
        LastStepSeconds = Math.Min(timestamp - previous, MaximumStepSeconds);
        Seconds = AmbientBackdropScene.NormalizeTime(Seconds + LastStepSeconds);
        return true;
    }
}

internal sealed class AmbientBackdropPointer
{
    private const double FollowRate = 9.0;
    private const double LeaveRate = 3.8;
    private double _x = 0.5;
    private double _y = 0.5;
    private double _targetX = 0.5;
    private double _targetY = 0.5;
    private double _activity;
    private double _targetActivity;

    internal AmbientPoint Position => new(_x, _y);
    internal double Activity => _activity;

    internal void Move(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return;
        _targetX = Math.Clamp(x, 0, 1);
        _targetY = Math.Clamp(y, 0, 1);
        _targetActivity = 1;
    }

    internal void Leave() => _targetActivity = 0;

    internal void Reset()
    {
        _x = _targetX = 0.5;
        _y = _targetY = 0.5;
        _activity = _targetActivity = 0;
    }

    internal void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return;
        var follow = 1 - Math.Exp(-FollowRate * seconds);
        _x += (_targetX - _x) * follow;
        _y += (_targetY - _y) * follow;
        var activityRate = _targetActivity > _activity ? FollowRate : LeaveRate;
        var activityBlend = 1 - Math.Exp(-activityRate * seconds);
        _activity += (_targetActivity - _activity) * activityBlend;
        if (_targetActivity == 0 && _activity < 0.0001)
            _activity = 0;
    }
}
