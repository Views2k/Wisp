using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.Core;

namespace Wisp.App;

public partial class SetupWindow : Window
{
    private const double DesignMinimumWidth = 540;
    private const double DesignMinimumHeight = 440;
    private static readonly string[] Titles =
        ["Welcome to Wisp", "Connect FH6 Data Out", "Check your game display", "Make the HUD yours"];
    private readonly AppController _controller;
    private CancellationTokenSource? _testCancellation;
    private Task<SetupTestResult>? _testTask;
    private int _step;
    private int _testGeneration;
    private bool _ready;
    private bool _testing;
    private bool _closing;
    private bool _allowClose;
    private bool? _appearanceStacked;

    public SetupWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = controller.ViewModel;
        var settings = controller.Settings;
        PortBox.Text = settings.UdpPort.ToString(CultureInfo.InvariantCulture);
        MinimalChoice.IsChecked = settings.LayoutMode == HudLayoutMode.Minimal;
        CombinedChoice.IsChecked = settings.LayoutMode == HudLayoutMode.Combined;
        SeparateChoice.IsChecked = settings.LayoutMode == HudLayoutMode.SeparateBoxes;
        NativeChoice.IsChecked = settings.LayoutMode == HudLayoutMode.Native;
        DigitalChoice.IsChecked = settings.NativeGaugeMode == NativeGaugeMode.Digital;
        AnalogueChoice.IsChecked = settings.NativeGaugeMode == NativeGaugeMode.Analogue;
        MphChoice.IsChecked = settings.SpeedUnit == SpeedUnit.MilesPerHour;
        KphChoice.IsChecked = settings.SpeedUnit == SpeedUnit.KilometersPerHour;
        WheelSpeedChoice.IsChecked = settings.SpeedSource == SpeedSourceMode.WheelIndicated;
        Fh6SpeedChoice.IsChecked = settings.SpeedSource == SpeedSourceMode.Fh6VehicleSpeed;
        ManualChoice.IsChecked = settings.GearDisplayMode == GearDisplayMode.Manual;
        AutomaticChoice.IsChecked = settings.GearDisplayMode == GearDisplayMode.Automatic;
        _ready = true;
        UpdatePreview();
        UpdateStep();
        Closing += OnSetupClosing;
        SourceInitialized += (_, _) =>
        {
            var preference = 2;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, sizeof(int));
            FitToWorkArea();
        };
        DpiChanged += (_, _) => FitToWorkArea();
        LocationChanged += (_, _) => FitToWorkArea();
    }

    private void UpdateStep()
    {
        WelcomeStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConnectionStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DisplayStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        AppearanceStep.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"SETUP · STEP {_step + 1} OF {Titles.Length}";
        StepTitle.Text = Titles[_step];
        UpdateStepBadges();
        NextButton.Content = _step == 3 ? "Finish setup" : "Continue";
        StepScroll.ScrollToTop();
        ClearError();
        UpdateNavigation();
    }

    private void UpdateStepBadges()
    {
        Border[] badges = [WelcomeBadge, ConnectionBadge, DisplayBadge, AppearanceBadge];
        TextBlock[] labels = [WelcomeBadgeText, ConnectionBadgeText, DisplayBadgeText, AppearanceBadgeText];
        for (var index = 0; index < badges.Length; index++)
        {
            var current = index == _step;
            badges[index].BorderBrush = (Brush)FindResource(current ? "AccentBrush" : "StrokeBrush");
            labels[index].Foreground = (Brush)FindResource(current ? "TextBrush" : "MutedBrush");
            labels[index].FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateNavigation()
    {
        if (!_ready)
        {
            return;
        }

        BackButton.IsEnabled = _step > 0 && !_closing;
        PortBox.IsEnabled = !_testing;
        DataOutConfirmation.IsEnabled = !_testing;
        TestButton.IsEnabled = !_testing && DataOutConfirmation.IsChecked == true;
        CancelTestButton.IsEnabled = _testing && !_closing;
        CancelTestButton.Visibility = _testing ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !_testing && !_closing && (_step switch
        {
            1 => HasMatchingTest(),
            2 => DisplayConfirmation.IsChecked == true && StockHudConfirmation.IsChecked == true,
            3 => HasMatchingTest() && DisplayConfirmation.IsChecked == true && StockHudConfirmation.IsChecked == true,
            _ => true
        });
    }

    private bool HasMatchingTest()
    {
        try
        {
            return DataOutConfirmation.IsChecked == true &&
                   _controller.SetupTelemetry.SuccessfulEvidence?.Port == UdpPortInput.Parse(PortBox.Text);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testing)
        {
            return;
        }

        _testing = true;
        ClearError();
        TestStatus.Foreground = (Brush)FindResource("MutedBrush");
        TestProgress.Visibility = Visibility.Visible;
        _testCancellation = new CancellationTokenSource();
        var generation = ++_testGeneration;
        UpdateNavigation();
        var progress = new Progress<string>(message =>
        {
            if (_testing && !_closing && generation == _testGeneration)
            {
                TestStatus.Text = message;
            }
        });
        try
        {
            _testTask = _controller.SetupTelemetry.RunAsync(PortBox.Text, progress, _testCancellation.Token);
            var result = await _testTask;
            if (!_closing)
            {
                TestStatus.Text = result.Message;
                TestStatus.Foreground = (Brush)FindResource(result.Passed ? "AccentBrush" : "WarningBrush");
                TestButton.Content = result.Passed ? "Test again" : "Retry Data Out test";
            }
        }
        catch (Exception exception) when (SetupTelemetryTest.IsExpectedListenerFailure(exception))
        {
            _controller.SetupTelemetry.Invalidate();
            if (!_closing)
            {
                TestStatus.Text = "The test could not finish. Close any other telemetry listener, then retry.";
            }
        }
        finally
        {
            _testing = false;
            _testCancellation?.Dispose();
            _testCancellation = null;
            TestProgress.Visibility = Visibility.Collapsed;
            UpdateNavigation();
        }
    }

    private void CancelTest_Click(object sender, RoutedEventArgs e) => _testCancellation?.Cancel();

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        BackButton.IsEnabled = false;
        await CancelTestAsync();
        if (!_closing && _step > 0)
        {
            _step--;
            UpdateStep();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!NextButton.IsEnabled)
        {
            return;
        }

        if (_step < Titles.Length - 1)
        {
            _step++;
            UpdateStep();
            return;
        }

        try
        {
            _controller.CompleteSetup(new SetupPreferences(
                UdpPortInput.Parse(PortBox.Text),
                KphChoice.IsChecked == true ? SpeedUnit.KilometersPerHour : SpeedUnit.MilesPerHour,
                Fh6SpeedChoice.IsChecked == true ? SpeedSourceMode.Fh6VehicleSpeed : SpeedSourceMode.WheelIndicated,
                SelectedLayout,
                AnalogueChoice.IsChecked == true ? NativeGaugeMode.Analogue : NativeGaugeMode.Digital,
                AutomaticChoice.IsChecked == true ? GearDisplayMode.Automatic : GearDisplayMode.Manual,
                DataOutConfirmation.IsChecked == true,
                DisplayConfirmation.IsChecked == true,
                StockHudConfirmation.IsChecked == true));
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowError("Setup could not be saved. The dashboard and HUD remain locked. Check write access to Wisp's local settings folder, then click Finish setup again. Your earlier settings are preserved.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            ShowError(exception.Message);
        }
    }

    private HudLayoutMode SelectedLayout => NativeChoice.IsChecked == true
        ? HudLayoutMode.Native
        : SeparateChoice.IsChecked == true
            ? HudLayoutMode.SeparateBoxes
            : CombinedChoice.IsChecked == true ? HudLayoutMode.Combined : HudLayoutMode.Minimal;

    private void Preference_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        var unit = KphChoice.IsChecked == true ? SpeedUnit.KilometersPerHour : SpeedUnit.MilesPerHour;
        var gear = AutomaticChoice.IsChecked == true ? GearDisplayMode.Automatic : GearDisplayMode.Manual;
        var sample = HudPreviewSample.Create(unit, gear);
        DigitalGauge.Frame = sample;
        AnalogueGauge.Frame = sample;
        SampleSpeed.Text = sample.Speed.ToString(CultureInfo.InvariantCulture);
        SampleUnit.Text = unit == SpeedUnit.MilesPerHour ? "MPH" : "KM/H";
        var native = SelectedLayout == HudLayoutMode.Native;
        NativeChoices.Visibility = native ? Visibility.Visible : Visibility.Collapsed;
        NumberPreview.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
        DigitalPreview.Visibility = native && DigitalChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AnaloguePreview.Visibility = native && AnalogueChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PreviewTitle.Text = native
            ? AnalogueChoice.IsChecked == true ? "Native · Analogue" : "Native · Digital"
            : SelectedLayout switch
            {
                HudLayoutMode.Combined => "Combined",
                HudLayoutMode.SeparateBoxes => "Two boxes",
                _ => "Minimal"
            };
        PreviewDescription.Text = native
            ? "Original HUD artwork. Electric cars use their own power and regen gauges automatically."
            : "Speed sample shown. Layout and G-meter controls are available in Appearance.";
        SpeedSourceDescription.Text = Fh6SpeedChoice.IsChecked == true
            ? "Uses FH6's vehicle-speed value. No tire learning needed."
            : "Follows driven-wheel rotation. Tire learning happens as you drive.";
    }

    private void AppearanceLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = SetupPresentation.UseStackedAppearance(e.NewSize.Width);
        if (_appearanceStacked == stacked)
        {
            return;
        }

        _appearanceStacked = stacked;
        AppearanceLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 16);
        Grid.SetColumnSpan(PreviewPane, stacked ? 3 : 1);
        Grid.SetColumn(PreferencesPane, stacked ? 0 : 2);
        Grid.SetColumnSpan(PreferencesPane, stacked ? 3 : 1);
        Grid.SetRow(PreferencesPane, stacked ? 1 : 0);
        PreferencesPane.Margin = new Thickness(0, stacked ? 14 : 0, 0, 0);
        PreviewStage.Height = stacked ? 190 : 282;
    }

    private void Port_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready)
        {
            _controller.SetupTelemetry.Invalidate();
            TestStatus.Text = "Port changed. Match it in FH6 and run the Data Out test again.";
            TestStatus.Foreground = (Brush)FindResource("MutedBrush");
            UpdateNavigation();
        }
    }

    private void Confirmation_Changed(object sender, RoutedEventArgs e) => UpdateNavigation();

    private async Task CancelTestAsync()
    {
        _testCancellation?.Cancel();
        if (_testTask is not null)
        {
            try
            {
                await _testTask;
            }
            catch (Exception exception) when (SetupTelemetryTest.IsExpectedListenerFailure(exception))
            {
                // The visible test handler reports these errors. Cancellation
                // still needs to finish before changing steps or closing.
            }
        }
    }

    private async void OnSetupClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_testing)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        UpdateNavigation();
        await CancelTestAsync();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void FitToWorkArea()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var fittedSize = ControlWindowGeometry.FitToPhysicalWorkArea(
            new Size(Width, Height), CurrentMonitorPhysicalWorkAreaSize(), dpi.DpiScaleX, dpi.DpiScaleY);
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
        var monitor = handle != IntPtr.Zero ? MonitorFromWindow(handle, 2) : IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Size(
                SystemParameters.WorkArea.Width * dpi.DpiScaleX,
                SystemParameters.WorkArea.Height * dpi.DpiScaleY);
        }

        return new Size(info.WorkArea.Right - info.WorkArea.Left, info.WorkArea.Bottom - info.WorkArea.Top);
    }

    private void ShowError(string message)
    {
        WizardError.Text = message;
        WizardError.Visibility = Visibility.Visible;
    }

    private void ClearError() => WizardError.Visibility = Visibility.Collapsed;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);

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
