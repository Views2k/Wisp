using Wisp.Core;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class VehicleStateTests
{
    [Fact]
    public void LegacyConstructionDefaultsToUnknownNonElectricPowertrain()
    {
        var state = TestVehicleState.Create();

        Assert.Equal(-1, state.NumCylinders);
        Assert.Equal(0f, state.PowerWatts);
        Assert.Equal(0f, state.TorqueNm);
        Assert.False(state.IsElectric);
    }

    [Fact]
    public void ZeroCylinderStateExposesElectricSignal()
    {
        var state = TestVehicleState.Create() with
        {
            NumCylinders = 0,
            PowerWatts = -84_500f,
            TorqueNm = -310.25f
        };

        Assert.True(state.IsElectric);
        Assert.Equal(-84_500f, state.PowerWatts);
        Assert.Equal(-310.25f, state.TorqueNm);
    }
}
