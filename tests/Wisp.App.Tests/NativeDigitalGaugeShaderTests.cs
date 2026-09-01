using System.Windows;
using System.Security.Cryptography;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeDigitalGaugeShaderTests
{
    [Fact]
    public void NativeMaterialParametersUseCurrentLargeDashAndExactRedlineAmounts()
    {
        var exactRedline = ExactRedlineResult.Exact(9_500 * 2 * Math.PI / 60);
        var frame = new NativeGaugeFrame(
            true,
            0,
            4_500,
            10_000,
            TransmissionGear.Third,
            SpeedUnit.MilesPerHour,
            exactRedline);

        var parameters = NativeDigitalGaugeVisual.GaugeParametersFor(frame);

        // FH6 exposes an already-rounded 10,000 RPM native tach ceiling.
        Assert.Equal(0.45, parameters.X, 10);
        Assert.Equal(0.95, parameters.Y, 10);
    }

    [Fact]
    public void MissingExactRedlineFailsClosedAtTheEndOfTheNativeMaterial()
    {
        var frame = new NativeGaugeFrame(
            true,
            0,
            4_500,
            10_000,
            TransmissionGear.Third,
            SpeedUnit.MilesPerHour,
            ExactRedlineResult.Unavailable());

        var parameters = NativeDigitalGaugeVisual.GaugeParametersFor(frame);

        Assert.Equal(0.45, parameters.X, 10);
        Assert.Equal(1, parameters.Y, 10);
    }

    [Fact]
    public void CompiledNativeMaterialMatchesAuditedBytecodeAndReferenceGeometry()
    {
        var resource = Application.GetResourceStream(
            new Uri("/Wisp;component/Shaders/DigitalGauge.ps", UriKind.Relative));
        Assert.NotNull(resource);
        using var shaderStream = resource.Stream;
        Assert.Equal(
            "2E25F55E05AE675E54371310B2C9DF7D85E668B52EB7F238CA5C17C6F736F67D",
            Convert.ToHexString(SHA256.HashData(shaderStream)));

        var parameters = new Point(0.45, 0.75);
        var markerTop = PeakReferenceAlphaX(parameters, 5, 135, 155);
        var markerBottom = PeakReferenceAlphaX(parameters, 18, 130, 150);
        var redline = EvaluateReferencePixel(parameters, 252, 12);
        var beforeRedline = EvaluateReferencePixel(parameters, 220, 12);

        Assert.True(markerTop >= markerBottom + 3);
        Assert.True(redline.Alpha > 0.4);
        Assert.True(redline.Red > redline.Green * 2);
        Assert.True(redline.Blue > redline.Green * 2);
        Assert.Equal(beforeRedline.Red, beforeRedline.Green, 10);
        Assert.Equal(beforeRedline.Green, beforeRedline.Blue, 10);
    }

    private static int PeakReferenceAlphaX(Point parameters, int y, int left, int right)
    {
        var peak = left;
        for (var x = left + 1; x <= right; x++)
        {
            if (EvaluateReferencePixel(parameters, x, y).Alpha >
                EvaluateReferencePixel(parameters, peak, y).Alpha)
            {
                peak = x;
            }
        }

        return peak;
    }

    private static ReferencePixel EvaluateReferencePixel(Point parameters, int x, int y)
    {
        var uvX = (x + 0.5) / 302;
        var uvY = (y + 0.5) / 24;
        var horizontal = (uvX * 302) - 17 + (uvY * 4.8000001907348633);
        var vertical = uvY * 24;
        var current = parameters.X * 283;
        var redline = parameters.Y * 283;
        var verticalOffset = vertical - 12.5;
        var redlineHalf = redline * 0.5;
        var trackHalf = 140.5 - redlineHalf;
        var trackEdge = Saturate(
            Length(
                Math.Max(Math.Abs(horizontal - 2 - redline - trackHalf) - trackHalf, 0),
                Math.Max(Math.Abs(verticalOffset) - 6.5, 0)) - 0.5);
        var currentSide = 0.5 + (Saturate(current - horizontal) * 0.5);
        var currentTrack = currentSide * (1 - trackEdge);
        var inactiveEdge = Saturate(
            Length(
                Math.Max((1 - redlineHalf) + Math.Abs(horizontal - redlineHalf + 1), 0),
                Math.Max(Math.Abs(verticalOffset) - 6.5, 0)) - 0.5);
        var currentRatio = Saturate(horizontal / Math.Min(current, redline));
        var inactiveLevel = 0.25 + (currentRatio * 0.25);
        inactiveLevel += (-0.099999994039535522 - (currentRatio * 0.25)) *
            Saturate(horizontal - current);
        var inactiveTrack = inactiveLevel * (1 - inactiveEdge);
        var redlineSide = Saturate(horizontal - redline);
        var redlineGreen = redlineSide * -0.46666663885116577;
        var combinedTrack = inactiveTrack + ((currentTrack - inactiveTrack) * redlineSide);
        var marker = 1 - Saturate(
            Length(
                Math.Max(Math.Abs(horizontal - current) - 0.5, 0),
                Math.Max(Math.Abs(verticalOffset) - 7, 0)));
        var redFactor = (1 - redlineSide) + (marker * redlineSide);
        var greenFactor = (1 + redlineGreen) - (marker * redlineGreen);
        var alpha = combinedTrack + ((1 - combinedTrack) * marker);
        var green = alpha * redFactor;
        var blue = alpha * greenFactor;
        var halo = 1 - Saturate(
            (Length(
                Math.Max(Math.Abs(horizontal - current) - 2, 0),
                Math.Max(Math.Abs(vertical - 13) - 5, 0)) + 11) *
            0.058823529630899429);
        var alphaWithHalo = alpha + ((1 - alpha) * halo);
        var greenWithHalo = green + ((1 - green) * halo);
        var blueWithHalo = blue + ((1 - blue) * halo);
        var differenceHalf = (parameters.X - parameters.Y) * 141.5;
        var midpoint = (parameters.X + parameters.Y) * 141.5;
        var redlineGlow = 1 - Saturate(
            (Length(
                Math.Max(8 - differenceHalf + Math.Abs(horizontal - midpoint), 0),
                Math.Max(Math.Abs(vertical + 20) - 7, 0)) - 1) *
            0.10000000149011612);
        var finalBlue = blueWithHalo +
            (((alphaWithHalo * 0.30000001192092896) + 0.69999998807907104) * redlineGlow);

        return new ReferencePixel(alphaWithHalo, greenWithHalo, finalBlue, alphaWithHalo);
    }

    private static double Length(double x, double y) => Math.Sqrt((x * x) + (y * y));

    private static double Saturate(double value) => Math.Clamp(value, 0, 1);

    private readonly record struct ReferencePixel(double Red, double Green, double Blue, double Alpha);
}
