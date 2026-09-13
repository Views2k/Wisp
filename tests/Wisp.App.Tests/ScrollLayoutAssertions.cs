using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

internal static class ScrollLayoutAssertions
{
    internal static void Verify(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        VerifyDashboard(window, surface, tabs);
        (string TabHeader, string ViewName, double DesignWidth, string? Category)[] pages =
        [
            ("Appearance", "AppearanceScaleView", 480, "Layout"),
            ("Appearance", "AppearanceGaugesScaleView", 480, "Gauges"),
            ("Appearance", "AppearanceColorsScaleView", 480, "Colors"),
            ("Appearance", "AppearanceBehaviourScaleView", 480, "Behaviour"),
            ("Diagnostics", "DiagnosticsScaleView", 900, null),
            ("Profiles", "ProfilesScaleView", 900, null),
            ("Release Notes", "ReleaseNotesScaleView", 900, null)
        ];
        var scrollingCases = 0;
        var compactCases = 0;
        var fourKCases = 0;
        foreach (var page in pages)
        {
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, page.TabHeader));
            if (page.Category is { } category)
            {
                Assert.IsType<RadioButton>(window.FindName($"Appearance{category}Category")).IsChecked = true;
            }
            var preview = page.Category is null ? null :
                Assert.IsAssignableFrom<FrameworkElement>(window.FindName("HudPreviewSurface"));
            var previewPane = page.Category is null ? null :
                Assert.IsType<Grid>(window.FindName("AppearancePreviewPane"));
            var viewbox = Assert.IsType<Viewbox>(window.FindName(page.ViewName));
            var wrapper = Assert.IsType<Decorator>(viewbox.Parent);
            var scroll = Assert.IsType<ScrollViewer>(wrapper.Parent);

