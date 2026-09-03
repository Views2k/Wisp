using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wisp.Core;

namespace Wisp.App;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;
    private const uint DefaultToNearestMonitor = 2;
    private const double DesignMinimumWidth = 720;
    private const double DesignMinimumHeight = 440;
    private const double SidebarWidth = 168;
    private readonly AppController _controller;
    private bool _loaded;
    private bool _sidebarOpen = true;
    private int _sidebarAnimationVersion;

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        var accentTheme = AppColorThemes.Resolve(controller.Settings.ColorTheme);
        var backgroundTheme = AppBackgroundThemes.Resolve(controller.Settings.BackgroundTheme);
        var hudBorderTheme = AppColorThemes.Resolve(controller.Settings.HudBorderTheme);
        var boostTheme = BoostGaugeThemes.Resolve(controller.Settings.BoostGaugeTheme);
        AppThemeResources.Apply(Resources, accentTheme, backgroundTheme);
        HudBorderThemeResources.Apply(Resources, hudBorderTheme);
        BoostGaugeThemeResources.Apply(Resources, boostTheme.Name);
        ThemePicker.SelectedValue = accentTheme.Name;
        BackgroundThemePicker.SelectedValue = backgroundTheme.Name;
        HudBorderThemePicker.SelectedValue = hudBorderTheme.Name;
        BoostThemePicker.SelectedValue = boostTheme.Name;
        ActiveThemeName.Text = accentTheme.Name;
        ActiveBackgroundThemeName.Text = backgroundTheme.Name;
        ActiveHudBorderThemeName.Text = hudBorderTheme.Name;
        ActiveBoostThemeName.Text = boostTheme.Name;
        SetSidebarOpen(!controller.Settings.SidebarCollapsed, animate: false);
        DataContext = controller.ViewModel;
        MphRadio.IsChecked = controller.Settings.SpeedUnit == SpeedUnit.MilesPerHour;
        KphRadio.IsChecked = controller.Settings.SpeedUnit == SpeedUnit.KilometersPerHour;
        WheelSpeedSourceRadio.IsChecked = controller.Settings.SpeedSource == SpeedSourceMode.WheelIndicated;
        Fh6SpeedSourceRadio.IsChecked = controller.Settings.SpeedSource == SpeedSourceMode.Fh6VehicleSpeed;
        MinimalLayoutRadio.IsChecked = controller.Settings.LayoutMode == HudLayoutMode.Minimal;
        CombinedLayoutRadio.IsChecked = controller.Settings.LayoutMode == HudLayoutMode.Combined;
        SeparateBoxesLayoutRadio.IsChecked = controller.Settings.LayoutMode == HudLayoutMode.SeparateBoxes;
        NativeLayoutRadio.IsChecked = controller.Settings.LayoutMode == HudLayoutMode.Native;
        NativeDigitalRadio.IsChecked = controller.Settings.NativeGaugeMode == NativeGaugeMode.Digital;
        NativeAnalogueRadio.IsChecked = controller.Settings.NativeGaugeMode == NativeGaugeMode.Analogue;
        ManualGearDisplayRadio.IsChecked = controller.Settings.GearDisplayMode == GearDisplayMode.Manual;
        AutomaticGearDisplayRadio.IsChecked = controller.Settings.GearDisplayMode == GearDisplayMode.Automatic;
        RootTabs.SelectedItem = controller.Settings.HasCompletedSetup ? DashboardTab : SetupTab;
        UpdateLockButtonLabels(controller.Settings.OverlayLocked);
        SourceInitialized += (_, _) =>
        {
            ApplyNativeRoundedCorners();
            FitToCurrentWorkArea();
        };
        DpiChanged += (_, _) => FitToCurrentWorkArea();
        LocationChanged += (_, _) => FitToCurrentWorkArea();
        Loaded += (_, _) => _loaded = true;
        Closed += (_, _) => StopSidebarAnimation();
    }

    internal bool IsSidebarOpen => _sidebarOpen;

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ThemePicker.SelectedItem is not AppColorTheme accentTheme)
        {
            return;
        }

        var backgroundTheme = BackgroundThemePicker.SelectedItem as AppBackgroundTheme ??
                              AppBackgroundThemes.Resolve(_controller.Settings.BackgroundTheme);
        AppThemeResources.Apply(Resources, accentTheme, backgroundTheme);
        ActiveThemeName.Text = accentTheme.Name;
        _controller.SetColorTheme(accentTheme.Name);
    }

    private void BackgroundThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || BackgroundThemePicker.SelectedItem is not AppBackgroundTheme backgroundTheme)
        {
            return;
        }

        var accentTheme = ThemePicker.SelectedItem as AppColorTheme ??
                          AppColorThemes.Resolve(_controller.Settings.ColorTheme);
        AppThemeResources.Apply(Resources, accentTheme, backgroundTheme);
        ActiveBackgroundThemeName.Text = backgroundTheme.Name;
        _controller.SetBackgroundTheme(backgroundTheme.Name);
    }

    private void HudBorderThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || HudBorderThemePicker.SelectedItem is not AppColorTheme hudBorderTheme)
        {
            return;
        }

        HudBorderThemeResources.Apply(Resources, hudBorderTheme);
        ActiveHudBorderThemeName.Text = hudBorderTheme.Name;
        _controller.SetHudBorderTheme(hudBorderTheme.Name);
    }

    private void BoostThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || BoostThemePicker.SelectedItem is not BoostGaugeTheme boostTheme)
        {
            return;
        }

        ActiveBoostThemeName.Text = boostTheme.Name;
        BoostGaugeThemeResources.Apply(Resources, boostTheme.Name);
        _controller.SetBoostGaugeTheme(boostTheme.Name);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSidebarOpen(!_sidebarOpen, animate: true);
        _controller.SetSidebarCollapsed(!_sidebarOpen);
    }

    internal void SetSidebarOpen(bool open, bool animate)
    {
        var targetWidth = open ? SidebarWidth : 0;
        var contentOffset = SidebarColumn.Width.Value + ContentTranslation.X - targetWidth;
        var sidebarOffset = SidebarTranslation.X;
        var chevronAngle = SidebarChevronRotation.Angle;
        StopSidebarAnimation();
        var animationVersion = _sidebarAnimationVersion;
        _sidebarOpen = open;

        if (!open && SidebarHost.IsKeyboardFocusWithin)
        {
            SidebarToggleButton.Focus();
        }

        // Layout changes once; the brief transition only animates render transforms.
        SidebarColumn.Width = new GridLength(targetWidth);
        SidebarHost.IsEnabled = open;
        SidebarHost.IsHitTestVisible = open;
        ContentTranslation.X = 0;
        SidebarTranslation.X = open ? 0 : -SidebarWidth;
        SidebarChevronRotation.Angle = open ? 0 : 180;
        var label = open ? "Hide sidebar" : "Show sidebar";
        SidebarToggleButton.ToolTip = label;
        AutomationProperties.SetName(SidebarToggleButton, label);

        if (!animate || !IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            SidebarHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        SidebarHost.Visibility = Visibility.Visible;
        // A new WPF clock starts on the next render tick. Keep its unanimated
        // value at the current position so rapid reversals cannot flash the end state.
        ContentTranslation.X = contentOffset;
        SidebarTranslation.X = sidebarOffset;
        SidebarChevronRotation.Angle = chevronAngle;
        var contentAnimation = SidebarAnimation(contentOffset, 0);
        contentAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _sidebarAnimationVersion)
            {
                return;
            }

            StopSidebarAnimation();
            ContentTranslation.X = 0;
            SidebarTranslation.X = _sidebarOpen ? 0 : -SidebarWidth;
            SidebarChevronRotation.Angle = _sidebarOpen ? 0 : 180;
            SidebarHost.Visibility = _sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        };
        ContentTranslation.BeginAnimation(TranslateTransform.XProperty, contentAnimation);
        SidebarTranslation.BeginAnimation(TranslateTransform.XProperty,
            SidebarAnimation(sidebarOffset, open ? 0 : -SidebarWidth));
        SidebarChevronRotation.BeginAnimation(RotateTransform.AngleProperty,
            SidebarAnimation(chevronAngle, open ? 0 : 180));
    }

    private static DoubleAnimation SidebarAnimation(double from, double to) => new(from, to,
        new Duration(TimeSpan.FromMilliseconds(200)))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        FillBehavior = FillBehavior.Stop
    };

    private void StopSidebarAnimation()
    {
        _sidebarAnimationVersion++;
        ContentTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private async void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        var applyButton = sender as Button;
        if (applyButton is not null)
        {
            applyButton.IsEnabled = false;
        }

        try
        {
            var port = UdpPortInput.Parse(_controller.ViewModel.UdpPortText);
            await _controller.RestartListenerAsync(port);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or SocketException)
        {
            MessageBox.Show(this, exception.Message, "Invalid UDP listener", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (applyButton is not null)
            {
                applyButton.IsEnabled = true;
            }
        }
    }

    private async void CheckCompatibility_Click(object sender, RoutedEventArgs e) =>
        await _controller.CheckNativeCompatibilityUpdatesAsync();

    private async void CheckApplicationUpdate_Click(object sender, RoutedEventArgs e)
    {
        var installer = await _controller.CheckForApplicationUpdateAsync();
        if (installer is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Wisp {installer.Version} is verified and ready. Install it now and restart Wisp?",
            "Wisp update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmation != MessageBoxResult.Yes)
        {
            _controller.MarkApplicationUpdateDeferred(installer);
            return;
        }

        if (Application.Current is not App app)
        {
            MessageBox.Show(
                this,
                "Wisp could not start the update.",
                "Update not started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var startResult = await app.TryBeginApplicationUpdateAsync(installer);
        if (!startResult.Started)
        {
            MessageBox.Show(
                this,
                startResult.Error,
                "Update not started",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void ImportCompatibility_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import signed Wisp compatibility pack",
            Filter = "Signed compatibility packs (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _controller.ImportNativeCompatibilityPackAsync(dialog.FileName);
        }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        switch (sender)
        {
            case CheckBox checkBox:
                checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                break;
            case Slider slider:
                slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
                break;
        }

        _controller.ApplyViewOptions();
    }

    private void Unit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.UnitSelectionIndex = KphRadio.IsChecked == true ? 1 : 0;
        _controller.ApplyViewOptions();
    }

    private void SpeedSource_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.SpeedSourceSelectionIndex = Fh6SpeedSourceRadio.IsChecked == true
            ? (int)SpeedSourceMode.Fh6VehicleSpeed
            : (int)SpeedSourceMode.WheelIndicated;
        _controller.ApplyViewOptions();
    }

    private void GearDisplay_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.GearDisplaySelectionIndex = AutomaticGearDisplayRadio.IsChecked == true
            ? (int)GearDisplayMode.Automatic
            : (int)GearDisplayMode.Manual;
        _controller.ApplyViewOptions();
    }

    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.LayoutSelectionIndex = CombinedLayoutRadio.IsChecked == true
            ? (int)HudLayoutMode.Combined
            : SeparateBoxesLayoutRadio.IsChecked == true
                ? (int)HudLayoutMode.SeparateBoxes
                : NativeLayoutRadio.IsChecked == true
                    ? (int)HudLayoutMode.Native
                    : (int)HudLayoutMode.Minimal;
        _controller.ApplyViewOptions();
    }

    private void NativeGauge_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.NativeGaugeSelectionIndex = NativeAnalogueRadio.IsChecked == true
            ? (int)NativeGaugeMode.Analogue
            : (int)NativeGaugeMode.Digital;
        _controller.ApplyViewOptions();
    }

    private void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        var locked = !_controller.Settings.OverlayLocked;
        _controller.SetOverlayLocked(locked);
        UpdateLockButtonLabels(locked);
    }

    private void UpdateLockButtonLabels(bool locked)
    {
        var label = locked ? "Edit HUD layout" : "Lock HUD layout";
        LockButton.Content = label;
        AppearanceLockButton.Content = label;
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _controller.ResetOverlayPosition();
    }

    private void RelearnTires_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.RelearnCurrentTires())
        {
            _controller.ViewModel.ReportControlError("Start driving before relearning the current tire profile");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FitToCurrentWorkArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var workAreaSize = CurrentMonitorPhysicalWorkAreaSize();
        var dpi = VisualTreeHelper.GetDpi(this);
        var fittedSize = ControlWindowGeometry.FitToPhysicalWorkArea(
            new Size(Width, Height),
            workAreaSize,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        MinWidth = Math.Min(DesignMinimumWidth, fittedSize.Width);
        MinHeight = Math.Min(DesignMinimumHeight, fittedSize.Height);
        if (Width > fittedSize.Width)
        {
            Width = fittedSize.Width;
        }

        if (Height > fittedSize.Height)
        {
            Height = fittedSize.Height;
        }
    }

    private Size CurrentMonitorPhysicalWorkAreaSize()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Size(
                SystemParameters.WorkArea.Width * dpi.DpiScaleX,
                SystemParameters.WorkArea.Height * dpi.DpiScaleY);
        }

        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Size(
                SystemParameters.WorkArea.Width * dpi.DpiScaleX,
                SystemParameters.WorkArea.Height * dpi.DpiScaleY);
        }

        return new Size(
            info.WorkArea.Right - info.WorkArea.Left,
            info.WorkArea.Bottom - info.WorkArea.Top);
    }

    private void ApplyNativeRoundedCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmRoundCorners;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowCornerPreference,
            ref preference,
            Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}
