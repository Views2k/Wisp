using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class MessagingActivationTests
{
    [Fact]
    public async Task SignedActivationWorksWithoutNewsAndExplicitDisablePreservesOrdinaryReporting()
    {
        using var fixture = new SupplementaryFixture();
        var clock = new Clock(fixture.Now);
        var header = Activation(fixture, true, 2);
        var handler = new Handler(() => Response(header));
        using var transport = new SupplementaryHttpTransport(new("https://example.invalid/"), clock, handler);
        using var client = new SupplementaryClient(new(new Uri("https://example.invalid/")), new(fixture.Verifier, fixture.Directory), clock, transport);
        Assert.False(client.MessagingEnabled);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.True(client.MessagingEnabled);
        Assert.Empty(client.Content.Items);
        Assert.True(client.IsReportingEnabled);
        header = Activation(fixture, false, 3);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.False(client.MessagingEnabled);
        Assert.True(client.IsReportingEnabled);
        header = Activation(fixture, true, 2);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.False(client.MessagingEnabled);
        header = Activation(fixture, true, 4);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.True(client.MessagingEnabled);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(client.MessagingEnabled);
        Assert.Equal(4, handler.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("purpose")]
    [InlineData("error")]
    [InlineData("cached")]
    [InlineData("no-store-missing")]
    public async Task MissingInvalidOrUntrustedActivationFailsClosedAndDoesNotDisableOrdinarySupport(string failure)
    {
        using var fixture = new SupplementaryFixture();
        var clock = new Clock(fixture.Now);
        var header = Activation(fixture, true, 1);
        var failed = false;
        var handler = new Handler(() =>
        {
            if (!failed) return Response(header);
            var payload = Payload(fixture, true, 2);
            if (failure == "expired") { payload["issuedAt"] = Stamp(fixture.Now.AddMinutes(-3)); payload["expiresAt"] = Stamp(fixture.Now.AddMinutes(-1)); }
            if (failure == "purpose") payload["purpose"] = "wisp-supplementary-content";
            var value = Convert.ToBase64String(fixture.Envelope(payload));
            if (failure == "signature")
            {
                var envelope = JsonNode.Parse(fixture.Envelope(payload))!;
                envelope["signature"] = Convert.ToBase64String(new byte[64]);
                value = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
            }
            var response = Response(failure == "missing" ? null : failure == "malformed" ? "enabled" : value);
            if (failure == "error") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            if (failure == "cached") response.StatusCode = HttpStatusCode.NotModified;
            if (failure == "no-store-missing") response.Headers.CacheControl = null;
            return response;
        });
        using var transport = new SupplementaryHttpTransport(new("https://example.invalid/"), clock, handler);
        using var client = new SupplementaryClient(new(new Uri("https://example.invalid/")), new(fixture.Verifier, fixture.Directory), clock, transport);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.True(client.MessagingEnabled);
        failed = true;
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.False(client.MessagingEnabled);
        Assert.True(client.IsReportingEnabled);
    }

    [Fact]
    public void ActivationIsNotRestoredFromTheAnnouncementCacheAfterRestart()
    {
        using var fixture = new SupplementaryFixture();
        var store = new SupplementaryContentStore(fixture.Verifier, fixture.Directory);
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(fixture.Envelope(), fixture.Now));
        using var client = new SupplementaryClient(new(new Uri("https://example.invalid/")), new(fixture.Verifier, fixture.Directory), new Clock(fixture.Now));
        Assert.NotEmpty(client.Content.Items);
        Assert.False(client.MessagingEnabled);
        Assert.True(client.IsReportingEnabled);
    }

    private static JsonObject Payload(SupplementaryFixture fixture, bool enabled, int revision) => new()
    {
        ["schemaVersion"] = 1, ["purpose"] = "wisp-player-messaging", ["revision"] = revision,
        ["enabled"] = enabled, ["issuedAt"] = Stamp(fixture.Now), ["expiresAt"] = Stamp(fixture.Now.AddMinutes(2)),
    };
    private static string Activation(SupplementaryFixture fixture, bool enabled, int revision) => Convert.ToBase64String(fixture.Envelope(Payload(fixture, enabled, revision)));
    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static HttpResponseMessage Response(string? header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (header is not null) response.Headers.TryAddWithoutValidation("X-Wisp-Messaging", header);
        return response;
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(response()); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _elapsed;
        public override DateTimeOffset GetUtcNow() => now + TimeSpan.FromTicks(_elapsed);
        public override long GetTimestamp() => _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan time) => _elapsed += time.Ticks;
    }
}
