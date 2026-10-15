using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Supplementary;

internal sealed record SupportInboxMessage(Guid Id, string Reference, string Kind, string Title, string Message,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ReadAt, bool Replied = false);
internal sealed record SupportInboxSnapshot(int SchemaVersion, DateTimeOffset CheckedAt, SupportInboxMessage[] Messages);

internal sealed class SupportInboxClient : IDisposable
{
    private readonly string _secret;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action _changed;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _supportEnabled;
    private readonly Action? _submitted;
    private readonly Action? _captureDiagnostics;
    private bool _hasSubmitted;
    private Task _completion = Task.CompletedTask;
    private readonly object _activationGate = new();
    private CancellationTokenSource? _pollCancellation;
    private bool _disposed;
    private SupportInboxSnapshot _snapshot = new(1, DateTimeOffset.MinValue, []);
    internal SupportInboxSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal string Status { get; private set; } = "Checking for replies…";

    internal SupportInboxClient(Uri origin, string secret, Action changed, Func<bool> enabled, HttpMessageHandler? handler = null,
        bool hasSubmitted = false, Action? submitted = null, Func<bool>? supportEnabled = null, Action? captureDiagnostics = null)
    {
        if (origin.Scheme != "https" || origin.Port != 443 || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Messages require the configured HTTPS service.", nameof(origin));
        if (secret.Length != 64 || secret.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid inbox identity.", nameof(secret));
        _secret = secret; _endpoint = new(origin, "/api/v1/inbox"); _changed = changed; _enabled = enabled;
        _hasSubmitted = hasSubmitted; _submitted = submitted; _captureDiagnostics = captureDiagnostics;
        _supportEnabled = supportEnabled ?? enabled;
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 1, ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 16 }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    internal void Start() => SetActive(true);
    internal void SetActive(bool active)
    {
        lock (_activationGate)
        {
            if (_disposed) return;
            if (!active || !_enabled()) { _pollCancellation?.Cancel(); return; }
            if (_pollCancellation is { IsCancellationRequested: false }) return;
            var previous = _pollCancellation;
            var previousCompletion = _completion;
            var poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pollCancellation = poll;
            _completion = Task.Run(() => RunAsync(poll.Token));
            if (previous is not null) _ = previousCompletion.ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        }
    }
    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (_enabled())
                    try { _captureDiagnostics?.Invoke(); } catch (Exception) { }
                await RefreshAsync(cancellation).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(25 + Random.Shared.NextDouble() * 5), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Status = "Replies are unavailable. Reopen Wisp to retry."; Notify(); }
    }
    internal async Task RefreshAsync(CancellationToken cancellation = default, Guid? read = null)
    {
        if (!_hasSubmitted) { Status = "Replies to your messages will appear here."; Notify(); return; }
        if (!_enabled()) { Status = "Messages are temporarily paused."; Notify(); return; }
        var result = await SendAsync(new { schemaVersion = 1, secret = _secret, action = read.HasValue ? "mark-read" : "read", messageId = read }, cancellation).ConfigureAwait(false);
        if (result.Status != SupplementaryRequestStatus.Success) { Status = "Unable to check replies. Wisp will retry automatically."; Notify(); return; }
        try
        {
            var next = JsonSerializer.Deserialize<SupportInboxSnapshot>(result.Body, SupplementarySchema.Json);
            if (!Valid(next)) throw new JsonException();
            Volatile.Write(ref _snapshot, next!);
            Status = next!.Messages.Length == 0 ? "No replies yet." : "Replies are up to date.";
        }
        catch (JsonException) { Status = "Replies could not be read. Wisp will retry automatically."; }
        Notify();
    }
    internal static bool Valid(SupportInboxSnapshot? snapshot) => snapshot is { SchemaVersion: 1, Messages.Length: <= 30 } &&
        snapshot.Messages.All(m => m is not null && m.Id != Guid.Empty && m.Reference is { Length: 36 } && m.Reference.StartsWith("WSP-", StringComparison.Ordinal) &&
            m.Kind is "reply" or "diagnostics-request" && m.Title is { Length: > 0 and <= 100 } && m.Message is { Length: > 0 and <= 4000 } &&
            m.ExpiresAt > m.CreatedAt && m.ExpiresAt - m.CreatedAt <= TimeSpan.FromDays(30)) &&
        snapshot.Messages.Select(m => m.Id).Distinct().Count() == snapshot.Messages.Length;

