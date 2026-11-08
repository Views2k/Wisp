using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Wisp.Update;

internal static class UpdateTransport
{
    internal static readonly HttpRequestOptionsKey<UpdateRequestTimer> TimerOption = new("Wisp.Update.RequestTimer");

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PreAuthenticate = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        UseProxy = false,
        Proxy = null,
        SslOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.Online
        },
        MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 1,
        ConnectCallback = ConnectAsync
    };

    // The default connection (a dual-mode TCP socket with Nagle off), timed for the request that opened it.
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        context.InitialRequestMessage.Options.TryGetValue(TimerOption, out var timer);
        timer?.ConnectStarted();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            timer?.Connected();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

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

    internal static HttpRequestMessage CreatePageRequest(Uri uri)
    {
        var request = CreateRequest(uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
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
