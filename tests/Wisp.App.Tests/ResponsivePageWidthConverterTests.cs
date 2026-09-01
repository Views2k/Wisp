using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ResponsivePageWidthConverterTests
{
    private readonly ResponsivePageWidthConverter _converter = new();

    [Theory]
    [InlineData(0, 900)]
    [InlineData(719, 900)]
    [InlineData(900, 900)]
    [InlineData(1080, 1080)]
    [InlineData(1600, 1600)]
    [InlineData(2400, 2400)]
    [InlineData(3840, 3840)]
    public void PageWidthUsesFluidBounds(double viewportWidth, double expected) =>
        Assert.Equal(expected, Convert(viewportWidth));

    [Fact]
    public void InvalidMeasurementsFailToTheCompactDesignWidth()
    {
        Assert.Equal(900, Convert(double.NaN));
        Assert.Equal(900, Convert(double.PositiveInfinity));
        Assert.Equal(900, Convert("not-a-width"));
        Assert.Same(Binding.DoNothing,
            _converter.ConvertBack(1200d, typeof(double), null!, CultureInfo.InvariantCulture));
    }

    private double Convert(object value) => Assert.IsType<double>(
        _converter.Convert(value, typeof(double), null!, CultureInfo.InvariantCulture));
}
