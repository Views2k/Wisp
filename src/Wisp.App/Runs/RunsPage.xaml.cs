using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Data;

namespace Wisp.App.Runs;

public partial class RunsPage : UserControl
{
    private bool _capturingShortcut;
    private bool _capturingMarkerShortcut;
    private Button ActiveShortcutButton => _capturingMarkerShortcut ? MarkerShortcutCaptureButton : ShortcutCaptureButton;
    private RunsViewModel? Model => DataContext as RunsViewModel;
    public RunsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => FinishShortcut();
        ShortcutCaptureButton.LostKeyboardFocus += (_, _) => FinishShortcut();
        MarkerShortcutCaptureButton.LostKeyboardFocus += (_, _) => FinishShortcut();
        IsVisibleChanged += (_, _) => Model?.SetPageVisible(IsVisible);
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is RunsViewModel previous) { previous.ShortcutCaptureActive = false; previous.SetPageVisible(false); previous.FocusChartsRequested -= FocusCharts; previous.ReportOpened -= OpenReport; }
            if (Model is { } current) { current.FocusChartsRequested += FocusCharts; current.ReportOpened += OpenReport; }
            Model?.SetPageVisible(IsVisible);
        };
    }
    private void FocusCharts(object? sender, EventArgs e)
    {
        Model?.ShowGraphs();
        _ = Dispatcher.InvokeAsync(() => ScrollTo(GraphsSurface, GraphScroll));
    }
    private void ViewGraphs_Click(object sender, RoutedEventArgs e)
    {
        Model?.ShowGraphs();
        _ = Dispatcher.InvokeAsync(() =>
        {
            GraphScroll.ScrollToHome();
            if (Window.GetWindow(this)?.IsActive != true || Model?.IsGraphWorkspaceOpen != true) return;
            GraphPicker.UpdateLayout();
            if (GraphPicker.SelectedItem is { } selected && GraphPicker.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem choice) choice.Focus();
            else GraphPicker.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void GraphPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => GraphScroll?.ScrollToHome();
    private void RunSummary_Click(object sender, RoutedEventArgs e)
    {
        Model?.ShowSummary();
        _ = Dispatcher.InvokeAsync(() =>
        {
            ScrollTo(RunHeading, RunsScroll);
            if (Window.GetWindow(this)?.IsActive == true && Model?.IsSummaryVisible == true) ShowGraphsButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void OpenReport(object? sender, EventArgs e)
    {
        Model?.ShowSummary();
        if (!IsVisible) return;
        _ = Dispatcher.InvokeAsync(() => { if (IsVisible) ScrollTo(RunHeading, RunsScroll); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void ScrollTo(FrameworkElement target, ScrollViewer scroll)
    {
        if (!IsVisible) return;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y);
    }
    private void Chart_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RunChartView chart) { chart.IntervalSelected -= SelectInterval; chart.IntervalSelected += SelectInterval; }
    }
    private void Chart_Unloaded(object sender, RoutedEventArgs e) { if (sender is RunChartView chart) chart.IntervalSelected -= SelectInterval; }
    private void SelectInterval(double from, double to) => Model?.SelectInterval(from, to);
    private void AlternativeChart_Loaded(object sender, RoutedEventArgs e)
    { if (sender is RunAlternativePlotView chart) { chart.PointSelected -= SelectAlternativePoint; chart.PointSelected += SelectAlternativePoint; } }
    private void AlternativeChart_Unloaded(object sender, RoutedEventArgs e)
    { if (sender is RunAlternativePlotView chart) chart.PointSelected -= SelectAlternativePoint; }
    private void SelectAlternativePoint(RunAlternativeSelection point) => Model?.SelectAlternativePoint(point);
    private void ShortcutEnabled_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is CheckBox toggle) model.HotkeyEnabled = toggle.IsChecked == true; }
    private void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    { BeginShortcut(marker: false); }
    private void MarkerShortcutEnabled_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is CheckBox toggle) model.MarkerHotkeyEnabled = toggle.IsChecked == true; }
    private void CaptureMarkerShortcut_Click(object sender, RoutedEventArgs e) => BeginShortcut(marker: true);
    private void BeginShortcut(bool marker)
    {
        FinishShortcut(); _capturingMarkerShortcut = marker;
        ActiveShortcutButton.Focus();
        _capturingShortcut = true; if (Model is { } model) model.ShortcutCaptureActive = true;
        ActiveShortcutButton.SetCurrentValue(ContentProperty, "Press shortcut…");
    }
    private void CaptureShortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingShortcut || Model is not { } model) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { FinishShortcut(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = OverlayHotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= OverlayHotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= OverlayHotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= OverlayHotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= OverlayHotkeyModifiers.Windows;
        if (OverlayHotkeyChord.TryCreate(modifiers, key, out var chord, out var error))
        {
            if (_capturingMarkerShortcut) model.ConfigureMarkerHotkey(model.MarkerHotkeyEnabled, chord);
            else model.ConfigureHotkey(model.HotkeyEnabled, chord);
            FinishShortcut();
        }
        else ActiveShortcutButton.SetCurrentValue(ContentProperty, error);
    }
    private void FinishShortcut()
    {
        _capturingShortcut = false; if (Model is { } model) model.ShortcutCaptureActive = false;
        ShortcutCaptureButton.GetBindingExpression(ContentProperty)?.UpdateTarget();
        MarkerShortcutCaptureButton.GetBindingExpression(ContentProperty)?.UpdateTarget();
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import a Wisp run", Filter = "Wisp run files (*.wisprun)|*.wisprun", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ImportAsync(dialog.FileName);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export this Wisp run to a new file",
            Filter = "Wisp run file · re-import and compare (*.wisprun)|*.wisprun|CSV · all recorded telemetry (*.csv)|*.csv",
            FileName = "wisp-run",
            DefaultExt = ".wisprun",
            AddExtension = true,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, cancel) =>
        {
            var extension = dialog.FilterIndex == 2 ? ".csv" : ".wisprun";
            if (!System.IO.Path.GetExtension(dialog.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                cancel.Cancel = true;
                MessageBox.Show(Window.GetWindow(this), "Use " + extension + " for the selected file type.", "Check file extension", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!System.IO.File.Exists(dialog.FileName)) return;
            cancel.Cancel = true;
            MessageBox.Show(Window.GetWindow(this), "Choose a new filename to keep the existing run file.", "File already exists", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ExportSelectedAsync(dialog.FileName);
    }
    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanExportImage: true } model) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the current report and graphs as an image",
            Filter = "PNG image (*.png)|*.png",
            FileName = "wisp-run-report",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, cancel) =>
        {
            if (!System.IO.File.Exists(dialog.FileName)) return;
            cancel.Cancel = true;
            MessageBox.Show(Window.GetWindow(this), "Choose a new filename to keep the existing image.", "File already exists", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ExportImageAsync(dialog.FileName);
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (MessageBox.Show(Window.GetWindow(this), "Remove this run from your saved runs? You can undo the removal.",
            "Remove run", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await model.DeleteSelectedAsync();
    }
    private async void UndoDelete_Click(object sender, RoutedEventArgs e) { if (Model is { } model) await model.UndoDeleteAsync(); }
}

public sealed class RunChartHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0 ? Math.Clamp(height - 50, 180, 270) : 270d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
