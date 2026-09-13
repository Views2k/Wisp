using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ScrollEdgeFadeTests
{
    [Fact]
    public void OnlyEdgesWithRemainingVerticalContentFadeWithoutMovingLayout() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 180, 700);
        var presenter = Presenter(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        foreach (var position in new[] { 0d, 160d, viewer.ScrollableHeight })
        {
            viewer.ScrollToVerticalOffset(position);
            Layout(viewer);
            var offset = viewer.VerticalOffset;
            var viewport = new Size(viewer.ViewportWidth, viewer.ViewportHeight);
            var extent = new Size(viewer.ExtentWidth, viewer.ExtentHeight);
            var contentSize = ((FrameworkElement)viewer.Content).RenderSize;
            var presenterBounds = Bounds(presenter, viewer);
            ScrollEdgeFade.Refresh(presenter);
            var frame = Render(presenter);
            var top = Alpha(frame, frame.PixelWidth / 2, 1);
            var middle = Alpha(frame, frame.PixelWidth / 2, frame.PixelHeight / 2);
            var bottom = Alpha(frame, frame.PixelWidth / 2, frame.PixelHeight - 2);
            Assert.InRange(middle, 250, 255);
            if (offset == 0) Assert.InRange(top, 250, 255);
            else Assert.InRange(top, 0, 60);
            if (Math.Abs(offset - viewer.ScrollableHeight) < 0.01) Assert.InRange(bottom, 250, 255);
            else Assert.InRange(bottom, 0, 60);
            Assert.Equal(offset, viewer.VerticalOffset);
            Assert.Equal(viewport, new Size(viewer.ViewportWidth, viewer.ViewportHeight));
            Assert.Equal(extent, new Size(viewer.ExtentWidth, viewer.ExtentHeight));
            Assert.Equal(contentSize, ((FrameworkElement)viewer.Content).RenderSize);
            Assert.Equal(presenterBounds, Bounds(presenter, viewer));
        }
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void HorizontalAndVerticalFadesCombineWithoutDimmingTheCenter() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 600, 700, horizontal: true);
        var presenter = Presenter(viewer);
        viewer.ScrollToHorizontalOffset(130);
        viewer.ScrollToVerticalOffset(160);
        Layout(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        var frame = Render(presenter);
        var centerX = frame.PixelWidth / 2;
        var centerY = frame.PixelHeight / 2;
        Assert.InRange(Alpha(frame, centerX, centerY), 250, 255);
        Assert.InRange(Alpha(frame, 1, centerY), 0, 60);
        Assert.InRange(Alpha(frame, frame.PixelWidth - 2, centerY), 0, 60);
        Assert.InRange(Alpha(frame, centerX, 1), 0, 60);
        Assert.InRange(Alpha(frame, centerX, frame.PixelHeight - 2), 0, 60);
        Assert.True(Alpha(frame, 1, 1) <= Alpha(frame, 1, centerY));

        viewer.ScrollToLeftEnd();
        viewer.ScrollToBottom();
        Layout(viewer);
        ScrollEdgeFade.Refresh(presenter);
        frame = Render(presenter);
        Assert.InRange(Alpha(frame, 1, centerY), 250, 255);
        Assert.InRange(Alpha(frame, centerX, frame.PixelHeight - 2), 250, 255);
        Assert.InRange(Alpha(frame, frame.PixelWidth - 2, centerY), 0, 60);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void ScrollbarsStayPixelIdenticalAndReceiveNoOpacityMask() => OnSta(() =>
    {
        var viewer = Viewer(220, 180, 600, 700, horizontal: true, barsVisible: true);
        var presenter = Presenter(viewer);
        viewer.ScrollToHorizontalOffset(70);
        viewer.ScrollToVerticalOffset(120);
        Layout(viewer);
        var bars = Descendants(viewer).OfType<ScrollBar>().Where(bar => bar.Visibility == Visibility.Visible &&
            bar.ActualWidth > 0 && bar.ActualHeight > 0).ToArray();
        Assert.Equal(2, bars.Length);
        var original = bars.Select(Render).Select(Pixels).ToArray();
        var slots = bars.Select(bar => Bounds(bar, viewer)).ToArray();

        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        Assert.NotNull(presenter.OpacityMask);
        Assert.Null(viewer.OpacityMask);
        for (var index = 0; index < bars.Length; index++)
        {
            Assert.Null(bars[index].OpacityMask);
            Assert.Equal(original[index], Pixels(Render(bars[index])));
            Assert.Equal(slots[index], Bounds(bars[index], viewer));
        }
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Theory]
    [InlineData(8, 10)]
    [InlineData(32, 12)]
    [InlineData(80, 16)]
    public void SmallViewportsKeepAnOpaqueMiddleWithTheFadeDepthCapped(double width, double height) => OnSta(() =>
    {
        var viewer = Viewer(width, height, width, 200);
        var presenter = Presenter(viewer);
        viewer.ScrollToVerticalOffset(70);
        Layout(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        var frame = Render(presenter);
        Assert.InRange(Alpha(frame, frame.PixelWidth / 2, (int)(frame.PixelHeight * 0.3)), 250, 255);
        Assert.InRange(Alpha(frame, frame.PixelWidth / 2, (int)(frame.PixelHeight * 0.6)), 250, 255);
        Assert.True(double.IsFinite(viewer.VerticalOffset));
        Assert.Equal(width, viewer.ActualWidth);
        Assert.Equal(height, viewer.ActualHeight);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void FittingAndZeroSizePresentersDoNotAcquireAMask() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 50, 40);
        var presenter = Presenter(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        Assert.Equal(0, viewer.ScrollableWidth);
        Assert.Equal(0, viewer.ScrollableHeight);
        Assert.Null(presenter.OpacityMask);
        ScrollEdgeFade.SetIsEnabled(presenter, false);

        var empty = new ScrollContentPresenter();
        empty.Measure(new Size(0, 0));
        empty.Arrange(new Rect(0, 0, 0, 0));
        ScrollEdgeFade.SetIsEnabled(empty, true);
        ScrollEdgeFade.Refresh(empty);
        Assert.Null(empty.OpacityMask);
        ScrollEdgeFade.SetIsEnabled(empty, false);
    });

    [Fact]
    public void CustomMasksArePreservedBeforeAndAfterEnablingTheBehavior() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 180, 700);
        var presenter = Presenter(viewer);
        var original = new SolidColorBrush(Colors.White) { Opacity = 0.4 };
        presenter.OpacityMask = original;
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        Assert.Same(original, presenter.OpacityMask);
        Assert.Equal(0.4, original.Opacity);
        Assert.False(original.IsFrozen);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
        Assert.Same(original, presenter.OpacityMask);

        presenter.ClearValue(UIElement.OpacityMaskProperty);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        Assert.NotNull(presenter.OpacityMask);
        var replacement = new SolidColorBrush(Colors.White) { Opacity = 0.7 };
        presenter.OpacityMask = replacement;
        ScrollEdgeFade.Refresh(presenter);
        Assert.Same(replacement, presenter.OpacityMask);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
        Assert.Same(replacement, presenter.OpacityMask);
    });

    [Fact]
    public void TextEditorsRetainTheirNativeContentAndSelectionWithoutAFadeMask() => OnSta(() =>
    {
        var text = string.Join(Environment.NewLine, Enumerable.Range(0, 50).Select(index => $"Line {index}"));
        var editor = new TextBox
        {
            Width = 180,
            Height = 100,
            Text = text,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var host = new Border { Width = 180, Height = 100, Child = editor };
        Layout(host);
        editor.Select(4, 7);
        var scroller = Assert.Single(Descendants(editor).OfType<ScrollViewer>());
        Assert.Same(editor, scroller.TemplatedParent);
        Assert.True(scroller.ScrollableHeight > 0);
        var presenter = Assert.Single(Descendants(scroller).OfType<ScrollContentPresenter>());
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        ScrollEdgeFade.Refresh(presenter);
        Assert.Null(presenter.OpacityMask);
        Assert.Equal(text, editor.Text);
        Assert.Equal(4, editor.SelectionStart);
        Assert.Equal(7, editor.SelectionLength);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void ApplicationPresenterStyleStillAppliesInsideAnExplicitlyStyledViewer() => OnSta(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "src", "Wisp.App", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = document.Descendants(presentation + "Style").Single(element =>
            element.Attribute("TargetType")?.Value.Contains("ScrollContentPresenter", StringComparison.Ordinal) == true &&
            element.Attribute(xaml + "Key") is null);
        style = new XElement(style);
        style.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:Wisp.App;assembly=Wisp");
        var markup = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:Wisp.App;assembly=Wisp\">" +
            style + "</ResourceDictionary>";
        var viewer = Viewer(180, 140, 180, 700, arrange: false);
        viewer.Resources = Assert.IsType<ResourceDictionary>(XamlReader.Parse(markup));
        var explicitStyle = new Style(typeof(ScrollViewer));
        viewer.Style = explicitStyle;
        Layout(viewer);
        var presenter = Presenter(viewer);
        Assert.True(ScrollEdgeFade.GetIsEnabled(presenter));
        ScrollEdgeFade.Refresh(presenter);
        Assert.NotNull(presenter.OpacityMask);
        Assert.Same(explicitStyle, viewer.Style);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void ScrollEventsUpdateTheEdgesAndReuseTheMaskWhileTheEdgesStayTheSame() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 180, 700);
        var presenter = Presenter(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        presenter.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var atTop = Assert.IsAssignableFrom<Brush>(presenter.OpacityMask);
        Assert.True(atTop.IsFrozen);

        viewer.ScrollToVerticalOffset(120);
        Layout(viewer);
        var middle = Assert.IsAssignableFrom<Brush>(presenter.OpacityMask);
        Assert.NotSame(atTop, middle);
        Assert.True(middle.IsFrozen);
        var frame = Render(presenter);
        Assert.InRange(Alpha(frame, frame.PixelWidth / 2, 1), 0, 60);
        viewer.ScrollToVerticalOffset(180);
        Layout(viewer);
        Assert.Same(middle, presenter.OpacityMask);

        viewer.ScrollToBottom();
        Layout(viewer);
        Assert.NotSame(middle, presenter.OpacityMask);
        frame = Render(presenter);
        Assert.InRange(Alpha(frame, frame.PixelWidth / 2, frame.PixelHeight - 2), 250, 255);
        presenter.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    [Fact]
    public void DisablingAndUnloadingRemoveOnlyTheOwnedMask() => OnSta(() =>
    {
        var viewer = Viewer(180, 140, 180, 700);
        var presenter = Presenter(viewer);
        ScrollEdgeFade.SetIsEnabled(presenter, true);
        presenter.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        ScrollEdgeFade.Refresh(presenter);
        Assert.NotNull(presenter.OpacityMask);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
        Assert.Null(presenter.OpacityMask);
        viewer.ScrollToVerticalOffset(120);
        Layout(viewer);
        Assert.Null(presenter.OpacityMask);

        ScrollEdgeFade.SetIsEnabled(presenter, true);
        presenter.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        ScrollEdgeFade.Refresh(presenter);
        Assert.NotNull(presenter.OpacityMask);
        presenter.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Null(presenter.OpacityMask);
        viewer.ScrollToVerticalOffset(220);
        Layout(viewer);
        Assert.Null(presenter.OpacityMask);
        ScrollEdgeFade.SetIsEnabled(presenter, false);
    });

    private static ScrollViewer Viewer(double width, double height, double contentWidth,
        double contentHeight, bool horizontal = false, bool barsVisible = false, bool arrange = true)
    {
        var viewer = new ScrollViewer
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = barsVisible ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = horizontal
                ? barsVisible ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            Content = new Border { Width = contentWidth, Height = contentHeight, Background = Brushes.White }
        };
        if (arrange) Layout(viewer);
        return viewer;
    }

    private static ScrollContentPresenter Presenter(DependencyObject root) =>
        Assert.Single(Descendants(root).OfType<ScrollContentPresenter>());

    private static void Layout(FrameworkElement element)
    {
        var size = new Size(element.Width, element.Height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        element.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        element.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual relativeTo) =>
        element.TransformToAncestor(relativeTo).TransformBounds(new Rect(element.RenderSize));

    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte Alpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
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
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Scroll edge fade fixture timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
