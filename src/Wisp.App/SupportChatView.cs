using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Wisp.App.Supplementary;

namespace Wisp.App;

internal sealed class SupportChatView : Grid
{
    private readonly Func<string, bool, Task<SupplementarySupportReport?>> _prepare;
    private readonly Func<SupplementarySupportReport, CancellationToken, Task<SupplementarySupportResult>> _submit;
    private readonly Func<bool, Guid[]?, Task> _refresh;
    private readonly StackPanel _messages = new() { Margin = new Thickness(18, 8, 18, 8) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    internal TextBox Composer { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, MinHeight = 64, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
    internal CheckBox Diagnostics { get; } = new() { Content = "Attach diagnostics", Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    internal Button Send { get; } = Button("Send", true);
    private readonly Button _edit = Button("Back"), _older = Button("Earlier messages");
    private readonly TextBlock _status = Text("", true);
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160, Visibility = Visibility.Collapsed, FontSize = 12 };
    private readonly TextBlock _reviewLabel = Text("Review the message and attachment, then send.", true);
    private SupplementarySupportReport? _pending;
    private CancellationTokenSource? _sending;
    private bool _busy, _enabled = true, _receiptPending, _reading;
    private string? _renderKey;
    internal bool CanMarkRead { get; set; }

    internal Border CreatePopupSurface(Action close, Action drag)
    {
        var layout = new DockPanel();
        var heading = new DockPanel { Margin = new Thickness(18, 12, 12, 10) };
        var dismiss = Button("×"); dismiss.MinWidth = 32; dismiss.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(dismiss, "Close chat"); dismiss.Click += (_, _) => close();
        DockPanel.SetDock(dismiss, Dock.Right); heading.Children.Add(dismiss);
        var title = Text("Views"); title.FontSize = 20; title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        heading.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is TextBlock || ReferenceEquals(e.OriginalSource, heading)) drag(); };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading); layout.Children.Add(this);
        var frame = new Border { Child = layout, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "WindowBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return frame;
    }

