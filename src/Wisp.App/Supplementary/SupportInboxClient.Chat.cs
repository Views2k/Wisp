using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupportChatMessage(Guid Id, string Reference, string Sender, string Kind, string Title,
    string Message, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ReadAt, bool Diagnostics);
internal sealed record SupportChatSnapshot(int SchemaVersion, DateTimeOffset CheckedAt, SupportChatMessage[] Messages, string? NextCursor = null);

internal sealed partial class SupportInboxClient
{
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    private SupportChatSnapshot _chat = new(1, DateTimeOffset.MinValue, []);
    internal SupportChatSnapshot Chat => Volatile.Read(ref _chat);
    internal bool ChatOpen { get; set; }

    internal async Task RefreshChatAsync(CancellationToken cancellation = default, bool older = false, Guid[]? readIds = null)
    {
        if (!_hasSubmitted) { Status = "Start a conversation with Views."; Notify(); return; }
        if (!_enabled()) { Status = "Messages are temporarily paused."; Notify(); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        var entered = false;
        try
        {
            await _chatGate.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            var prior = Chat;
            if (older && prior.NextCursor is null) return;
            var response = await SendAsync(new { schemaVersion = 1, secret = _secret, action = "chat-read", cursor = older ? prior.NextCursor : null, readIds }, linked.Token).ConfigureAwait(false);
            if (response.Status != SupplementaryRequestStatus.Success) { Status = "Connection interrupted. Retrying automatically…"; return; }
            var next = JsonSerializer.Deserialize<SupportChatSnapshot>(response.Body, SupplementarySchema.Json);
            if (!ValidChat(next)) throw new JsonException();
            var retained = !older && next!.NextCursor is null ? [] : prior.Messages;
            var cached = retained.Select(m => m.Sender == "views" && m.ReadAt is null && readIds?.Contains(m.Id) == true ? m with { ReadAt = next!.CheckedAt } : m);
            var messages = cached.Concat(next!.Messages).GroupBy(m => (m.Sender, m.Id)).Select(g => g.Last())
                .Where(m => m.ExpiresAt > next.CheckedAt).OrderBy(m => m.CreatedAt).ThenBy(m => m.Sender).ThenBy(m => m.Id).ToArray();
            // After a long disconnection, expose the gap between the newest page
            // and the cached thread instead of silently skipping those messages.
            var gap = next.NextCursor is not null && !next.Messages.Any(m => prior.Messages.Any(p => p.Sender == m.Sender && p.Id == m.Id));
            var cursor = older || prior.CheckedAt == DateTimeOffset.MinValue || gap ? next.NextCursor : prior.NextCursor;
            Volatile.Write(ref _chat, next with { Messages = messages, NextCursor = cursor });
            Status = "";
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { Status = "Messages could not be read. Retrying automatically…"; }
        finally { if (entered) _chatGate.Release(); Notify(); }
    }

    internal static bool ValidChat(SupportChatSnapshot? value) => value is { SchemaVersion: 1, Messages.Length: <= 20 } &&
        (value.NextCursor is null || value.NextCursor.Length is > 0 and <= 80) &&
        value.Messages.All(m => m is not null && m.Id != Guid.Empty && m.Reference is { Length: 36 } && m.Reference.StartsWith("WSP-", StringComparison.Ordinal) &&
            (m.Sender == "player" && m.Kind == "message" || m.Sender == "views" && m.Kind is "reply" or "diagnostics-request") &&
            m.Title is { Length: > 0 and <= 100 } && m.Message is { Length: > 0 and <= 4000 } &&
            m.ExpiresAt > m.CreatedAt && m.ExpiresAt - m.CreatedAt <= TimeSpan.FromDays(30)) &&
        value.Messages.Select(m => (m.Sender, m.Id)).Distinct().Count() == value.Messages.Length;

    internal async Task<SupplementarySupportResult> SubmitChatAsync(SupplementarySupportReport report, CancellationToken cancellation)
    {
        if (!_enabled() || !_supportEnabled()) return new(SupplementaryRequestStatus.Disabled);
        if (!SupplementarySchema.Valid(report, DateTimeOffset.UtcNow) || report.ReplyTo is not null) return new(SupplementaryRequestStatus.Invalid);
        _hasSubmitted = true;
        try { _submitted?.Invoke(); } catch (Exception) { }
        var response = await SendAsync(new { schemaVersion = 1, secret = _secret, action = "chat-send", report }, cancellation).ConfigureAwait(false);
        if (response.HttpStatus == 400) return new(SupplementaryRequestStatus.Invalid);
        if (response.HttpStatus == 429) return new(SupplementaryRequestStatus.DailyLimit);
        var result = SupplementarySupportReceipts.Read(response, DateTimeOffset.UtcNow);
        if (result.Status == SupplementaryRequestStatus.Success) await RefreshChatAsync(cancellation).ConfigureAwait(false);
        return result;
    }
}
