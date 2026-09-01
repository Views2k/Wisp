using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCompatibilityUpdateClientTests
{
    // Reserved test-only URI. Every configured request below uses a fake handler, never the network.
    private static readonly Uri TestEndpoint = new("https://compatibility.invalid/catalog.json");

    [Fact]
    public async Task UnconfiguredProductionClientStaysOffline()
    {
        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]>());
        using var client = new NativeCompatibilityUpdateClient(null, catalog);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.False(client.IsConfigured);
        Assert.False(client.IsChecking);
        Assert.Equal(NativeCompatibilityUpdateCode.NotConfigured, result.Code);
        Assert.Same(NativeHudBuildContract.BuiltIn, FindBuiltIn(catalog));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task BothEndpointAndPinnedPublisherAreRequiredBeforeAnyRequest(bool endpoint, bool pins)
    {
        using var fixture = new Fixture();
        var catalog = pins ? fixture.Catalog : new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]>());
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("No request was authorized."));
        using var http = new HttpClient(handler);
        using var client = new NativeCompatibilityUpdateClient(endpoint ? TestEndpoint : null, catalog, http);

        Assert.Equal(NativeCompatibilityUpdateCode.NotConfigured, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.False(client.IsConfigured);
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData("http://compatibility.invalid/catalog.json")]
    [InlineData("/catalog.json")]
    [InlineData("https://unused@compatibility.invalid/catalog.json")]
    [InlineData("https://compatibility.invalid/catalog.json#fragment")]
    public void EndpointMustBeExplicitAbsoluteHttpsWithoutCredentialsOrFragment(string value)
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler);

        Assert.Throws<ArgumentException>(() => new NativeCompatibilityUpdateClient(
            new Uri(value, UriKind.RelativeOrAbsolute), fixture.Catalog, http));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void ProductionHandlerCannotFollowRedirectsDecompressOrSendAmbientCookiesAndCredentials()
    {
        using var handler = NativeCompatibilityUpdateClient.CreateTransportHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.True(handler.CheckCertificateRevocationList);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
    }

    [Fact]
    public async Task SuccessfulResponseIsInstalledOnlyThroughTheSignedCatalog()
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(TestEndpoint, request.RequestUri);
            Assert.Null(request.Content);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.Contains(request.Headers.AcceptEncoding, value => value.Value == "identity");
            return Task.FromResult(Response(request, fixture.Envelope()));
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(NativeCompatibilityUpdateCode.Installed, result.Code);
        Assert.Equal(NativeCompatibilityInstallCode.Installed, result.Installation!.Code);
        Assert.NotNull(fixture.FindImported());
        Assert.Equal(1, fixture.Catalog.Generation);
        Assert.Same(result, client.LastResult);
        Assert.False(client.IsChecking);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task SubsequentExplicitCheckMakesOneAttemptAndLeavesIdenticalPackUnchanged()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope();
        using var handler = new FakeHandler((request, _) => Task.FromResult(Response(request, bytes)));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.True((await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Changed);
        var second = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(second.Success);
        Assert.False(second.Changed);
        Assert.Equal(NativeCompatibilityUpdateCode.UpToDate, second.Code);
        Assert.Equal(2, handler.SendCount);
        Assert.Equal(1, fixture.Catalog.Generation);
    }

    [Fact]
    public async Task MalformedResponseDoesNotReplaceKnownGoodPackOrEchoBody()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Catalog.Install(fixture.Envelope(5), fixture.Clock.GetUtcNow()).Success);
        using var handler = new FakeHandler((request, _) =>
            Task.FromResult(Response(request, Encoding.UTF8.GetBytes("DO_NOT_ECHO_RESPONSE_BODY"))));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.CatalogRejected, result.Code);
        Assert.Equal(NativeCompatibilityInstallCode.InvalidEnvelope, result.Installation!.Code);
        Assert.Equal(5, fixture.FindImported()!.Revision);
        Assert.Equal(1, fixture.Catalog.Generation);
        Assert.DoesNotContain("DO_NOT_ECHO", result.Message);
        Assert.DoesNotContain("DO_NOT_ECHO", client.Status);
    }

    [Fact]
    public async Task ValidSignatureFromAnUnpinnedKeyIsStillRejected()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        using var handler = new FakeHandler((request, _) => Task.FromResult(Response(request, other.Envelope())));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.CatalogRejected, result.Code);
        Assert.Equal(NativeCompatibilityInstallCode.UntrustedPublisher, result.Installation!.Code);
        Assert.Null(fixture.FindImported());
        Assert.Same(NativeHudBuildContract.BuiltIn, FindBuiltIn(fixture.Catalog));
    }

    [Fact]
    public async Task RemoteReplayCannotReplaceANewerAcceptedRevision()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Catalog.Install(fixture.Envelope(5), fixture.Clock.GetUtcNow()).Success);
        using var handler = new FakeHandler((request, _) => Task.FromResult(Response(request, fixture.Envelope(4))));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityInstallCode.RollbackRejected, result.Installation!.Code);
        Assert.Equal(5, fixture.FindImported()!.Revision);
        Assert.Equal(1, fixture.Catalog.Generation);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PackExpiringWhileRequestIsInFlightIsRejectedAtCompletionTime()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope();
        using var handler = new FakeHandler((request, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromDays(2));
            return Task.FromResult(Response(request, bytes));
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.CatalogRejected, result.Code);
        Assert.Equal(NativeCompatibilityInstallCode.Expired, result.Installation!.Code);
        Assert.Null(fixture.FindImported());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(206)]
    public async Task HttpFailureDoesNotReadResponseBodyOrRetry(int status)
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = request,
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.HttpFailure, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(1, handler.SendCount);
        Assert.True(stream.WasDisposed);
        Assert.Equal(0, fixture.Catalog.Generation);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(304)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RedirectIsRejectedWithoutFollowingLocationOrReadingBody(int status)
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((request, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = request,
                Content = new StreamContent(stream)
            };
            response.Headers.Location = new Uri("http://redirect.invalid/untrusted.json");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.RedirectRejected, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData("https://redirect.invalid/catalog.json")]
    [InlineData("https://compatibility.invalid/different.json")]
    [InlineData("http://compatibility.invalid/catalog.json")]
    public async Task UnexpectedFinalUriIsRejectedEvenIfInjectedHandlerReportsSuccess(string value)
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, value),
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.RedirectRejected, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(0, stream.BytesRead);
        Assert.Null(fixture.FindImported());
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task ContentEncodingIsRejectedWithoutDecompressionOrBodyRead(string encoding)
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentEncoding.Add(encoding);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.InvalidResponse, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task DeclaredOversizeIsRejectedBeforeReadingTheStream()
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentLength = NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 1L;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.TooLarge, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task UndeclaredOversizeStopsAfterLimitPlusOneByte()
    {
        using var fixture = new Fixture();
        var stream = new CountingStream(new byte[NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 4096]);
        using var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.TooLarge, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 1, stream.BytesRead);
        Assert.True(stream.WasDisposed);
        Assert.Equal(0, fixture.Catalog.Generation);
    }

    [Fact]
    public async Task ExactlyTheEnvelopeLimitCanStillCarryAValidSignedPack()
    {
        using var fixture = new Fixture();
        var signed = fixture.Envelope();
        var padded = new byte[NativeCompatibilityEnvelope.MaximumEnvelopeBytes];
        Array.Fill(padded, (byte)' ');
        signed.CopyTo(padded, 0);
        var stream = new CountingStream(padded);
        using var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.Installed, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(NativeCompatibilityEnvelope.MaximumEnvelopeBytes, stream.BytesRead);
        Assert.NotNull(fixture.FindImported());
    }

    [Fact]
    public async Task DeclaredLengthMismatchCannotInstallEvenASignedBody()
    {
        using var fixture = new Fixture();
        var bytes = fixture.Envelope();
        using var handler = new FakeHandler((request, _) =>
        {
            var response = Response(request, bytes);
            response.Content.Headers.ContentLength = bytes.Length + 1;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        Assert.Equal(NativeCompatibilityUpdateCode.InvalidResponse, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Null(fixture.FindImported());
    }

    [Fact]
    public async Task HeaderDeadlineBoundsTheAttemptWithoutRetry()
    {
        using var fixture = new Fixture();
        var never = NewSignal();
        using var handler = new FakeHandler(async (request, token) =>
        {
            await never.Task.WaitAsync(token);
            return Response(request, fixture.Envelope());
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http, headerTimeout: TimeSpan.FromMilliseconds(100));

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.TimedOut, result.Code);
        Assert.Equal(1, handler.SendCount);
        Assert.False(client.IsChecking);
        Assert.Null(fixture.FindImported());
    }

    [Fact]
    public async Task BodyDeadlineAppliesAfterHeadersHaveAlreadyCompleted()
    {
        using var fixture = new Fixture();
        var stream = new WaitingStream();
        using var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(stream)
        }));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http, bodyTimeout: TimeSpan.FromMilliseconds(100));

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.TimedOut, result.Code);
        Assert.True(stream.WasDisposed);
        Assert.Equal(1, handler.SendCount);
        Assert.Null(fixture.FindImported());
    }

    [Fact]
    public async Task LateResponseFromHandlerIgnoringCancellationIsDisposedAndNeverInstalled()
    {
        using var fixture = new Fixture();
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new CountingStream(fixture.Envelope());
        using var handler = new FakeHandler((_, _) => gate.Task);
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http, headerTimeout: TimeSpan.FromMilliseconds(100));

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(NativeCompatibilityUpdateCode.TimedOut, result.Code);
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, TestEndpoint),
            Content = new StreamContent(stream)
        });
        await stream.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.BytesRead);
        Assert.Null(fixture.FindImported());
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task TransportExceptionsNeverExposeTheirUntrustedDiagnosticText()
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException("DO_NOT_ECHO_REMOTE_DIAGNOSTIC"));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var result = await client.CheckOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NativeCompatibilityUpdateCode.NetworkFailure, result.Code);
        Assert.DoesNotContain("DO_NOT_ECHO", result.Message);
        Assert.DoesNotContain("DO_NOT_ECHO", client.Status);
        Assert.Equal(1, handler.SendCount);
        Assert.Same(NativeHudBuildContract.BuiltIn, FindBuiltIn(fixture.Catalog));
    }

    [Fact]
    public async Task OverlappingChecksCoalesceIntoOneRequestAndOneCatalogCommit()
    {
        using var fixture = new Fixture();
        var gate = NewSignal();
        var signed = fixture.Envelope();
        using var handler = new FakeHandler(async (request, token) =>
        {
            await gate.Task.WaitAsync(token);
            return Response(request, signed);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var checks = Enumerable.Range(0, 10).Select(_ => client.CheckOnceAsync()).ToArray();
        Assert.True(client.IsChecking);
        Assert.Equal(1, handler.SendCount);
        gate.SetResult();
        var results = await Task.WhenAll(checks);

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, fixture.Catalog.Generation);
        Assert.False(client.IsChecking);
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelAnotherWaitersSharedRequest()
    {
        using var fixture = new Fixture();
        var gate = NewSignal();
        var signed = fixture.Envelope();
        var requestToken = CancellationToken.None;
        using var handler = new FakeHandler(async (request, token) =>
        {
            requestToken = token;
            await gate.Task.WaitAsync(token);
            return Response(request, signed);
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);
        using var firstCancellation = new CancellationTokenSource();

        var first = client.CheckOnceAsync(firstCancellation.Token);
        var second = client.CheckOnceAsync(TestContext.Current.CancellationToken);
        firstCancellation.Cancel();
        Assert.Equal(NativeCompatibilityUpdateCode.Cancelled, (await first).Code);
        Assert.False(requestToken.IsCancellationRequested);
        Assert.True(client.IsChecking);
        gate.SetResult();

        Assert.True((await second).Success);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, fixture.Catalog.Generation);
    }

    [Fact]
    public async Task LastWaiterCancellationAbortsThePendingRequestWithoutInstallation()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var cancelled = NewSignal();
        var never = NewSignal();
        using var handler = new FakeHandler(async (request, token) =>
        {
            var wait = never.Task.WaitAsync(token);
            entered.TrySetResult();
            try
            {
                await wait;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }

            return Response(request, fixture.Envelope());
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);
        using var caller = new CancellationTokenSource();

        var pending = client.CheckOnceAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        caller.Cancel();
        Assert.Equal(NativeCompatibilityUpdateCode.Cancelled, (await pending).Code);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(fixture.FindImported());
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task PreCancelledCallerDoesNotStartAnAttempt()
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(NativeCompatibilityUpdateCode.Cancelled, (await client.CheckOnceAsync(cancellation.Token)).Code);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task DisposalCancelsPendingChecksAndPreventsNewRequests()
    {
        using var fixture = new Fixture();
        var never = NewSignal();
        using var handler = new FakeHandler(async (request, token) =>
        {
            await never.Task.WaitAsync(token);
            return Response(request, fixture.Envelope());
        });
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);

        var pending = client.CheckOnceAsync(TestContext.Current.CancellationToken);
        client.Dispose();

        Assert.Equal(NativeCompatibilityUpdateCode.Cancelled, (await pending).Code);
        Assert.Equal(NativeCompatibilityUpdateCode.Disposed, (await client.CheckOnceAsync(TestContext.Current.CancellationToken)).Code);
        Assert.Equal(1, handler.SendCount);
        Assert.Null(fixture.FindImported());
    }

    [Fact]
    public async Task DisposingClientDoesNotDisposeAnInjectedBorrowedHttpClient()
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((request, _) => Task.FromResult(Response(request, fixture.Envelope())));
        using var http = new HttpClient(handler);
        using var client = fixture.Client(http);
        client.Dispose();

        using var response = await http.GetAsync(TestEndpoint, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(0, fixture.Catalog.Generation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31000)]
    public void TransportTimeoutsCannotBecomeUnbounded(int milliseconds)
    {
        using var fixture = new Fixture();
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException());
        using var http = new HttpClient(handler);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Client(http, headerTimeout: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Client(http, bodyTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static NativeHudCompatibilityPack? FindBuiltIn(NativeCompatibilityCatalog catalog) => catalog.Find(
        NativeHudBuildContract.SupportedVersion, NativeHudBuildContract.SupportedExecutableLength,
        NativeHudBuildContract.SupportedSha256);

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new ByteArrayContent(bytes)
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            return send(request, cancellationToken);
        }
    }

    private class CountingStream(byte[] bytes) : Stream
    {
        private int _position;
        public int BytesRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public TaskCompletionSource DisposedSignal { get; } = NewSignal();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            BytesRead += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            DisposedSignal.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class WaitingStream() : CountingStream([])
    {
        private readonly TaskCompletionSource _never = NewSignal();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _never.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _keyId;
        private readonly JsonObject _pack;
        private readonly DateTimeOffset _issued;
        private readonly DateTimeOffset _expires;

        public Fixture()
        {
            var publicKey = _signer.ExportSubjectPublicKeyInfo();
            _keyId = NativeCompatibilitySignature.GetKeyId(publicKey);
            Catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
                new Dictionary<string, byte[]> { [_keyId] = publicKey });
            using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
            _pack = JsonNode.Parse(stream)!.AsObject();
            _pack["gameVersion"] = "6.430.772.0";
            _pack["executableSha256"] = new string('A', 64);
            _pack["id"] = "fh6-transport-test";
            _issued = Clock.GetUtcNow().AddMinutes(-1);
            _expires = Clock.GetUtcNow().AddDays(1);
        }

        public NativeCompatibilityCatalog Catalog { get; }
        public MutableClock Clock { get; } = new();

        public NativeCompatibilityUpdateClient Client(HttpClient http, TimeSpan? headerTimeout = null, TimeSpan? bodyTimeout = null) =>
            new(TestEndpoint, Catalog, http, headerTimeout, bodyTimeout, Clock);

        public NativeHudCompatibilityPack? FindImported() => Catalog.Find(
            "6.430.772.0", NativeHudBuildContract.SupportedExecutableLength, new string('A', 64));

        public byte[] Envelope(int revision = 2)
        {
            var pack = _pack.DeepClone().AsObject();
            pack["revision"] = revision;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["format"] = 1,
                ["purpose"] = "wisp-native-hud-compatibility",
                ["issuedUtc"] = _issued.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["expiresUtc"] = _expires.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["pack"] = pack
            });
            var signature = _signer.SignData(NativeCompatibilitySignature.CreateSigningInput(payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["format"] = 1,
                ["keyId"] = _keyId,
                ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(signature)
            });
        }

        public void Dispose() => _signer.Dispose();
    }
}
