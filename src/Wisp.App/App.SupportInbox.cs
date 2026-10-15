using Wisp.App.Supplementary;

namespace Wisp.App;

public partial class App
{
    private SupportInboxClient? _supportInbox;
    private void InitializeSupportInbox(string directory, string ownedRoot)
    {
        try
        {
            var secret = SupportInboxClient.LoadOrCreateSecret(directory, ownedRoot);
            if (secret is not null && SupplementaryRuntime.Configuration.Origin is { } origin)
            {
                var active = System.IO.Path.Combine(directory, "support-inbox.active");
                var submitted = System.IO.File.Exists(active) &&
                    (System.IO.File.GetAttributes(active) & System.IO.FileAttributes.ReparsePoint) == 0;
                _supportInbox = new(origin, secret, NotifySupplementaryContentChanged, () => PlayerMessagingEnabled,
                    hasSubmitted: submitted, submitted: () =>
                    {
                        SupplementaryPaths.CheckDirectory(directory, ownedRoot);
                        if (System.IO.File.Exists(active)) return;
                        using var file = new System.IO.FileStream(active, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None);
                        file.WriteByte(1); file.Flush(true);
                    }, supportEnabled: () => SupplementaryAvailable, captureDiagnostics: () =>
                    {
                        if (PlayerMessagingEnabled)
                            _recentDiagnostics.ObserveContext(DebugLogging.HealthContextRecorder.Current.Snapshot(),
                                DebugLogging.ComponentDiagnosticHistory.Current.Snapshot(), settings: _controller?.SupportDiagnosticSettingsSnapshot);
                    });
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { _supportInbox = null; } // Local inbox storage cannot interrupt telemetry initialization.
    }
    internal bool PlayerMessagingEnabled => SupplementaryAvailable && _supplementaryClient?.MessagingEnabled == true;
    private void StartSupportInbox() => _supportInbox?.SetActive(PlayerMessagingEnabled);
    private void RefreshSupportMessagingActivation()
    {
        var enabled = PlayerMessagingEnabled;
        _supportInbox?.SetActive(enabled);
        if (!enabled) _recentDiagnostics.Clear();
    }
    private void StopSupportInbox() => _supportInbox?.Dispose();
    internal SupportInboxSnapshot SupportInbox => _supportInbox?.Snapshot ?? new(1, DateTimeOffset.MinValue, []);
    internal string SupportInboxStatus => _supportInbox?.Status ?? "Replies are unavailable. Restart Wisp to retry.";
    internal SupportChatSnapshot SupportChat => _supportInbox?.Chat ?? new(1, DateTimeOffset.MinValue, []);
    internal Task RefreshSupportChatAsync(bool older = false, Guid[]? readIds = null) => _supportInbox?.RefreshChatAsync(older: older, readIds: readIds) ?? Task.CompletedTask;
    internal void SetSupportChatOpen(bool open) { if (_supportInbox is { } inbox) inbox.ChatOpen = open; }
    internal Task RefreshSupportInboxAsync(Guid? read = null) => _supportInbox?.RefreshAsync(read: read) ?? Task.CompletedTask;
    private Task<SupplementarySupportResult> SubmitPrivateSupportAsync(SupplementarySupportReport report, CancellationToken cancellation)
    {
        if (!PlayerMessagingEnabled)
        {
            if (report.ReplyTo is not null) return Task.FromResult(new SupplementarySupportResult(SupplementaryRequestStatus.Disabled));
            report = report with { RecentDiagnostics = null };
        }
        // Bind only the player's voluntary new report. Never retry an uncertain write through a different endpoint.
        if (_supportInbox is not null) return _supportInbox.SubmitSupportAsync(report, cancellation);
        return report.ReplyTo is null
            ? _supplementaryClient?.SubmitSupportAsync(report, cancellation) ?? Task.FromResult(new SupplementarySupportResult(SupplementaryRequestStatus.Unavailable))
            : Task.FromResult(new SupplementarySupportResult(SupplementaryRequestStatus.Unavailable));
    }
}
