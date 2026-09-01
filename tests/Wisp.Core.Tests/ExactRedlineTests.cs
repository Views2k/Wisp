using Xunit;

namespace Wisp.Core.Tests;

public sealed class ExactRedlineTests
{
    [Theory]
    [InlineData(6_200)]
    [InlineData(7_500)]
    [InlineData(9_500)]
    public void ExactProviderAngularVelocityRoundTripsToRpm(double expectedRpm)
    {
        var angularVelocity = expectedRpm * 2 * Math.PI / 60;
        var result = ExactRedlineResult.Exact(angularVelocity);

        Assert.True(result.IsExact);
        Assert.Equal(expectedRpm, result.Rpm, 6);
        Assert.Equal(ExactRedlineResult.NativeHudProviderSource, result.Source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidProviderValueFailsClosed(double angularVelocity)
    {
        var result = ExactRedlineResult.Exact(angularVelocity);

        Assert.False(result.IsExact);
        Assert.Equal(ExactRedlineStatus.InvalidProvider, result.Status);
        Assert.Equal(0, result.Rpm);
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.GameNotRunning, ExactRedlineStatus.GameNotRunning)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, ExactRedlineStatus.UnsupportedBuild)]
    [InlineData(NativeAssistProviderStatus.AccessDenied, ExactRedlineStatus.AccessDenied)]
    [InlineData(NativeAssistProviderStatus.InvalidProvider, ExactRedlineStatus.InvalidProvider)]
    [InlineData(NativeAssistProviderStatus.PlayerNotUnique, ExactRedlineStatus.PlayerNotUnique)]
    [InlineData(NativeAssistProviderStatus.TelemetryMismatch, ExactRedlineStatus.TelemetryMismatch)]
    [InlineData(NativeAssistProviderStatus.ReadFailure, ExactRedlineStatus.ReadFailure)]
    public void NativeHudFailureMapsToExactRedlineFailure(
        NativeAssistProviderStatus providerStatus,
        ExactRedlineStatus expected)
    {
        var snapshot = NativeHudSnapshot.Unavailable(providerStatus, 4, 314);

        Assert.False(snapshot.Available);
        Assert.False(snapshot.ExactRedline.IsExact);
        Assert.Equal(expected, snapshot.ExactRedline.Status);
        Assert.Equal(314, snapshot.CarOrdinal);
    }
}
