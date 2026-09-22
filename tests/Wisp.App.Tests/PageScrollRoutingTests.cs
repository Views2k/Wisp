using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class PageScrollRoutingTests
{
    [Fact]
    public void FittingNestedViewerReproducesWheelTrapAndHandsOffWhenEnabled() => OnSta(() =>
    {
        var content = new Border { Height = 40 };
        var inner = Viewer(content, 100);
        var (host, outer) = Host(inner);
        Assert.Equal(0, inner.ScrollableHeight);

        Wheel(content, -120);
        Layout(host);
        Assert.Equal(0, outer.VerticalOffset);

        PageScrollRouting.SetIsEnabled(host, true);
        string? wheelContext = null;
        Wheel(content, -120, context => wheelContext = context);
        Layout(host);
        Assert.True(outer.VerticalOffset > 0, HandoffContext(wheelContext, inner, outer));
        Assert.Equal(0, inner.VerticalOffset);

        outer.ScrollToTop();
        Layout(host);
        PageScrollRouting.SetIsEnabled(host, false);
        Wheel(content, -120);
        Layout(host);
        Assert.Equal(0, outer.VerticalOffset);
    });

    [Fact]
    public void NestedViewerScrollsLocallyThenHandsOffAtBothDirectionalEdges() => OnSta(() =>
    {
        var content = new Border { Height = 800 };
        var inner = Viewer(content, 100);
        var (host, outer) = Host(inner);
        PageScrollRouting.SetIsEnabled(host, true);

        Wheel(content, -120);
        Layout(host);
        Assert.True(inner.VerticalOffset > 0);
        Assert.Equal(0, outer.VerticalOffset);

        inner.ScrollToBottom();
        Layout(host);
        var end = inner.VerticalOffset;
        string? wheelContext = null;
        Wheel(content, -120, context => wheelContext = context);
        Layout(host);
        Assert.Equal(end, inner.VerticalOffset);
        Assert.True(outer.VerticalOffset > 0, HandoffContext(wheelContext, inner, outer));

        outer.ScrollToVerticalOffset(200);
        inner.ScrollToTop();
        Layout(host);
        Wheel(content, 120);
        Layout(host);
        Assert.Equal(0, inner.VerticalOffset);
        Assert.True(outer.VerticalOffset < 200);

        outer.ScrollToTop();
        Layout(host);
        var preview = Wheel(content, 120);
        Layout(host);
        Assert.False(preview);
        Assert.Equal(0, outer.VerticalOffset);
    });

    [Fact]
    public void NonScrollingOptionListDoesNotBlockThePageOrChangeSelection() => OnSta(() =>
    {
        var list = new ListBox { Height = 100, ItemsSource = new[] { "First", "Second" }, SelectedIndex = 1 };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var (host, outer) = Host(list);
        PageScrollRouting.SetIsEnabled(host, true);
        var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));

        string? wheelContext = null;
        var handled = Wheel(item, -120, context => wheelContext = context);
        Assert.True(handled, HandoffContext(wheelContext, Descendants(host).OfType<ScrollViewer>().ToArray()));
        Layout(host);
        Assert.True(outer.VerticalOffset > 0);
        Assert.Equal(1, list.SelectedIndex);
    });

    [Fact]
    public void NotesEditorScrollsItsOwnTextBeforeHandingOffWithoutEditingIt() => OnSta(() =>
    {
        var notes = new TextBox
        {
            Height = 100,
            Text = string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(index => $"Note {index}")),
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var (host, outer) = Host(notes);
        PageScrollRouting.SetIsEnabled(host, true);
        var editor = Assert.Single(Descendants(notes).OfType<ScrollViewer>());
        var source = Assert.Single(Descendants(editor).OfType<ScrollContentPresenter>());
        var text = notes.Text;
        notes.Select(3, 5);

        Wheel(source, -120);
        Layout(host);
        Assert.True(editor.VerticalOffset > 0);
        Assert.Equal(0, outer.VerticalOffset);
        editor.ScrollToBottom();
        Layout(host);
        Wheel(source, -120);
        Layout(host);
        Assert.True(outer.VerticalOffset > 0);
        Assert.Equal(text, notes.Text);
        Assert.Equal(3, notes.SelectionStart);
        Assert.Equal(5, notes.SelectionLength);
    });

    [Fact]
    public void VirtualizedLibraryRetainsLogicalScrollingAndSelection() => OnSta(() =>
    {
        var list = new ListBox { Height = 100, ItemsSource = Enumerable.Range(0, 200).ToArray(), SelectedIndex = 0 };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        var (host, outer) = Host(list);
        PageScrollRouting.SetIsEnabled(host, true);
        var inner = Assert.Single(Descendants(list).OfType<ScrollViewer>());
        var panel = Assert.Single(Descendants(list).OfType<VirtualizingStackPanel>());
        var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        Wheel(first, -120);
        Layout(host);
        Assert.True(inner.VerticalOffset > 0);
        Assert.Equal(0, outer.VerticalOffset);
        Assert.True(inner.CanContentScroll);
        Assert.InRange(panel.Children.Count, 1, 199);

        inner.ScrollToEnd();
        Layout(host);
        var last = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(199));
        string? wheelContext = null;
        Wheel(last, -120, context => wheelContext = context);
        Layout(host);
        Assert.True(outer.VerticalOffset > 0, HandoffContext(wheelContext, inner, outer));
        Assert.Equal(0, list.SelectedIndex);
        Assert.InRange(panel.Children.Count, 1, 199);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoveringAPlotUsesNormalPageScrollingWithoutChangingItsSelection(bool alternative) => OnSta(() =>
    {
        FrameworkElement plot = alternative ? new RunAlternativePlotView { Height = 280 } : new RunChartView { Height = 280, CursorSeconds = 4 };
        var selected = 0;
        if (plot is RunChartView chart) chart.IntervalSelected += (_, _) => selected++;
        if (plot is RunAlternativePlotView scatter) scatter.PointSelected += _ => selected++;
        var (host, outer) = Host(plot);
        PageScrollRouting.SetIsEnabled(host, true);
        Assert.False(Wheel(plot, -120));
        Layout(host);
        Assert.True(outer.VerticalOffset > 0);
        Assert.Equal(0, selected);
        if (plot is RunChartView timeChart) Assert.Equal(4, timeChart.CursorSeconds);
    });

    [Theory]
    [InlineData(ModifierKeys.Control, false)]
    [InlineData(ModifierKeys.Shift, false)]
    [InlineData(ModifierKeys.None, true)]
    public void ModifierGesturesAndCapturedDragsRemainWithTheirControl(ModifierKeys modifiers, bool captured) => OnSta(() =>
    {
        var content = new Border { Height = 40 };
        var (host, outer) = Host(Viewer(content, 100));
        var routed = false;
        host.PreviewMouseWheel += (_, args) => routed = PageScrollRouting.TryRoute(host, args, modifiers, captured);
        Assert.False(Wheel(content, -120));
        Layout(host);
        Assert.False(routed);
        Assert.Equal(0, outer.VerticalOffset);
    });

    [Fact]
    public void HandoffSkipsExhaustedAncestorsAndUsesOneNativeWheelEvent() => OnSta(() =>
    {
        var content = new Border { Height = 40 };
        var inner = Viewer(content, 80);
        var middle = Viewer(inner, 120);
        var (host, outer) = Host(middle);
        PageScrollRouting.SetIsEnabled(host, true);
        var events = new List<int>();
        outer.AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler((_, args) => events.Add(args.Delta)), true);
        string? wheelContext = null;
        var handled = Wheel(content, -30, context => wheelContext = context);
        Assert.True(handled, HandoffContext(wheelContext, inner, middle, outer));
        Layout(host);
        var routedOffset = outer.VerticalOffset;
        Assert.Equal(new[] { -30 }, events);
        Assert.True(routedOffset > 0);
        Assert.Equal(0, inner.VerticalOffset);
        Assert.Equal(0, middle.VerticalOffset);

        outer.ScrollToTop();
        Layout(host);
        outer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, -30) { RoutedEvent = Mouse.MouseWheelEvent });
        Layout(host);
        Assert.Equal(routedOffset, outer.VerticalOffset);
    });

    private static ScrollViewer Viewer(UIElement content, double height = double.NaN) => new()
    { Content = content, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private static (Border Host, ScrollViewer Outer) Host(UIElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(content);
        stack.Children.Add(new Border { Height = 1000 });
        var outer = Viewer(stack);
        var host = new Border { Child = outer };
        Layout(host);
        return (host, outer);
    }

    private static bool Wheel(UIElement source, int delta, Action<string>? recordContext = null)
    {
        var inputBefore = recordContext is null ? null :
            $"ModifiersBefore={Keyboard.Modifiers}; CapturedTypeBefore={Mouse.Captured?.GetType().FullName ?? "none"}";
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        source.RaiseEvent(args);
        var handledDuringPreview = args.Handled;
        if (!args.Handled)
        {
            args.RoutedEvent = Mouse.MouseWheelEvent;
            source.RaiseEvent(args);
        }
        recordContext?.Invoke($"{inputBefore}; PreviewHandled={handledDuringPreview}; FinalHandled={args.Handled}; " +
            $"ModifiersAfter={Keyboard.Modifiers}; CapturedTypeAfter={Mouse.Captured?.GetType().FullName ?? "none"}");
        return handledDuringPreview;
    }

    private static string HandoffContext(string? input, params ScrollViewer[] viewers) =>
        $"{input}; " + string.Join("; ", viewers.Select((viewer, index) =>
            $"Viewer{index}: Offset={viewer.VerticalOffset}, Scrollable={viewer.ScrollableHeight}, " +
            $"Extent={viewer.ExtentHeight}, Viewport={viewer.ViewportHeight}, Enabled={viewer.IsEnabled}, " +
            $"Bar={viewer.VerticalScrollBarVisibility}"));

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(320, 240));
        host.Arrange(new Rect(0, 0, 320, 240));
        host.UpdateLayout();
        host.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        host.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Page wheel routing check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
