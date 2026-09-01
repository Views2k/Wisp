using System.Net;

namespace Wisp.Update.Tests;

internal sealed class ScriptedHttpHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _requestCount);
        var response = await responder(request, sequence, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }

    internal static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        return response;
    }
}
