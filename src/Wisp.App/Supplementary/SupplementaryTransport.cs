using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryConfiguration(Uri? Origin)
{
    // Deployment must deliberately pin a real origin and independently provisioned public key. No test trust ships.
    internal static SupplementaryConfiguration Unconfigured { get; } = new((Uri?)null);
}
internal sealed record SupplementaryResponse(SupplementaryRequestStatus Status, byte[] Body, int HttpStatus = 0);
internal interface ISupplementaryTransport : IDisposable
{
    Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximumResponseBytes, CancellationToken cancellation);
}

internal sealed class SupplementaryHttpTransport : ISupplementaryTransport
{
    private readonly Uri _origin;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal SupplementaryHttpTransport(Uri origin, TimeProvider clock, HttpMessageHandler? testHandler = null)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != "https" || origin.Port != 443 || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("The supplementary service requires a fixed HTTPS origin.", nameof(origin));
        _origin = origin;
        _clock = clock;
        _http = new HttpClient(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
            Credentials = null, PreAuthenticate = false, MaxConnectionsPerServer = 1, MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximumResponseBytes, CancellationToken cancellation)
    {
        if (route is not ("/api/v1/content" or "/api/v1/events" or "/api/v1/support") ||
            (route == "/api/v1/content") != (body is null) || maximumResponseBytes is <= 0 or > 128 * 1024 ||
            body?.Length > (route == "/api/v1/support" ? SupplementarySchema.MaximumSupportBytes : SupplementarySchema.MaximumBatchBytes))
            return new(SupplementaryRequestStatus.Invalid, []);
        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(_origin, route));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code == 204 && body is null) return new(SupplementaryRequestStatus.Success, [], code);
            var supportLimit = route == "/api/v1/support" && code == 429;
            if (!response.IsSuccessStatusCode && !supportLimit)
                return new(code is 408 or 429 or >= 500 ? SupplementaryRequestStatus.Unavailable : SupplementaryRequestStatus.Rejected, [], code);
            if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > maximumResponseBytes)
                return new(SupplementaryRequestStatus.Rejected, [], code);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(Math.Min(maximumResponseBytes, 4096));
            var block = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length, maximumResponseBytes - (int)buffer.Length + 1)), linked.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > maximumResponseBytes) return new(SupplementaryRequestStatus.Rejected, [], code);
                buffer.Write(block, 0, read);
            }
            return new(supportLimit ? SupplementaryRequestStatus.Unavailable : SupplementaryRequestStatus.Success, buffer.ToArray(), code);
        }
        catch (OperationCanceledException)
        { return new(cancellation.IsCancellationRequested ? SupplementaryRequestStatus.Cancelled : SupplementaryRequestStatus.TimedOut, []); }
        catch (Exception error) when (error is HttpRequestException or IOException or ObjectDisposedException)
        { return new(SupplementaryRequestStatus.Unavailable, []); }
    }
    public void Dispose() => _http.Dispose();
}
