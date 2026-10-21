using System.Globalization;
using System.Net;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class LatestTagRedirectTests
{
    private const string TagPage = "https://github.com/Views2k/Wisp/releases/tag/";

    [Theory]
    [InlineData("9.8.7.0")]
    [InlineData("9.8.8.0")]
    public async Task ATagNoNewerThanTheInstalledVersionNeedsNoApiRequest(string current)
    {
        using var site = new Site(_ => Redirect(TagPage + "v9.8.7"));

        Assert.Null(await site.Client.CheckForUpdateAsync(Version.Parse(current), TestContext.Current.CancellationToken));

        Assert.Equal(1, site.PageRequests);
        Assert.Equal(0, site.ApiRequests);
        var page = Assert.Single(site.Requests);
        Assert.Equal(HttpMethod.Get, page.Method);
        Assert.Equal(new Uri("https://github.com/Views2k/Wisp/releases/latest"), page.RequestUri);
        Assert.Null(page.Headers.Authorization);
    }

    [Fact]
    public async Task ANewTagIsFetchedFromTheApiOnceAndReusedWhileItStaysTheLatest()
    {
        var tag = "v9.8.7";
        using var site = new Site(_ => Redirect(TagPage + tag));

        var first = await site.Client.CheckForUpdateAsync(Version.Parse("9.8.6.0"), TestContext.Current.CancellationToken);
        var second = await site.Client.CheckForUpdateAsync(Version.Parse("9.8.6.0"), TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, site.ApiRequests);

        tag = "v9.8.8";
        await site.Client.CheckForUpdateAsync(Version.Parse("9.8.6.0"), TestContext.Current.CancellationToken);
        Assert.Equal(2, site.ApiRequests);
    }

    [Theory]
    [InlineData("https://evil.example/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("https://github.com.evil.example/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("http://github.com/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("https://github.com:8443/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("https://user@github.com/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("https://GitHub.com/Views2k/Wisp/releases/tag/v9.8.7")]
    [InlineData("https://github.com/Views2k/Other/releases/tag/v9.8.7")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/../")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8.7/../../../Other/releases/tag/v9.8.7")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/%76%39.8.7")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8.7?tab=assets")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8.7#assets")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8.7/")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/latest")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v9.8.7-beta")]
    [InlineData("https://github.com/Views2k/Wisp/releases/tag/v09.8.7")]
    [InlineData("/Views2k/Wisp/releases/tag/v9.8.7")]
    public async Task AnyOtherRedirectTargetLeavesTheCheckToTheApi(string location)
    {
        // Accepting the redirect would end this check without an API request (the tag equals the installed version).
        using var site = new Site(_ => Redirect(location));

        Assert.Null(await site.Client.CheckForUpdateAsync(Version.Parse("9.8.7.0"), TestContext.Current.CancellationToken));

        Assert.Equal(1, site.ApiRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AnAnswerOtherThanAFoundRedirectLeavesTheCheckToTheApi(HttpStatusCode status)
    {
        using var site = new Site(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("<html></html>") };
            response.Headers.TryAddWithoutValidation("Location", TagPage + "v9.8.7");
            return response;
        });

        Assert.Null(await site.Client.CheckForUpdateAsync(Version.Parse("9.8.7.0"), TestContext.Current.CancellationToken));

        Assert.Equal(1, site.ApiRequests);
    }

    [Fact]
    public async Task AFailedPageRequestLeavesTheCheckToTheApi()
    {
        using var site = new Site(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError));

        var release = await site.Client.CheckForUpdateAsync(Version.Parse("9.8.6.0"), TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal(ReleaseTestData.FixtureVersion, release.Version);
        Assert.Equal(1, site.ApiRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledCheckDoesNotFallBackToTheApi(bool cancelAfterThePageAnswered)
    {
        using var cancellation = new CancellationTokenSource();
        if (!cancelAfterThePageAnswered) await cancellation.CancelAsync();
        using var site = new Site(_ =>
        {
            if (cancelAfterThePageAnswered) cancellation.Cancel();
            return Redirect(TagPage + "v9.8.7");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => site.Client.CheckForUpdateAsync(Version.Parse("9.8.6.0"), cancellation.Token));

        Assert.Equal(0, site.ApiRequests);
    }

    [Fact]
    public async Task WhileTheApiIsRateLimitedAnUnchangedTagStillCompletesTheCheck()
    {
        var page = HttpStatusCode.OK;
        var tag = "v9.8.7";
        using var site = new Site(_ => page == HttpStatusCode.Found ? Redirect(TagPage + tag) : new HttpResponseMessage(page),
            api: () =>
            {
                var now = DateTimeOffset.UtcNow;
                var response = ScriptedHttpHandler.JsonResponse("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden);
                response.Headers.Date = now;
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset",
                    now.AddMinutes(40).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                return response;
            });

        await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => site.Client.CheckForUpdateAsync(Version.Parse("9.8.7.0"), TestContext.Current.CancellationToken));
        Assert.Equal(1, site.ApiRequests);

        page = HttpStatusCode.Found;
        Assert.Null(await site.Client.CheckForUpdateAsync(Version.Parse("9.8.7.0"), TestContext.Current.CancellationToken));

        tag = "v9.8.8";
        await Assert.ThrowsAsync<UpdateRateLimitedException>(
            () => site.Client.CheckForUpdateAsync(Version.Parse("9.8.7.0"), TestContext.Current.CancellationToken));
        Assert.Equal(1, site.ApiRequests);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
        Assert.True(response.Headers.TryAddWithoutValidation("Location", location));
        return response;
    }

    private sealed class Site : IDisposable
    {
        private readonly ScriptedHttpHandler _handler;
        private readonly HttpClient _http;
        private int _pageRequests;
        private int _apiRequests;

        internal Site(Func<HttpRequestMessage, HttpResponseMessage> page, Func<HttpResponseMessage>? api = null)
        {
            // Each request is counted when it is attempted; like the real transport, a cancelled one then fails.
            _handler = new ScriptedHttpHandler((request, _, cancellationToken) =>
            {
                lock (Requests) Requests.Add(request);
                if (request.RequestUri == ReleaseUriPolicy.LatestReleasePageUri)
                {
                    Interlocked.Increment(ref _pageRequests);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(page(request));
                }

                Assert.Equal(ReleaseUriPolicy.LatestReleaseUri, request.RequestUri);
                Interlocked.Increment(ref _apiRequests);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(api?.Invoke() ?? ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson()));
            });
            _http = new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan };
            Client = new WispUpdateClient(_http);
        }

        internal WispUpdateClient Client { get; }
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal int PageRequests => Volatile.Read(ref _pageRequests);
        internal int ApiRequests => Volatile.Read(ref _apiRequests);

        public void Dispose()
        {
            Client.Dispose();
            _http.Dispose();
            _handler.Dispose();
        }
    }
}
