using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupportChatTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static SupportChatMessage Message(string sender = "views") => new(Guid.NewGuid(), "WSP-" + new string('A', 32), sender,
        sender == "player" ? "message" : "reply", "Chat", "Please try once more.", Now, Now.AddDays(30), null, false);

    [Fact]
    public async Task HistoryMergesEarlierPagesAndReadReceiptsWithoutLosingMessages()
    {
        var incoming = Message(); var outgoing = Message("player") with { CreatedAt = Now.AddSeconds(-1), ExpiresAt = Now.AddDays(29) };
        var handler = new Handler();
        using var client = Client(handler);
        handler.Page(new(1, Now, [incoming], "1:p:" + outgoing.Id));
        await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        handler.Page(new(1, Now, [outgoing]));
        await client.RefreshChatAsync(TestContext.Current.CancellationToken, older: true);
        Assert.Equal(2, client.Chat.Messages.Length); Assert.Null(client.Chat.NextCursor);
        handler.Page(new(1, Now, [outgoing, incoming with { ReadAt = Now }]));
        await client.RefreshChatAsync(TestContext.Current.CancellationToken, readIds: [incoming.Id]);
        Assert.Equal(2, client.Chat.Messages.Length);
        Assert.NotNull(client.Chat.Messages.Single(m => m.Sender == "views").ReadAt);
        Assert.Contains("chat-read", handler.Requests.Last(), StringComparison.Ordinal);
        Assert.Contains("readIds", handler.Requests.Last(), StringComparison.Ordinal);
        handler.Body = "{}"; await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, client.Chat.Messages.Length);
        handler.Page(new(1, Now, [])); await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        Assert.Empty(client.Chat.Messages);
    }

    [Fact]
    public async Task ReconnectExposesMissingPagesWithoutResettingAnExpandedThread()
    {
        var old = Message(); var next = Message() with { CreatedAt = Now.AddMinutes(1), ExpiresAt = Now.AddDays(30) };
        var handler = new Handler(); using var client = Client(handler);
        handler.Page(new(1, Now, [old])); await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        var cursor = "1:v:" + old.Id;
        handler.Page(new(1, Now.AddMinutes(1), [next], cursor)); await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cursor, client.Chat.NextCursor);
        handler.Page(new(1, Now.AddMinutes(1), [old])); await client.RefreshChatAsync(TestContext.Current.CancellationToken, older: true);
        Assert.Null(client.Chat.NextCursor);
        handler.Page(new(1, Now.AddMinutes(1), [next], cursor)); await client.RefreshChatAsync(TestContext.Current.CancellationToken);
        Assert.Null(client.Chat.NextCursor); Assert.Equal(2, client.Chat.Messages.Length);
        await client.RefreshChatAsync(TestContext.Current.CancellationToken, readIds: [old.Id]);
        Assert.NotNull(client.Chat.Messages.Single(m => m.Id == old.Id).ReadAt);
    }

    [Fact]
    public async Task UncertainSendRetriesExactReportAndPausePreventsRequests()
    {
        var enabled = true; var handler = new Handler { Code = HttpStatusCode.ServiceUnavailable };
        using var client = new SupportInboxClient(new("https://example.invalid/"), new string('a', 64), () => { }, () => enabled, handler);
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, "2.6.11", "fixture", "stable", "feedback", "Chat", "Hello.");
        Assert.Equal(SupplementaryRequestStatus.Rejected, (await client.SubmitChatAsync(report, TestContext.Current.CancellationToken)).Status);
        await client.SubmitChatAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(handler.Requests[0], handler.Requests[1]);
        enabled = false;
        Assert.Equal(SupplementaryRequestStatus.Disabled, (await client.SubmitChatAsync(report, TestContext.Current.CancellationToken)).Status);
        await client.RefreshChatAsync(TestContext.Current.CancellationToken); Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void HistoryRejectsNullRowsDuplicatesAndOversizedPages()
    {
        var m = Message();
        Assert.True(SupportInboxClient.ValidChat(new(1, Now, [m])));
        Assert.False(SupportInboxClient.ValidChat(new(1, Now, [m, m])));
        Assert.False(SupportInboxClient.ValidChat(new(1, Now, [null!])));
        Assert.False(SupportInboxClient.ValidChat(new(1, Now, Enumerable.Range(0, 21).Select(_ => Message()).ToArray())));
    }

    private static SupportInboxClient Client(Handler handler) => new(new("https://example.invalid/"), new string('a', 64), () => { }, () => true, handler, hasSubmitted: true);
    private sealed class Handler : HttpMessageHandler
    {
        internal string Body = "{}";
        internal HttpStatusCode Code = HttpStatusCode.OK;
        internal List<string> Requests = [];
        internal void Page(SupportChatSnapshot page) => Body = JsonSerializer.Serialize(page, SupplementarySchema.Json);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(Code) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
