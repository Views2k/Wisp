using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace Wisp.Update;

public sealed class WispUpdateClient : IDisposable
{
    public const long MaximumInstallerSizeBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultApiBodyTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(30);

    // GitHub's unauthenticated allowance is counted per hour, so no service-requested wait is longer.
    internal static readonly TimeSpan MaximumRateLimitWait = TimeSpan.FromHours(1);

    private const int MaximumApiResponseBytes = 1024 * 1024;
    private const int MaximumRedirects = 3;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _apiBodyTimeout;
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeProvider _clock;
    private readonly object _rateLimitGate = new();
    private long _rateLimitStarted;
    private TimeSpan? _rateLimitWait;
    private HttpStatusCode _rateLimitStatus;
    private UpdateRelease? _lastSeenRelease;
    private bool _disposed;

    private WispUpdateClient(
        HttpClient httpClient,
        bool ownsHttpClient,
        TimeSpan headerTimeout,
        TimeSpan apiBodyTimeout,
        TimeSpan downloadTimeout,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(clock);
        ValidateTimeout(headerTimeout, nameof(headerTimeout), TimeSpan.FromMinutes(1));
        ValidateTimeout(apiBodyTimeout, nameof(apiBodyTimeout), TimeSpan.FromMinutes(1));
        ValidateTimeout(downloadTimeout, nameof(downloadTimeout), TimeSpan.FromHours(2));
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _headerTimeout = headerTimeout;
        _apiBodyTimeout = apiBodyTimeout;
        _downloadTimeout = downloadTimeout;
        _clock = clock;
    }

    internal WispUpdateClient(
        HttpClient httpClient,
        TimeSpan? headerTimeout = null,
        TimeSpan? apiBodyTimeout = null,
        TimeSpan? downloadTimeout = null)
        : this(httpClient, false,
            headerTimeout ?? DefaultHeaderTimeout,
            apiBodyTimeout ?? DefaultApiBodyTimeout,
            downloadTimeout ?? DefaultDownloadTimeout,
            TimeProvider.System)
    {
    }

    internal WispUpdateClient(HttpClient httpClient, TimeProvider clock)
        : this(httpClient, false, DefaultHeaderTimeout, DefaultApiBodyTimeout, DefaultDownloadTimeout, clock)
    {
    }

    public static WispUpdateClient CreateDefault() =>
        new(UpdateTransport.CreateClient(), true,
            DefaultHeaderTimeout, DefaultApiBodyTimeout, DefaultDownloadTimeout, TimeProvider.System);

