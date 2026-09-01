using Xunit;

namespace Wisp.Update.Tests;

public sealed class VersionResourceTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("Wisp", "Wisp")]
    [InlineData("Wisp\0", "Wisp")]
    [InlineData("Wisp  \0 ", "Wisp")]
    [InlineData("1.2.3.0\0  ", "1.2.3.0")]
    public void RemovesOnlyTrailingWin32Padding(string? value, string? expected)
    {
        Assert.Equal(expected, VersionResourceText.Normalize(value));
    }

    [Theory]
    [InlineData(" Wisp")]
    [InlineData("Wisp\t")]
    [InlineData("Wisp\n")]
    [InlineData("Wi\0sp")]
    public void PreservesSemanticAndUnsupportedWhitespaceCharacters(string value)
    {
        Assert.Equal(value, VersionResourceText.Normalize(value));
    }
}
