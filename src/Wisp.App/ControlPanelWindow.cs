using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.App.Runs;
using Wisp.Core;

namespace Wisp.App;

public abstract partial class ControlPanelWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;
    private const uint DefaultToNearestMonitor = 2;
    private readonly AppController _controller;
    private bool _controlsReady;
    private bool _loaded;
    private bool _sidebarOpen = true;
    private string? _applicationUpdateVersion;
    private IInputElement? _focusBeforeApplicationUpdateConfirmation;
    private IInputElement? _focusBeforeHudProfileDialog;
    private Guid? _activeHudProfileId;
    private HudProfileDialogMode _hudProfileDialogMode;
    private bool _hudProfileChangePending;
    private string? _pendingHudProfileName;
    private bool _capturingOverlayHotkey;
    private bool _updatingColorEditors;

    private Grid TitleBar => FindControl<Grid>(nameof(TitleBar));
    private Grid ControlBody => FindControl<Grid>(nameof(ControlBody));
    private TabControl RootTabs => FindControl<TabControl>(nameof(RootTabs));
    private TabItem DashboardTab => FindControl<TabItem>(nameof(DashboardTab));
    private Border DashboardRunPanel => FindControl<Border>(nameof(DashboardRunPanel));
    private Button LockButton => FindControl<Button>(nameof(LockButton));
    private TabItem RunsTab => FindControl<TabItem>(nameof(RunsTab));
    private TabItem DiagnosticsTab => FindControl<TabItem>(nameof(DiagnosticsTab));
    private Expander ConnectionHelp => FindControl<Expander>(nameof(ConnectionHelp));
    private RunsPageBase RunsSurface => FindControl<RunsPageBase>(nameof(RunsSurface));
    private Button AppearanceLockButton => FindControl<Button>(nameof(AppearanceLockButton));
    private RadioButton MinimalLayoutRadio => FindControl<RadioButton>(nameof(MinimalLayoutRadio));
    private RadioButton CombinedLayoutRadio => FindControl<RadioButton>(nameof(CombinedLayoutRadio));
    private RadioButton SeparateBoxesLayoutRadio => FindControl<RadioButton>(nameof(SeparateBoxesLayoutRadio));
    private RadioButton NativeLayoutRadio => FindControl<RadioButton>(nameof(NativeLayoutRadio));
    private RadioButton NativeDigitalRadio => FindControl<RadioButton>(nameof(NativeDigitalRadio));
    private RadioButton NativeAnalogueRadio => FindControl<RadioButton>(nameof(NativeAnalogueRadio));
    private RadioButton MphRadio => FindControl<RadioButton>(nameof(MphRadio));
    private RadioButton KphRadio => FindControl<RadioButton>(nameof(KphRadio));
    private RadioButton NewtonMetersRadio => FindControl<RadioButton>(nameof(NewtonMetersRadio));
    private RadioButton PoundFeetRadio => FindControl<RadioButton>(nameof(PoundFeetRadio));
    private RadioButton WheelSpeedSourceRadio => FindControl<RadioButton>(nameof(WheelSpeedSourceRadio));
    private RadioButton Fh6SpeedSourceRadio => FindControl<RadioButton>(nameof(Fh6SpeedSourceRadio));
    private RadioButton AutomaticGearDisplayRadio => FindControl<RadioButton>(nameof(AutomaticGearDisplayRadio));
    private RadioButton ManualGearDisplayRadio => FindControl<RadioButton>(nameof(ManualGearDisplayRadio));
    private ListBox ColorTargetSelector => FindControl<ListBox>(nameof(ColorTargetSelector));
    private ColorWheelEditor ColorEditor => FindControl<ColorWheelEditor>(nameof(ColorEditor));
    private Button OverlayHotkeyCaptureButton => FindControl<Button>(nameof(OverlayHotkeyCaptureButton));
    private TextBlock PortApplyFeedback => FindControl<TextBlock>(nameof(PortApplyFeedback));
    private CheckBox CpuRenderingToggle => FindControl<CheckBox>(nameof(CpuRenderingToggle));
    private CheckBox DebugLoggingToggle => FindControl<CheckBox>(nameof(DebugLoggingToggle));
    private Button DebugLogExportButton => FindControl<Button>(nameof(DebugLogExportButton));
    private Button DebugLogDeleteButton => FindControl<Button>(nameof(DebugLogDeleteButton));
    private TextBlock HudProfileStatusText => FindControl<TextBlock>(nameof(HudProfileStatusText));
    private Border HudProfileEmptyState => FindControl<Border>(nameof(HudProfileEmptyState));
    private ListBox HudProfileList => FindControl<ListBox>(nameof(HudProfileList));
    private Grid HudProfileDialog => FindControl<Grid>(nameof(HudProfileDialog));
    private TextBlock HudProfileDialogTitle => FindControl<TextBlock>(nameof(HudProfileDialogTitle));
    private TextBlock HudProfileDialogDescription => FindControl<TextBlock>(nameof(HudProfileDialogDescription));
    private StackPanel HudProfileNamePanel => FindControl<StackPanel>(nameof(HudProfileNamePanel));
    private TextBox HudProfileNameInput => FindControl<TextBox>(nameof(HudProfileNameInput));
    private TextBlock HudProfileDialogError => FindControl<TextBlock>(nameof(HudProfileDialogError));
    private Button ConfirmHudProfileButton => FindControl<Button>(nameof(ConfirmHudProfileButton));
    private Grid ApplicationUpdateConfirmation => FindControl<Grid>(nameof(ApplicationUpdateConfirmation));
    private TextBlock ApplicationUpdateConfirmationVersion => FindControl<TextBlock>(nameof(ApplicationUpdateConfirmationVersion));
    private Border ApplicationUpdateConfirmationDetails => FindControl<Border>(nameof(ApplicationUpdateConfirmationDetails));
    private TextBlock ApplicationUpdateConfirmationSummary => FindControl<TextBlock>(nameof(ApplicationUpdateConfirmationSummary));
    private Button ConfirmApplicationUpdateButton => FindControl<Button>(nameof(ConfirmApplicationUpdateButton));

    private T FindControl<T>(string name) where T : class =>
        FindName(name) as T ?? throw new InvalidOperationException($"The control panel is missing {name}.");

    protected ControlPanelWindow(AppController controller)
    {
        _controller = controller;
    }

    protected void InitializeControlPanel()
    {
        _controlsReady = true;
        var controller = _controller;
        var accentTheme = AppColorThemes.Resolve(controller.Settings.ColorTheme);
        var backgroundTheme = AppBackgroundThemes.Resolve(controller.Settings.BackgroundTheme);
        var hudBorderTheme = AppColorThemes.Resolve(controller.Settings.HudBorderTheme);
        var boostTheme = BoostGaugeThemes.Resolve(controller.Settings.BoostGaugeTheme);
        AppThemeResources.Apply(
            Resources,
            accentTheme,
            backgroundTheme,
            controller.Settings.CustomAccentColor,
            controller.Settings.CustomBackgroundColor,
            controller.Settings.ApplicationStyle,
            controller.Settings.CustomParticleColor);
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
        FindControl<DriftGaugeSettingsControl>("DriftGaugeSettings").Initialize(controller);
        FindControl<PowerTorqueGaugeSettingsControl>("PowerTorqueGaugeSettings").Initialize(controller);
        FindControl<ShiftCueSettingsControl>("ShiftCueSettings").Initialize(controller);
        RunsSurface.DataContext = controller.Runs;
        DashboardRunPanel.DataContext = controller.Runs;
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
        RootTabs.SelectedItem = DashboardTab;
        InitializeFeatureTour();
        UpdateLockButtonLabels(controller.Settings.OverlayLocked);
        SourceInitialized += (_, _) =>
        {
            ApplyNativeRoundedCorners();
            FitToCurrentWorkArea();
        };
        DpiChanged += (_, _) => FitToCurrentWorkArea();
        LocationChanged += (_, _) => FitToCurrentWorkArea();
        Loaded += (_, _) => _loaded = true;
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseConnectionPanel(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseConnectionPanel(); };
        Closed += (_, _) =>
        {
            CloseConnectionPanel();
            StopSidebarAnimation();
            RunsSurface.DataContext = null;
            DashboardRunPanel.DataContext = null;
        };
    }

    internal bool IsSidebarOpen => _sidebarOpen;

    protected void ColorTargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_controlsReady)
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
                ColorEditor.Description = "Outline used by Combined and Box layouts";
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
                    3 => "Low end of the shared boost, tire, power, and torque gauge gradient",
                    4 => "Middle color of the shared boost, tire, power, and torque gauge gradient",
                    _ => "High end of the shared boost, tire, power, and torque gauge gradient"
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
            case 7:
                ColorEditor.Title = "App borders";
                ColorEditor.Description = "Outlines around app panels and controls";
                ColorEditor.MinimumOpacity = 0;
                ColorEditor.MaximumBrightness = 1;
                color = ResolveApplicationStyleColor(settings.ApplicationStyle.BorderColor, "OrbitBorderBrush", "#41536A");
                break;
            case 8:
                ColorEditor.Title = "Main text";
                ColorEditor.Description = "App headings, values, and control labels";
                ColorEditor.MinimumOpacity = 0.7;
                ColorEditor.MaximumBrightness = 1;
                color = ResolveApplicationStyleColor(settings.ApplicationStyle.TextColor, "TextBrush", "#F5F8FC");
                break;
            case 9:
                ColorEditor.Title = "Secondary text";
                ColorEditor.Description = "Supporting labels and descriptions in the app";
                ColorEditor.MinimumOpacity = 0.7;
                ColorEditor.MaximumBrightness = 1;
                color = ResolveApplicationStyleColor(settings.ApplicationStyle.MutedTextColor, "MutedBrush", "#91A0B3");
                break;
            case 10:
            case 11:
                var trail = ColorTargetSelector.SelectedIndex == 11;
                ColorEditor.Title = trail ? "G-force trail" : "G-force dot";
                ColorEditor.Description = trail ? "The recent path behind the moving G-force dot" : "The moving dot inside every G-force meter";
                ColorEditor.MinimumOpacity = 0.25;
                ColorEditor.MaximumBrightness = 1;
                if (!ColorCustomization.TryParse(trail ? settings.CustomGForceTrailColor : settings.CustomGForceColor, out color))
                    ColorCustomization.TryParse(settings.LayoutMode == HudLayoutMode.Native ? (trail ? "#66FFFFFF" : "#CCFFFFFF") : "#55E6C1", out color);
                break;
            case 12:
                ColorEditor.Title = "Background particles";
                ColorEditor.Description = "Particle color only. Use accent color keeps it linked to the app accent.";
                ColorEditor.MinimumOpacity = 0;
                ColorEditor.MaximumBrightness = 1;
                color = ColorCustomization.ResolveParticle(settings);
                break;
            case 13:
                ColorEditor.Title = "Drift cut flash";
                ColorEditor.Description = "Power and torque numbers pulse with this color during a Drift mode cut.";
                ColorEditor.MinimumOpacity = 1;
                ColorEditor.MaximumBrightness = 1;
                color = ColorCustomization.ResolvePowerTorqueDriftFlash(settings.PowerTorqueDriftFlashColor);
                break;
            case 14:
            case 15:
            case 16:
                var stage = ColorTargetSelector.SelectedIndex - 13;
                ColorEditor.Title = stage == 1 ? "Shift cue · approach (beta)" : stage == 2 ? "Shift cue · prepare (beta)" : "Shift cue · shift (beta)";
                ColorEditor.Description = "Beta performance shift cue color. The shift stage flashes.";
                ColorEditor.MinimumOpacity = 1;
                ColorEditor.MaximumBrightness = 1;
                var argb = DiagnosticsViewModel.ResolveShiftCueColor(settings, stage);
                color = Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
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

    private Color ResolveApplicationStyleColor(string? customColor, string resourceKey, string fallback)
    {
        if (ColorCustomization.TryParse(customColor, out var color))
            return color;

        return TryFindResource(resourceKey) switch
        {
            SolidColorBrush brush => brush.Color,
            GradientBrush { GradientStops.Count: > 0 } brush => brush.GradientStops[0].Color,
            _ => (Color)ColorConverter.ConvertFromString(fallback)
        };
    }

    protected void RefreshApplicationColorEditor() => LoadSelectedColorTarget();

    protected void ColorEditor_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color> e)
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
            case 7:
            case 8:
            case 9:
                var style = _controller.Settings.ApplicationStyle.Clone();
                if (ColorTargetSelector.SelectedIndex is 8 or 9)
                {
                    var selected = ColorEditor.SelectedColor;
                    selected.A = Math.Max(selected.A, (byte)179);
                    value = ColorCustomization.ToHex(selected);
                }
                switch (ColorTargetSelector.SelectedIndex)
                {
                    case 7: style.BorderColor = value; break;
                    case 8: style.TextColor = value; break;
                    case 9: style.MutedTextColor = value; break;
                }
                _controller.SetApplicationStyle(style);
                ApplyAppColorResources(_controller.Settings.CustomAccentColor, _controller.Settings.CustomBackgroundColor);
                break;
            case 10:
                _controller.SetCustomGForceColor(value);
                break;
            case 11:
                _controller.SetCustomGForceTrailColor(value);
                break;
            case 12:
                _controller.SetCustomParticleColor(value);
                ApplyAppColorResources(_controller.Settings.CustomAccentColor, _controller.Settings.CustomBackgroundColor);
                break;
            case 13:
                _controller.SetPowerTorqueDriftFlashColor(value);
                break;
            case 14:
            case 15:
            case 16:
                _controller.SetShiftCueColor(ColorTargetSelector.SelectedIndex - 13, value);
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
            customBackground,
            _controller.Settings.ApplicationStyle,
            _controller.Settings.CustomParticleColor);

    protected void UseAccentParticleColor_Click(object sender, RoutedEventArgs e)
    {
        _controller.SetCustomParticleColor(null);
        ApplyAppColorResources(_controller.Settings.CustomAccentColor, _controller.Settings.CustomBackgroundColor);
        LoadSelectedColorTarget();
    }

    protected void ResetDriftFlashColor_Click(object sender, RoutedEventArgs e)
    {
        _controller.SetPowerTorqueDriftFlashColor(null);
        LoadSelectedColorTarget();
    }

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

    protected void SaveHudProfile_Click(object sender, RoutedEventArgs e) =>
        ShowHudProfileDialog(HudProfileDialogMode.Create);

    protected void ApplyHudProfile_Click(object sender, RoutedEventArgs e)
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

    protected void UpdateHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetHudProfile(sender, out var profile))
        {
            ShowHudProfileDialog(HudProfileDialogMode.Update, profile);
        }
    }

    protected void RenameHudProfile_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetHudProfile(sender, out var profile))
        {
            ShowHudProfileDialog(HudProfileDialogMode.Rename, profile);
        }
    }

    protected void DeleteHudProfile_Click(object sender, RoutedEventArgs e)
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
        _hudProfileChangePending = false;
        _pendingHudProfileName = null;
        HudProfileNameInput.IsEnabled = true;
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

    protected void CancelHudProfileDialog_Click(object sender, RoutedEventArgs e) =>
        HideHudProfileDialog();

    protected void HudProfileDialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        HideHudProfileDialog();
    }

    protected void HudProfileNameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        ConfirmHudProfileDialog_Click(sender, e);
    }

    protected void ConfirmHudProfileDialog_Click(object sender, RoutedEventArgs e)
    {
        HudPreset? savedProfile = null;
        string error = string.Empty;
        var succeeded = _hudProfileChangePending || (_hudProfileDialogMode switch
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
        });

        if (!succeeded)
        {
            HudProfileDialogError.Text = error;
            HudProfileDialogError.Visibility = Visibility.Visible;
            return;
        }

        if (!_hudProfileChangePending)
        {
            _pendingHudProfileName = savedProfile?.Name ?? _controller.Settings.HudPresets
                .FirstOrDefault(profile => profile.Id == _activeHudProfileId)?.Name;
            _hudProfileChangePending = true;
            HudProfileNameInput.IsEnabled = false;
        }
        if (!_controller.TrySavePendingSettings())
        {
            HudProfileDialogError.Text = "Wisp could not write these changes to disk. Retry to confirm they are saved before closing Wisp.";
            HudProfileDialogError.Visibility = Visibility.Visible;
            ConfirmHudProfileButton.Content = "Try again";
            HudProfileStatusText.Text = "The last profile save attempt failed.";
            RefreshHudProfileList();
            return;
        }

        var mode = _hudProfileDialogMode;
        var profileName = _pendingHudProfileName;
        HideHudProfileDialog();
        RefreshHudProfileList();
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
        _hudProfileChangePending = false;
        _pendingHudProfileName = null;
        HudProfileNameInput.IsEnabled = true;
        if (_focusBeforeHudProfileDialog is { } previousFocus)
        {
            Keyboard.Focus(previousFocus);
        }
        _focusBeforeHudProfileDialog = null;
    }

    private enum HudProfileDialogMode { Create, Update, Rename, Delete }

    protected void StarWispOnGitHub_Click(object sender, RoutedEventArgs e)
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

    protected void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSidebarOpen(!_sidebarOpen, animate: true);
        _controller.SetSidebarCollapsed(!_sidebarOpen);
    }

    internal void SetSidebarOpen(bool open, bool animate)
    {
        _sidebarOpen = open;
        ApplySidebarLayout(open, animate);
    }

    protected abstract void ApplySidebarLayout(bool open, bool animate);

    protected abstract void StopSidebarAnimation();

    protected async void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        var applyButton = sender as Button;
        if (applyButton is not null)
        {
            applyButton.IsEnabled = false;
        }
        PortApplyFeedback.Text = string.Empty;
        PortApplyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        try
        {
            var port = UdpPortInput.Parse(_controller.ViewModel.UdpPortText);
            PortApplyFeedback.Text = $"Applying UDP port {port}…";
            await _controller.RestartListenerAsync(port);
            _controller.ViewModel.UdpPort = port;
            PortApplyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            PortApplyFeedback.Text = $"UDP port {port} applied.";
        }
        catch (ArgumentOutOfRangeException)
        {
            PortApplyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            PortApplyFeedback.Text = "Enter a UDP port from 1024 to 65535, excluding 5200–5300.";
        }
        catch (SocketException exception)
        {
            PortApplyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            PortApplyFeedback.Text = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "That UDP port is already in use. Choose another port in Wisp and FH6."
                : $"Could not apply the UDP port. {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            PortApplyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            PortApplyFeedback.Text = exception.Message;
        }
        finally
        {
            if (applyButton is not null)
            {
                applyButton.IsEnabled = true;
            }
        }
    }

    protected async void CheckCompatibility_Click(object sender, RoutedEventArgs e) =>
        await _controller.CheckNativeCompatibilityUpdatesAsync();

    protected async void CheckApplicationUpdate_Click(object sender, RoutedEventArgs e)
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
        ApplicationUpdateConfirmationVersion.Text = $"Wisp {ApplicationVersionInfo.Format(Version.Parse(details.Version))}";
        ApplicationUpdateConfirmationSummary.Text = details.ReleaseSummary;
        ApplicationUpdateConfirmationDetails.Visibility = string.IsNullOrWhiteSpace(details.ReleaseSummary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TitleBar.IsEnabled = false;
        ControlBody.IsEnabled = false;
        ApplicationUpdateConfirmation.Visibility = Visibility.Visible;
        ConfirmApplicationUpdateButton.Focus();
    }

    protected void CancelApplicationUpdate_Click(object sender, RoutedEventArgs e) =>
        HideApplicationUpdateConfirmation();

    protected void ApplicationUpdateConfirmation_KeyDown(object sender, KeyEventArgs e)
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

    protected async void ConfirmApplicationUpdate_Click(object sender, RoutedEventArgs e)
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

    protected async void ImportCompatibility_Click(object sender, RoutedEventArgs e)
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

    protected void CpuRenderingToggle_Click(object sender, RoutedEventArgs e)
    {
        _controller.SetCpuRenderingEnabled(CpuRenderingToggle.IsChecked == true);
        CpuRenderingToggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    protected async void DebugLoggingToggle_Click(object sender, RoutedEventArgs e)
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

    protected async void ExportDebugLogs_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show(this, "Wisp could not create the debug ZIP. Try another destination.",
                    "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            DebugLogExportButton.IsEnabled = true;
        }
    }

    protected async void DeleteDebugLogs_Click(object sender, RoutedEventArgs e)
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

    protected void LegacyInterface_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        if (sender is CheckBox checkBox)
        {
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        }
        _controller.SetUseLegacyInterface(_controller.ViewModel.UseLegacyInterface);
    }

    protected void Option_Changed(object sender, RoutedEventArgs e)
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

    protected void OverlayHotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturingOverlayHotkey = true;
        OverlayHotkeyCaptureButton.SetCurrentValue(ContentProperty, "Press a shortcut…");
        OverlayHotkeyCaptureButton.Focus();
    }

    protected void OverlayHotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
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

    protected void OverlayHotkeyCapture_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
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

    protected void Unit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.UnitSelectionIndex = KphRadio.IsChecked == true ? 1 : 0;
        _controller.ApplyViewOptions();
    }

    protected void TorqueUnit_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _controller.ViewModel.TorqueUnitSelectionIndex = PoundFeetRadio.IsChecked == true ? 1 : 0;
        _controller.ApplyViewOptions();
    }

    protected void ResetDashboardPeaks_Click(object sender, RoutedEventArgs e) =>
        _controller.ViewModel.ResetDashboardPeaks();

    protected void SpeedSource_Checked(object sender, RoutedEventArgs e)
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

    protected void GearDisplay_Checked(object sender, RoutedEventArgs e)
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

    protected void Layout_Checked(object sender, RoutedEventArgs e)
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

    protected void NativeGauge_Checked(object sender, RoutedEventArgs e)
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

    protected void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        var locked = !_controller.Settings.OverlayLocked;
        _controller.SetOverlayLocked(locked);
        UpdateLockButtonLabels(locked);
    }

    private void UpdateLockButtonLabels(bool locked)
    {
        var label = locked ? "Edit HUD layout" : "Lock HUD layout";
        if (FindName("DashboardLockLabel") is TextBlock dashboardLockLabel)
        {
            dashboardLockLabel.Text = label;
        }
        else
        {
            LockButton.Content = label;
        }
        System.Windows.Automation.AutomationProperties.SetName(LockButton, label);
        AppearanceLockButton.Content = label;
    }

    protected void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _controller.ResetOverlayPosition();
    }

    protected void OpenHudControls_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedIndex = 2;

    protected void OpenDiagnostics_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedItem = DiagnosticsTab;

    protected void OpenSetup_Click(object sender, RoutedEventArgs e)
    {
        RootTabs.SelectedItem = DiagnosticsTab;
        ConnectionHelp.IsExpanded = true;
        ConnectionHelp.BringIntoView();
    }

    protected void OpenRuns_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedItem = RunsTab;

    internal void ShowRunSaveProblem()
    {
        CloseConnectionPanel();
        RootTabs.SelectedItem = RunsTab;
        RunsSurface.Focus();
    }

    protected void RelearnTires_Click(object sender, RoutedEventArgs e)
    {
        if (!_controller.RelearnCurrentTires())
        {
            _controller.ViewModel.ReportControlError("Start driving before relearning the current tire profile");
        }
    }

    protected void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

    protected void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    protected void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected virtual Size MinimumControlPanelSize => new(720, 440);

    protected void FitToCurrentWorkArea()
    {
        var workAreaSize = CurrentMonitorPhysicalWorkAreaSize();
        var dpi = VisualTreeHelper.GetDpi(this);
        var minimumSize = ControlWindowGeometry.FitToPhysicalWorkArea(
            MinimumControlPanelSize,
            workAreaSize,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        MinWidth = minimumSize.Width;
        MinHeight = minimumSize.Height;

        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var fittedSize = ControlWindowGeometry.FitToPhysicalWorkArea(
            new Size(Width, Height),
            workAreaSize,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
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
