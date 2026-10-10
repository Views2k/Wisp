using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Wisp.App.Supplementary;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private DispatcherTimer? _contentSlotTimer;
    private Border? _contentBanner;
    private TextBlock? _contentBannerTitle, _contentBannerMessage, _contentBannerIcon;
    private Button? _contentBannerAction;
    private BindingBase? _automaticBannerMessage;
    private string? _automaticBannerTitle, _automaticBannerIcon;
    private SupplementaryContentItem? _appliedBanner;
    private bool _contentSlotsClosed;

    private void InitializeSupplementaryContentSurfaces()
    {
        _contentBanner = FindControl<Border>("DashboardUpdateBanner");
        _contentBannerTitle = FindControl<TextBlock>("DashboardUpdateTitle");
        _contentBannerMessage = FindControl<TextBlock>("DashboardUpdateMessage");
        _contentBannerIcon = FindControl<TextBlock>("DashboardUpdateIcon");
        _contentBannerAction = FindControl<Button>("DashboardUpdateAction");
        _automaticBannerTitle = _contentBannerTitle.Text;
        _automaticBannerIcon = _contentBannerIcon.Text;
        _automaticBannerMessage = BindingOperations.GetBindingBase(_contentBannerMessage, TextBlock.TextProperty);
        _contentSlotTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _contentSlotTimer.Tick += (_, _) => RefreshSupplementaryContentSurfaces();
        Loaded += (_, _) => RefreshSupplementaryContentSurfaces();
        Activated += (_, _) => RefreshSupplementaryContentSurfaces();
        IsVisibleChanged += (_, _) => RefreshSupplementaryContentSurfaces();
        StateChanged += (_, _) => RefreshSupplementaryContentSurfaces();
        var app = Application.Current as App;
        if (app is not null) app.SupplementaryContentChanged += SupplementaryContentChanged;
        _controller.ViewModel.PropertyChanged += DashboardUpdateAvailabilityChanged;
        RefreshDashboardBannerVisibility();
        Closed += (_, _) =>
        {
            _contentSlotsClosed = true;
            _contentSlotTimer.Stop();
            if (app is not null) app.SupplementaryContentChanged -= SupplementaryContentChanged;
            _controller.ViewModel.PropertyChanged -= DashboardUpdateAvailabilityChanged;
        };
    }

    private void SupplementaryContentChanged(object? sender, EventArgs e)
    {
        RefreshSupplementaryContentSurfaces();
        if (_supplementaryNoticeExpander?.IsExpanded == true) RefreshSupplementaryNotices();
    }

    private void RefreshSupplementaryContentSurfaces()
    {
        _contentSlotTimer?.Stop();
        if (_contentSlotsClosed || !IsVisible || WindowState == WindowState.Minimized ||
            _contentBanner is null || Application.Current is not App app) return;
        var (content, audience) = app.GetSupplementaryContent();
        var now = DateTimeOffset.UtcNow;
        var slots = SupplementaryContentSlots.Select(content, audience, now);
        _supportReminder?.ApplyNote(slots.Note?.Title, slots.Note?.Message);
        if (_appliedBanner != slots.Banner)
        {
            _appliedBanner = slots.Banner;
            if (slots.Banner is { } banner)
            {
                _contentBannerTitle!.Text = banner.Title;
                _contentBannerMessage!.Text = banner.Message;
                _contentBannerIcon!.Text = "i";
                _contentBannerAction!.Visibility = Visibility.Collapsed;
            }
            else
            {
                _contentBannerTitle!.Text = _automaticBannerTitle;
                _contentBannerIcon!.Text = _automaticBannerIcon;
                if (_automaticBannerMessage is not null)
                    BindingOperations.SetBinding(_contentBannerMessage!, TextBlock.TextProperty, _automaticBannerMessage);
                _contentBannerAction!.ClearValue(VisibilityProperty);
            }
        }
        RefreshDashboardBannerVisibility();
        if (slots.NextChange is { } next && _contentSlotTimer is { } timer)
        {
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (next - now).TotalMilliseconds));
            timer.Start();
        }
    }
}