    internal async Task<SupplementarySupportResult> SubmitSupportAsync(SupplementarySupportReport report, CancellationToken cancellation)
    {
        if (!_supportEnabled() || !_enabled() && (report.ReplyTo is not null || report.RecentDiagnostics is not null))
            return new(SupplementaryRequestStatus.Disabled);
        // A lost acknowledgement must not leave an accepted case's future replies
        // undiscoverable after restart. Polling an empty, unregistered inbox is safe.
        _hasSubmitted = true;
        try { _submitted?.Invoke(); } catch (Exception) { }
        var response = await SendAsync(new { schemaVersion = 1, secret = _secret, action = "support", report }, cancellation).ConfigureAwait(false);
        var result = SupplementarySupportReceipts.Read(response, DateTimeOffset.UtcNow);
        if (result.Status == SupplementaryRequestStatus.Success && _enabled())
        {
            await RefreshAsync(cancellation).ConfigureAwait(false);
        }
        return result;
    }
    private async Task<SupplementaryResponse> SendAsync(object body, CancellationToken cancellation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token, deadline.Token);
        try
        {
            var bytes = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(body, SupplementarySchema.Json), linked.Token).ConfigureAwait(false);
            if (bytes.Length > 2 * 1024 * 1024) return new(SupplementaryRequestStatus.Invalid, []);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > 524288)
                return new(SupplementaryRequestStatus.Rejected, [], code);
            await using var input = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > 524288) return new(SupplementaryRequestStatus.Rejected, [], code);
                output.Write(buffer, 0, count);
            }
            return new(response.IsSuccessStatusCode ? SupplementaryRequestStatus.Success : code == 429 ? SupplementaryRequestStatus.Unavailable : SupplementaryRequestStatus.Rejected, output.ToArray(), code);
        }
        catch (OperationCanceledException) { return new(cancellation.IsCancellationRequested || _lifetime.IsCancellationRequested ? SupplementaryRequestStatus.Cancelled : SupplementaryRequestStatus.TimedOut, []); }
        catch (Exception e) when (e is HttpRequestException or IOException or ObjectDisposedException) { return new(SupplementaryRequestStatus.Unavailable, []); }
    }
    private void Notify() { try { _changed(); } catch (Exception) { } }
    public void Dispose()
    {
        lock (_activationGate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _http.Dispose();
            var poll = _pollCancellation;
            _ = _completion.ContinueWith(_ => { poll?.Dispose(); _lifetime.Dispose(); }, TaskScheduler.Default);
        }
    }

    internal static string? LoadOrCreateSecret(string directory, string ownedRoot)
    {
        try
        {
            SupplementaryPaths.CheckDirectory(directory, ownedRoot);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "support-inbox.key");
            byte[] entropy = Encoding.UTF8.GetBytes("Wisp support inbox v1");
            if (!File.Exists(path))
            {
                var secret = RandomNumberGenerator.GetBytes(32);
                var encrypted = ProtectedData.Protect(secret, entropy, DataProtectionScope.CurrentUser);
                try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 512, FileOptions.WriteThrough); file.Write(encrypted); file.Flush(true); }
                catch (IOException) when (File.Exists(path)) { }
                CryptographicOperations.ZeroMemory(secret);
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4096) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            if (clear.Length != 32) return null;
            var value = Convert.ToHexString(clear).ToLowerInvariant(); CryptographicOperations.ZeroMemory(clear); return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or System.Security.SecurityException) { return null; }
    }
}
