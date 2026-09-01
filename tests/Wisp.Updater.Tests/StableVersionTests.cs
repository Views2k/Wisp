using Xunit;

namespace Wisp.Updater.Tests;

public sealed class StableVersionTests
{
    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.0", false)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("v1.2.3", false)]
    public void ParserAcceptsOnlyStrictStableThreePartVersions(string value, bool expected)
    {
        Assert.Equal(expected, StableVersion.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.0", true)]
    [InlineData("1.2.3.0\0  ", true)]
    [InlineData("1.2.3.1", false)]
    [InlineData("1.2.4.0", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData(" 1.2.3.0", false)]
    [InlineData("1.2.3.0\t", false)]
    [InlineData("1.2.3.0\n", false)]
    [InlineData("1.2.\0.3.0", false)]
    public void ResourceMatcherAllowsOnlyEquivalentReleaseVersion(string value, bool expected)
    {
        Assert.Equal(expected, new StableVersion(1, 2, 3).MatchesVersionResource(value));
    }
}
