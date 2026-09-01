using System.Windows;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAnalogNeedleTests
{
    [Fact]
    public void NeedleMaterialPreservesBothSourceAuthoredAspectRatios()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Wisp.App",
            "Shaders",
            "AnalogNeedle.hlsl"));
        Assert.Contains("(180.0 / 110.0)", source, StringComparison.Ordinal);
        Assert.Contains("(180.0 / 94.0)", source, StringComparison.Ordinal);

        var electricXaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Wisp.App",
            "NativeElectricAnalogSpeedometer.xaml"));
        Assert.Contains("IsElectricMaterial=\"True\"", electricXaml, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }

    [Fact]
    public void LiveNeedleUsesOnlyTheCompositorUpdatePath()
    {
        Assert.True(NativeAnalogSpeedometer.ShouldUpdateTachometerImmediately(renderingAttached: false));
        Assert.False(NativeAnalogSpeedometer.ShouldUpdateTachometerImmediately(renderingAttached: true));
    }

    [Fact]
    public void StaticNativeNeedleSpansTheAuthoredOuterLeadBounds()
    {
        const int width = 110;
        const int height = 180;
        const double canvasLeft = 178.5;
        const double canvasTop = 54;
        const double gaugeCenter = 144;
        var visible = 0;
        var minimumRadius = double.PositiveInfinity;
        var maximumRadius = 0d;

        for (var pixelY = 0; pixelY < height; pixelY++)
        {
            for (var pixelX = 0; pixelX < width; pixelX++)
            {
                if (StaticNeedleAlpha(pixelX, pixelY, width, height) <= 2d / 255d)
                {
                    continue;
                }

                visible++;
                var x = canvasLeft + pixelX + 0.5 - gaugeCenter;
                var y = canvasTop + pixelY + 0.5 - gaugeCenter;
                var radius = Math.Sqrt((x * x) + (y * y));
                minimumRadius = Math.Min(minimumRadius, radius);
                maximumRadius = Math.Max(maximumRadius, radius);
            }
        }

        Assert.InRange(visible, 500, 600);
        Assert.InRange(minimumRadius, 81, 83);
        Assert.InRange(maximumRadius, 143, 145);
        Assert.InRange(maximumRadius - minimumRadius, 61, 63);
    }

    private static double StaticNeedleAlpha(int pixelX, int pixelY, int width, int height)
    {
        var u = (pixelX + 0.5) / width;
        var v = (pixelY + 0.5) / height;
        var x = u + 0.2800000011920929;
        var y = (v - 0.5) * (180d / 110d);
        var derivative = 1d / 110d;
        var radius = Math.Sqrt((x * x) + (y * y));
        var radialDistance = radius - 0.7070000171661377;
        var halfWidth = 0.029999999329447746 -
                        (radialDistance * 0.017452005296945572);
        var leading = (halfWidth + y) / derivative;
        var trailing = (halfWidth - y) / derivative;
        var lineCoverage = Saturate(leading) * Saturate(trailing);
        var shadowCoverage =
            Saturate((leading * 0.20000000298023224) + 0.6000000238418579) *
            Saturate((trailing * 0.20000000298023224) + 0.6000000238418579);
        var radialStart = Saturate(radialDistance / derivative);
        var radialEnd = Saturate((0.5730000138282776 - radialDistance) / derivative);
        var endMask = Math.Max(
            Saturate((radialDistance - 0.27300000190734863) / derivative),
            Saturate((radius - 0.7070000171661377) * 3.66300368309021));
        var commonMask = radialStart * radialEnd * endMask;
        return Math.Max(
            commonMask * lineCoverage,
            commonMask * 0.05000000074505806 * shadowCoverage);
    }

    private static double Saturate(double value) => Math.Clamp(value, 0, 1);
}
