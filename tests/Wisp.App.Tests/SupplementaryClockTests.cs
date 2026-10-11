using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryClockTests
{
    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public async Task ClockSkewAndWallTimeChangesPreserveOriginalEvidenceAndImmutableRetries(int days)
    {
        using var f = new SupplementaryFixture();
        var time = new ManualClock(f.Now.AddDays(days));
        var bytes = new List<byte[]>();
        var attempts = 0;
        using var transport = new SupplementaryHttpTransport(new("https://fixture.invalid/"), time, new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            { time.Advance(20); return ContentTime(f.Now); }
            bytes.Add(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            return ++attempts < 3 ? new(HttpStatusCode.ServiceUnavailable) : Json("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0}");
        }));
        using var client = new SupplementaryClient(new(new Uri("https://fixture.invalid/")), new(f.Verifier, f.Directory), time, transport,
            () => 0, (delay, _) => { time.Advance((long)delay.TotalMilliseconds); return Task.CompletedTask; });
        var observed = time.GetUtcNow().AddSeconds(-30);
        Assert.True(client.TryEnqueue(f.Event() with { ObservedAt = observed }));
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        time.Wall = time.Wall.AddDays(20);
        time.Advance(5000);
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, bytes.Count);
        Assert.All(bytes, value => Assert.Equal(bytes[0], value));
        using var document = JsonDocument.Parse(bytes[0]);
        var value = document.RootElement.GetProperty("events")[0];
        Assert.Equal(observed, value.GetProperty("observedAt").GetDateTimeOffset());
        var calibration = value.GetProperty("clock");
        Assert.Equal(20, calibration.GetProperty("uncertaintyMs").GetInt32());
        var effective = calibration.GetProperty("serverAnchorAt").GetDateTimeOffset()
            .AddMilliseconds(calibration.GetProperty("anchorAgeMs").GetInt64() - calibration.GetProperty("observationAgeMs").GetInt64());
        Assert.InRange((f.Now.AddSeconds(-30) - effective).TotalMilliseconds, 0, 20);
        Assert.Equal(0, client.DroppedEvents);
    }

    [Fact]
    public void MonotonicAnchorExpiresAndNeverInventsAnObservationAge()
    {
        using var f = new SupplementaryFixture();
        var time = new ManualClock(f.Now.AddDays(3));
        var anchor = new SupplementaryClock(time);
        Assert.Null(anchor.Capture(time.GetTimestamp(), 0));
        anchor.Observe(new(SupplementaryRequestStatus.Success, [], 204, f.Now, 15));
        Assert.True(anchor.IsFresh);
        Assert.Null(anchor.Capture(time.GetTimestamp(), -1));
        var queued = time.GetTimestamp();
        time.Advance(1000);
        time.Wall = time.Wall.AddDays(-30);
        Assert.Equal(1100, anchor.Capture(queued, 100)!.ObservationAgeMs);
        time.Advance(3600000);
        Assert.False(anchor.IsFresh);
        Assert.Null(anchor.Capture(queued, 0));
        anchor.Observe(new(SupplementaryRequestStatus.Unavailable, [], 503, f.Now, 0));
        Assert.False(anchor.IsFresh);
    }

    [Theory]
    [InlineData("date-only")]
    [InlineData("cacheable")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("error")]
    public async Task CalibrationRequiresTheExplicitSuccessfulNonCacheableOriginHeader(string mode)
    {
        using var f = new SupplementaryFixture();
        var time = new ManualClock(f.Now);
        using var transport = new SupplementaryHttpTransport(new("https://fixture.invalid/"), time, new Handler(_ =>
        {
            var response = ContentTime(f.Now);
            if (mode == "date-only") { response.Headers.Remove("X-Wisp-Server-Time"); response.Headers.Date = f.Now; }
            if (mode == "cacheable") response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) };
            if (mode == "duplicate") response.Headers.TryAddWithoutValidation("X-Wisp-Server-Time", f.Now.ToString("O"));
            if (mode == "malformed") { response.Headers.Remove("X-Wisp-Server-Time"); response.Headers.TryAddWithoutValidation("X-Wisp-Server-Time", "tomorrow"); }
            if (mode == "error") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            return response;
        }));
        var result = await transport.SendAsync("/api/v1/content", null, 2048, TestContext.Current.CancellationToken);
        Assert.Null(result.ServerTime);
        Assert.Null(result.UncertaintyMs);
    }

    [Fact]
    public async Task InitialContentFailureDefersEventsAndRecoversOnTheNormalCollectionCycle()
    {
        using var f = new SupplementaryFixture();
        var time = new ManualClock(f.Now.AddDays(3));
        var gets = 0;
        var posts = 0;
        var cycles = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new SupplementaryHttpTransport(new("https://fixture.invalid/"), time, new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return ++gets == 1 ? new(HttpStatusCode.ServiceUnavailable) : ContentTime(f.Now.AddMilliseconds(time.GetTimestamp()));
            posts++;
            using var data = JsonDocument.Parse(request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            Assert.All(data.RootElement.GetProperty("events").EnumerateArray(), value => Assert.True(value.TryGetProperty("clock", out _)));
            return Json("{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0}");
        }));
        using var client = new SupplementaryClient(new(new Uri("https://fixture.invalid/")), new(f.Verifier, f.Directory), time, transport, () => 0,
            async (delay, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(60), delay);
                if (++cycles == 1) { Assert.Equal(0, posts); time.Advance(60000); return; }
                completed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        Assert.True(client.TryEnqueue(f.Event() with { ObservedAt = time.Wall }));
        client.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        client.Stop();
        await client.Completion;
        Assert.Equal(2, gets);
        Assert.Equal(1, posts);
        Assert.Equal(0, client.DroppedEvents);
    }

    private static HttpResponseMessage ContentTime(DateTimeOffset time)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.CacheControl = new() { NoStore = true };
        response.Headers.TryAddWithoutValidation("X-Wisp-Server-Time", time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Wall = now;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => Wall;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1000;
        internal void Advance(long milliseconds) { _timestamp += milliseconds; Wall = Wall.AddMilliseconds(milliseconds); }
    }
}
