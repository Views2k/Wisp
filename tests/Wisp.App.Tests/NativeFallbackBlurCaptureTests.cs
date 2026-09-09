using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeFallbackBlurCaptureTests
{
    [Fact]
    public void ElectricFallbackRetainsItsExistingBlurResponse()
    {
        Assert.Equal(-0.041887902047863905,
            NativeGaugeGeometry.AnalogNeedleBlurRadians(6, 0.01), 12);
    }

    // wisp-debug-20260908-1810.zip, SHA256:
    // 56d1710a820a94411f180790a128f3e490a789ae24ccaf0a3ea62b88604ae939
    // Actual analogue OverlayWindow, native source, same car throughout.
    // Capture start QPC 1728876213878; frequency 10,000,000 ticks/second.
    // Six 100ms windows centered at 2, 8.5, 10.3, 14.5, 26.6 and 29.1 seconds
    // cover three rising and three falling ramps. Each row retains the first
    // and last samples inside its window. Expected blur is the time-weighted
    // trapezoidal mean of every recorded native blur sample between them.
    // A fixed 0.025-radian tolerance allows sampling/interpolation differences;
    // interpreting the native setting as 4ms misses every fixture by >0.13rad.
    [Theory]
    [InlineData(1728895730393L, 1728896712892L, 189.52073669433594, 260.9763126423177, -0.38781161698427596)]
    [InlineData(1728960763958L, 1728961642713L, 267.54595947265625, 234.30935668945312, 0.21751579040217012)]
    [InlineData(1728978760976L, 1728979575726L, 282.22306201224825, 256.5691054147102, 0.17797788943412357)]
    [InlineData(1729020776587L, 1729021657445L, 299.47515513983024, 324.9488990015621, -0.1598921838712369)]
    [InlineData(1729141801764L, 1729142650286L, 270.0370013820525, 306.2369621246427, -0.23391775391302203)]
    [InlineData(1729166787120L, 1729167677230L, 220.6304931640625, 182.5897059961832, 0.2496181742764003)]
    public void FallbackBlurMatchesCapturedNativeRamps(
        long firstTimestamp,
        long lastTimestamp,
        double firstAngle,
        double lastAngle,
        double observedNativeBlur)
    {
        var elapsedSeconds = (lastTimestamp - firstTimestamp) / 10_000_000d;

        var actual = NativeGaugeGeometry.CombustionNeedleBlurRadians(
            lastAngle - firstAngle, elapsedSeconds);

        Assert.InRange(actual, observedNativeBlur - 0.025, observedNativeBlur + 0.025);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void SameAngularVelocityKeepsBlurAcrossCallbackCadences(int direction)
    {
        var shortFrame = NativeGaugeGeometry.CombustionNeedleBlurRadians(direction * 9, 0.015);
        var longFrame = NativeGaugeGeometry.CombustionNeedleBlurRadians(direction * 21, 0.035);

        Assert.Equal(shortFrame, longFrame, 12);
        Assert.InRange(Math.Abs(shortFrame), 0.01, 0.64);
    }

    [Theory]
    [InlineData(120, -0.65)]
    [InlineData(-120, 0.65)]
    public void ExtremeMotionRetainsNativeSignedBlurCap(double delta, double expected)
    {
        Assert.Equal(expected, NativeGaugeGeometry.CombustionNeedleBlurRadians(delta, 0.005), 12);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, -0.01)]
    [InlineData(10, 0.251)]
    [InlineData(double.NaN, 0.01)]
    [InlineData(double.PositiveInfinity, 0.01)]
    [InlineData(10, double.NaN)]
    [InlineData(10, double.PositiveInfinity)]
    public void InvalidMotionOrTimingProducesNoBlur(double delta, double elapsed)
    {
        Assert.Equal(0, NativeGaugeGeometry.CombustionNeedleBlurRadians(delta, elapsed));
    }
}
