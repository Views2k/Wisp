using System.Net;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class InstallerDownloadTests
{
    private static readonly Uri DeliveryUri =
        new("https://release-assets.githubusercontent.com/github-production-release-asset/12345/signed-installer?token=test");

    [Fact]
    public async Task CanonicalRedirectedInstallerIsStreamedVerifiedAndStaged()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, digest, (request, sequence) =>
        {
            if (sequence == 2)
            {
                Assert.Equal(ReleaseUriPolicy.InitialDownloadUri(ReleaseTestData.FixtureVersion), request.RequestUri);
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                Assert.Contains(request.Headers.AcceptEncoding, header => header.Value == "identity");
                return Redirect(DeliveryUri);
            }

            Assert.Equal(3, sequence);
            Assert.Equal(DeliveryUri, request.RequestUri);
            return Bytes(installer);
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http);
        var progress = new CaptureProgress<UpdateDownloadProgress>();
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        var verified = await client.DownloadInstallerAsync(
            release, directory.Path, progress, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(directory.Path, "Wisp-Setup-9.8.7.exe"), verified.StagedPath);
        Assert.Equal(ReleaseTestData.FixtureVersion, verified.Version);
        Assert.Equal(installer.LongLength, verified.Size);
        Assert.Equal(digest, verified.Sha256);
        Assert.Equal(installer, await File.ReadAllBytesAsync(verified.StagedPath, TestContext.Current.CancellationToken));
        Assert.Equal(new UpdateDownloadProgress(0, installer.LongLength), progress.Values[0]);
        Assert.Equal(new UpdateDownloadProgress(installer.LongLength, installer.LongLength), progress.Values[^1]);
        Assert.Equal(3, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/123/file")]
    [InlineData("https://example.com/github-production-release-asset/123/file")]
    [InlineData("https://release-assets.githubusercontent.com/not-a-release-asset/123/file")]
    [InlineData("https://user%40example.com@release-assets.githubusercontent.com/github-production-release-asset/123/file")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123/file#fragment")]
    public async Task RedirectsOutsideTheHttpsHostAndPathAllowlistAreRejected(string location)
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, digest, (_, sequence) =>
        {
            Assert.Equal(2, sequence);
            return Redirect(new Uri(location));
        });
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task MoreThanThreeRedirectsAreRejected()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, digest, (_, sequence) =>
            Redirect(new Uri($"https://release-assets.githubusercontent.com/github-production-release-asset/{sequence}/file")));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(5, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExactBodySizeIsRequiredWhenContentLengthIsAbsent()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        var truncated = installer[..^1];
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, digest, (_, sequence) =>
            sequence == 2 ? Stream(truncated) : throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExactSha256IsRequiredAndFailedDownloadsLeaveNoPartialFile()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, new string('0', 64), (_, sequence) =>
            sequence == 2 ? Bytes(installer) : throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task MzAndPeHeadersAreRequiredAfterSizeAndHashPass()
    {
        var notPe = new byte[128];
        var digest = ReleaseTestData.Sha256(notPe);
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(notPe, digest, (_, sequence) =>
            sequence == 2 ? Bytes(notPe) : throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ProductAndVersionResourceMustMatchWispRelease()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        var differentVersion = new SemanticVersion(9, 8, 8);
        using var directory = new TemporaryDirectory();
        using var handler = CreateDownloadHandler(installer, digest, (_, sequence) =>
            sequence == 2 ? Bytes(installer) : throw new InvalidOperationException(), differentVersion);
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UpdateSecurityException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ExistingCanonicalDestinationIsNeverOverwrittenOrDownloadedAgain()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "Wisp-Setup-9.8.7.exe");
        var original = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);
        using var handler = CreateDownloadHandler(installer, digest, (_, _) =>
            throw new InvalidOperationException("No asset request should be sent."));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task StagingDirectoryMustBeAbsoluteAndAlreadyExist()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        var digest = ReleaseTestData.Sha256(installer);
        using var handler = CreateDownloadHandler(installer, digest, (_, _) =>
            throw new InvalidOperationException("No asset request should be sent."));
        using var http = new HttpClient(handler);
        using var client = new WispUpdateClient(http);
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadInstallerAsync(
            release, "relative", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => client.DownloadInstallerAsync(
            release, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void TestFixtureHasTheExpectedIndependentPeIdentity()
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(ReleaseTestData.FixtureExecutablePath());

        Assert.Equal("Wisp", info.ProductName);
        Assert.Equal("9.8.7.0", info.FileVersion);
        Assert.Equal("9.8.7.0", info.ProductVersion);
    }

    private static ScriptedHttpHandler CreateDownloadHandler(
        byte[] installer,
        string digest,
        Func<HttpRequestMessage, int, HttpResponseMessage> assetResponder,
        SemanticVersion? version = null)
    {
        var selectedVersion = version ?? ReleaseTestData.FixtureVersion;
        return new ScriptedHttpHandler((request, sequence, _) =>
        {
            if (sequence == 1)
            {
                return Task.FromResult(ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson(
                    selectedVersion, installer.LongLength, digest)));
            }

            return Task.FromResult(assetResponder(request, sequence));
        });
    }

    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = location;
        return response;
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static HttpResponseMessage Stream(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new MemoryStream(bytes, writable: false))
    };
}
