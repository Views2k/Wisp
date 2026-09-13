using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Wisp.App.Runs;

public class RunsPageBase : UserControl
{
    private static readonly DependencyPropertyKey IsLibraryCompactPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsLibraryCompact), typeof(bool), typeof(RunsPageBase), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLibraryCompactProperty = IsLibraryCompactPropertyKey.DependencyProperty;
    public bool IsLibraryCompact => (bool)GetValue(IsLibraryCompactProperty);
    private PageElements _elements = null!;
    private bool _capturingShortcut;
    private bool _capturingMarkerShortcut;
    private Button ActiveShortcutButton => _capturingMarkerShortcut ? _elements.MarkerShortcutCaptureButton : _elements.ShortcutCaptureButton;
    protected RunsViewModel? Model => DataContext as RunsViewModel;
    protected virtual bool UsesModularWorkspace => false;

    protected sealed record PageElements(
        Button ShortcutCaptureButton,
        Button MarkerShortcutCaptureButton,
        ScrollViewer GraphScroll,
        ListBox GraphPicker,
        FrameworkElement RunHeading,
        ScrollViewer RunsScroll,
        FrameworkElement GraphsSurface,
        Button ShowGraphsButton);

    protected void InitializeRunsPage(PageElements elements)
    {
        if (_elements is not null) throw new InvalidOperationException("The runs page is already initialized.");
        _elements = elements;
        Unloaded += (_, _) => FinishShortcut();
        _elements.ShortcutCaptureButton.LostKeyboardFocus += (_, _) => FinishShortcut();
        _elements.MarkerShortcutCaptureButton.LostKeyboardFocus += (_, _) => FinishShortcut();
        IsVisibleChanged += (_, _) => Model?.SetPageVisible(IsVisible);
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is RunsViewModel previous) { previous.ShortcutCaptureActive = false; previous.SetPageVisible(false); previous.FocusChartsRequested -= FocusCharts; previous.ReportOpened -= OpenReport; previous.PropertyChanged -= ModelPropertyChanged; }
            if (Model is { } current) { current.SetModularWorkspaceEnabled(UsesModularWorkspace); current.FocusChartsRequested += FocusCharts; current.ReportOpened += OpenReport; current.PropertyChanged += ModelPropertyChanged; }
            Model?.SetPageVisible(IsVisible);
            OnModelPropertyChanged(null);
        };
    }

    protected void SetLibraryCompact(bool compact) => SetValue(IsLibraryCompactPropertyKey, compact);
    protected virtual void OnModelPropertyChanged(PropertyChangedEventArgs? change) { }
    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs change) => OnModelPropertyChanged(change);

    private void FocusCharts(object? sender, EventArgs e)
    {
        Model?.ShowGraphs();
        _ = Dispatcher.InvokeAsync(() => ScrollTo(_elements.GraphsSurface, _elements.GraphScroll));
    }
    protected void ViewGraphs_Click(object sender, RoutedEventArgs e)
    {
        Model?.ShowGraphs();
        _ = Dispatcher.InvokeAsync(() =>
        {
            _elements.GraphScroll.ScrollToHome();
            if (Window.GetWindow(this)?.IsActive != true || Model?.IsGraphWorkspaceOpen != true) return;
            _elements.GraphPicker.UpdateLayout();
            if (_elements.GraphPicker.SelectedItem is { } selected && _elements.GraphPicker.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem choice) choice.Focus();
            else _elements.GraphPicker.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    protected void GraphPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => _elements?.GraphScroll.ScrollToHome();
    protected void RunSummary_Click(object sender, RoutedEventArgs e)
    {
        Model?.ShowSummary();
        _ = Dispatcher.InvokeAsync(() =>
        {
            ScrollTo(_elements.RunHeading, _elements.RunsScroll);
            if (Window.GetWindow(this)?.IsActive == true && Model?.IsSummaryVisible == true) _elements.ShowGraphsButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void OpenReport(object? sender, EventArgs e)
    {
        Model?.ShowSummary();
        if (!IsVisible) return;
        _ = Dispatcher.InvokeAsync(() => { if (IsVisible) ScrollTo(_elements.RunHeading, _elements.RunsScroll); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void ScrollTo(FrameworkElement target, ScrollViewer scroll)
    {
        if (!IsVisible) return;
        scroll.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + target.TranslatePoint(new Point(), scroll).Y);
    }
    protected void Chart_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RunChartView chart) { chart.IntervalSelected -= SelectInterval; chart.IntervalSelected += SelectInterval; }
    }
    protected void Chart_Unloaded(object sender, RoutedEventArgs e) { if (sender is RunChartView chart) chart.IntervalSelected -= SelectInterval; }
    private void SelectInterval(double from, double to) => Model?.SelectInterval(from, to);
    protected void AlternativeChart_Loaded(object sender, RoutedEventArgs e)
    { if (sender is RunAlternativePlotView chart) { chart.PointSelected -= SelectAlternativePoint; chart.PointSelected += SelectAlternativePoint; } }
    protected void AlternativeChart_Unloaded(object sender, RoutedEventArgs e)
    { if (sender is RunAlternativePlotView chart) chart.PointSelected -= SelectAlternativePoint; }
    private void SelectAlternativePoint(RunAlternativeSelection point) => Model?.SelectAlternativePoint(point);
    protected void ShortcutEnabled_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is CheckBox toggle) model.HotkeyEnabled = toggle.IsChecked == true; }
    protected void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    { BeginShortcut(marker: false); }
    protected void MarkerShortcutEnabled_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is CheckBox toggle) model.MarkerHotkeyEnabled = toggle.IsChecked == true; }
    protected void CaptureMarkerShortcut_Click(object sender, RoutedEventArgs e) => BeginShortcut(marker: true);
    private void BeginShortcut(bool marker)
    {
        FinishShortcut(); _capturingMarkerShortcut = marker;
        ActiveShortcutButton.Focus();
        _capturingShortcut = true; if (Model is { } model) model.ShortcutCaptureActive = true;
        ActiveShortcutButton.SetCurrentValue(ContentProperty, "Press shortcut…");
    }
    protected void CaptureShortcut_KeyDown(object sender, KeyEventArgs e)
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
        _elements.ShortcutCaptureButton.GetBindingExpression(ContentProperty)?.UpdateTarget();
        _elements.MarkerShortcutCaptureButton.GetBindingExpression(ContentProperty)?.UpdateTarget();
    }
    protected async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Wisp runs",
            Multiselect = true,
            Filter = "Wisp runs and library archives (*.wisprun;*.zip)|*.wisprun;*.zip|Wisp run files (*.wisprun)|*.wisprun|Wisp library archive (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ImportManyAsync(dialog.FileNames);
    }
    protected async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanManageAllRuns: true } model) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export all saved runs",
            Filter = "Wisp library archive (*.zip)|*.zip",
            FileName = $"wisp-runs-{DateTime.Now:yyyy-MM-dd}",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, cancel) =>
        {
            if (!System.IO.Path.GetExtension(dialog.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(dialog.FileName))
            {
                cancel.Cancel = true;
                MessageBox.Show(Window.GetWindow(this), "Choose a new ZIP filename to keep your existing files.", "Choose another filename", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await model.ExportAllAsync(dialog.FileName);
    }
    protected async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanManageAllRuns: true } model) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Remove all {model.Library.Count} saved runs from the library?\n\nYou can undo this removal. Use Export all first if you want a portable backup.",
            "Delete all runs", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await model.DeleteAllAsync();
    }
    protected void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Model is not { CanManageRun: true } model) return;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            DataContext = model,
            Style = (Style)FindResource("RunExportMenuStyle")
        };
        foreach (var (label, csv) in new[] { ("Wisp run file · share or compare", false), ("CSV · recorded telemetry", true) })
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("RunExportItemStyle") };
            item.SetBinding(IsEnabledProperty, new Binding(nameof(RunsViewModel.CanManageRun)));
            item.Click += async (_, _) => await ExportFormatAsync(csv);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    protected async void Export_Click(object sender, RoutedEventArgs e) => await ExportFormatAsync(csv: false);

    private async Task ExportFormatAsync(bool csv)
    {
        if (Model is not { CanManageRun: true } model) return;
        var extension = csv ? ".csv" : ".wisprun";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export this Wisp run to a new file",
            Filter = csv ? "CSV · all recorded telemetry (*.csv)|*.csv" : "Wisp run file · re-import and compare (*.wisprun)|*.wisprun",
            FileName = "wisp-run",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, cancel) =>
        {
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
    protected async void SaveImage_Click(object sender, RoutedEventArgs e)
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
    protected async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (MessageBox.Show(Window.GetWindow(this), "Remove this run from your saved runs? You can undo the removal.",
            "Remove run", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await model.DeleteSelectedAsync();
    }
    protected async void UndoDelete_Click(object sender, RoutedEventArgs e) { if (Model is { } model) await model.UndoDeleteAsync(); }
}
