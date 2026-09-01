using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wisp.App;

public enum NativeCompatibilityUpdateCode
{
    NotConfigured,
    Installed,
    UpToDate,
    Cancelled,
    Disposed,
    TimedOut,
    NetworkFailure,
    HttpFailure,
    RedirectRejected,
    InvalidResponse,
    TooLarge,
    CatalogRejected
}

public sealed record NativeCompatibilityUpdateResult(
    NativeCompatibilityUpdateCode Code,
    bool Changed,
    string Message,
    NativeCompatibilityInstallResult? Installation = null)
{
    public bool Success => Code is NativeCompatibilityUpdateCode.Installed or NativeCompatibilityUpdateCode.UpToDate;
}

/// <summary>
/// Optional, single-attempt transport for one explicitly configured HTTPS signed-envelope endpoint.
/// It has no endpoint discovery, schedule, retry loop, telemetry dependency, or unauthenticated install path.
/// The application remains offline-first: absent endpoint or publisher pins means no request is made.
/// </summary>
public sealed class NativeCompatibilityUpdateClient : IDisposable
{
    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultBodyTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Uri? _endpoint;
    private readonly NativeCompatibilityCatalog _catalog;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _bodyTimeout;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private Operation? _inFlight;
    private NativeCompatibilityUpdateResult? _lastResult;
    private string _status;
    private bool _disposed;

    public NativeCompatibilityUpdateClient(Uri? endpoint, NativeCompatibilityCatalog catalog)
        : this(endpoint, catalog, CreateHttpClient(), ownsHttpClient: true,
            DefaultHeaderTimeout, DefaultBodyTimeout, TimeProvider.System)
    {
    }

    // Only tests/internal composition can inject a client. Its handler must disable redirects and decompression.
    // HttpClient does not expose handler configuration, so production owns its hardened handler instead.
    internal NativeCompatibilityUpdateClient(
        Uri? endpoint,
        NativeCompatibilityCatalog catalog,
        HttpClient httpClient,
        TimeSpan? headerTimeout = null,
        TimeSpan? bodyTimeout = null,
        TimeProvider? clock = null)
        : this(endpoint, catalog, httpClient, ownsHttpClient: false,
            headerTimeout ?? DefaultHeaderTimeout, bodyTimeout ?? DefaultBodyTimeout, clock ?? TimeProvider.System)
    {
    }

    private NativeCompatibilityUpdateClient(
        Uri? endpoint,
        NativeCompatibilityCatalog catalog,
        HttpClient httpClient,
        bool ownsHttpClient,
        TimeSpan headerTimeout,
        TimeSpan bodyTimeout,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (endpoint is not null && (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
                                     endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0))
        {
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

            throw new ArgumentException("Compatibility updates require an absolute HTTPS endpoint without credentials or a fragment.", nameof(endpoint));
        }

        ValidateTimeout(headerTimeout, nameof(headerTimeout));
        ValidateTimeout(bodyTimeout, nameof(bodyTimeout));
        _endpoint = endpoint;
        _catalog = catalog;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _headerTimeout = headerTimeout;
        _bodyTimeout = bodyTimeout;
        _clock = clock;
        _status = IsConfigured ? "Compatibility update checks are ready." : "Compatibility updates are not configured.";
    }

