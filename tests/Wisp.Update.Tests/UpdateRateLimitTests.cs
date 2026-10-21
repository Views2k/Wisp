using System.Globalization;
using System.Net;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class UpdateRateLimitTests
{
    // The PC clock is a year behind the service. The wait must follow the service's own Date.
    private static readonly DateTimeOffset ServiceNow = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PcNow = ServiceNow.AddYears(-1);

    [Theory]
    [InlineData(40, 40)]
    [InlineData(300, 60)]
    [InlineData(-10, 0)]
    public async Task ExhaustedAllowanceWaitsUntilTheResetWithoutSendingRequests(int resetMinutes, int expectedWaitMinutes)
    {
        var clock = new ManualClock(PcNow);
        using var handler = new ScriptedHttpHandler((_, sequence, _) => Task.FromResult(sequence == 1
            ? Refusal(HttpStatusCode.Forbidden,
                ("X-RateLimit-Remaining", "0"),
                ("X-RateLimit-Reset", ServiceNow.AddMinutes(resetMinutes).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)))
            : ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson())));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, clock);

        var refused = await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        if (expectedWaitMinutes > 0)
        {
            clock.Advance(TimeSpan.FromMinutes(expectedWaitMinutes) - TimeSpan.FromSeconds(1));
            var waiting = await Assert.ThrowsAsync<UpdateRateLimitedException>(
                () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.Forbidden, waiting.StatusCode);
            Assert.Equal(1, handler.RequestCount);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseTestData.FixtureVersion, release.Version);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RetryAfterWaitsForTheRequestedDelay(HttpStatusCode status)
    {
        var clock = new ManualClock(PcNow);
        using var handler = new ScriptedHttpHandler((_, sequence, _) => Task.FromResult(sequence == 1
            ? Refusal(status, ("Retry-After", "120"))
            : ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson())));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, clock);

        await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(119));
        await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task TooManyRequestsWithoutATimeIsRateLimitedAndLeavesTheNextCheckToTheRegularInterval()
    {
        var clock = new ManualClock(PcNow);
        using var handler = new ScriptedHttpHandler((_, sequence, _) => Task.FromResult(sequence == 1
            ? Refusal(HttpStatusCode.TooManyRequests)
            : ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson())));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, clock);

        var refused = await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("12")]
    [InlineData(null)]
    public async Task ForbiddenWithAllowanceLeftStaysAnHttpErrorWithoutAWait(string? remaining)
    {
        var clock = new ManualClock(PcNow);
        using var handler = new ScriptedHttpHandler((_, sequence, _) => Task.FromResult(sequence == 1
            ? remaining is null
                ? Refusal(HttpStatusCode.Forbidden)
                : Refusal(HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", remaining),
                    ("X-RateLimit-Reset", ServiceNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)))
            : ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson())));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, clock);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ServerErrorsAreNeverReadAsRateLimits()
    {
        using var handler = new ScriptedHttpHandler((_, _, _) => Task.FromResult(
            Refusal(HttpStatusCode.ServiceUnavailable, ("Retry-After", "120"), ("X-RateLimit-Remaining", "0"))));
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, new ManualClock(PcNow));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    private static HttpResponseMessage Refusal(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = ScriptedHttpHandler.JsonResponse("""{"message":"API rate limit exceeded"}""", status);
        response.Headers.Date = ServiceNow;
        foreach (var (name, value) in headers)
        {
            Assert.True(response.Headers.TryAddWithoutValidation(name, value));
        }

        return response;
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
