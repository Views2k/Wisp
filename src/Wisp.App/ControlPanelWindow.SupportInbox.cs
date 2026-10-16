using System.Windows;
using System.Windows.Controls;
using Wisp.App.Supplementary;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private Expander? _supportComposer;
    private TextBox? _supportTitleInput, _supportMessageInput;
    private CheckBox? _supportDiagnosticsInput;
    private Guid? _supportReplyTo;
    private Action? _refreshSupportQuota;
    private bool? _supportMessagingActive;
    private Button? _chatLauncher;
    private Window? _chatWindow;
    private SupportChatView? _chatView;
    private bool _closeChat;

    private FrameworkElement CreateSupportInbox()
    {
        _chatLauncher = SupplementaryButton("Chat with Views");
        _chatLauncher.Visibility = Visibility.Collapsed;
        _chatLauncher.Click += (_, _) => OpenSupportChat();
        if (Application.Current is App app)
        {
            void Changed(object? sender, EventArgs args) => RefreshSupportReplies();
            app.SupplementaryContentChanged += Changed;
            Closed += (_, _) =>
            {
                app.SupplementaryContentChanged -= Changed;
                app.SetSupportChatOpen(false);
                _closeChat = true; _chatView?.Stop(); _chatWindow?.Close();
            };
        }
        return _chatLauncher;
    }

    private void RefreshSupportReplies()
    {
        if (_chatLauncher is null || Application.Current is not App app) return;
        var enabled = app.PlayerMessagingEnabled;
        _chatLauncher.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_supportComposer is { } composer) composer.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        var unread = app.SupportChat.Messages.Count(m => m.Sender == "views" && m.ReadAt is null && m.ExpiresAt > DateTimeOffset.UtcNow);
        _chatLauncher.Content = unread > 0 ? $"Chat with Views · {unread} new" : "Chat with Views";
        _chatView?.Update(app.SupportChat, app.SupportInboxStatus, enabled && _controller.FeatureAllowed("support"));
        if (!enabled && _chatWindow?.IsVisible == true) _chatWindow.Hide();
        if (_supportDiagnosticsInput is { } consent && _supportMessagingActive != enabled)
        {
            _supportMessagingActive = enabled;
            consent.Content = enabled ? "Include recent diagnostics (up to 20 minutes)" : "Include game platform, version and connection state";
            consent.IsChecked = !enabled;
        }
    }

    private void OpenSupportChat()
    {
        if (Application.Current is not App app || !app.PlayerMessagingEnabled) return;
        if (_chatWindow is null)
        {
            _chatView = new SupportChatView(
                (title, message, diagnostics) => Task.Run(() => app.CreateSupplementarySupportPreview("feedback", title, message, diagnostics)),
                (report, cancellation) => app.SubmitSupplementarySupportAsync(report, cancellation, chat: true),
                (older, ids) => app.RefreshSupportChatAsync(older, ids));
            var frame = _chatView.CreatePopupSurface(() => _chatWindow?.Hide(), () => _chatWindow?.DragMove());
            _chatWindow = new Window
            {
                Title = "Views · Wisp",
                Owner = this,
                Content = frame,
                Resources = Resources,
                Width = 480,
                Height = 640,
                MinWidth = 360,
                MinHeight = 400,
                MaxHeight = SystemParameters.WorkArea.Height,
                MaxWidth = SystemParameters.WorkArea.Width,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = false,
                FontFamily = FontFamily,
                FontSize = 14
            };
            _chatWindow.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; _chatWindow.Hide(); } };
            _chatWindow.Closing += (_, e) => { if (!_closeChat) { e.Cancel = true; _chatWindow.Hide(); } };
            _chatWindow.IsVisibleChanged += async (_, _) =>
            {
                app.SetSupportChatOpen(_chatWindow.IsVisible);
                _chatView.CanMarkRead = _chatWindow.IsVisible && _chatWindow.IsActive;
                if (_chatWindow.IsVisible) await app.RefreshSupportChatAsync();
            };
            _chatWindow.Activated += async (_, _) => { _chatView.CanMarkRead = true; await _chatView.MarkVisibleAsync(); };
            _chatWindow.Deactivated += (_, _) => _chatView.CanMarkRead = false;
            void FeatureChanged(object? sender, EventArgs args) => RefreshSupportReplies();
            _controller.FeatureControlsChanged += FeatureChanged;
            Closed += (_, _) => _controller.FeatureControlsChanged -= FeatureChanged;
        }
        RefreshSupportReplies(); _chatWindow.Show(); _chatWindow.Activate(); _chatView!.Composer.Focus();
    }
}