    public bool IsConfigured => _endpoint is not null && _catalog.HasTrustedPublishers;
    public string Status => Volatile.Read(ref _status);
    public NativeCompatibilityUpdateResult? LastResult => Volatile.Read(ref _lastResult);
    public bool IsChecking
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is not null;
            }
        }
    }

    /// <summary>
    /// Concurrent callers share one request. Cancelling one caller stops its wait, not other callers;
    /// when the last caller leaves, the shared operation is cancelled. A later call is one new attempt, never a retry loop.
    /// </summary>
    public async Task<NativeCompatibilityUpdateResult> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledWait();
        }

        Operation operation;
        lock (_gate)
        {
            if (_disposed)
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.Disposed, false,
                    "The compatibility update client is closed.");
            }

            if (!IsConfigured)
            {
                var unconfigured = new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.NotConfigured, false,
                    "Compatibility updates require an explicitly configured HTTPS endpoint and pinned publisher key.");
                Publish(unconfigured);
                return unconfigured;
            }

            if (_inFlight?.IsCancelling == true)
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.Cancelled, false,
                    "The previous compatibility check is still stopping; no additional request was started.");
            }

            if (_inFlight is null)
            {
                operation = new Operation(_lifetime.Token) { Waiters = 1 };
                _inFlight = operation;
                Volatile.Write(ref _status, "Checking for signed compatibility updates.");
                operation.Task = RunOperationAsync(operation);
            }
            else
            {
                operation = _inFlight;
                operation.Waiters++;
            }
        }

        try
        {
            return await operation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelledWait();
        }
        finally
        {
            bool cancelShared;
            lock (_gate)
            {
                operation.Waiters--;
                cancelShared = operation.Waiters == 0 && !operation.Finished;
                if (cancelShared)
                {
                    operation.IsCancelling = true;
                }
            }

            if (cancelShared)
            {
                CancelSafely(operation.Cancellation);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Cancellation invokes callbacks; never hold the state lock across them.
        CancelSafely(_lifetime);
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _lifetime.Dispose();
    }

    private async Task<NativeCompatibilityUpdateResult> RunOperationAsync(Operation operation)
    {
        try
        {
            var result = await FetchAndInstallAsync(operation.Cancellation.Token).ConfigureAwait(false);
            Publish(result);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                operation.Finished = true;
                if (ReferenceEquals(_inFlight, operation))
                {
                    _inFlight = null;
                }

            }

            operation.Cancellation.Dispose();
        }
    }

    private async Task<NativeCompatibilityUpdateResult> FetchAndInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using var response = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.RedirectRejected, false,
                    "Compatibility update redirects are not allowed; the offline catalog is unchanged.");
            }

            // Defense in depth for injected handlers. Production prevents the redirect before any follow-up request.
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || !finalUri.IsAbsoluteUri || finalUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(finalUri.AbsoluteUri, _endpoint!.AbsoluteUri, StringComparison.Ordinal))
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.RedirectRejected, false,
                    "The compatibility response did not come from the configured HTTPS endpoint.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.HttpFailure, false,
                    "The compatibility endpoint did not return a complete successful response; the offline catalog is unchanged.");
            }

            if (response.Content.Headers.ContentEncoding.Any(encoding =>
                    !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
            {
                return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.InvalidResponse, false,
                    "Compressed compatibility responses are not accepted.");
            }

            var bytes = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Use completion time, not request-start time: a pack which expires in flight cannot be freshly accepted.
            var installed = _catalog.Install(bytes, _clock.GetUtcNow());
            return new NativeCompatibilityUpdateResult(
                installed.Success
                    ? installed.Changed ? NativeCompatibilityUpdateCode.Installed : NativeCompatibilityUpdateCode.UpToDate
                    : NativeCompatibilityUpdateCode.CatalogRejected,
                installed.Changed, installed.Message, installed);
        }
        catch (RejectedResponseException exception)
        {
            return new NativeCompatibilityUpdateResult(exception.Code, false, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.Cancelled, false,
                    "The compatibility check was cancelled before installation.")
                : new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.TimedOut, false,
                    "The compatibility update timed out; the offline catalog is unchanged.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ObjectDisposedException)
        {
            return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.NetworkFailure, false,
                "The compatibility update is unavailable; the offline catalog is unchanged.");
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new NativeCompatibilityUpdateResult(NativeCompatibilityUpdateCode.InvalidResponse, false,
                "The compatibility response could not be safely read; the offline catalog is unchanged.");
        }
    }

    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
            // Also bound a misbehaving injected handler which ignores cancellation; dispose any late response.
            _ = DisposeLateResultAsync(pending);
            throw;
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximum = NativeCompatibilityEnvelope.MaximumEnvelopeBytes;
        var expectedLength = content.Headers.ContentLength;
        if (expectedLength is > maximum)
        {
            throw new RejectedResponseException(NativeCompatibilityUpdateCode.TooLarge,
                "The compatibility response exceeds the 128 KiB limit.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_bodyTimeout);
        var pendingStream = content.ReadAsStreamAsync(deadline.Token);
        Stream stream;
        try
        {
            stream = await pendingStream.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            _ = DisposeLateResultAsync(pendingStream);
            throw;
        }

        using (stream)
        using (var bytes = new MemoryStream())
        {
            // One extra byte detects unadvertised/chunked oversize. No body is buffered by HttpClient first.
            var buffer = new byte[8192];
            while (true)
            {
                var wanted = (int)Math.Min(buffer.Length, maximum + 1L - bytes.Length);
                var count = await stream.ReadAsync(buffer.AsMemory(0, wanted), deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (bytes.Length + count > maximum)
                {
                    throw new RejectedResponseException(NativeCompatibilityUpdateCode.TooLarge,
                        "The compatibility response exceeds the 128 KiB limit.");
                }

                bytes.Write(buffer, 0, count);
            }

            if (expectedLength is not null && bytes.Length != expectedLength.Value)
            {
                throw new RejectedResponseException(NativeCompatibilityUpdateCode.InvalidResponse,
                    "The compatibility response length does not match its declared length.");
            }

            return bytes.ToArray();
        }
    }

    private static async Task DisposeLateResultAsync<T>(Task<T> pending) where T : IDisposable
    {
        try
        {
            (await pending.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // No diagnostic from an abandoned transport operation is allowed to expose URLs or response contents.
        }
    }

    private void Publish(NativeCompatibilityUpdateResult result)
    {
        Volatile.Write(ref _lastResult, result);
        Volatile.Write(ref _status, result.Message);
    }

    private static NativeCompatibilityUpdateResult CancelledWait() =>
        new(NativeCompatibilityUpdateCode.Cancelled, false, "Stopped waiting for the compatibility check.");

    private static void CancelSafely(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation can finish and dispose its token between releasing the state lock and cancelling it.
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Compatibility transport timeouts must be positive and at most 30 seconds.");
        }
    }

    internal static HttpClientHandler CreateTransportHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseDefaultCredentials = false,
        PreAuthenticate = false,
        CheckCertificateRevocationList = true,
        MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 1
    };

    private static HttpClient CreateHttpClient() => new(CreateTransportHandler())
    {
        // Header and body deadlines are enforced separately because ResponseHeadersRead does not time out the body.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private sealed class Operation(CancellationToken lifetime)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task<NativeCompatibilityUpdateResult> Task { get; set; } = null!;
        public int Waiters { get; set; }
        public bool Finished { get; set; }
        public bool IsCancelling { get; set; }
    }

    private sealed class RejectedResponseException(NativeCompatibilityUpdateCode code, string message) : Exception(message)
    {
        public NativeCompatibilityUpdateCode Code { get; } = code;
    }
}
