using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunWorkspacePanelLayoutTests
{
    [Fact]
    public void MixedWidthsShareRowsWithoutOverlappingAndKeepTheirOrder() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { MinimumColumnWidth = 300, Gap = 12 };
        var compact = Add(panel, RunWorkspaceWidth.Compact, 60);
        var wide = Add(panel, RunWorkspaceWidth.Wide, 80);
        var full = Add(panel, RunWorkspaceWidth.Full, 45);
        var last = Add(panel, RunWorkspaceWidth.Compact, 30);
        Arrange(panel, 960);

        Assert.Equal(new Rect(0, 0, 312, 60), Bounds(compact, panel));
        Assert.Equal(new Rect(324, 0, 636, 80), Bounds(wide, panel));
        Assert.Equal(new Rect(0, 92, 960, 45), Bounds(full, panel));
        Assert.Equal(new Rect(0, 149, 312, 30), Bounds(last, panel));
        Assert.Equal(179, panel.DesiredSize.Height);
    });

    [Fact]
    public void NarrowWindowsStackModulesAndRestoreTheWideLayoutOnReturn() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { MinimumColumnWidth = 300, Gap = 12 };
        var children = new[]
        {
            Add(panel, RunWorkspaceWidth.Compact, 60),
            Add(panel, RunWorkspaceWidth.Wide, 80),
            Add(panel, RunWorkspaceWidth.Full, 45)
        };
        Arrange(panel, 960);
        var wideBounds = children.Select(child => Bounds(child, panel)).ToArray();
        Arrange(panel, 480);

        Assert.Equal(new Rect(0, 0, 480, 60), Bounds(children[0], panel));
        Assert.Equal(new Rect(0, 72, 480, 80), Bounds(children[1], panel));
        Assert.Equal(new Rect(0, 164, 480, 45), Bounds(children[2], panel));
        Arrange(panel, 960);
        Assert.Equal(wideBounds, children.Select(child => Bounds(child, panel)).ToArray());
    });

    [Fact]
    public void HidingAndReorderingModulesDoesNotLeaveEmptySlots() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { MinimumColumnWidth = 300, Gap = 12 };
        var first = Add(panel, RunWorkspaceWidth.Compact, 60);
        var hidden = Add(panel, RunWorkspaceWidth.Full, 200);
        var last = Add(panel, RunWorkspaceWidth.Compact, 70);
        Arrange(panel, 960);
        hidden.Visibility = Visibility.Collapsed;
        Arrange(panel, 960);
        Assert.Equal(new Rect(0, 0, 312, 60), Bounds(first, panel));
        Assert.Equal(new Rect(324, 0, 312, 70), Bounds(last, panel));
        Assert.Equal(70, panel.DesiredSize.Height);

        panel.Children.Remove(last);
        panel.Children.Insert(0, last);
        Arrange(panel, 960);
        Assert.Equal(new Rect(0, 0, 312, 70), Bounds(last, panel));
        Assert.Equal(new Rect(324, 0, 312, 60), Bounds(first, panel));
        first.Visibility = last.Visibility = Visibility.Collapsed;
        Arrange(panel, 960);
        Assert.Equal(0, panel.DesiredSize.Height);
    });

    [Fact]
    public void ResizingRemeasuresWrappedContentBeforePlacingTheFollowingModule() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { MinimumColumnWidth = 300, Gap = 12 };
        var text = new TextBlock
        {
            Text = string.Join(" ", Enumerable.Repeat("A longer explanation for this recorded measurement.", 12)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        };
        RunWorkspacePanelLayout.SetSpan(text, RunWorkspaceWidth.Full);
        panel.Children.Add(text);
        var following = Add(panel, RunWorkspaceWidth.Full, 40);
        Arrange(panel, 960);
        var wideHeight = text.ActualHeight;
        Arrange(panel, 360);

        Assert.True(text.ActualHeight > wideHeight);
        Assert.Equal(text.ActualHeight + panel.Gap, Bounds(following, panel).Top, 5);
        Assert.Equal(360, text.ActualWidth);
        Assert.Equal(Bounds(following, panel).Bottom, panel.DesiredSize.Height, 5);
        Arrange(panel, 960);
        Assert.Equal(wideHeight, text.ActualHeight, 5);
        Assert.Equal(wideHeight + panel.Gap, Bounds(following, panel).Top, 5);
    });

    [Fact]
    public void FilledRowsKeepGreedyOrderAndUseTheEntireRowWithEqualCardHeights() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { FillRows = true };
        var first = AddContentSized(panel, RunWorkspaceWidth.Wide, 60);
        var second = AddContentSized(panel, RunWorkspaceWidth.Wide, 80);
        var compact = AddContentSized(panel, RunWorkspaceWidth.Compact, 100);
        var full = AddContentSized(panel, RunWorkspaceWidth.Full, 40);
        var last = AddContentSized(panel, RunWorkspaceWidth.Compact, 30);
        Arrange(panel, 1074);

        Assert.Equal(new Rect(0, 0, 1074, 60), Bounds(first, panel));
        Assert.Equal(new Rect(0, 72, 712, 100), Bounds(second, panel));
        Assert.Equal(new Rect(724, 72, 350, 100), Bounds(compact, panel));
        Assert.Equal(new Rect(0, 184, 1074, 40), Bounds(full, panel));
        Assert.Equal(new Rect(0, 236, 1074, 30), Bounds(last, panel));
        Assert.Equal(266, panel.DesiredSize.Height);

        second.Visibility = Visibility.Collapsed;
        Arrange(panel, 1074);
        Assert.Equal(new Rect(0, 0, 712, 100), Bounds(first, panel));
        Assert.Equal(new Rect(724, 0, 350, 100), Bounds(compact, panel));
        Assert.Equal(112, Bounds(full, panel).Top);
        Assert.Equal(194, panel.DesiredSize.Height);
        foreach (UIElement child in panel.Children) child.Visibility = Visibility.Collapsed;
        Arrange(panel, 1074);
        Assert.Equal(0, panel.DesiredSize.Height);
        panel.Children.Clear();
        Arrange(panel, 1074);
        Assert.Equal(0, panel.DesiredSize.Height);
    });

    [Theory]
    [InlineData(711)]
    [InlineData(712)]
    [InlineData(1073)]
    [InlineData(1074)]
    [InlineData(1664)]
    public void FilledRowsRemainGapFreeAndOrderedAcrossColumnBreakpoints(double width) => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { FillRows = true };
        var children = new[]
        {
            AddContentSized(panel, RunWorkspaceWidth.Wide, 50),
            AddContentSized(panel, RunWorkspaceWidth.Wide, 80),
            AddContentSized(panel, RunWorkspaceWidth.Compact, 65),
            AddContentSized(panel, RunWorkspaceWidth.Full, 90),
            AddContentSized(panel, RunWorkspaceWidth.Compact, 40)
        };
        Arrange(panel, width);
        var original = children.Select(child => Bounds(child, panel)).ToArray();
        foreach (var row in original.GroupBy(bounds => bounds.Top))
        {
            var cards = row.ToArray();
            Assert.Equal(0, cards[0].Left, 5);
            Assert.Equal(width, cards[^1].Right, 5);
            Assert.All(cards, card => Assert.Equal(cards[0].Height, card.Height, 5));
            for (var index = 1; index < cards.Length; index++)
                Assert.Equal(cards[index - 1].Right + panel.Gap, cards[index].Left, 5);
        }
        for (var index = 1; index < original.Length; index++)
        {
            var previous = original[index - 1];
            var current = original[index];
            Assert.True(current.Top == previous.Top ? current.Left > previous.Left : current.Top >= previous.Bottom + panel.Gap);
        }
        Assert.Equal(original.Max(bounds => bounds.Bottom), panel.DesiredSize.Height, 5);

        Arrange(panel, 480);
        Assert.All(children, child => Assert.Equal(480, child.ActualWidth, 5));
        Arrange(panel, width);
        Assert.Equal(original, children.Select(child => Bounds(child, panel)).ToArray());
    });

    [Fact]
    public void FilledRowsMeasureWrappedTextAtItsExpandedWidth() => OnSta(() =>
    {
        var panel = new RunWorkspacePanelLayout { FillRows = true };
        var text = new TextBlock
        {
            Text = string.Join(" ", Enumerable.Repeat("Review the measured engine output.", 16)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16
        };
        RunWorkspacePanelLayout.SetSpan(text, RunWorkspaceWidth.Compact);
        panel.Children.Add(text);
        var following = AddContentSized(panel, RunWorkspaceWidth.Full, 40);
        var reference = new TextBlock { Text = text.Text, TextWrapping = TextWrapping.Wrap, FontSize = text.FontSize };
        var surface = new StackPanel();
        surface.Children.Add(panel);
        surface.Children.Add(reference);
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(1.5, 1.5));
        TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Display);
        Arrange(surface, 1074);

        Assert.Equal(1074, text.ActualWidth, 5);
        Assert.Equal(reference.DesiredSize.Height, text.DesiredSize.Height, 5);
        Assert.Equal(reference.ActualHeight, text.ActualHeight, 5);
        Assert.Equal(reference.DesiredSize.Height + panel.Gap, Bounds(following, panel).Top, 5);
        Assert.Equal(Bounds(following, panel).Bottom, panel.DesiredSize.Height, 5);
    });

    private static Border AddContentSized(Panel panel, RunWorkspaceWidth width, double height)
    {
        var child = new Border { Child = new Border { Height = height } };
        RunWorkspacePanelLayout.SetSpan(child, width);
        panel.Children.Add(child);
        return child;
    }

    private static Border Add(Panel panel, RunWorkspaceWidth width, double height)
    {
        var child = new Border { Height = height };
        RunWorkspacePanelLayout.SetSpan(child, width);
        panel.Children.Add(child);
        return child;
    }

    private static void Arrange(Panel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement child, Visual parent) =>
        child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Workspace layout check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
