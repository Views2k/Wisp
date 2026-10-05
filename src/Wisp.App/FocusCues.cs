using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wisp.App;

// Focus outlines help keyboard navigation, but a mouse click also gives a control
// keyboard focus. Templates combine IsKeyboardFocused with this inherited flag so
// an outline appears after Tab or arrow keys and not after clicking.
public static class FocusCues
{
    public static readonly DependencyProperty ShowKeyboardFocusProperty = DependencyProperty.RegisterAttached(
        "ShowKeyboardFocus", typeof(bool), typeof(FocusCues),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private static bool _keyboard, _installed;

    public static bool GetShowKeyboardFocus(DependencyObject element) => (bool)element.GetValue(ShowKeyboardFocusProperty);
    public static void SetShowKeyboardFocus(DependencyObject element, bool value) => element.SetValue(ShowKeyboardFocusProperty, value);

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputManager.Current.PostProcessInput += (_, e) => Observe(e.StagingItem.Input);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Apply));
        // A context menu sits outside its owner's tree and does not inherit the window's value.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(Apply));
    }

    private static void Apply(object sender, RoutedEventArgs e) => SetShowKeyboardFocus((DependencyObject)sender, _keyboard);

    private static bool? ModeFor(InputEventArgs input) => input switch
    {
        KeyEventArgs key when key.RoutedEvent == Keyboard.PreviewKeyDownEvent && !IsModifier(key.Key == Key.System ? key.SystemKey : key.Key) => true,
        MouseButtonEventArgs button when button.RoutedEvent == Mouse.PreviewMouseDownEvent => false,
        StylusDownEventArgs or TouchEventArgs => false,
        _ => null
    };

    private static void Observe(InputEventArgs input)
    {
        if (ModeFor(input) is not { } keyboard || keyboard == _keyboard) return;
        _keyboard = keyboard;
        // Every root on this thread, so open menus and drop-downs follow too.
        foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>())
            if (source.RootVisual is { } root && root.CheckAccess()) SetShowKeyboardFocus(root, keyboard);
    }

    // Alt+Tab and shortcut chords switch windows; they are not navigation.
    private static bool IsModifier(Key key) => key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
