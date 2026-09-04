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
        (int TabIndex, string ViewName)[] pages =
        [
            (0, "DashboardScaleView"),
            (1, "AppearanceScaleView"),
            (2, "DiagnosticsScaleView"),
            (3, "ProfilesScaleView"),
            (4, "SetupScaleView"),
            (6, "ReleaseNotesScaleView")
        ];
        var scrollingCases = 0;
        var compactCases = 0;
        var fourKCases = 0;
        foreach (var page in pages)
        {
            tabs.SelectedIndex = page.TabIndex;
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
                var expectedScale = Math.Min(1, scroll.ViewportWidth / 900);
                var expectedContentWidth = Math.Max(scroll.ViewportWidth, 900);
                Assert.Equal(expectedScale, scale.ScaleX, 5);
                Assert.Equal(expectedScale, scale.ScaleY, 5);
                Assert.Equal(expectedContentWidth, designSurface.Width, 3);
                Assert.Equal(expectedContentWidth, designSurface.ActualWidth, 3);
                Assert.Equal(scroll.ViewportWidth, viewbox.ActualWidth, 3);
                Assert.InRange(scroll.ScrollableWidth, 0, 0.01);
                Assert.True(wrapper.IsHitTestVisible);
                if (scroll.ViewportWidth < ResponsivePageWidthConverter.MinimumWidth)
                {
                    compactCases++;
                    Assert.True(scale.ScaleX < 1);
                }
                if (size.Width == 3840)
                {
                    fourKCases++;
                    Assert.True(scroll.ViewportWidth > 3000);
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
                }
            }
        }

        Assert.True(scrollingCases > 0, "The scroll regression never moved content.");
        Assert.True(compactCases >= pages.Length, "The responsive regression never downscaled every page.");
        Assert.Equal(pages.Length, fourKCases);
    }

    private static void Settle(FrameworkElement surface)
    {
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }
}
