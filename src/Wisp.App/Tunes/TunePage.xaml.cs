using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Wisp.App.Tunes;

public partial class TunePage : UserControl
{
    public static readonly DependencyProperty UseLegacyStyleProperty = DependencyProperty.Register(nameof(UseLegacyStyle), typeof(bool), typeof(TunePage),
        new PropertyMetadata(false, (owner, _) => ((TunePage)owner).ApplyStyle()));
    private IInputElement? _previousFocus;
    private TuneViewModel? Model => DataContext as TuneViewModel;
    public bool UseLegacyStyle { get => (bool)GetValue(UseLegacyStyleProperty); set => SetValue(UseLegacyStyleProperty, value); }
    public bool IsDialogOpen => Model?.IsDialogOpen == true;
    public event EventHandler? DialogStateChanged;

    public TunePage()
    {
        InitializeComponent();
        DataContextChanged += ContextChanged;
        Loaded += (_, _) => Model?.SetPageVisible(IsVisible);
        Unloaded += (_, _) => Model?.SetPageVisible(false);
        IsVisibleChanged += (_, _) => Model?.SetPageVisible(IsLoaded && IsVisible);
    }
    private void ApplyStyle() => Resources["TuneControlRadius"] = new CornerRadius(UseLegacyStyle ? 7 : 12);
    private void ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TuneViewModel old) { old.PropertyChanged -= ModelChanged; old.SetPageVisible(false); }
        if (Model is { } current) { current.PropertyChanged += ModelChanged; current.SetPageVisible(IsLoaded && IsVisible); }
        DialogStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TuneViewModel.IsDialogOpen)) return;
        if (IsDialogOpen)
        {
            _previousFocus = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsDialogOpen || Window.GetWindow(this)?.IsActive != true) return;
                TuneNameInput.Focus(); TuneNameInput.SelectAll();
            }));
        }
        else
        {
            if (Window.GetWindow(this)?.IsActive == true && _previousFocus is UIElement { IsVisible: true, IsEnabled: true } focus) focus.Focus();
            _previousFocus = null;
        }
        DialogStateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Current_Click(object sender, RoutedEventArgs e) => Model?.SetWorkspace(TuneWorkspace.Current);
    private void Saved_Click(object sender, RoutedEventArgs e) => Model?.SetWorkspace(TuneWorkspace.Saved);
    private void Compare_Click(object sender, RoutedEventArgs e) => Model?.SetWorkspace(TuneWorkspace.Compare);
    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Model?.CancelDialog(); e.Handled = true;
    }
    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model?.ConfirmDialogCommand.CanExecute(null) != true) return;
        Model.ConfirmDialogCommand.Execute(null); e.Handled = true;
    }
    private void Saved_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SavedTuneList, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        if (Model?.LoadCommand.CanExecute(null) == true) Model.LoadCommand.Execute(null);
    }
    private void Saved_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model?.LoadCommand.CanExecute(null) != true) return;
        Model.LoadCommand.Execute(null); e.Handled = true;
    }
}

public sealed class TuneSavedDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTimeOffset time ? time.ToLocalTime().ToString("g", culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
