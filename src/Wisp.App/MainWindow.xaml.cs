using System.Diagnostics;
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
    private string? _applicationUpdateVersion;
    private IInputElement? _focusBeforeApplicationUpdateConfirmation;
    private IInputElement? _focusBeforeHudProfileDialog;
    private Guid? _activeHudProfileId;
    private HudProfileDialogMode _hudProfileDialogMode;
    private bool _capturingOverlayHotkey;
    private bool _updatingColorEditors;

    public MainWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        var accentTheme = AppColorThemes.Resolve(controller.Settings.ColorTheme);
        var backgroundTheme = AppBackgroundThemes.Resolve(controller.Settings.BackgroundTheme);
        var hudBorderTheme = AppColorThemes.Resolve(controller.Settings.HudBorderTheme);
        var boostTheme = BoostGaugeThemes.Resolve(controller.Settings.BoostGaugeTheme);
        AppThemeResources.Apply(
            Resources,
            accentTheme,
            backgroundTheme,
            controller.Settings.CustomAccentColor,
            controller.Settings.CustomBackgroundColor);
        HudBorderThemeResources.Apply(
            Resources,
            hudBorderTheme.Name,
            controller.Settings.CustomHudBorderColor);
        BoostGaugeThemeResources.Apply(
            Resources,
            boostTheme.Name,
            controller.Settings.CustomBoostLowColor,
            controller.Settings.CustomBoostMidColor,
            controller.Settings.CustomBoostHighColor);
        TractionCueThemeResources.Apply(Resources, ColorCustomization.ResolveTractionCue(controller.Settings));
        ColorTargetSelector.SelectedIndex = 0;
        LoadSelectedColorTarget();
        SetSidebarOpen(!controller.Settings.SidebarCollapsed, animate: false);
        DataContext = controller.ViewModel;
        MphRadio.IsChecked = controller.Settings.SpeedUnit == SpeedUnit.MilesPerHour;
        KphRadio.IsChecked = controller.Settings.SpeedUnit == SpeedUnit.KilometersPerHour;
        NewtonMetersRadio.IsChecked = controller.Settings.TorqueUnit == TorqueUnit.NewtonMeters;
        PoundFeetRadio.IsChecked = controller.Settings.TorqueUnit == TorqueUnit.PoundFeet;
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
        RefreshHudProfileList();
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

    private void ColorTargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controller is null || ColorEditor is null)
        {
            return;
        }

        LoadSelectedColorTarget();
    }

    private void LoadSelectedColorTarget()
    {
        var settings = _controller.Settings;
        Color color;
        switch (ColorTargetSelector.SelectedIndex)
        {
            case 1:
                ColorEditor.Title = "Background and surfaces";
                ColorEditor.Description = "Creates a coordinated, readable window, panel, card, and control hierarchy";
                ColorEditor.MinimumOpacity = 0.82;
                ColorEditor.MaximumBrightness = 0.34;
                ColorCustomization.TryParse(ColorCustomization.ResolveBackground(settings).Window, out color);
                break;
            case 2:
                ColorEditor.Title = "HUD border";
                ColorEditor.Description = "Outline used by Combined and Two boxes layouts";
                ColorEditor.MinimumOpacity = 0;
                ColorEditor.MaximumBrightness = 1;
                color = ColorCustomization.ResolveHudBorder(settings);
                break;
            case 3:
            case 4:
            case 5:
                var gauge = ColorCustomization.ResolveGauge(settings);
                ColorEditor.Title = ColorTargetSelector.SelectedIndex switch
                {
                    3 => "Gauge start",
                    4 => "Gauge middle",
                    _ => "Gauge end"
                };
                ColorEditor.Description = ColorTargetSelector.SelectedIndex switch
                {
                    3 => "Low end of the shared boost and tire gauge gradient",
                    4 => "Middle color of the shared boost and tire gauge gradient",
                    _ => "High end of the shared boost and tire gauge gradient"
                };
                ColorEditor.MinimumOpacity = 0.25;
                ColorEditor.MaximumBrightness = 1;
                ColorCustomization.TryParse(ColorTargetSelector.SelectedIndex switch
                {
                    3 => gauge.Low,
                    4 => gauge.Mid,
                    _ => gauge.High
                }, out color);
                break;
            case 6:
                ColorEditor.Title = "Traction hook cue";
                ColorEditor.Description = "Speed digit flash shown when the traction hook catches wheelspin";
                ColorEditor.MinimumOpacity = ColorCustomization.TractionCueMinimumOpacity;
                ColorEditor.MaximumBrightness = 1;
                color = ColorCustomization.ResolveTractionCue(settings);
                break;
            default:
                ColorEditor.Title = "App accent";
                ColorEditor.Description = "Highlights, selections, buttons, and status color";
                ColorEditor.MinimumOpacity = 0.35;
                ColorEditor.MaximumBrightness = 1;
                color = ColorCustomization.ResolveAccent(settings);
                break;
        }

        SetEditorColor(ColorEditor, color);
    }

    private void ColorEditor_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color> e)
    {
        if (!_loaded || _updatingColorEditors)
        {
            return;
        }

        var value = ColorCustomization.ToHex(ColorEditor.SelectedColor);
        switch (ColorTargetSelector.SelectedIndex)
        {
            case 1:
                value = ColorCustomization.NormalizeBackground(value);
                ApplyAppColorResources(_controller.Settings.CustomAccentColor, value);
                _controller.SetCustomBackgroundColor(value);
                break;
            case 2:
                HudBorderThemeResources.Apply(Resources, _controller.Settings.HudBorderTheme, value);
                _controller.SetCustomHudBorderColor(value);
                break;
            case 3:
            case 4:
            case 5:
                var gauge = ColorCustomization.ResolveGauge(_controller.Settings);
                ApplyGaugeColors(
                    ColorTargetSelector.SelectedIndex == 3 ? value : gauge.Low,
                    ColorTargetSelector.SelectedIndex == 4 ? value : gauge.Mid,
                    ColorTargetSelector.SelectedIndex == 5 ? value : gauge.High);
                break;
            case 6:
                value = ColorCustomization.NormalizeTractionCue(value);
                _controller.SetCustomTractionCueColor(value);
                ApplyTractionCueColor();
                break;
            default:
                ApplyAppColorResources(value, _controller.Settings.CustomBackgroundColor);
                _controller.SetCustomAccentColor(value);
                ApplyTractionCueColor();
                break;
        }
    }

    private void ApplyAppColorResources(string? customAccent, string? customBackground) =>
        AppThemeResources.Apply(
            Resources,
            AppColorThemes.Resolve(_controller.Settings.ColorTheme),
            AppBackgroundThemes.Resolve(_controller.Settings.BackgroundTheme),
            customAccent,
            customBackground);

    private void ApplyGaugeColors(string? low, string? mid, string? high)
    {
        BoostGaugeThemeResources.Apply(Resources, _controller.Settings.BoostGaugeTheme, low, mid, high);
        _controller.SetCustomGaugeColors(low, mid, high);
    }

    private void ApplyTractionCueColor() =>
        TractionCueThemeResources.Apply(Resources, ColorCustomization.ResolveTractionCue(_controller.Settings));

    private void SetEditorColor(ColorWheelEditor editor, Color color)
    {
        _updatingColorEditors = true;
        try
        {
            editor.SelectedColor = color;
        }
        finally
        {
            _updatingColorEditors = false;
        }
    }

    internal void ApplyHudPresetToControls()
    {
        var wasLoaded = _loaded;
        _loaded = false;
        try
        {
            var settings = _controller.Settings;
            MphRadio.IsChecked = settings.SpeedUnit == SpeedUnit.MilesPerHour;
            KphRadio.IsChecked = settings.SpeedUnit == SpeedUnit.KilometersPerHour;
            NewtonMetersRadio.IsChecked = settings.TorqueUnit == TorqueUnit.NewtonMeters;
            PoundFeetRadio.IsChecked = settings.TorqueUnit == TorqueUnit.PoundFeet;
            MinimalLayoutRadio.IsChecked = settings.LayoutMode == HudLayoutMode.Minimal;
            CombinedLayoutRadio.IsChecked = settings.LayoutMode == HudLayoutMode.Combined;
            SeparateBoxesLayoutRadio.IsChecked = settings.LayoutMode == HudLayoutMode.SeparateBoxes;
            NativeLayoutRadio.IsChecked = settings.LayoutMode == HudLayoutMode.Native;
            NativeDigitalRadio.IsChecked = settings.NativeGaugeMode == NativeGaugeMode.Digital;
            NativeAnalogueRadio.IsChecked = settings.NativeGaugeMode == NativeGaugeMode.Analogue;
            ManualGearDisplayRadio.IsChecked = settings.GearDisplayMode == GearDisplayMode.Manual;
            AutomaticGearDisplayRadio.IsChecked = settings.GearDisplayMode == GearDisplayMode.Automatic;

            LoadSelectedColorTarget();
            ApplyAppColorResources(settings.CustomAccentColor, settings.CustomBackgroundColor);
            HudBorderThemeResources.Apply(Resources, settings.HudBorderTheme, settings.CustomHudBorderColor);
            BoostGaugeThemeResources.Apply(
                Resources,
                settings.BoostGaugeTheme,
                settings.CustomBoostLowColor,
                settings.CustomBoostMidColor,
                settings.CustomBoostHighColor);
            ApplyTractionCueColor();
        }
        finally
        {
            _loaded = wasLoaded;
        }
    }

    private void RefreshHudProfileList()
    {
        HudProfileList.ItemsSource = null;
        HudProfileList.ItemsSource = _controller.Settings.HudPresets.ToArray();
        HudProfileEmptyState.Visibility = _controller.Settings.HudPresets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HudProfileList.Visibility = _controller.Settings.HudPresets.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SaveHudProfile_Click(object sender, RoutedEventArgs e) =>
        ShowHudProfileDialog(HudProfileDialogMode.Create);

    private void ApplyHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetHudProfile(sender, out var profile))
        {
            return;
        }

        if (_controller.TryApplyHudPreset(profile.Id, out var error))
        {
            HudProfileStatusText.Text = $"{profile.Name} applied.";
        }
        else
        {
            HudProfileStatusText.Text = error;
        }
    }

    private void UpdateHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetHudProfile(sender, out var profile))
        {
            ShowHudProfileDialog(HudProfileDialogMode.Update, profile);
        }
    }

    private void RenameHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetHudProfile(sender, out var profile))
        {
            ShowHudProfileDialog(HudProfileDialogMode.Rename, profile);
        }
    }

    private void DeleteHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetHudProfile(sender, out var profile))
        {
            ShowHudProfileDialog(HudProfileDialogMode.Delete, profile);
        }
    }

    private static bool TryGetHudProfile(object sender, out HudPreset profile)
    {
        if (sender is FrameworkElement { DataContext: HudPreset selected })
        {
            profile = selected;
            return true;
        }

        profile = null!;
        return false;
    }

    private void ShowHudProfileDialog(HudProfileDialogMode mode, HudPreset? profile = null)
    {
        _hudProfileDialogMode = mode;
        _activeHudProfileId = profile?.Id;
        _focusBeforeHudProfileDialog = Keyboard.FocusedElement;
        HudProfileDialogError.Text = string.Empty;
        HudProfileDialogError.Visibility = Visibility.Collapsed;
        HudProfileNameInput.Text = mode == HudProfileDialogMode.Rename ? profile?.Name ?? string.Empty : string.Empty;
        HudProfileNamePanel.Visibility = mode is HudProfileDialogMode.Create or HudProfileDialogMode.Rename
            ? Visibility.Visible
            : Visibility.Collapsed;

        switch (mode)
        {
            case HudProfileDialogMode.Create:
                HudProfileDialogTitle.Text = "Save HUD profile";
                HudProfileDialogDescription.Text = "Give this combination a name. Wisp will save the current HUD layout, gauges, units, sizing, opacity, orientation, and complete color palette together.";
                ConfirmHudProfileButton.Content = "Save profile";
                break;
            case HudProfileDialogMode.Update:
                HudProfileDialogTitle.Text = $"Update {profile?.Name}?";
                HudProfileDialogDescription.Text = "Replace this profile with the current Appearance setup and complete color palette.";
                ConfirmHudProfileButton.Content = "Update profile";
                break;
            case HudProfileDialogMode.Rename:
                HudProfileDialogTitle.Text = "Rename profile";
                HudProfileDialogDescription.Text = "Choose a new name. The saved HUD combination will not change.";
                ConfirmHudProfileButton.Content = "Rename profile";
                break;
            default:
                HudProfileDialogTitle.Text = $"Delete {profile?.Name}?";
                HudProfileDialogDescription.Text = "This removes only the saved profile. Your current HUD and all other settings stay unchanged.";
                ConfirmHudProfileButton.Content = "Delete profile";
                break;
        }

        TitleBar.IsEnabled = false;
        ControlBody.IsEnabled = false;
        HudProfileDialog.Visibility = Visibility.Visible;
        if (HudProfileNamePanel.Visibility == Visibility.Visible)
        {
            HudProfileNameInput.Focus();
            HudProfileNameInput.SelectAll();
        }
        else
        {
            ConfirmHudProfileButton.Focus();
        }
    }

    private void CancelHudProfileDialog_Click(object sender, RoutedEventArgs e) =>
        HideHudProfileDialog();

    private void HudProfileDialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        HideHudProfileDialog();
    }

    private void HudProfileNameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        ConfirmHudProfileDialog_Click(sender, e);
    }

    private void ConfirmHudProfileDialog_Click(object sender, RoutedEventArgs e)
    {
        HudPreset? savedProfile = null;
        string error;
        var succeeded = _hudProfileDialogMode switch
        {
            HudProfileDialogMode.Create => _controller.TryCreateHudPreset(
                HudProfileNameInput.Text,
                out savedProfile,
                out error),
            HudProfileDialogMode.Update when _activeHudProfileId is { } id =>
                _controller.TryUpdateHudPreset(id, out savedProfile, out error),
            HudProfileDialogMode.Rename when _activeHudProfileId is { } id =>
                _controller.TryRenameHudPreset(id, HudProfileNameInput.Text, out error),
            HudProfileDialogMode.Delete when _activeHudProfileId is { } id =>
                DeleteHudProfile(id, out error),
            _ => FailHudProfileAction(out error)
        };

        if (!succeeded)
        {
            HudProfileDialogError.Text = error;
            HudProfileDialogError.Visibility = Visibility.Visible;
            return;
        }

        var mode = _hudProfileDialogMode;
        var profileName = savedProfile?.Name ?? _controller.Settings.HudPresets
            .FirstOrDefault(profile => profile.Id == _activeHudProfileId)?.Name;
        HideHudProfileDialog();
        RefreshHudProfileList();
        RootTabs.SelectedItem = ProfilesTab;
        HudProfileStatusText.Text = mode switch
        {
            HudProfileDialogMode.Create => $"{profileName} saved.",
            HudProfileDialogMode.Update => $"{profileName} updated from the current HUD.",
            HudProfileDialogMode.Rename => "Profile renamed.",
            _ => "Profile deleted."
        };
    }

    private bool DeleteHudProfile(Guid id, out string error)
    {
        if (_controller.DeleteHudPreset(id))
        {
            error = string.Empty;
            return true;
        }

        error = "That profile is no longer available.";
        return false;
    }

    private static bool FailHudProfileAction(out string error)
    {
        error = "That profile is no longer available.";
        return false;
    }

    private void HideHudProfileDialog()
    {
        HudProfileDialog.Visibility = Visibility.Collapsed;
        TitleBar.IsEnabled = true;
        ControlBody.IsEnabled = true;
        HudProfileDialogError.Text = string.Empty;
        HudProfileNameInput.Text = string.Empty;
        _activeHudProfileId = null;
        if (_focusBeforeHudProfileDialog is { } previousFocus)
        {
            Keyboard.Focus(previousFocus);
        }
        _focusBeforeHudProfileDialog = null;
    }

    private enum HudProfileDialogMode { Create, Update, Rename, Delete }

    private void StarWispOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Views2k/Wisp")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            _controller.ViewModel.ReportControlError("Windows could not open the Wisp GitHub page");
        }
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
        var details = await _controller.GetAvailableApplicationUpdateDetailsAsync();
        if (details is null)
        {
            return;
        }

        ShowApplicationUpdateConfirmation(details);
    }

    private void ShowApplicationUpdateConfirmation(ApplicationUpdateDetails details)
    {
        _applicationUpdateVersion = details.Version;
        _focusBeforeApplicationUpdateConfirmation = Keyboard.FocusedElement;
        ApplicationUpdateConfirmationVersion.Text = $"Wisp {details.Version}";
        ApplicationUpdateConfirmationSummary.Text = details.ReleaseSummary;
        ApplicationUpdateConfirmationDetails.Visibility = string.IsNullOrWhiteSpace(details.ReleaseSummary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TitleBar.IsEnabled = false;
        ControlBody.IsEnabled = false;
        ApplicationUpdateConfirmation.Visibility = Visibility.Visible;
        ConfirmApplicationUpdateButton.Focus();
    }

    private void CancelApplicationUpdate_Click(object sender, RoutedEventArgs e) =>
        HideApplicationUpdateConfirmation();

    private void ApplicationUpdateConfirmation_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        HideApplicationUpdateConfirmation();
    }

    private void HideApplicationUpdateConfirmation()
    {
        ApplicationUpdateConfirmation.Visibility = Visibility.Collapsed;
        TitleBar.IsEnabled = true;
        ControlBody.IsEnabled = true;
        _applicationUpdateVersion = null;
        ApplicationUpdateConfirmationSummary.Text = string.Empty;
        ApplicationUpdateConfirmationDetails.Visibility = Visibility.Collapsed;
        if (_focusBeforeApplicationUpdateConfirmation is { } previousFocus)
        {
            Keyboard.Focus(previousFocus);
        }
        _focusBeforeApplicationUpdateConfirmation = null;
    }

    private async void ConfirmApplicationUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_applicationUpdateVersion is null)
        {
            HideApplicationUpdateConfirmation();
            return;
        }

        HideApplicationUpdateConfirmation();
        var installer = await _controller.PrepareApplicationUpdateAsync();
        if (installer is null)
        {
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

    private async void DebugLoggingToggle_Click(object sender, RoutedEventArgs e)
    {
        DebugLoggingToggle.IsEnabled = false;
        try
        {
            await _controller.SetDebugLoggingEnabledAsync(DebugLoggingToggle.IsChecked == true);
        }
        finally
        {
            DebugLoggingToggle.IsEnabled = true;
        }
    }

    private async void ExportDebugLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Wisp debug logs",
            Filter = "ZIP archives (*.zip)|*.zip",
            FileName = $"wisp-debug-{DateTimeOffset.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        DebugLogExportButton.IsEnabled = false;
        try
        {
            if (!await _controller.ExportDebugLogsAsync(dialog.FileName))
            {
                MessageBox.Show(this, "Wisp could not create the debug ZIP. Local logs were unchanged.",
                    "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            DebugLogExportButton.IsEnabled = true;
        }
    }

    private async void DeleteDebugLogs_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "Delete all local Wisp debug logs? Exported ZIP files are not affected.",
                "Delete local debug logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        DebugLogDeleteButton.IsEnabled = false;
        try
        {
            if (!await _controller.DeleteDebugLogsAsync())
            {
                MessageBox.Show(this, "Wisp could not delete every local debug log.",
                    "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            DebugLogDeleteButton.IsEnabled = true;
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

    private void OverlayHotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturingOverlayHotkey = true;
        OverlayHotkeyCaptureButton.SetCurrentValue(ContentProperty, "Press a shortcut…");
        OverlayHotkeyCaptureButton.Focus();
    }

    private void OverlayHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingOverlayHotkey)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            OverlayHotkeyCaptureButton.SetCurrentValue(ContentProperty, "Add another key…");
            return;
        }
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndOverlayHotkeyCapture();
            return;
        }

        var modifiers = ToOverlayHotkeyModifiers(Keyboard.Modifiers);
        if (!OverlayHotkeyChord.TryCreate(modifiers, key, out var chord, out var error))
        {
            OverlayHotkeyCaptureButton.SetCurrentValue(ContentProperty, "Try another shortcut…");
            _controller.ViewModel.ReportControlError(error);
            return;
        }

        _controller.ViewModel.OverlayHotkeyModifiers = chord.Modifiers;
        _controller.ViewModel.OverlayHotkeyKey = chord.Key;
        _controller.ApplyViewOptions();
        EndOverlayHotkeyCapture();
    }

    private void OverlayHotkeyCapture_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        EndOverlayHotkeyCapture();
    }

    private void EndOverlayHotkeyCapture()
    {
        if (!_capturingOverlayHotkey)
        {
            return;
        }
        _capturingOverlayHotkey = false;
        OverlayHotkeyCaptureButton.GetBindingExpression(ContentProperty)?.UpdateTarget();
    }

    private static OverlayHotkeyModifiers ToOverlayHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = OverlayHotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= OverlayHotkeyModifiers.Control;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= OverlayHotkeyModifiers.Alt;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= OverlayHotkeyModifiers.Shift;
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= OverlayHotkeyModifiers.Windows;
        }
        return result;
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

    private void TorqueUnit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.TorqueUnitSelectionIndex = PoundFeetRadio.IsChecked == true ? 1 : 0;
        _controller.ApplyViewOptions();
    }

    private void ResetDashboardPeaks_Click(object sender, RoutedEventArgs e) =>
        _controller.ViewModel.ResetDashboardPeaks();

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
