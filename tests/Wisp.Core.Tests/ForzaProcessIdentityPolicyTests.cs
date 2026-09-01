using Xunit;

namespace Wisp.Core.Tests;

public sealed class ForzaProcessIdentityPolicyTests
{
    [Theory]
    [InlineData("forzahorizon6", "")]
    [InlineData("Forza-Horizon-6", "")]
    [InlineData("GameHost", "Forza Horizon 6")]
    [InlineData("XGameHelper", "Forza Horizon 6")]
    public void RecognizesGameNameOrTitle(string processName, string windowTitle)
    {
        Assert.True(ForzaProcessIdentityPolicy.Matches(processName, windowTitle, null));
    }

    [Fact]
    public void RecognizesForegroundHelperInKnownGameDirectory()
    {
        Assert.True(ForzaProcessIdentityPolicy.Matches(
            "GameHost",
            string.Empty,
            @"X:\Games\ForzaHorizon6\GameHost.exe",
            [@"X:\Games\ForzaHorizon6"]));
    }

    [Theory]
    [InlineData("Discord", "Discord", @"X:\Apps\Discord\Discord.exe")]
    [InlineData("Discord", "Forza Horizon 6", @"X:\Games\ForzaHorizon6\Discord.exe")]
    [InlineData("chrome", "Forza Horizon 6", @"X:\Apps\Chrome\chrome.exe")]
    [InlineData("NotForzaHorizon6Helper", "", @"X:\Apps\Helper.exe")]
    [InlineData("XGameHelper", "", @"C:\Windows\System32\XGameHelper.exe")]
    [InlineData("XGameHelper", "Another Game", @"C:\Windows\System32\XGameHelper.exe")]
    [InlineData("GameHost", "", @"X:\Games\AnotherGame\GameHost.exe")]
    public void RejectsUnrelatedForegroundApplications(
        string processName,
        string windowTitle,
        string executablePath)
    {
        Assert.False(ForzaProcessIdentityPolicy.Matches(
            processName,
            windowTitle,
            executablePath,
            [@"X:\Games\ForzaHorizon6"]));
    }
}
