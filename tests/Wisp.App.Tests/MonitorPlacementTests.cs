using Xunit;

namespace Wisp.App.Tests;

public sealed class MonitorPlacementTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY2-2560x1440-SpeedV4-Native-Analogue", @"\\.\DISPLAY2")]
    [InlineData(@"\\.\DISPLAY12-3840x2160-SpeedV4-Legacy", @"\\.\DISPLAY12")]
    [InlineData("Primary-1920x1080-SpeedV4-Legacy", "Primary")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("-1920x1080", null)]
    public void PlacementKeysNameTheMonitorTheyWereSavedOn(string? key, string? device) =>
        Assert.Equal(device, MonitorPlacement.DeviceOf(key));
}
