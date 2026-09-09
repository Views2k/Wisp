using System.Text;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunLineReaderTests
{
    [Theory]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    public async Task BoundarySizedLinesKeepFollowingLinesAndEof(int length)
    {
        var text = new string('x', length);
        using var input = Input(text + "\nsecond\r\n\nlast\r\r");
        var lines = new RunStore.BoundedLineReader(input);
        Assert.Equal(text, await lines.ReadLineAsync(8192));
        Assert.Equal("second", await lines.ReadLineAsync(8192));
        Assert.Equal("", await lines.ReadLineAsync(8192));
        Assert.Equal("last", await lines.ReadLineAsync(8192));
        Assert.Null(await lines.ReadLineAsync(8192));
    }

    [Fact]
    public async Task CrLfAcrossReadBoundaryAndUnicodeRemainIntact()
    {
        var first = new string('x', 4095);
        var second = new string('\u4E00', 8191);
        using var input = Input(first + "\r\n" + second + "\n");
        var lines = new RunStore.BoundedLineReader(input);
        Assert.Equal(first, await lines.ReadLineAsync(8192));
        Assert.Equal(second, await lines.ReadLineAsync(8192));
        Assert.Null(await lines.ReadLineAsync(8192));
    }

    [Theory]
    [InlineData(8192, "\r\n")]
    [InlineData(8193, "\n")]
    [InlineData(8193, "")]
    public async Task LineLimitIncludesCrAndAppliesBeforeEof(int length, string ending)
    {
        using var input = Input(new string('x', length) + ending);
        var lines = new RunStore.BoundedLineReader(input);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await lines.ReadLineAsync(8192));
    }

    [Fact]
    public async Task LargeHeaderDoesNotRelaxFollowingSampleLimit()
    {
        var header = new string('h', 131072);
        using var input = Input(header + "\n" + new string('s', 8193));
        var lines = new RunStore.BoundedLineReader(input);
        Assert.Equal(header, await lines.ReadLineAsync(131072));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await lines.ReadLineAsync(8192));
    }

    [Fact]
    public async Task HeaderAboveItsOwnLimitIsRejected()
    {
        using var input = Input(new string('h', 131073));
        var lines = new RunStore.BoundedLineReader(input);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await lines.ReadLineAsync(131072));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("\n", "")]
    [InlineData("\r", "")]
    public async Task EmptyAndCrOnlyEofMatchExistingFormat(string text, string? expected)
    {
        using var input = Input(text);
        var lines = new RunStore.BoundedLineReader(input);
        Assert.Equal(expected, await lines.ReadLineAsync(8192));
        Assert.Null(await lines.ReadLineAsync(8192));
    }

    private static StreamReader Input(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);
}
