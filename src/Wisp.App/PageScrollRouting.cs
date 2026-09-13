using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Wisp.App;

internal static class PageScrollRouting
{
    internal static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PageScrollRouting), new PropertyMetadata(false, EnabledChanged));

    internal static void SetIsEnabled(UIElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not UIElement element) return;
        if ((bool)change.NewValue) element.PreviewMouseWheel += PreviewMouseWheel;
        else element.PreviewMouseWheel -= PreviewMouseWheel;
    }

    private static void PreviewMouseWheel(object sender, MouseWheelEventArgs args) =>
        TryRoute((UIElement)sender, args, Keyboard.Modifiers, Mouse.Captured is not null);

    internal static bool TryRoute(UIElement scope, MouseWheelEventArgs args, ModifierKeys modifiers, bool mouseCaptured)
    {
        if (args.Handled || args.Delta == 0 || modifiers != ModifierKeys.None || mouseCaptured) return false;
        ScrollViewer? nearest = null;
        ScrollViewer? target = null;
        var reachedScope = false;
        for (var current = args.OriginalSource as DependencyObject; current is not null; current = Parent(current))
        {
            // A dropdown owns its wheel while open or focused. Text editors keep their
            // own scrolling until the editor reaches its edge, like any nested viewer.
            if (current is ComboBox combo && (combo.IsDropDownOpen || combo.IsKeyboardFocusWithin) || current is MenuBase)
                return false;
            if (current is ScrollViewer scroll)
            {
                nearest ??= scroll;
                if (target is null && CanMove(scroll, args.Delta)) target = scroll;
            }
            if (ReferenceEquals(current, scope)) { reachedScope = true; break; }
        }
        if (!reachedScope || target is null || ReferenceEquals(nearest, target)) return false;

        // WPF consumes wheel events even when an inner viewer cannot move. Start
        // the normal bubbling event at the first ancestor that can, retaining its
        // native line/page preferences and logical (virtualized) scrolling.
        var forwarded = new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
        { RoutedEvent = Mouse.MouseWheelEvent };
        target.RaiseEvent(forwarded);
        args.Handled = forwarded.Handled;
        return forwarded.Handled;
    }

    private static bool CanMove(ScrollViewer scroll, int delta) =>
        scroll.IsEnabled && scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled &&
        scroll.ScrollableHeight > 0 && (delta > 0 ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight);

    private static DependencyObject? Parent(DependencyObject element) => element switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(element),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(element)
    };
}
