using System.Windows;
using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PowerTorqueGaugeLayoutTests
{
    [Fact]
    public void DefaultAnalogGridAlignsEveryRowAndColumn()
    {
        var layout = AnalogSupplementaryGaugeLayout.Calculate(new Size(416, 365.5), 76,
            true, true, true, true, 1, 1, 1, 1);
        Assert.Equal(new Rect(276, 76, 136, 136), layout.BoostBounds);
        Assert.Equal(new Rect(276, 214, 136, 136), layout.TireBounds);
        Assert.Equal(new Rect(410, 76, 136, 136), layout.PowerBounds);
        Assert.Equal(new Rect(410, 214, 136, 136), layout.TorqueBounds);
    }

    [Theory]
    [InlineData(.5, 1, 2, 1.25)]
    [InlineData(2, .5, 1, 2)]
    [InlineData(1.25, 2, .5, 1)]
    [InlineData(.5, .5, .5, .5)]
    public void IndependentSizesKeepDialCentersAlignedAndRimsSeparated(
        double boostScale, double tireScale, double powerScale, double torqueScale)
    {
        var layout = AnalogSupplementaryGaugeLayout.Calculate(new Size(416, 365.5), 76,
            true, true, true, true, boostScale, tireScale, powerScale, torqueScale);
        var boost = layout.BoostBounds;
        var tire = layout.TireBounds;
        var power = layout.PowerBounds;
        var torque = layout.TorqueBounds;
        Assert.Equal(boost.Left + boost.Width / 2, tire.Left + tire.Width / 2);
        Assert.Equal(power.Left + power.Width / 2, torque.Left + torque.Width / 2);
        Assert.Equal(boost.Top + boost.Height / 2, power.Top + power.Height / 2);
        Assert.Equal(tire.Top + tire.Height / 2, torque.Top + torque.Height / 2);
        var bounds = new[] { boost, tire, power, torque };
        var scales = new[] { boostScale, tireScale, powerScale, torqueScale };
        for (var index = 0; index < bounds.Length; index++)
        {
            Assert.Equal(136 * scales[index], bounds[index].Width);
            Assert.True(new Rect(layout.Size).Contains(bounds[index]));
            for (var other = index + 1; other < bounds.Length; other++)
                Assert.False(DialInk(bounds[index], scales[index]).IntersectsWith(DialInk(bounds[other], scales[other])));
        }
    }

    [Fact]
    public void TorqueAndPowerScalesDoNotResizeEachOther()
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76, true, true, 2, .5);
        Assert.Equal(272, layout.PowerBounds.Width);
        Assert.Equal(68, layout.TorqueBounds.Width);
        Assert.Equal(layout.PowerBounds.Left + 136, layout.TorqueBounds.Left + 34);
        Assert.True(new Rect(layout.Size).Contains(layout.TorqueBounds));
    }

    [Fact]
    public void DisabledGaugesLeaveExistingBoundsUnchanged()
    {
        var size = new Size(416, 365.5);
        var layout = PowerTorqueGaugeLayout.Calculate(size, 76, false, false, 2);
        Assert.Equal(size, layout.Size);
        Assert.True(layout.PowerBounds.IsEmpty);
        Assert.True(layout.TorqueBounds.IsEmpty);
    }

    [Theory]
    [InlineData(416, 365.5, 76, 0.5)]
    [InlineData(416, 365.5, 76, 1)]
    [InlineData(416, 365.5, 76, 2)]
    [InlineData(327.5, 232, 76, 2)]
    [InlineData(345, 417, 76, 1)]
    [InlineData(160, 120, 4, 1)]
    [InlineData(390, 166, 4, 1)]
    public void AttachedPairStaysOutsideExistingArtworkAndInsideWindow(
        double width, double height, double top, double scale)
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(width, height), top, true, true, scale);
        var window = new Rect(layout.Size);
        Assert.True(layout.PowerBounds.Left > width);
        Assert.True(layout.TorqueBounds.Top > layout.PowerBounds.Bottom);
        Assert.True(window.Contains(layout.PowerBounds));
        Assert.True(window.Contains(layout.TorqueBounds));
        Assert.Equal(top, layout.PowerBounds.Top);
        Assert.Equal(136 * scale, layout.PowerBounds.Width);
        Assert.Equal(layout.PowerBounds.Width, layout.PowerBounds.Height);
    }

    [Fact]
    public void TorqueAloneUsesTheTopSlot()
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76, false, true, 1);
        Assert.True(layout.PowerBounds.IsEmpty);
        Assert.Equal(76, layout.TorqueBounds.Top);
        Assert.Equal(365.5, layout.Size.Height);
    }

    [Theory]
    [InlineData(double.NaN, 136)]
    [InlineData(double.PositiveInfinity, 136)]
    [InlineData(0, 68)]
    [InlineData(5, 272)]
    public void InvalidScaleCannotBreakLayout(double scale, double diameter)
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76, true, false, scale);
        Assert.Equal(diameter, layout.PowerBounds.Width);
        Assert.Equal(diameter, layout.PowerBounds.Height);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void CompactNativeColumnKeepsDialRimsAndGlowSeparated(double scale)
    {
        var layout = PowerTorqueGaugeLayout.Calculate(new Size(416, 365.5), 76,
            true, true, scale, besideNativeAnalogSatellites: true);
        var boost = new Rect(276, 76, 136, 136);
        var tire = new Rect(276, 214, 136, 136);
        var powerInk = DialInk(layout.PowerBounds, scale);
        var torqueInk = DialInk(layout.TorqueBounds, scale);
        Assert.Equal(410, layout.PowerBounds.Left);
        Assert.False(DialInk(boost, 1).IntersectsWith(powerInk));
        Assert.False(DialInk(tire, 1).IntersectsWith(torqueInk));
        Assert.False(powerInk.IntersectsWith(torqueInk));
        Assert.True(new Rect(layout.Size).Contains(layout.PowerBounds));
        Assert.True(new Rect(layout.Size).Contains(layout.TorqueBounds));
        if (scale == 1)
        {
            Assert.Equal(boost.Top, layout.PowerBounds.Top);
            Assert.Equal(tire.Top, layout.TorqueBounds.Top);
        }
    }

    [Fact]
    public void FourAuthoredSupplementaryDialsUseTheSameDefaultSize()
    {
        var path = AppSourceDirectory();
        var document = XDocument.Load(Path.Combine(path, "OverlayWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "AttachedAnalogBoost", "AttachedAnalogTireTemperature", "AttachedPowerGauge", "AttachedTorqueGauge" })
        {
            var gauge = document.Descendants().Single(node => (string?)node.Attribute(xaml + "Name") == name);
            Assert.Equal(PowerTorqueGaugeLayout.GaugeDiameter,
                double.Parse(gauge.Attribute("Width")!.Value, CultureInfo.InvariantCulture));
            Assert.Equal(gauge.Attribute("Width")!.Value, gauge.Attribute("Height")!.Value);
        }
        XNamespace local = "clr-namespace:Wisp.App";
        var boost = XDocument.Load(Path.Combine(path, "BoostGaugeWindow.xaml"))
            .Descendants(local + "AnalogBoostGaugeView").Single();
        var tire = XDocument.Load(Path.Combine(path, "TireTemperatureGaugeWindow.xaml"))
            .Descendants(local + "AnalogTireTemperatureGaugeView").Single();
        Assert.Equal("136", boost.Attribute("Width")?.Value);
        Assert.Equal("136", tire.Attribute("Width")?.Value);
    }

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1366, 768, 1)]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, .5)]
    public void DefaultDetachedSlotsDoNotOverlapEachOtherOrTheHud(int width, int height, double scale)
    {
        var area = new Rect(0, 0, width, height);
        var anchor = new Rect(width - 390, height - 325, 320, 270);
        var cell = new Size(144 * scale, 144 * scale);
        var bounds = Enumerable.Range(0, 4).Select(slot => new Rect(
            DetachedSupplementaryGaugeLayout.Place(area, anchor, cell, cell, slot), cell)).ToArray();
        foreach (var rectangle in bounds)
        {
            Assert.True(area.Contains(rectangle));
            Assert.False(rectangle.IntersectsWith(anchor));
        }
        for (var a = 0; a < bounds.Length; a++)
            for (var b = a + 1; b < bounds.Length; b++) Assert.False(bounds[a].IntersectsWith(bounds[b]));
    }

    [Fact]
    public void DefaultDetachedSlotsReserveTheLargestConfiguredGauge()
    {
        var area = new Rect(0, 0, 1920, 1080);
        var anchor = new Rect(1530, 760, 320, 270);
        var subjects = new[] { new Size(288, 288), new Size(144, 144), new Size(72, 72), new Size(72, 72) };
        var bounds = subjects.Select((size, slot) => new Rect(
            DetachedSupplementaryGaugeLayout.Place(area, anchor, size, new Size(288, 288), slot), size)).ToArray();
        for (var a = 0; a < bounds.Length; a++)
            for (var b = a + 1; b < bounds.Length; b++) Assert.False(bounds[a].IntersectsWith(bounds[b]));
        Assert.Equal(OverlayPlacementGeometry.PlaceAbove(area, anchor, subjects[0]), bounds[0].TopLeft);
    }

    private static Rect DialInk(Rect bounds, double scale)
    {
        var inset = bounds.Width * .07 - 4 * scale;
        return new Rect(bounds.Left + inset, bounds.Top + inset,
            bounds.Width - inset * 2, bounds.Height - inset * 2);
    }

    private static string AppSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "Wisp.App");
            if (File.Exists(Path.Combine(path, "OverlayWindow.xaml"))) return path;
        }
        throw new DirectoryNotFoundException("Wisp.App source directory was not found.");
    }
}
