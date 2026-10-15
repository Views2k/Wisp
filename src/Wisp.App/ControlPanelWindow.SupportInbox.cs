using System.Windows;
using System.Windows.Controls;
using Wisp.App.Supplementary;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private StackPanel? _supportReplyCards;
    private TextBlock? _supportReplyStatus;
    private Expander? _supportReplyExpander, _supportComposer;
    private TextBox? _supportTitleInput, _supportMessageInput;
    private CheckBox? _supportDiagnosticsInput;
    private Guid? _supportReplyTo;
    private Action? _refreshSupportQuota;
    private string? _supportReplyKey;
    private bool? _supportMessagingActive;
    private readonly HashSet<Guid> _openSupportReplies = [];

    private FrameworkElement CreateSupportInbox()
    {
        _supportReplyCards = new StackPanel();
        _supportReplyStatus = SupplementaryText("Checking for replies…", muted: true);
        var refresh = SupplementaryButton("Check replies");
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try { if (Application.Current is App app) await app.RefreshSupportInboxAsync(); }
            finally { refresh.IsEnabled = true; RefreshSupportReplies(); }
        };
        var content = new StackPanel(); content.Children.Add(_supportReplyStatus); content.Children.Add(_supportReplyCards); content.Children.Add(refresh);
        _supportReplyExpander = new Expander { Header = "Replies from Wisp", Content = content, IsExpanded = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _supportReplyExpander.SetResourceReference(StyleProperty, "MoreOptionsStyle");
        if (Application.Current is App application)
        {
            void Changed(object? sender, EventArgs args) => RefreshSupportReplies();
            application.SupplementaryContentChanged += Changed;
            Closed += (_, _) => application.SupplementaryContentChanged -= Changed;
        }
        RefreshSupportReplies();
        return _supportReplyExpander;
    }

    private void RefreshSupportReplies()
    {
        if (_supportReplyCards is null || _supportReplyStatus is null || _supportReplyExpander is null || Application.Current is not App app) return;
        var enabled = app.PlayerMessagingEnabled;
        _supportReplyExpander.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_supportDiagnosticsInput is { } consent && _supportMessagingActive != enabled)
        {
            _supportMessagingActive = enabled;
            consent.Content = enabled ? "Include recent diagnostics (up to 20 minutes)" : "Include game platform, version and connection state";
            consent.IsChecked = !enabled;
        }
        if (!enabled)
        {
            _supportReplyCards.Children.Clear();
            _supportReplyKey = null;
            _supportReplyTo = null;
            _refreshSupportQuota?.Invoke();
            return;
        }
        var messages = app.SupportInbox.Messages.Where(m => m.ExpiresAt > DateTimeOffset.UtcNow).ToArray();
        var unread = messages.Count(m => m.ReadAt is null);
        _supportReplyExpander.Header = unread > 0 ? $"Replies from Wisp · {unread} unread" : "Replies from Wisp";
        _supportReplyStatus.Text = app.SupportInboxStatus;
        var key = string.Join(';', messages.Select(m => $"{m.Id}:{m.ReadAt}:{m.Replied}"));
        if (_supportReplyKey == key) return;
        _supportReplyKey = key;
        _supportReplyCards.Children.Clear();
        foreach (var message in messages)
        {
            var content = new StackPanel();
            content.Children.Add(SupplementaryText(message.Message));
            content.Children.Add(SupplementaryText($"{message.CreatedAt.ToLocalTime():g} · {message.Reference}", muted: true));
            if (message.Kind == "diagnostics-request") content.Children.Add(SupplementaryText($"You can attach the last 20 minutes of diagnostics when you reply. Request expires {message.ExpiresAt.ToLocalTime():g}.", muted: true));
            var reply = SupplementaryButton(message.Replied ? "Reply sent" : message.Kind == "diagnostics-request" ? "Respond to request" : "Reply");
            reply.IsEnabled = !message.Replied;
            reply.Click += (_, _) =>
            {
                if (message.ExpiresAt <= DateTimeOffset.UtcNow) { RefreshSupportReplies(); return; }
                if (_supplementaryReviewedReport is not null || _supplementarySendCancellation is not null)
                { _supportReplyStatus.Text = "Finish or cancel your current message before replying."; return; }
                if (_supportMessageInput is { Text.Length: > 0 })
                { _supportReplyStatus.Text = "Send or clear your current draft before replying."; return; }
                _supportReplyTo = message.Id;
                _refreshSupportQuota?.Invoke();
                if (_supportTitleInput is { } title) title.Text = message.Title;
                if (_supportDiagnosticsInput is { } diagnostics) diagnostics.IsChecked = false;
                if (_supportComposer is { } composer) { composer.IsExpanded = true; composer.BringIntoView(); }
                _supportMessageInput?.Focus();
            };
            content.Children.Add(reply);
            var item = new Expander { Header = $"{(message.ReadAt is null ? "New · " : "")}{message.Title}", Content = content, Margin = new Thickness(0, 8, 0, 0), IsExpanded = _openSupportReplies.Contains(message.Id) };
            item.SetResourceReference(StyleProperty, "MoreOptionsStyle");
            item.Expanded += async (_, _) => { _openSupportReplies.Add(message.Id); if (message.ReadAt is null) await app.RefreshSupportInboxAsync(message.Id); };
            item.Collapsed += (_, _) => _openSupportReplies.Remove(message.Id);
            _supportReplyCards.Children.Add(item);
        }
    }
}
