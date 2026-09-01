using Xunit;

namespace Wisp.Core.Tests;

public sealed class TransmissionDisplayFilterTests
{
    [Fact]
    public void SuppressesCapturedFastDownshiftNeutralGap()
    {
        var filter = new TransmissionDisplayFilter();
        var start = DateTimeOffset.UtcNow;

        Assert.Equal(TransmissionGear.Fourth, filter.Observe(State(TransmissionGear.Fourth, start, 42)));
        Assert.Equal(TransmissionGear.Fourth, filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(10), 42)));
        Assert.Equal(TransmissionGear.Fourth, filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(182), 41.8f)));
        Assert.Equal(TransmissionGear.Third, filter.Observe(State(TransmissionGear.Third, start.AddMilliseconds(190), 41.7f)));
    }

    [Fact]
    public void DeliberatelyHeldMovingNeutralAppearsAfterConfirmation()
    {
        var filter = new TransmissionDisplayFilter();
        var start = DateTimeOffset.UtcNow;

        _ = filter.Observe(State(TransmissionGear.Third, start, 20));
        Assert.Equal(TransmissionGear.Third, filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(10), 20)));
        Assert.Equal(TransmissionGear.Neutral, filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(240), 19)));
    }

    [Fact]
    public void StationaryNeutralIsImmediate()
    {
        var filter = new TransmissionDisplayFilter();
        var start = DateTimeOffset.UtcNow;

        _ = filter.Observe(State(TransmissionGear.First, start, 0));

        Assert.Equal(TransmissionGear.Neutral, filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(10), 0)));
    }

    [Fact]
    public void CarChangeCannotCarryAFormerCarsGear()
    {
        var filter = new TransmissionDisplayFilter();
        var start = DateTimeOffset.UtcNow;
        _ = filter.Observe(State(TransmissionGear.Sixth, start, 30, 100));

        Assert.Equal(
            TransmissionGear.Neutral,
            filter.Observe(State(TransmissionGear.Neutral, start.AddMilliseconds(10), 30, 200)));
    }

    private static VehicleState State(
        TransmissionGear gear,
        DateTimeOffset receivedAtUtc,
        float groundSpeed,
        int carOrdinal = 100) => TestVehicleState.Create() with
        {
            CarOrdinal = carOrdinal,
            Gear = gear,
            ReceivedAtUtc = receivedAtUtc,
            GroundSpeedMetersPerSecond = groundSpeed
        };
}
