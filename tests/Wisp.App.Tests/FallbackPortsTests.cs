using Xunit;

namespace Wisp.App.Tests;

public sealed class FallbackPortsTests
{
    [Fact]
    public void TheNextFivePortsAreTriedAndForzasReservedRangeIsSkipped()
    {
        Assert.Equal([5501, 5502, 5503, 5504, 5505], AppController.FallbackPorts(5500));
        Assert.Empty(AppController.FallbackPorts(5199));
        Assert.Equal([5301, 5302, 5303], AppController.FallbackPorts(5298));
        Assert.Equal([65534, 65535], AppController.FallbackPorts(65533));
        Assert.Empty(AppController.FallbackPorts(65535));
    }
}
