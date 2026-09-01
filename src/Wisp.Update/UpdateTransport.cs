using System.Net;
using System.Net.Http.Headers;

namespace Wisp.Update;

internal static class UpdateTransport
{
    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseDefaultCredentials = false,
        PreAuthenticate = false,
        Credentials = null,
        UseProxy = false,
        Proxy = null,
        CheckCertificateRevocationList = true,
        MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 1
    };

    internal static HttpClient CreateClient() => new(CreateHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    internal static HttpRequestMessage CreateApiRequest(Uri uri)
    {
        var request = CreateRequest(uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    internal static HttpRequestMessage CreateDownloadRequest(Uri uri)
    {
        var request = CreateRequest(uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Wisp-Updater", "1.0"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }
}
