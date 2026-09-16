using System.Globalization;
using System.Windows;
using Xunit;

namespace Wisp.App.Tests;

public sealed class GForceGaugeLayoutTests
{
    [Theory]
    [InlineData(.5, 72, 295, 166)]
    [InlineData(1, 72, 390, 166)]
    [InlineData(2, 200, 580, 298)]
    public void UniformScaleReservesEnoughSpaceAndKeepsTheNativeHorizontalCenter(
        double scale, double nativeTop, double combinedWidth, double combinedHeight)
    {
        Assert.Equal(nativeTop, GForceGaugeLayout.NativeTopPadding(scale));
        Assert.Equal(new Size(combinedWidth, combinedHeight), GForceGaugeLayout.CombinedSize(scale));
        foreach (var mode in new[] { NativeGaugeMode.Digital, NativeGaugeMode.Analogue })
        {
            var bounds = GForceGaugeLayout.NativeBounds(mode, scale);
            Assert.Equal(144 * scale, bounds.Width);
            Assert.Equal(100 * scale, bounds.Height);
            Assert.Equal(mode == NativeGaugeMode.Analogue ? 267 : 248, bounds.Left + bounds.Width / 2);
            Assert.True(bounds.Top >= 0);
            if (scale <= 1) Assert.Equal(50, bounds.Top + bounds.Height / 2);
            if (scale == 2) Assert.True(bounds.Bottom <= nativeTop);
        }
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void PreviewPositionsUseTheSameReservedSpaceAsTheOverlay(double scale)
    {
        var converter = new GForceGaugeLayoutConverter();
        var body = Assert.IsType<Thickness>(converter.Convert(scale, typeof(Thickness), "0,72,0,0", CultureInfo.InvariantCulture));
        var satellites = Assert.IsType<Thickness>(converter.Convert(scale, typeof(Thickness), "276,76,0,0", CultureInfo.InvariantCulture));
        var meter = Assert.IsType<Thickness>(converter.Convert(scale, typeof(Thickness), "195,0,0,0", CultureInfo.InvariantCulture));
        Assert.Equal(GForceGaugeLayout.NativeTopPadding(scale), body.Top);
        Assert.Equal(body.Top + 4, satellites.Top);
        Assert.Equal(GForceGaugeLayout.NativeBounds(NativeGaugeMode.Analogue, scale).TopLeft, new Point(meter.Left, meter.Top));
        Assert.Equal(GForceGaugeLayout.CombinedSize(scale).Width,
            converter.Convert(scale, typeof(double), "combined-width", CultureInfo.InvariantCulture));
    }
}
