namespace Wisp.Core;

public sealed record TransmissionDisplayOptions
{
    public TimeSpan MovingNeutralConfirmation { get; init; } = TimeSpan.FromMilliseconds(225);
    public double MovingSpeedMetersPerSecond { get; init; } = 1.0;
}

public sealed class TransmissionDisplayFilter
{
    private readonly TransmissionDisplayOptions _options;
    private TransmissionGear _displayedGear = TransmissionGear.Unknown;
    private DateTimeOffset? _neutralObservedAtUtc;
    private int _carOrdinal;

    public TransmissionDisplayFilter(TransmissionDisplayOptions? options = null)
    {
        _options = options ?? new TransmissionDisplayOptions();
    }

    public TransmissionGear Observe(VehicleState state)
    {
        if (state.CarOrdinal != _carOrdinal)
        {
            Reset();
            _carOrdinal = state.CarOrdinal;
        }

        if (state.Gear == TransmissionGear.Unknown)
        {
            return _displayedGear;
        }

        if (state.Gear != TransmissionGear.Neutral)
        {
            _neutralObservedAtUtc = null;
            _displayedGear = state.Gear;
            return _displayedGear;
        }

        var moving = Math.Abs(state.GroundSpeedMetersPerSecond) >= _options.MovingSpeedMetersPerSecond;
        var hasEngagedGear = _displayedGear is TransmissionGear.Reverse or
            >= TransmissionGear.First and <= TransmissionGear.Tenth;
        if (!moving || !hasEngagedGear)
        {
            _neutralObservedAtUtc = null;
            _displayedGear = TransmissionGear.Neutral;
            return _displayedGear;
        }

        _neutralObservedAtUtc ??= state.ReceivedAtUtc;
        if (state.ReceivedAtUtc - _neutralObservedAtUtc.Value >= _options.MovingNeutralConfirmation)
        {
            _displayedGear = TransmissionGear.Neutral;
        }

        return _displayedGear;
    }

    public void Reset()
    {
        _displayedGear = TransmissionGear.Unknown;
        _neutralObservedAtUtc = null;
        _carOrdinal = 0;
    }
}
