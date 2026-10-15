using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.App.Supplementary;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private ContentControl? _supplementarySurface;
    private DispatcherTimer? _supplementaryNoticeTimer;
    private readonly HashSet<string> _dismissedSupplementaryNotices = new(StringComparer.Ordinal);
    private string? _supplementaryNoticeKey;
    private StackPanel? _supplementaryNotices;
    private Expander? _supplementaryNoticeExpander;
    private TextBlock? _supplementaryNoticeStatus;
    private TextBlock? _supplementarySupportStatus;
    private TextBox? _supplementaryReceipt;
    private SupplementarySupportReport? _supplementaryReviewedReport;
    private CancellationTokenSource? _supplementarySendCancellation;
    private bool _supplementaryUiClosed;

    protected void SupplementaryPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_supplementarySurface is not null || sender is not ContentControl surface) return;
        _supplementarySurface = surface;
        var panel = new StackPanel();
        surface.Content = panel;
        _supplementaryNoticeStatus = SupplementaryText("Open notices to see current news for this version of Wisp.");
        _supplementaryNotices = new StackPanel();
        var notices = new StackPanel();
        notices.Children.Add(_supplementaryNoticeStatus);
        notices.Children.Add(_supplementaryNotices);
        _supplementaryNoticeExpander = new Expander { Header = "News, known issues and troubleshooting", Content = notices, Margin = new Thickness(0, 12, 0, 8) };
        _supplementaryNoticeExpander.SetResourceReference(StyleProperty, "MoreOptionsStyle");
        panel.Children.Add(_supplementaryNoticeExpander);
        _supplementaryNoticeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _supplementaryNoticeTimer.Tick += (_, _) => RefreshSupplementaryNotices();
        _supplementaryNoticeExpander.Expanded += (_, _) => UpdateSupplementaryNoticeActivity();
        _supplementaryNoticeExpander.Collapsed += (_, _) => UpdateSupplementaryNoticeActivity();
        surface.IsVisibleChanged += (_, _) => UpdateSupplementaryNoticeActivity();
        StateChanged += (_, _) => UpdateSupplementaryNoticeActivity();
        void RefreshNoticeControl(object? source, EventArgs args)
        {
            if (_controller.FeatureAllowed("news")) UpdateSupplementaryNoticeActivity();
            else
            {
                _supplementaryNoticeTimer.Stop();
                _supplementaryNotices.Children.Clear();
                _supplementaryNoticeKey = null;
                _supplementaryNoticeStatus.Text = _controller.FeatureControls.Reason("news") ?? "News is temporarily paused.";
            }
        }
        _controller.FeatureControlsChanged += RefreshNoticeControl;
        RefreshNoticeControl(null, EventArgs.Empty);
        Closed += (_, _) =>
        {
            _controller.FeatureControlsChanged -= RefreshNoticeControl;
            _supplementaryUiClosed = true;
            _supplementaryNoticeTimer.Stop();
            _supplementarySendCancellation?.Cancel();
        };
        panel.Children.Add(CreateSupportInbox());
        panel.Children.Add(CreateSupplementarySupportForm());
        RefreshSupportReplies();
    }

    private void UpdateSupplementaryNoticeActivity()
    {
        _supplementaryNoticeTimer?.Stop();
        if (_supplementaryUiClosed || _supplementarySurface?.IsVisible != true ||
            WindowState == WindowState.Minimized || _supplementaryNoticeExpander?.IsExpanded != true) return;
        RefreshSupplementaryNotices();
        if (_controller.FeatureAllowed("news")) _supplementaryNoticeTimer?.Start();
    }

    private void RefreshSupplementaryNotices()
    {
        if (_supplementaryNotices is null || _supplementaryNoticeStatus is null) return;
        if (!_controller.FeatureAllowed("news"))
        {
            _supplementaryNotices.Children.Clear();
            _supplementaryNoticeKey = null;
            _supplementaryNoticeStatus.Text = _controller.FeatureControls.Reason("news") ?? "News is temporarily paused.";
            return;
        }
        if (Application.Current is not App app) return;
        var (content, audience) = app.GetSupplementaryContent();
        var now = DateTimeOffset.UtcNow;
        var matching = content.ForAudience(audience, now).Where(i => i.Kind is not ("support-note" or "dashboard-banner"));
        var visible = matching.Where(item => !_dismissedSupplementaryNotices.Contains($"{content.Revision}:{item.Id}")).ToArray();
        var key = $"{content.Revision}:{content.IsCurrent(now)}:{string.Join(',', visible.Select(item => item.Id))}";
        _supplementaryNoticeStatus.Text = !content.IsCurrent(now)
            ? "No current verified notices are available. Wisp checks quietly in the background when this service is configured."
            : visible.Length == 0 ? "No current notices for your version and game state."
            : $"{visible.Length} current notice{(visible.Length == 1 ? "" : "s")} · checked content issued {content.IssuedAt.ToLocalTime():g}.";
        if (_supplementaryNoticeTimer is { } timer)
        {
            var next = content.Items.SelectMany(item => new[] { item.StartsAt, item.ExpiresAt }).Append(content.ExpiresAt)
                .Where(time => time > now).DefaultIfEmpty(now.AddMinutes(1)).Min();
            timer.Interval = TimeSpan.FromSeconds(Math.Clamp((next - now).TotalSeconds, 1, 60));
        }
        if (_supplementaryNoticeKey == key) return;
        _supplementaryNoticeKey = key;
        _supplementaryNotices.Children.Clear();
        foreach (var item in visible)
        {
            var body = new StackPanel();
            body.Children.Add(SupplementaryText($"{SupplementaryNoticeKind(item.Kind)} · {item.Severity}", muted: true));
            body.Children.Add(SupplementaryText(item.Title, bold: true));
            body.Children.Add(SupplementaryText(item.Message));
            body.Children.Add(SupplementaryText($"Until {item.ExpiresAt.ToLocalTime():g}", muted: true));
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (item.Url is { } url && SupplementaryContentVerifier.ValidUrl(url))
            {
                var open = SupplementaryButton($"Open {new Uri(url).Host}");
                open.ToolTip = url;
                open.Click += (_, _) =>
                {
                    // Recheck audience/expiry immediately before acting on a previously rendered notice.
                    var current = app.GetSupplementaryContent();
                    if (!current.Content.ForAudience(current.Audience, DateTimeOffset.UtcNow).Any(i => i.Id == item.Id && i.Url == url))
                    { RefreshSupplementaryNotices(); return; }
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                    { _supplementaryNoticeStatus.Text = "The browser could not open this link."; }
                };
                actions.Children.Add(open);
            }
            if (item.Dismissible)
            {
                var dismiss = SupplementaryButton("Dismiss for this session");
                dismiss.Click += (_, _) =>
                {
                    if (_dismissedSupplementaryNotices.Count >= 128) _dismissedSupplementaryNotices.Clear();
                    _dismissedSupplementaryNotices.Add($"{content.Revision}:{item.Id}");
                    RefreshSupplementaryNotices();
                };
                actions.Children.Add(dismiss);
            }
            body.Children.Add(actions);
            var card = new Border { Child = body, Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            card.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            _supplementaryNotices.Children.Add(card);
        }
    }

    private FrameworkElement CreateSupplementarySupportForm(bool messagingPreview = false)
    {
        var content = new StackPanel();
        var editor = new StackPanel();
        var categories = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var bug = new RadioButton { Content = "Diagnostics", GroupName = "SupplementaryCategory", IsChecked = true };
        var suggestion = new RadioButton { Content = "Suggestions", GroupName = "SupplementaryCategory" };
        var feedback = new RadioButton { Content = "Feedback", GroupName = "SupplementaryCategory" };
        bug.SetResourceReference(StyleProperty, "SegmentRadioStyle");
        suggestion.SetResourceReference(StyleProperty, "SegmentRadioStyle");
        feedback.SetResourceReference(StyleProperty, "SegmentRadioStyle");
        categories.Children.Add(bug); categories.Children.Add(suggestion); categories.Children.Add(feedback);
        editor.Children.Add(SupplementaryText("Message type", bold: true));
        editor.Children.Add(categories);
        var title = new TextBox { MaxLength = 100 };
        _supportTitleInput = title;
        SupplementaryLabel(editor, "Title (up to 100 characters)", title);
        var message = new TextBox { MaxLength = 4000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _supportMessageInput = message;
        SupplementaryLabel(editor, "What happened, or what would help? (up to 4,000 characters)", message);
        var messaging = Application.Current is App current ? current.PlayerMessagingEnabled : messagingPreview;
        var diagnostics = new CheckBox { Content = messaging ? "Include recent diagnostics (up to 20 minutes)" : "Include game platform, version and connection state", IsChecked = !messaging, Margin = new Thickness(0, 12, 0, 0) };
        diagnostics.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
        diagnostics.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _supportDiagnosticsInput = diagnostics;
        editor.Children.Add(SupplementaryText("Review your message for personal or account details before sending.", muted: true));
        content.Children.Add(editor);
        var quotaStatus = SupplementaryText("One accepted message per day.", muted: true);
        AutomationProperties.SetLiveSetting(quotaStatus, AutomationLiveSetting.Polite);
        content.Children.Add(quotaStatus);
        var reviewButton = SupplementaryButton("Review before sending");
        content.Children.Add(diagnostics);
        content.Children.Add(reviewButton);
        var reviewPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        reviewPanel.Children.Add(SupplementaryText("Review the exact report", bold: true));
        reviewPanel.Children.Add(SupplementaryText("Review your message and the included details below, then confirm to send."));
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(preview, "Exact redacted report preview");
        reviewPanel.Children.Add(preview);
        var confirmation = new CheckBox { Content = "I reviewed this report and want to send it", Margin = new Thickness(0, 12, 0, 8) };
        confirmation.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
        confirmation.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        reviewPanel.Children.Add(confirmation);
        var buttons = new WrapPanel();
        var send = SupplementaryButton("Send message", primary: true);
        send.IsEnabled = false;
        var edit = SupplementaryButton("Back to editing");
        var cancel = SupplementaryButton("Cancel send");
        cancel.Visibility = Visibility.Collapsed;
        buttons.Children.Add(send); buttons.Children.Add(edit); buttons.Children.Add(cancel);
        reviewPanel.Children.Add(buttons);
        content.Children.Add(reviewPanel);
        _supplementarySupportStatus = SupplementaryText("");
        AutomationProperties.SetLiveSetting(_supplementarySupportStatus, AutomationLiveSetting.Polite);
        content.Children.Add(_supplementarySupportStatus);
        _supplementaryReceipt = new TextBox { IsReadOnly = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(_supplementaryReceipt, "Saved report reference");
        content.Children.Add(_supplementaryReceipt);
        var copy = SupplementaryButton("Copy reference");
        copy.Visibility = Visibility.Collapsed;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_supplementaryReceipt.Text); _supplementarySupportStatus.Text = "Reference copied."; }
            catch (ExternalException) { _supplementarySupportStatus.Text = "The reference could not be copied. You can select it above."; }
        };
        content.Children.Add(copy);
        var preparingReport = false;
        var quotaStarted = Stopwatch.GetTimestamp();
        var quotaDelay = TimeSpan.Zero;
        TimeSpan QuotaRemaining() => quotaDelay - Stopwatch.GetElapsedTime(quotaStarted);
        var quotaTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        void UpdateQuota()
        {
            quotaTimer.Stop();
            var remaining = QuotaRemaining();
            var limited = _supportReplyTo is null && remaining > TimeSpan.Zero;
            var available = _controller.FeatureAllowed("support");
            send.IsEnabled = available && !limited && confirmation.IsChecked == true && _supplementarySendCancellation is null;
            reviewButton.IsEnabled = available && !limited && !preparingReport;
            quotaStatus.Text = !available ? _controller.FeatureControls.Reason("support") ?? "Sending messages is temporarily paused." : limited
                ? $"Next message in {Math.Ceiling(remaining.TotalMinutes):N0} minutes. The daily limit resets at 00:00 UTC."
                : "One accepted message per day.";
            if (limited && !_supplementaryUiClosed)
            {
                quotaTimer.Interval = remaining;
                quotaTimer.Start();
            }
        }
        _refreshSupportQuota = UpdateQuota;
        quotaTimer.Tick += (_, _) => UpdateQuota();
        Activated += (_, _) => UpdateQuota();
        void RefreshSupportControl(object? source, EventArgs args)
        {
            if (!_controller.FeatureAllowed("support")) _supplementarySendCancellation?.Cancel();
            UpdateQuota();
        }
        _controller.FeatureControlsChanged += RefreshSupportControl;
        UpdateQuota();
        Closed += (_, _) => { quotaTimer.Stop(); _controller.FeatureControlsChanged -= RefreshSupportControl; };
        reviewButton.Click += async (_, _) =>
        {
            if (preparingReport || !_controller.FeatureAllowed("support") || _supportReplyTo is null && QuotaRemaining() > TimeSpan.Zero || Application.Current is not App app) return;
            var category = bug.IsChecked == true ? "bug" : suggestion.IsChecked == true ? "suggestion" : "feedback";
            var draftTitle = title.Text.Trim(); var draftMessage = message.Text.Trim();
            var includeDiagnostics = diagnostics.IsChecked == true; var replyTo = _supportReplyTo;
            preparingReport = true;
            editor.IsEnabled = false; diagnostics.IsEnabled = false; reviewButton.IsEnabled = false;
            _supplementarySupportStatus.Text = "Preparing your report…";
            try
            {
                var prepared = await Task.Run(() =>
                {
                    var report = app.CreateSupplementarySupportPreview(category, draftTitle, draftMessage, includeDiagnostics);
                    if (report is not null) report = report with { ReplyTo = replyTo };
                    return (Report: report, Valid: report is not null && SupplementarySchema.Valid(report, DateTimeOffset.UtcNow),
                        Json: report is null ? "" : JsonSerializer.Serialize(report, new JsonSerializerOptions(SupplementarySchema.Json) { WriteIndented = true }));
                });
                if (_supplementaryUiClosed) return;
                if (prepared.Report is null) { _supplementarySupportStatus.Text = "Feedback is temporarily unavailable. Your draft stays here; try reviewing again later."; return; }
                if (!prepared.Valid)
                { _supplementarySupportStatus.Text = "Check the title and message, then try reviewing again."; return; }
                _supplementaryReviewedReport = prepared.Report;
                preview.Text = prepared.Json;
                reviewButton.Visibility = Visibility.Collapsed;
                reviewPanel.Visibility = Visibility.Visible;
                confirmation.IsChecked = false;
                _supplementarySupportStatus.Text = "Ready for your confirmation.";
                preview.Focus();
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or ArgumentException)
            { _supplementarySupportStatus.Text = "The report could not be prepared. Your draft is kept; try again."; }
            finally
            {
                preparingReport = false;
                if (_supplementaryReviewedReport is null) { editor.IsEnabled = true; diagnostics.IsEnabled = true; }
                UpdateQuota();
            }
        };
        confirmation.Checked += (_, _) => UpdateQuota();
        confirmation.Unchecked += (_, _) => UpdateQuota();
        edit.Click += (_, _) =>
        {
            _supplementaryReviewedReport = null;
            preview.Clear();
            editor.IsEnabled = true; diagnostics.IsEnabled = true;
            reviewButton.Visibility = Visibility.Visible;
            reviewPanel.Visibility = Visibility.Collapsed;
            _supplementarySupportStatus.Text = "Review cancelled. Your editable draft stays here.";
            title.Focus();
        };
        cancel.Click += (_, _) => _supplementarySendCancellation?.Cancel();
        send.Click += async (_, _) =>
        {
            if (!_controller.FeatureAllowed("support") || _supportReplyTo is null && QuotaRemaining() > TimeSpan.Zero || _supplementarySendCancellation is not null || confirmation.IsChecked != true ||
                _supplementaryReviewedReport is not { } reviewed || Application.Current is not App app) return;
            using var cancellation = new CancellationTokenSource();
            _supplementarySendCancellation = cancellation;
            send.IsEnabled = false; edit.IsEnabled = false; confirmation.IsEnabled = false;
            cancel.Visibility = Visibility.Visible;
            _supplementarySupportStatus.Text = "Sending reviewed report…";
            SupplementarySupportResult result;
            try { result = await app.SubmitSupplementarySupportAsync(reviewed, cancellation.Token); }
            catch (Exception) { result = new(SupplementaryRequestStatus.Unavailable); }
            finally
            {
                _supplementarySendCancellation = null;
                cancel.Visibility = Visibility.Collapsed;
                edit.IsEnabled = true; confirmation.IsEnabled = true;
                UpdateQuota();
            }
            if (_supplementaryUiClosed) return;
            if (result.NextAllowedAt is { } next)
            {
                quotaDelay = result.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(Math.Clamp((next - DateTimeOffset.UtcNow).TotalSeconds, 0, 86400));
                quotaStarted = Stopwatch.GetTimestamp();
            }
            UpdateQuota();
            if (result is { Status: SupplementaryRequestStatus.Success, Reference: { } reference })
            {
                _supplementaryReceipt.Text = reference;
                _supplementaryReceipt.Visibility = Visibility.Visible;
                copy.Visibility = Visibility.Visible;
                _supplementarySupportStatus.Text = app.PlayerMessagingEnabled ? "Message sent. Replies will appear here in Extras." : "Message sent. Save your reference for follow-up.";
                _supplementaryReviewedReport = null;
                preview.Clear(); title.Clear(); message.Clear();
                reviewPanel.Visibility = Visibility.Collapsed;
                reviewButton.Visibility = Visibility.Visible;
                editor.IsEnabled = true; diagnostics.IsEnabled = true; _supportReplyTo = null; diagnostics.IsChecked = !app.PlayerMessagingEnabled; UpdateQuota();
                _supplementaryReceipt.Focus();
            }
            else _supplementarySupportStatus.Text = result.Status switch
            {
                SupplementaryRequestStatus.Cancelled => "Send cancelled. Delivery is not confirmed; the service may already have received it. Retry this same reviewed report to recover its reference without creating a duplicate.",
                SupplementaryRequestStatus.Invalid => "This preview is no longer valid. Go back to editing and review it again.",
                SupplementaryRequestStatus.DailyLimit => "Today's message limit has been reached. Your reviewed draft stays here; the next available time is shown above.",
                SupplementaryRequestStatus.Unconfigured or SupplementaryRequestStatus.Disabled => "Feedback is currently unavailable. Your reviewed report stays here.",
                SupplementaryRequestStatus.Rejected => "The service did not accept this report. Your reviewed report stays here; check its contents before trying again.",
                SupplementaryRequestStatus.ClockSkew => "Windows time does not match the service. Enable automatic date and time, then retry this same reviewed report.",
                _ => "Delivery could not be confirmed. Your reviewed report stays here. Retrying it uses the same report identifier to avoid duplicates."
            };
        };
        var expander = new Expander { Header = "Send diagnostics, suggestions or feedback", Content = content, Margin = new Thickness(0, 10, 0, 0) };
        expander.SetResourceReference(StyleProperty, "MoreOptionsStyle");
        _supportComposer = expander;
        return expander;
    }

    private static string SupplementaryNoticeKind(string kind) => kind switch
    {
        "support-note" => "Support",
        "announcement" => "News",
        "troubleshooting" => "Troubleshooting",
        "changelog" => "What's new",
        "incident" => "Known issue",
        "recommendation" => "Recommendation",
        _ => "Notice"
    };

    private static TextBlock SupplementaryText(string text, bool bold = false, bool muted = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return block;
    }

    private Button SupplementaryButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Margin = new Thickness(0, 10, 8, 0) };
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        return button;
    }

    private static void SupplementaryLabel(Panel panel, string text, Control control)
    {
        var label = new Label { Content = text, Target = control, Margin = new Thickness(0, 8, 0, 2), Padding = new Thickness(0) };
        label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        panel.Children.Add(label);
        AutomationProperties.SetName(control, text);
        panel.Children.Add(control);
    }
}