            foreach (var size in new[]
                     {
                         new Size(720, 440), new Size(980, 750), new Size(1280, 900),
                         new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160)
                     })
            {
                surface.Measure(size);
                surface.Arrange(new Rect(size));
                Settle(surface);
                var container = Assert.IsType<ContainerVisual>(VisualTreeHelper.GetChild(viewbox, 0));
                var scale = Assert.IsType<ScaleTransform>(container.Transform);
                var extent = scroll.ExtentHeight;
                var contentSize = viewbox.RenderSize;
                var designSurface = Assert.IsAssignableFrom<FrameworkElement>(viewbox.Child);
                var expectedScale = Math.Min(1, scroll.ViewportWidth / page.DesignWidth);
                var expectedContentWidth = Math.Max(scroll.ViewportWidth, page.DesignWidth);
                Assert.Equal(expectedScale, scale.ScaleX, 5);
                Assert.Equal(expectedScale, scale.ScaleY, 5);
                Assert.Equal(page.Category is null ? expectedContentWidth : scroll.ViewportWidth, designSurface.Width, 3);
                Assert.Equal(expectedContentWidth, designSurface.ActualWidth, 3);
                Assert.Equal(scroll.ViewportWidth, viewbox.ActualWidth, 3);
                Assert.Equal(Visibility.Visible, scroll.Visibility);
                var previewDisplayed = previewPane?.Visibility == Visibility.Visible;
                var previewBounds = !previewDisplayed ? Rect.Empty :
                    preview!.TransformToAncestor(surface).TransformBounds(new Rect(preview.RenderSize));
                if (preview is not null)
                {
                    Assert.Equal(page.DesignWidth, designSurface.MinWidth);
                    var toggle = Assert.IsType<Button>(window.FindName("AppearancePreviewToggle"));
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Grid>(window.FindName("AppearanceEditorPane")).Visibility);
                    Assert.Equal(size.Width <= 980 ? Visibility.Collapsed : Visibility.Visible, previewPane!.Visibility);
                    Assert.Equal(previewDisplayed ? Visibility.Collapsed : Visibility.Visible, toggle.Visibility);
                }
                if (previewDisplayed)
                {
                    Assert.True(previewBounds.Width > 0 && previewBounds.Height > 0,
                        PreviewLayoutDetails(page.ViewName, size, surface, preview!, previewBounds, scroll));
                    Assert.True(previewBounds.Top >= 0 && previewBounds.Top <= surface.ActualHeight &&
                                previewBounds.Bottom >= 0 && previewBounds.Bottom <= surface.ActualHeight + 0.01,
                        PreviewLayoutDetails(page.ViewName, size, surface, preview!, previewBounds, scroll));
                }
                Assert.InRange(scroll.ScrollableWidth, 0, 0.01);
                Assert.True(wrapper.IsHitTestVisible);
                if (scroll.ViewportWidth < page.DesignWidth)
                {
                    compactCases++;
                    Assert.True(scale.ScaleX < 1);
                }
                if (size.Width == 3840)
                {
                    fourKCases++;
                    if (page.Category is null)
                    {
                        Assert.True(scroll.ViewportWidth > 3000);
                    }
                    else
                    {
                        Assert.True(scroll.ViewportWidth > 1400);
                        Assert.True(scroll.ViewportWidth < surface.ActualWidth * 0.70);
                    }
                    Assert.Equal(1, scale.ScaleX, 5);
                }

                if (scroll.ScrollableHeight < 1)
                    continue;

                scrollingCases++;
                var presenter = Assert.IsAssignableFrom<IScrollInfo>(
                    scroll.Template.FindName("PART_ScrollContentPresenter", scroll));
                foreach (var fraction in new[] { 0.2, 0.7, 1, 0.4, 0 })
                {
                    var offset = scroll.ScrollableHeight * fraction;
                    presenter.SetVerticalOffset(offset);
                    Settle(surface);
                    Assert.Equal(offset, scroll.VerticalOffset, 4);
                    Assert.Same(scale, container.Transform);
                    Assert.Equal(contentSize, viewbox.RenderSize);
                    Assert.Equal(extent, scroll.ExtentHeight, 4);
                    Assert.Equal(0, viewbox.TranslatePoint(new Point(), wrapper).Y, 5);
                    if (previewDisplayed)
                    {
                        Assert.Equal(previewBounds,
                            preview!.TransformToAncestor(surface).TransformBounds(new Rect(preview.RenderSize)));
                    }
                }
            }
        }

        Assert.IsType<RadioButton>(window.FindName("AppearanceLayoutCategory")).IsChecked = true;
        Assert.True(scrollingCases > 0, "The scroll regression never moved content.");
        Assert.True(compactCases >= pages.Count(page => page.Category is null),
            "The responsive regression never downscaled the remaining fixed-minimum pages.");
        Assert.Equal(pages.Length, fourKCases);
        VerifyCompactAppearancePreview(window, surface, tabs);
    }

    private static void VerifyDashboard(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        tabs.SelectedIndex = 0;
        var scroll = Assert.IsType<ScrollViewer>(window.FindName("DashboardViewport"));
        var content = Assert.IsType<StackPanel>(window.FindName("DashboardContent"));
        var scale = Assert.IsType<ScaleTransform>(content.LayoutTransform);
        var instruments = Assert.IsType<DashboardColumns>(window.FindName("DashboardInstruments"));
        Assert.Same(content, scroll.Content);
        Assert.False(window.IsDashboardDisplayMode);
        Assert.True(double.IsNaN(content.Width));
        Size? initialContentSize = null;
        Rect[]? initialInstrumentSlots = null;
        var scrollingCases = 0;
        foreach (var size in new[]
                 {
                     new Size(720, 440), new Size(980, 750), new Size(1280, 900),
                     new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160), new Size(720, 440)
                 })
        {
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            Settle(surface);
            scroll.ScrollToTop();
            Settle(surface);
            Assert.Same(scale, content.LayoutTransform);
            Assert.Equal(1, scale.ScaleX);
            Assert.Equal(1, scale.ScaleY);
            Assert.True(content.ActualHeight > 0 && instruments.ActualHeight > 0);
            Assert.Equal(scroll.ViewportWidth - content.Margin.Left - content.Margin.Right, content.ActualWidth, 3);
            Assert.InRange(scroll.ScrollableWidth, 0, 0.01);
            var slots = instruments.Children.Cast<FrameworkElement>().Select(LayoutInformation.GetLayoutSlot).ToArray();
            Assert.Equal(5, slots.Length);
            Assert.All(slots, slot => Assert.True(slot.Width > 0 && slot.Height > 0 &&
                slot.Left >= 0 && slot.Right <= instruments.ActualWidth + 0.01 &&
                slot.Top >= 0 && slot.Bottom <= instruments.ActualHeight + 0.01));
            if (size.Width == 720)
            {
                Assert.True(slots.Select(slot => slot.Top).Distinct().Count() > 1,
                    "The compact dashboard did not reflow its metric groups.");
                if (initialContentSize is null)
                {
                    initialContentSize = content.RenderSize;
                    initialInstrumentSlots = slots;
                }
                else
                {
                    Assert.Equal(initialContentSize.Value.Width, content.ActualWidth, 3);
                    Assert.Equal(initialContentSize.Value.Height, content.ActualHeight, 3);
                    Assert.Equal(initialInstrumentSlots, slots);
                }
            }
            if (size.Width == 3840)
                Assert.Single(slots.Select(slot => slot.Top).Distinct());
            if (scroll.ScrollableHeight < 1) continue;
            scrollingCases++;
            var presenter = Assert.IsAssignableFrom<IScrollInfo>(
                scroll.Template.FindName("PART_ScrollContentPresenter", scroll));
            var extent = scroll.ExtentHeight;
            var contentSize = content.RenderSize;
            var originY = content.TranslatePoint(new Point(), scroll).Y;
            foreach (var fraction in new[] { 0.2, 0.7, 1, 0.4, 0 })
            {
                var offset = scroll.ScrollableHeight * fraction;
                presenter.SetVerticalOffset(offset);
                Settle(surface);
                Assert.Equal(offset, scroll.VerticalOffset, 4);
                Assert.Equal(originY - offset, content.TranslatePoint(new Point(), scroll).Y, 4);
                Assert.Same(scale, content.LayoutTransform);
                Assert.Equal(1, scale.ScaleX);
                Assert.Equal(1, scale.ScaleY);
                Assert.Equal(contentSize, content.RenderSize);
                Assert.Equal(extent, scroll.ExtentHeight, 4);
            }
        }
        Assert.True(scrollingCases > 0, "The dashboard scroll regression never moved content.");
    }

    private static void VerifyCompactAppearancePreview(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        tabs.SelectedIndex = 2;
        var toggle = Assert.IsType<Button>(window.FindName("AppearancePreviewToggle"));
        var editor = Assert.IsType<Grid>(window.FindName("AppearanceEditorPane"));
        var preview = Assert.IsType<Grid>(window.FindName("AppearancePreviewPane"));
        var previewScroll = Assert.IsType<ScrollViewer>(window.FindName("AppearancePreviewScroll"));
        surface.Measure(new Size(720, 440));
        surface.Arrange(new Rect(0, 0, 720, 440));
        Settle(surface);
        foreach (var category in new[] { "Layout", "Gauges", "Colors", "Behaviour" })
        {
            var selection = Assert.IsType<RadioButton>(window.FindName($"Appearance{category}Category"));
            selection.IsChecked = true;
            Settle(surface);
            Assert.Equal(Visibility.Visible, toggle.Visibility);
            Assert.True(toggle.IsEnabled && toggle.IsTabStop && toggle.Focusable);
            Assert.Equal("Show HUD preview", toggle.Content);
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.Equal(Visibility.Collapsed, preview.Visibility);
            var toggleBounds = toggle.TransformToAncestor(surface).TransformBounds(new Rect(toggle.RenderSize));
            Assert.True(toggleBounds.Width > 0 && toggleBounds.Height > 0 &&
                toggleBounds.Left >= 0 && toggleBounds.Top >= 0 &&
                toggleBounds.Right <= surface.ActualWidth && toggleBounds.Bottom <= surface.ActualHeight);

            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(surface);
            Assert.Equal("Back to appearance controls", toggle.Content);
            Assert.Equal(Visibility.Collapsed, editor.Visibility);
            Assert.Equal(Visibility.Visible, preview.Visibility);
            Assert.True(preview.ActualWidth > 0 && preview.ActualHeight > 0);
            Assert.InRange(previewScroll.ScrollableWidth, 0, 0.01);
            Assert.True(previewScroll.ScrollableHeight > 0, "Compact preview content needs a reachable vertical scrollbar.");
            var previewContent = Assert.IsAssignableFrom<FrameworkElement>(previewScroll.Content);
            var previewContentSize = previewContent.RenderSize;
            var previewExtent = previewScroll.ExtentHeight;
            previewScroll.ScrollToEnd();
            Settle(surface);
            Assert.Equal(previewScroll.ScrollableHeight, previewScroll.VerticalOffset, 4);
            Assert.Equal(previewContentSize, previewContent.RenderSize);
            Assert.Equal(previewExtent, previewScroll.ExtentHeight, 4);

            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Settle(surface);
            Assert.Equal("Show HUD preview", toggle.Content);
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.Equal(Visibility.Collapsed, preview.Visibility);
            Assert.True(selection.IsChecked);
        }
        Assert.IsType<RadioButton>(window.FindName("AppearanceLayoutCategory")).IsChecked = true;
        Settle(surface);
    }

    private static string PreviewLayoutDetails(string page, Size requestedSize, FrameworkElement surface,
        FrameworkElement preview, Rect bounds, ScrollViewer scroll)
    {
        var details = new List<string>
        {
            $"{page} at {requestedSize}: preview bounds={bounds}, surface={surface.RenderSize}, " +
            $"settings viewport={scroll.ViewportWidth:0.###}x{scroll.ViewportHeight:0.###}, " +
            $"settings extent={scroll.ExtentWidth:0.###}x{scroll.ExtentHeight:0.###}."
        };
        DependencyObject? ancestor = preview;
        for (var depth = 0; ancestor is not null && depth < 16; depth++)
        {
            if (ancestor is not FrameworkElement element)
            {
                ancestor = VisualTreeHelper.GetParent(ancestor);
                continue;
            }
            var rows = element is Grid grid && grid.RowDefinitions.Count > 0
                ? "; rows=" + string.Join(", ", grid.RowDefinitions.Select(row =>
                    $"{row.Height}:{row.ActualHeight:0.###}"))
                : string.Empty;
            details.Add($"{element.GetType().Name}#{element.Name}: visibility={element.Visibility}, " +
                $"desired={element.DesiredSize}, rendered={element.RenderSize}, " +
                $"slot={LayoutInformation.GetLayoutSlot(element)}{rows}");
            if (ReferenceEquals(element, surface))
                break;
            ancestor = VisualTreeHelper.GetParent(element);
        }
        return string.Join(Environment.NewLine, details);
    }

    private static void Settle(FrameworkElement surface)
    {
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }
}