    public async Task<UpdateRelease> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowWhileRateLimited();
        using var request = UpdateTransport.CreateApiRequest(ReleaseUriPolicy.LatestReleaseUri);
        using var response = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);

        RequireUnredirectedResponse(response, ReleaseUriPolicy.LatestReleaseUri);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            if (TryReadRateLimit(response, _clock.GetUtcNow(), out var wait))
            {
                lock (_rateLimitGate)
                {
                    _rateLimitStarted = _clock.GetTimestamp();
                    _rateLimitWait = wait;
                    _rateLimitStatus = response.StatusCode;
                }

                throw new UpdateRateLimitedException(response.StatusCode);
            }

            throw new HttpRequestException("The GitHub latest-release endpoint did not return HTTP 200.",
                inner: null, response.StatusCode);
        }

        RequireIdentityEncoding(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null ||
            (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UpdateSecurityException("The latest-release response is not JSON content.");
        }

        var json = await ReadApiBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return GitHubReleaseParser.Parse(json);
    }

    // The API is asked only when the release page names a tag newer than both the installed version and the
    // release the API last returned, so an unchanged release costs none of the shared API allowance.
    public async Task<UpdateRelease?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = SemanticVersion.FromSystemVersion(currentVersion);
        if (await TryReadLatestTagAsync(cancellationToken).ConfigureAwait(false) is { } tagged)
        {
            if (tagged <= current)
            {
                return null;
            }

            if (Volatile.Read(ref _lastSeenRelease) is { } seen && seen.Version == tagged)
            {
                return seen;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var latest = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastSeenRelease, latest);
        return latest.Version > current ? latest : null;
    }

    public async Task<VerifiedInstaller> DownloadInstallerAsync(
        UpdateRelease release,
        string stagingDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ValidateReleaseForDownload(release);

        if (!Path.IsPathFullyQualified(stagingDirectory))
        {
            throw new ArgumentException("The update staging directory must be an absolute path.", nameof(stagingDirectory));
        }

        var fullDirectory = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException("The update staging directory does not exist.");
        }

        var destination = Path.Combine(fullDirectory, release.FileName);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("The canonical staged installer path already exists.");
        }

        var temporaryPath = Path.Combine(fullDirectory, $".{release.FileName}.{Guid.NewGuid():N}.partial");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadTimeout);

        try
        {
            progress?.Report(new UpdateDownloadProgress(0, release.Size));
            using var response = await SendDownloadHeadersAsync(release, deadline.Token).ConfigureAwait(false);
            RequireIdentityEncoding(response);
            if (response.Content.Headers.ContentRange is not null)
            {
                throw new UpdateSecurityException("Partial installer responses are not accepted.");
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size)
            {
                throw new UpdateSecurityException("The installer Content-Length does not match the immutable release metadata.");
            }

            await using (var output = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
            }))
            {
                await DownloadBodyAsync(response.Content, output, release, progress, deadline.Token)
                    .ConfigureAwait(false);
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            deadline.Token.ThrowIfCancellationRequested();
            InstallerArtifactVerifier.Verify(temporaryPath, release.Version, release.Size);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: false);
            return new VerifiedInstaller(destination, release.Version, release.Size, release.Sha256);
        }
        catch
        {
            TryDeletePartialFile(temporaryPath);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendDownloadHeadersAsync(
        UpdateRelease release,
        CancellationToken cancellationToken)
    {
        var current = release.DownloadUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var redirects = 0; ; redirects++)
        {
            if (redirects == 0)
            {
                ReleaseUriPolicy.RequireInitialDownloadUri(current, release.TagName, release.FileName);
            }
            else
            {
                ReleaseUriPolicy.RequireRedirectTarget(current, release.TagName, release.FileName);
            }

            if (!visited.Add(current.AbsoluteUri))
            {
                throw new UpdateSecurityException("The installer download entered a redirect cycle.");
            }

            using var request = UpdateTransport.CreateDownloadRequest(current);
            var response = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                RequireUnredirectedResponse(response, current);
                if (!IsRedirect(response.StatusCode))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new HttpRequestException("The installer endpoint did not return HTTP 200.",
                            inner: null, response.StatusCode);
                    }

                    return response;
                }

                if (redirects >= MaximumRedirects)
                {
                    throw new UpdateSecurityException("The installer download exceeded the redirect limit.");
                }

                var location = response.Headers.Location ??
                    throw new UpdateSecurityException("The installer redirect omitted its Location header.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                ReleaseUriPolicy.RequireRedirectTarget(current, release.TagName, release.FileName);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendHeadersAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_headerTimeout);
        var pending = _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        try
        {
            return await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            _ = DisposeLateResponseAsync(pending);
            throw;
        }
    }

    // The release page is not part of the API allowance. Any answer other than the exact tag redirect,
    // including a failed request, leaves the check to the API; a cancelled check stays cancelled.
    private async Task<SemanticVersion?> TryReadLatestTagAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = UpdateTransport.CreatePageRequest(ReleaseUriPolicy.LatestReleasePageUri);
            using var response = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);
            RequireUnredirectedResponse(response, ReleaseUriPolicy.LatestReleasePageUri);
            return response.StatusCode == HttpStatusCode.Found &&
                ReleaseUriPolicy.TryParseLatestTagRedirect(response.Headers.Location, out var version)
                    ? version
                    : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Until the wait the service asked for has passed, another request would only be refused again.
    private void ThrowWhileRateLimited()
    {
        lock (_rateLimitGate)
        {
            if (_rateLimitWait is { } wait && _clock.GetElapsedTime(_rateLimitStarted) < wait)
            {
                throw new UpdateRateLimitedException(_rateLimitStatus);
            }

            _rateLimitWait = null;
        }
    }

    // GitHub answers 403 or 429 once this network has used its allowance (unauthenticated: 60 requests an hour
    // per public address, shared with every other GitHub client behind it). Retry-After, or X-RateLimit-Reset
    // when nothing remains, says when a request can succeed again. A 403 with allowance left is a refusal for
    // another reason and stays an HTTP error. The wait is measured from the service's Date, not the PC clock.
    internal static bool TryReadRateLimit(HttpResponseMessage response, DateTimeOffset now, out TimeSpan? wait)
    {
        wait = null;
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        var serviceNow = response.Headers.Date ?? now;
        var exhausted = SingleHeader(response, "X-RateLimit-Remaining") == "0";
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            wait = retryAfter.Delta ?? retryAfter.Date - serviceNow;
        }
        else if (exhausted && long.TryParse(SingleHeader(response, "X-RateLimit-Reset"), NumberStyles.None,
                     CultureInfo.InvariantCulture, out var reset))
        {
            wait = TimeSpan.FromSeconds(Math.Clamp(reset - serviceNow.ToUnixTimeSeconds(), 0,
                (long)MaximumRateLimitWait.TotalSeconds));
        }

        if (wait is null && !exhausted && response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        if (wait is { } requested)
        {
            wait = requested < TimeSpan.Zero ? TimeSpan.Zero
                : requested > MaximumRateLimitWait ? MaximumRateLimitWait : requested;
        }

        return true;
    }

    private static string? SingleHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value] ? value.Trim() : null;

    private async Task<byte[]> ReadApiBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumApiResponseBytes)
        {
            throw new UpdateSecurityException("The latest-release response exceeds the 1 MiB limit.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_apiBodyTimeout);
        await using var stream = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var wanted = (int)Math.Min(buffer.Length, MaximumApiResponseBytes + 1L - bytes.Length);
                var count = await stream.ReadAsync(buffer.AsMemory(0, wanted), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (bytes.Length + count > MaximumApiResponseBytes)
                {
                    throw new UpdateSecurityException("The latest-release response exceeds the 1 MiB limit.");
                }

                bytes.Write(buffer, 0, count);
            }

            if (content.Headers.ContentLength is { } expectedLength && expectedLength != bytes.Length)
            {
                throw new UpdateSecurityException("The latest-release response length does not match Content-Length.");
            }

            return bytes.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DownloadBodyAsync(
        HttpContent content,
        FileStream output,
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long received = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                received = checked(received + count);
                if (received > release.Size)
                {
                    throw new UpdateSecurityException("The installer body exceeds the immutable release size.");
                }

                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(received, release.Size));
            }

            if (received != release.Size)
            {
                throw new UpdateSecurityException("The installer body is shorter than the immutable release size.");
            }

            var actualHash = hash.GetHashAndReset();
            var expectedHash = Convert.FromHexString(release.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new UpdateSecurityException("The installer SHA-256 does not match the immutable release digest.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateReleaseForDownload(UpdateRelease release)
    {
        if (!ReleaseIdentity.TryParseInstallerVersion(release.FileName, out var version) || version != release.Version ||
            release.Size is <= 0 or > MaximumInstallerSizeBytes ||
            !GitHubReleaseParser.TryNormalizeSha256($"sha256:{release.Sha256}", out var normalized) ||
            !string.Equals(normalized, release.Sha256, StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The release metadata is not a canonical Wisp installer.");
        }

        ReleaseIdentity.RequireMatchingTag(release.TagName, release.Version);
        ReleaseUriPolicy.RequireInitialDownloadUri(release.DownloadUri, release.TagName, release.FileName);
    }

    private static void RequireIdentityEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(encoding =>
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UpdateSecurityException("Encoded update responses are not accepted.");
        }
    }

    private static void RequireUnredirectedResponse(HttpResponseMessage response, Uri requestedUri)
    {
        var actualUri = response.RequestMessage?.RequestUri;
        if (actualUri is null ||
            !string.Equals(actualUri.AbsoluteUri, requestedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The transport followed an unauthorized redirect.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static async Task DisposeLateResponseAsync(Task<HttpResponseMessage> pending)
    {
        try
        {
            (await pending.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // A timed-out injected handler must not leak a late response or its contents.
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The randomized partial file is never executed. Cleanup can be retried by the staging owner.
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName,
                "Update transport timeouts must be positive and within their bounded maximum.");
        }
    }
}
