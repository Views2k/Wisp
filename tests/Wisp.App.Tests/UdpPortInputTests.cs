using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class UdpPortInputTests
{
    [Theory]
    [InlineData("5500", 5500)]
    [InlineData(" 5500 ", 5500)]
    public void ParseAcceptsValidPort(string text, int expected)
    {
        Assert.Equal(expected, UdpPortInput.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    public void ParseRejectsEmptyOrNonnumericInput(string? text)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UdpPortInput.Parse(text));
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("65536")]
    public void ParseRejectsOutOfRangePort(string text)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UdpPortInput.Parse(text));
    }

    [Theory]
    [InlineData("5200")]
    [InlineData("5250")]
    [InlineData("5300")]
    public void ParseRejectsForzaReservedPort(string text)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UdpPortInput.Parse(text));
    }
}
