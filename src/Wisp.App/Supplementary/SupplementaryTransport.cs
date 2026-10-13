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
internal sealed record SupplementaryResponse(SupplementaryRequestStatus Status, byte[] Body, int HttpStatus = 0,
    DateTimeOffset? ServerTime = null, int? UncertaintyMs = null);
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
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximumResponseBytes, CancellationToken cancellation)
    {
        if (route is not ("/api/v1/content" or "/api/v1/feature-controls" or "/api/v1/events" or "/api/v1/support") ||
            (route is "/api/v1/content" or "/api/v1/feature-controls") != (body is null) || maximumResponseBytes is <= 0 or > 128 * 1024 ||
            body?.Length > (route == "/api/v1/support" ? SupplementarySchema.MaximumSupportBytes : SupplementarySchema.MaximumBatchBytes))
            return new(SupplementaryRequestStatus.Invalid, []);
        using var timeout = new CancellationTokenSource(RequestTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try
        {
            var started = _clock.GetTimestamp();
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(_origin, route));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            SupplementaryResponse Success(byte[] bytes)
            {
                DateTimeOffset? serverTime = null;
                int? uncertainty = null;
                var elapsed = _clock.GetElapsedTime(started, _clock.GetTimestamp()).TotalMilliseconds;
                if (body is null && response.Headers.CacheControl?.NoStore == true && elapsed is >= 0 and <= 15000 &&
                    response.Headers.TryGetValues("X-Wisp-Server-Time", out var times))
                {
                    var values = times.Take(2).ToArray();
                    if (values.Length == 1 && DateTimeOffset.TryParseExact(values[0], "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                    { serverTime = parsed; uncertainty = (int)Math.Ceiling(elapsed); }
                }
                return new(SupplementaryRequestStatus.Success, bytes, code, serverTime, uncertainty);
            }
            if (code == 204 && body is null) return Success([]);
            var supportLimit = route == "/api/v1/support" && code == 429;
            var validationError = code == 400 && body is not null;
            if (!response.IsSuccessStatusCode && !supportLimit && !validationError)
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
            if (validationError)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(buffer.ToArray(), new System.Text.Json.JsonDocumentOptions { MaxDepth = 8 });
                    var root = document.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == System.Text.Json.JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1 &&
                        root.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object && error.TryGetProperty("code", out var reason) &&
                        reason.ValueKind == System.Text.Json.JsonValueKind.String && reason.GetString() == "invalid_time")
                        return new(SupplementaryRequestStatus.ClockSkew, [], code);
                }
                catch (System.Text.Json.JsonException) { }
                return new(SupplementaryRequestStatus.Rejected, [], code);
            }
            return supportLimit ? new(SupplementaryRequestStatus.Unavailable, buffer.ToArray(), code) : Success(buffer.ToArray());
        }
        catch (OperationCanceledException)
        { return new(cancellation.IsCancellationRequested ? SupplementaryRequestStatus.Cancelled : SupplementaryRequestStatus.TimedOut, []); }
        catch (Exception error) when (error is HttpRequestException or IOException or ObjectDisposedException)
        { return new(SupplementaryRequestStatus.Unavailable, []); }
    }
    public void Dispose() => _http.Dispose();
}