    internal SupportChatView(Func<string, bool, Task<SupplementarySupportReport?>> prepare,
        Func<SupplementarySupportReport, CancellationToken, Task<SupplementarySupportResult>> submit,
        Func<bool, Guid[]?, Task> refresh)
    {
        _prepare = prepare; _submit = submit; _refresh = refresh;
        SetValue(Control.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        SetValue(Control.FontSizeProperty, 14d);
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        _scroll.Content = _messages; Children.Add(_scroll);
        var bottom = new StackPanel { Margin = new Thickness(18, 8, 18, 16) };
        SetRow(bottom, 1); Children.Add(bottom);
        _status.Margin = new Thickness(0, 0, 0, 8); _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        bottom.Children.Add(_status); bottom.Children.Add(Composer);
        AutomationProperties.SetName(Composer, "Message to Views");
        Diagnostics.ToolTip = "Review and attach up to 20 minutes of diagnostics.";
        Diagnostics.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
        Diagnostics.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        Diagnostics.Checked += (_, _) => UpdateActions();
        Diagnostics.Unchecked += (_, _) => UpdateActions();
        var actions = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.Children.Add(Diagnostics); actions.Children.Add(_edit); actions.Children.Add(Send);
        SetColumn(_edit, 1); SetColumn(Send, 2); _edit.Margin = new Thickness(8, 0, 8, 0); _edit.Visibility = Visibility.Collapsed;
        _reviewLabel.Visibility = Visibility.Collapsed; _reviewLabel.Margin = new Thickness(0, 8, 0, 8);
        bottom.Children.Add(_reviewLabel); bottom.Children.Add(_preview); bottom.Children.Add(actions);
        Send.Click += async (_, _) => await SendAsync();
        _edit.Click += (_, _) => { _pending = null; _receiptPending = false; Review(false); SetStatus(""); Composer.Focus(); };
        Composer.TextChanged += (_, _) => UpdateActions();
        Composer.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendAsync(); }
        };
        _older.Click += async (_, _) =>
        {
            _older.IsEnabled = false;
            try { await _refresh(true, null); } finally { _older.IsEnabled = true; }
        };
        _scroll.ScrollChanged += async (_, _) => await MarkVisibleAsync();
        UpdateActions();
    }

    internal void Update(SupportChatSnapshot snapshot, string status, bool enabled)
    {
        _enabled = enabled;
        if (!enabled) _sending?.Cancel();
        if (!_busy && _pending is null) SetStatus(enabled ? status : "Messaging is temporarily paused. Your draft is kept.");
        var key = string.Join(';', snapshot.Messages.Select(m => $"{m.Sender}:{m.Id}:{m.ReadAt}")) + snapshot.NextCursor;
        if (_renderKey != key)
        {
            var bottom = _scroll.ScrollableHeight - _scroll.VerticalOffset < 30;
            var previousHeight = _scroll.ExtentHeight;
            var previousOffset = _scroll.VerticalOffset;
            var olderAdded = _messages.Children.Count > 0 && snapshot.Messages.FirstOrDefault()?.Id != (_messages.Tag as Guid?);
            _renderKey = key; _messages.Children.Clear();
            _messages.Tag = snapshot.Messages.FirstOrDefault()?.Id;
            if (snapshot.NextCursor is not null) _messages.Children.Add(_older);
            if (snapshot.Messages.Length == 0)
            {
                var empty = Text("Send Views a message", true); empty.Margin = new Thickness(0, 32, 0, 0); empty.HorizontalAlignment = HorizontalAlignment.Center;
                _messages.Children.Add(empty);
            }
            DateTime? day = null;
            foreach (var message in snapshot.Messages)
            {
                var date = message.CreatedAt.LocalDateTime.Date;
                if (day != date) { var label = Text(date == DateTime.Today ? "Today" : date.ToString("MMM d, yyyy"), true); label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 14, 0, 10); _messages.Children.Add(label); day = date; }
                _messages.Children.Add(Bubble(message));
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (olderAdded) _scroll.ScrollToVerticalOffset(previousOffset + _scroll.ExtentHeight - previousHeight);
                else if (bottom) _scroll.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        UpdateActions();
    }

    private FrameworkElement Bubble(SupportChatMessage message)
    {
        var sent = message.Sender == "player";
        var content = new StackPanel();
        content.Children.Add(Text(sent ? "You" : "Views", true));
        content.Children.Add(Text(message.Message));
        if (message.Diagnostics) content.Children.Add(Text("Diagnostics attached", true));
        if (message.Kind == "diagnostics-request") content.Children.Add(Text("Views requested recent diagnostics.", true));
        var time = Text(message.CreatedAt.ToLocalTime().ToString("h:mm tt") + (sent ? " · Sent" : ""), true);
        time.HorizontalAlignment = HorizontalAlignment.Right; content.Children.Add(time);
        var bubble = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = sent ? new Thickness(50, 0, 0, 10) : new Thickness(0, 0, 50, 10),
            HorizontalAlignment = sent ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Tag = message
        };
        bubble.SetResourceReference(Border.BackgroundProperty, sent ? "RaisedBrush" : "CardBrush");
        return bubble;
    }

    internal async Task MarkVisibleAsync()
    {
        if (!CanMarkRead || !_enabled || _reading || !IsVisible) return;
        var ids = _messages.Children.OfType<Border>().Where(b => b.Tag is SupportChatMessage { Sender: "views", ReadAt: null } && b.ActualHeight > 0)
            .Where(b => { var p = b.TranslatePoint(new Point(), _scroll); return p.Y < _scroll.ViewportHeight && p.Y + b.ActualHeight > 0; })
            .Select(b => ((SupportChatMessage)b.Tag).Id).Take(60).ToArray();
        if (ids.Length == 0) return;
        _reading = true;
        try { await _refresh(false, ids); } finally { _reading = false; }
    }

    private async Task SendAsync()
    {
        if (_busy || !_enabled || string.IsNullOrWhiteSpace(Composer.Text)) return;
        _busy = true; UpdateActions();
        try
        {
            if (_pending is null)
            {
                _pending = await _prepare(Composer.Text.Trim(), Diagnostics.IsChecked == true);
                if (_pending is null) { SetStatus("Message unavailable. Your draft is kept; try again."); return; }
                if (Diagnostics.IsChecked == true || _pending.Message != Composer.Text.Trim())
                {
                    _preview.Text = Diagnostics.IsChecked == true ? JsonSerializer.Serialize(_pending, new JsonSerializerOptions(SupplementarySchema.Json) { WriteIndented = true }) : _pending.Message;
                    Review(true); SetStatus(""); return;
                }
            }
            _receiptPending = true;
            using var cancellation = new CancellationTokenSource(); _sending = cancellation;
            SetStatus("Sending…");
            var result = await _submit(_pending, cancellation.Token);
            if (result.Status == SupplementaryRequestStatus.Success)
            {
                _pending = null; _receiptPending = false; Composer.Clear(); Diagnostics.IsChecked = false; Review(false); SetStatus("");
                _scroll.ScrollToEnd(); Composer.Focus();
            }
            else
            {
                if (result.Status is SupplementaryRequestStatus.Invalid or SupplementaryRequestStatus.DailyLimit or SupplementaryRequestStatus.Disabled) _receiptPending = false;
                SetStatus(result.Status switch
                {
                    SupplementaryRequestStatus.Disabled => "Messaging is temporarily paused. Your message is kept.",
                    SupplementaryRequestStatus.Invalid => "This message could not be accepted. Select Back to edit it.",
                    SupplementaryRequestStatus.DailyLimit => "Sending limit reached. Your message is kept; try again later.",
                    _ => "Delivery not confirmed. Select Retry to check and send this same message."
                });
            }
        }
        catch (Exception) { SetStatus("Could not send. Your message is kept; try again."); }
        finally { _sending = null; _busy = false; UpdateActions(); }
    }

    private void Review(bool show)
    {
        _preview.Visibility = _reviewLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) _preview.Clear();
        UpdateActions();
    }
    private void UpdateActions()
    {
        Composer.IsReadOnly = _busy || _pending is not null;
        Diagnostics.IsEnabled = !_busy && _pending is null && _enabled;
        Send.IsEnabled = !_busy && _enabled && !string.IsNullOrWhiteSpace(Composer.Text);
        Send.Content = _busy ? "Sending…" : _receiptPending ? "Retry" : _pending is not null ? "Send" : Diagnostics.IsChecked == true ? "Review & send" : "Send";
        _edit.Visibility = _pending is not null && !_receiptPending ? Visibility.Visible : Visibility.Collapsed;
        _edit.IsEnabled = !_busy;
    }
    private void SetStatus(string text) { _status.Text = text; _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    internal void Stop() => _sending?.Cancel();
    private static TextBlock Text(string text, bool muted = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = muted ? 11 : 14, Margin = new Thickness(0, 2, 0, 2) };
        block.SetResourceReference(StyleProperty, typeof(TextBlock));
        block.FontFamily = new FontFamily("Segoe UI");
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return block;
    }
    private static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, MinHeight = 32, MinWidth = 64, Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); return button;
    }
}
