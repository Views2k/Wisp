using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupportInboxTests
{
    private const string Secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [Fact]
    public async Task MalformedInboxDoesNotReplaceKnownRepliesAndNextCheckRecovers()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new SupportInboxMessage(Guid.NewGuid(), "WSP-" + new string('A', 32), "reply", "Connection", "Please try again.", now, now.AddDays(1), null);
        var handler = new Handler();
        using var client = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => true, handler, hasSubmitted: true);
        handler.Body = JsonSerializer.Serialize(new SupportInboxSnapshot(1, now, [message]), SupplementarySchema.Json);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.Snapshot.Messages);
        handler.Body = "{\"schemaVersion\":1,\"checkedAt\":\"2026-10-14T00:00:00.000Z\",\"messages\":[null]}";
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Single(client.Snapshot.Messages);
        handler.Body = JsonSerializer.Serialize(new SupportInboxSnapshot(1, now, [message with { ReadAt = now }]), SupplementarySchema.Json);
        await client.RefreshAsync(TestContext.Current.CancellationToken, message.Id);
        Assert.NotNull(client.Snapshot.Messages[0].ReadAt);
        using var request = JsonDocument.Parse(handler.LastRequest!);
        Assert.Equal("mark-read", request.RootElement.GetProperty("action").GetString());
        Assert.Equal(Secret, request.RootElement.GetProperty("secret").GetString());
        Assert.Equal("/api/v1/inbox", handler.Path);
    }

    [Fact]
    public async Task NoInboxPollingBeforeThePlayerSendsSupportAndPausePreventsReads()
    {
        var handler = new Handler();
        using var unregistered = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => true, handler);
        await unregistered.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Calls);
        using var paused = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => false, handler, hasSubmitted: true);
        await paused.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FullUnicodeInboxFitsTheBoundedResponse()
    {
        var now = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 30).Select(_ => new SupportInboxMessage(Guid.NewGuid(), "WSP-" + new string('A', 32), "reply", "Connection", new string('界', 4000), now, now.AddDays(1), null)).ToArray();
        var handler = new Handler { Body = JsonSerializer.Serialize(new SupportInboxSnapshot(1, now, messages), new JsonSerializerOptions(SupplementarySchema.Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) };
        using var client = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => true, handler, hasSubmitted: true);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(30, client.Snapshot.Messages.Length);
    }

    [Fact]
    public void InboxSecretIsStableEncryptedAndCorruptionDoesNotCreateAnotherIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "wisp-inbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "Supplementary");
            var first = SupportInboxClient.LoadOrCreateSecret(directory, root);
            Assert.NotNull(first); Assert.Equal(64, first.Length);
            Assert.Equal(first, SupportInboxClient.LoadOrCreateSecret(directory, root));
            var path = Path.Combine(directory, "support-inbox.key");
            Assert.DoesNotContain(first, Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(SupportInboxClient.LoadOrCreateSecret(directory, root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VoluntarySupportWhileOffBindsItsCaseWithoutStartingInboxReads()
    {
        var handler = new Handler { Body = "{\"schemaVersion\":1,\"reference\":\"WSP-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}" };
        var persisted = false;
        using var client = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => false, handler,
            submitted: () => persisted = true, supportEnabled: () => true);
        client.SetActive(false);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            "2.6.9", "fixture", "stable", "feedback", "Connection", "Please check this connection.");
        var result = await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(SupplementaryRequestStatus.Success, result.Status);
        Assert.True(persisted);
        Assert.Equal(1, handler.Calls);
        using var request = JsonDocument.Parse(handler.LastRequest!);
        Assert.Equal("support", request.RootElement.GetProperty("action").GetString());
        Assert.Equal(Secret, request.RootElement.GetProperty("secret").GetString());
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(SupplementaryRequestStatus.Disabled,
            (await client.SubmitSupportAsync(report with { ReplyTo = Guid.NewGuid() }, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExplicitDisableCancelsInboxPollAndReEnableStartsOneNewPoll()
    {
        var enabled = false;
        var handler = new BlockingHandler();
        using var client = new SupportInboxClient(new("https://example.invalid/"), Secret, () => { }, () => enabled, handler, hasSubmitted: true);
        client.SetActive(false);
        Assert.Equal(0, handler.Calls);
        enabled = true; client.SetActive(true); client.SetActive(true);
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);
        enabled = false; client.SetActive(false);
        await handler.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        enabled = true; client.SetActive(true);
        await handler.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
        enabled = false; client.SetActive(false);
        await handler.SecondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal int Calls;
        internal readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            FirstCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously), SecondCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = Interlocked.Increment(ref Calls) == 1;
            (first ? FirstEntered : SecondEntered).TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { (first ? FirstCancelled : SecondCancelled).TrySetResult(); throw; }
            return new(HttpStatusCode.NoContent);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string Body = "{}";
        internal int Calls;
        internal string? LastRequest, Path;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Path = request.RequestUri!.AbsolutePath;
            LastRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
