using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAnalogMaterialContractTests
{
    [Fact]
    public void ShippedAnalogueMaterialPortsRemainConnectedAndCompiled()
    {
        var app = AppSourceDirectory();
        var project = File.ReadAllText(Path.Combine(app, "Wisp.App.csproj"));
        var xaml = File.ReadAllText(Path.Combine(app, "NativeAnalogSpeedometer.xaml"));
        var numberLayer = File.ReadAllText(Path.Combine(app, "NativeAnalogGaugeVisual.cs"));

        Assert.Contains("<Resource Include=\"Shaders\\*.ps\" />", project, StringComparison.Ordinal);
        Assert.Contains("<local:NativeAnalogMaterialVisual", xaml, StringComparison.Ordinal);
        Assert.Contains("<local:NativeAnalogNeedleVisual", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawArcSection", numberLayer, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawLine(", numberLayer, StringComparison.Ordinal);

        AssertPixelShader(
            Path.Combine(app, "Shaders", "AnalogGauge.ps"),
            Path.Combine(app, "Shaders", "AnalogGauge.hlsl"),
            "52064E7038D778CD8763E9EAFDC6DAD8B4DFFE677985CE6B9BFE7345C649A9CB",
            "0.9474999904632568",
            "0.75",
            "0.5333333611488342");
        AssertPixelShader(
            Path.Combine(app, "Shaders", "AnalogNeedle.ps"),
            Path.Combine(app, "Shaders", "AnalogNeedle.hlsl"),
            "2D5306D8BE3F0276A2D3F83FE52B2F5B70FD169FBA3EDD30227222240F473CDA",
            "0.7070000171661377",
            "0.5730000138282776",
            "0.05000000074505806",
            "float BlurAmount : register(c0);",
            "#define NATIVE_ASPECT_RATIO (180.0 / 110.0)",
            "#define NATIVE_ASPECT_RATIO (180.0 / 94.0)",
            "float y = (uv.y - 0.5) * NATIVE_ASPECT_RATIO;");
        AssertPixelShader(
            Path.Combine(app, "Shaders", "ElectricAnalogNeedle.ps"),
            Path.Combine(app, "Shaders", "AnalogNeedle.hlsl"),
            "FDD6C350D95526FE7291699337C7A5A92C0F885FBC956843AB8AE78667D430CD",
            "#ifdef ELECTRIC_NEEDLE");
    }

    [Fact]
    public void AnalogueGaugeMasksUseTheUnclampedNativeSweepAngle()
    {
        var source = File.ReadAllText(Path.Combine(
            AppSourceDirectory(),
            "Shaders",
            "AnalogGauge.hlsl"));

        Assert.Contains(
            "float sweepLeading = saturate((rawAngle + 0.0022499999031424522) / angularWidth);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "float sweepTrailing = saturate((1.0022499561309814 - rawAngle) / angularWidth);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "float beforeRedline = saturate((GaugeParameters.x - rawAngle) / angularWidth);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "float sweepLeading = saturate((angle +",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "float sweepTrailing = saturate((1.0022499561309814 - angle)",
            source,
            StringComparison.Ordinal);

        // The bottom-center point is in FH6's excluded 120-degree sector.
        // The raw angle must remain above 1 so the trailing mask rejects it;
        // saturating first would turn it into 1 and recreate a full circle.
        var bottomCenterRawAngle = (Math.PI + 2.6179938316345215) * 0.23873241245746613;
        Assert.True(bottomCenterRawAngle > 1.02);
        Assert.Equal(0d, SweepTrailing(bottomCenterRawAngle, 0.001), precision: 12);
        Assert.True(SweepTrailing(Math.Clamp(bottomCenterRawAngle, 0d, 1d), 0.001) > 0d);
    }

    [Fact]
    public void AnalogueGaugeUsesOneNativeCutoutPerThousandRpm()
    {
        var frame = new NativeGaugeFrame(
            true,
            0,
            1_000,
            10_500,
            TransmissionGear.First,
            SpeedUnit.MilesPerHour,
            ExactRedlineResult.Exact(9_000 * 2 * Math.PI / 60));

        var parameters = NativeAnalogMaterialVisual.GaugeParametersFor(frame);
        Assert.Equal(9d / 11d, parameters.X, 12);
        Assert.Equal(1d / 11d, parameters.Y, 12);

        var source = File.ReadAllText(Path.Combine(
            AppSourceDirectory(),
            "Shaders",
            "AnalogGauge.hlsl"));
        Assert.Equal(2, Count(source, "floor(("));
        Assert.DoesNotContain("round((", source, StringComparison.Ordinal);
        Assert.DoesNotContain("333.3333435 RPM dash interval", source, StringComparison.Ordinal);
    }

    private static double SweepTrailing(double angle, double angularWidth) =>
        Math.Clamp((1.0022499561309814 - angle) / angularWidth, 0d, 1d);

    private static int Count(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private static void AssertPixelShader(
        string bytecodePath,
        string sourcePath,
        string expectedSha256,
        params string[] requiredConstants)
    {
        var bytecode = File.ReadAllBytes(bytecodePath);
        Assert.True(bytecode.Length > 4);
        Assert.Equal(new byte[] { 0x00, 0x03, 0xFF, 0xFF }, bytecode[..4]);
        Assert.Equal(
            expectedSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytecode)));

        var source = File.ReadAllText(sourcePath);
        Assert.Contains("sampler2D ImplicitInput : register(s0);", source, StringComparison.Ordinal);
        foreach (var constant in requiredConstants)
        {
            Assert.Contains(constant, source, StringComparison.Ordinal);
        }
    }

    private static string AppSourceDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "Wisp.App");

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
}
