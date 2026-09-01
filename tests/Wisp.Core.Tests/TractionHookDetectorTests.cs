using Xunit;

namespace Wisp.Core.Tests;

public sealed class TractionHookDetectorTests
{
    [Fact]
    public void SignalsHookAfterWheelspinConvergesForThreePackets()
    {
        var detector = new TractionHookDetector();
        var spinning = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(180, 180, 180, 180),
            slipRatio: new WheelValues(0.7f, 0.7f, 0.7f, 0.7f));
        var hooked = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 100, 100),
            slipRatio: new WheelValues(0.02f, 0.02f, 0.02f, 0.02f));

        Assert.False(detector.Observe(spinning, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
        Assert.True(detector.Observe(hooked, 0.3));
    }

    [Fact]
    public void DoesNotSignalWithoutPriorSlip()
    {
        var detector = new TractionHookDetector();
        var hooked = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 100, 100));

        Assert.False(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
    }

    [Fact]
    public void SignalsOnlyOncePerSlipEvent()
    {
        var detector = new TractionHookDetector();
        var spinning = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(180, 180, 180, 180),
            slipRatio: new WheelValues(0.7f, 0.7f, 0.7f, 0.7f));
        var hooked = TestVehicleState.Create(
            groundSpeed: 30,
            wheelSpeed: new WheelValues(100, 100, 100, 100));

        detector.Observe(spinning, 0.3);
        detector.Observe(hooked, 0.3);
        detector.Observe(hooked, 0.3);
        Assert.True(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
        Assert.False(detector.Observe(hooked, 0.3));
    }

    [Fact]
    public void RequiresAUsableRadiusAndDrivingSpeed()
    {
        var detector = new TractionHookDetector();
        var spinning = TestVehicleState.Create(
            groundSpeed: 3,
            wheelSpeed: new WheelValues(180, 180, 180, 180),
            slipRatio: new WheelValues(0.7f, 0.7f, 0.7f, 0.7f));

        Assert.False(detector.Observe(spinning, null));
        Assert.False(detector.Observe(spinning, 0.3));
    }
}
