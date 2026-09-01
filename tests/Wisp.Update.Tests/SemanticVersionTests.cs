using Xunit;

namespace Wisp.Update.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("65535.65535.65535", 65535, 65535, 65535)]
    public void StrictVersionAcceptsCanonicalCore(string value, int major, int minor, int patch)
    {
        var parsed = SemanticVersion.Parse(value);

        Assert.Equal(new SemanticVersion(major, minor, patch), parsed);
        Assert.Equal(value, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.0")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-alpha")]
    [InlineData("1.2.3+build")]
    [InlineData("1.2.3 ")]
    [InlineData("65536.0.0")]
    public void StrictVersionRejectsNonCanonicalInput(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));
    }

    [Theory]
    [InlineData("v0.0.0")]
    [InlineData("v1.2.3")]
    public void StrictTagRequiresLowercaseVAndCanonicalCore(string value)
    {
        var version = SemanticVersion.ParseTag(value);

        Assert.Equal(value, version.ToTagString());
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2.3-rc.1")]
    public void StrictTagRejectsOtherForms(string value)
    {
        Assert.False(SemanticVersion.TryParseTag(value, out _));
    }

    [Fact]
    public void SystemVersionRequiresAThreePartVersionAndZeroRevision()
    {
        Assert.Equal(new SemanticVersion(1, 2, 3), SemanticVersion.FromSystemVersion(new Version(1, 2, 3)));
        Assert.Equal(new SemanticVersion(1, 2, 3), SemanticVersion.FromSystemVersion(new Version(1, 2, 3, 0)));
        Assert.Throws<ArgumentException>(() => SemanticVersion.FromSystemVersion(new Version(1, 2)));
        Assert.Throws<ArgumentException>(() => SemanticVersion.FromSystemVersion(new Version(1, 2, 3, 4)));
    }

    [Fact]
    public void VersionsUseSemanticOrdering()
    {
        Assert.True(new SemanticVersion(2, 0, 0) > new SemanticVersion(1, 99, 99));
        Assert.True(new SemanticVersion(1, 3, 0) > new SemanticVersion(1, 2, 99));
        Assert.True(new SemanticVersion(1, 2, 4) > new SemanticVersion(1, 2, 3));
    }
}
