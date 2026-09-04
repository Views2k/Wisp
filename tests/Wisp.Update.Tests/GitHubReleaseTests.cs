using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class GitHubReleaseTests
{
    [Fact]
    public void ProductionTransportDisablesAmbientAuthorityAndTransparentChanges()
    {
        using var handler = UpdateTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.True(handler.CheckCertificateRevocationList);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public async Task LatestReleaseUsesTheExactUnauthenticatedGitHubApiRequest()
    {
        using var handler = new ScriptedHttpHandler((request, sequence, _) =>
        {
            Assert.Equal(1, sequence);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(ReleaseUriPolicy.LatestReleaseUri, request.RequestUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            Assert.Contains(request.Headers.Accept, header => header.MediaType == "application/vnd.github+json");
            Assert.Contains(request.Headers.AcceptEncoding, header => header.Value == "identity");
            Assert.Equal("2026-03-10", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Task.FromResult(ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson()));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http);

        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseTestData.FixtureVersion, release.Version);
        Assert.Equal("Wisp-Setup-9.8.7.exe", release.FileName);
        Assert.Equal(123, release.Size);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal(ReleaseUriPolicy.InitialDownloadUri(release.Version), release.DownloadUri);
        Assert.Equal("A focused Wisp update.", release.ReleaseSummary);
    }

    [Fact]
    public async Task ReleaseBodyBecomesBoundedPlainTextWithoutLinkTargetsOrMarkup()
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release => release["body"] =
            "## What's new\n- **Smoother gauges**\n- Read [the notes](https://example.invalid/untrusted)" +
            "<script>ignored()</script>\u202e\nSee https://example.invalid/also-untrusted\n" + new string('x', 800));
        using var handler = new ScriptedHttpHandler((_, _, _) =>
            Task.FromResult(ScriptedHttpHandler.JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(
            "What's new" + Environment.NewLine + "Smoother gauges" + Environment.NewLine + "Read the notes",
            release.ReleaseSummary);
        Assert.DoesNotContain("https://", release.ReleaseSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", release.ReleaseSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("**", release.ReleaseSummary, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202e', release.ReleaseSummary);
        Assert.True(release.ReleaseSummary.Length <= 480);
    }

    [Fact]
    public async Task MissingReleaseBodyProducesNoSummaryWithoutChangingReleaseValidation()
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release => release.Remove("body"));
        using var handler = new ScriptedHttpHandler((_, _, _) =>
            Task.FromResult(ScriptedHttpHandler.JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.Empty(release.ReleaseSummary);
        Assert.Equal(ReleaseTestData.FixtureVersion, release.Version);
        Assert.Equal(123, release.Size);
        Assert.Equal(new string('a', 64), release.Sha256);
    }

    [Theory]
    [InlineData("draft", true)]
    [InlineData("prerelease", true)]
    [InlineData("immutable", false)]
    public async Task DraftPrereleaseAndMutableReleasesAreRejected(string property, bool value)
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release => release[property] = value);

        await AssertRejectedAsync(json);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("immutable")]
    public async Task MissingReleaseSecurityStateIsRejected(string property)
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release => release.Remove(property));

        await AssertRejectedAsync(json);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("v1.2.3+build")]
    public async Task NonCanonicalReleaseTagsAreRejected(string tag)
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release => release["tag_name"] = tag);

        await AssertRejectedAsync(json);
    }

    [Theory]
    [InlineData("name", "Wisp-Setup-9.8.7.zip")]
    [InlineData("name", "wisp-setup-9.8.7.exe")]
    [InlineData("state", "new")]
    [InlineData("size", "0")]
    [InlineData("size", "-1")]
    [InlineData("digest", "sha256:abcd")]
    [InlineData("digest", "SHA256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("digest", "sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task NonCanonicalInstallerMetadataIsRejected(string property, string value)
    {
        var json = ReleaseTestData.CreateJson(mutateAsset: asset =>
            asset[property] = property == "size" ? JsonValue.Create(long.Parse(value)) : JsonValue.Create(value));

        await AssertRejectedAsync(json);
    }

    [Fact]
    public async Task OversizeInstallerMetadataIsRejected()
    {
        var json = ReleaseTestData.CreateJson(size: WispUpdateClient.MaximumInstallerSizeBytes + 1);

        await AssertRejectedAsync(json);
    }

    [Theory]
    [InlineData("http://github.com/Views2k/Wisp/releases/download/v9.8.7/Wisp-Setup-9.8.7.exe")]
    [InlineData("https://github.com/Views2k/Other/releases/download/v9.8.7/Wisp-Setup-9.8.7.exe")]
    [InlineData("https://github.com/Views2k/Wisp/releases/download/v9.8.7/Wisp-Setup-9.8.7.exe?unexpected=1")]
    [InlineData("https://user@github.com/Views2k/Wisp/releases/download/v9.8.7/Wisp-Setup-9.8.7.exe")]
    public async Task InstallerUrlMustBeTheExactCanonicalReleaseUrl(string url)
    {
        var json = ReleaseTestData.CreateJson(mutateAsset: asset => asset["browser_download_url"] = url);

        await AssertRejectedAsync(json);
    }

    [Fact]
    public async Task DuplicateCanonicalInstallerAssetsAreRejected()
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release =>
        {
            var assets = (JsonArray)release["assets"]!;
            assets.Add(assets[0]!.DeepClone());
        });

        await AssertRejectedAsync(json);
    }

    [Fact]
    public async Task NullAssetEntriesCannotBypassTheSingleCanonicalAssetRule()
    {
        var json = ReleaseTestData.CreateJson(mutateRelease: release =>
        {
            var assets = (JsonArray)release["assets"]!;
            assets[0] = null;
        });

        await AssertRejectedAsync(json);
    }

    [Theory]
    [InlineData("9.8.6.0", true)]
    [InlineData("9.8.7.0", false)]
    [InlineData("9.8.8.0", false)]
    public async Task CheckReturnsOnlyAReleaseNewerThanTheCurrentVersion(string current, bool available)
    {
        using var handler = new ScriptedHttpHandler((_, _, _) =>
            Task.FromResult(ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson())));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        var result = await client.CheckForUpdateAsync(Version.Parse(current), TestContext.Current.CancellationToken);

        Assert.Equal(available, result is not null);
    }

    [Fact]
    public async Task ApiRedirectsAreRejectedEvenIfAnInjectedTransportFollowsThem()
    {
        using var handler = new ScriptedHttpHandler((_, _, _) =>
        {
            var response = ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson());
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/elsewhere");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApiBodiesOverOneMiBAreRejectedWithoutParsing()
    {
        using var handler = new ScriptedHttpHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
            };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertRejectedAsync(string json)
    {
        using var handler = new ScriptedHttpHandler((_, _, _) =>
            Task.FromResult(ScriptedHttpHandler.JsonResponse(json)));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));
    }
}
