using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class UpdateRequestTimingTests
{
    private static readonly Uri DeliveryUri =
        new("https://release-assets.githubusercontent.com/github-production-release-asset/12345/signed-installer?token=test");

    [Fact]
    public async Task InstallerRequestsWaitForTheDownloadDeadlineWhileChecksKeepTheShortOne()
    {
        // Two PCs on 2.7.3 whose checks took about 12 s timed out at 15 s on the installer's first request.
        using var handler = new ScriptedHttpHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                return ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, headerTimeout: TimeSpan.FromMilliseconds(100),
            downloadHeaderTimeout: TimeSpan.FromSeconds(30));
        using var directory = new TemporaryDirectory();
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        // The installer request waited past the check deadline and got its answer.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(false, "installer")]
    [InlineData(true, "installer-redirect-1")]
    public async Task ResponseTimeoutNamesTheRequestOfTheChainThatTimedOut(bool redirected, string expected)
    {
        using var handler = new ScriptedHttpHandler(async (request, sequence, cancellationToken) =>
        {
            if (sequence == 1)
            {
                return ScriptedHttpHandler.JsonResponse(ReleaseTestData.CreateJson());
            }

            if (sequence == 2 && redirected)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = DeliveryUri;
                return redirect;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay cannot complete.");
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, downloadHeaderTimeout: TimeSpan.FromMilliseconds(150));
        using var directory = new TemporaryDirectory();
        var release = await client.GetLatestReleaseAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadInstallerAsync(
            release, directory.Path, cancellationToken: TestContext.Current.CancellationToken));

        var timing = Assert.IsType<UpdateRequestTiming>(UpdateRequestTiming.From(error));
        Assert.Equal(expected, timing.Request);
        Assert.True(timing.Elapsed >= TimeSpan.FromMilliseconds(100));
        // The scripted handler opens no connection of its own: the whole wait is the response wait.
        Assert.Null(timing.Connect);
        Assert.Null(timing.Tls);
        Assert.Equal(timing.Elapsed, timing.Response);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task CheckTimeoutNamesTheApiRequest()
    {
        using var handler = new ScriptedHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay cannot complete.");
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http, headerTimeout: TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetLatestReleaseAsync(TestContext.Current.CancellationToken));

        Assert.Equal("release-api", UpdateRequestTiming.From(error)?.Request);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotATimeout()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new ScriptedHttpHandler(async (_, _, cancellationToken) =>
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay cannot complete.");
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new WispUpdateClient(http);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetLatestReleaseAsync(cancellation.Token));

        Assert.Null(UpdateRequestTiming.From(error));
    }

    [Fact]
    public async Task ProductionConnectionIsTimedForTheRequestThatOpenedIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = accepted.GetStream();
            var buffer = new byte[4096];
            var received = new StringBuilder();
            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
                if (count == 0) return;
                received.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }

            var response = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(response, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        using var http = UpdateTransport.CreateClient();
        var timer = new UpdateRequestTimer("probe", TimeProvider.System);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{port}/"));
        request.Options.Set(UpdateTransport.TimerOption, timer);

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        await server;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var timing = timer.Snapshot();
        Assert.Equal("probe", timing.Request);
        Assert.NotNull(timing.Connect);
        // Plain HTTP has no handshake; the stream filter still marks the connection ready.
        Assert.NotNull(timing.Tls);
        Assert.NotNull(timing.Response);
        Assert.True(timing.Connect + timing.Tls + timing.Response <= timing.Elapsed);
    }

    [Fact]
    public void TheLastPhasePresentIsTheOneTheDeadlineInterrupted()
    {
        var clock = new StepClock();
        var timer = new UpdateRequestTimer("installer", clock);
        clock.Advance(1);
        Assert.Equal(new UpdateRequestTiming("installer", Ms(1), null, null, Ms(1)), timer.Snapshot());

        timer.ConnectStarted();
        clock.Advance(10);
        Assert.Equal(new UpdateRequestTiming("installer", Ms(11), Ms(10), null, null), timer.Snapshot());

        timer.Connected();
        clock.Advance(12000);
        Assert.Equal(new UpdateRequestTiming("installer", Ms(12011), Ms(10), Ms(12000), null), timer.Snapshot());

        timer.Ready();
        clock.Advance(3000);
        Assert.Equal(new UpdateRequestTiming("installer", Ms(15011), Ms(10), Ms(12000), Ms(3000)), timer.Snapshot());
    }

    private static TimeSpan Ms(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private sealed class StepClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(long milliseconds) => _timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
