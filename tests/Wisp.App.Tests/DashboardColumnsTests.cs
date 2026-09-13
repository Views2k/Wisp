using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardColumnsTests
{
    [Fact]
    public void NarrowWindowStacksFullSizeGroupsAndWideWindowRestoresTheOriginalRows() => RunSta(() =>
    {
        var panel = CreatePanel(20, 40, 60, 80, 100);
        Layout(panel, 558);
        var original = Bounds(panel);
        Assert.Equal(256, panel.DesiredSize.Height);
        Assert.Equal(new Rect(0, 0, 270, 40), original[0]);
        Assert.Equal(new Rect(288, 0, 270, 40), original[1]);
        Assert.Equal(new Rect(0, 58, 270, 80), original[2]);
        Assert.Equal(new Rect(0, 156, 270, 100), original[4]);

        Layout(panel, 320);
        var narrow = Bounds(panel);
        Assert.Equal(372, panel.DesiredSize.Height);
        Assert.All(narrow, rect => { Assert.Equal(0, rect.X); Assert.Equal(320, rect.Width); });
        Assert.True(narrow.Zip(narrow.Skip(1)).All(pair => pair.First.Bottom + 18 == pair.Second.Top));

        Layout(panel, 558);
        Assert.Equal(original, Bounds(panel));
    });

    [Fact]
    public void HiddenOptionalGroupReflowsWithoutLeavingAGapAndRestoresWhenShown() => RunSta(() =>
    {
        var panel = CreatePanel(20, 40, 60);
        Layout(panel, 558);
        var original = Bounds(panel);
        panel.Children[1].Visibility = Visibility.Collapsed;
        Layout(panel, 558);
        Assert.Equal(60, panel.DesiredSize.Height);
        Assert.Equal(new Rect(0, 0, 270, 60), Bounds(panel)[0]);
        Assert.Equal(new Rect(288, 0, 270, 60), Bounds(panel)[2]);

        panel.Children[1].Visibility = Visibility.Visible;
        Layout(panel, 558);
        Assert.Equal(original, Bounds(panel));
    });

    [Fact]
    public void TextWrapsAtItsOriginalFontSizeInsteadOfBeingShrunk() => RunSta(() =>
    {
        var panel = new DashboardColumns { MaximumColumns = 1, MinimumColumnWidth = 200 };
        var label = new TextBlock
        {
            Text = "Telemetry is unavailable. Return to free roam to receive current readings.",
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(label);
        Layout(panel, 700);
        var wideHeight = label.ActualHeight;
        Layout(panel, 220);
        Assert.Equal(20, label.FontSize);
        Assert.True(label.ActualHeight > wideHeight);
        Assert.True(label.ActualWidth <= 220);
        Assert.True(label.RenderTransform.Value.IsIdentity);
        Assert.True(label.LayoutTransform.Value.IsIdentity);
        Assert.Equal(label.ActualHeight, panel.ActualHeight);
    });

    [Fact]
    public void EmptyAndFullyCollapsedPanelsDoNotReserveTelemetryRows() => RunSta(() =>
    {
        var panel = CreatePanel(20, 40);
        foreach (UIElement child in panel.Children) child.Visibility = Visibility.Collapsed;
        Layout(panel, 558);
        Assert.Equal(0, panel.DesiredSize.Height);
        panel.Children.Clear();
        Layout(panel, 558);
        Assert.Equal(0, panel.DesiredSize.Height);
    });

    private static DashboardColumns CreatePanel(params double[] heights)
    {
        var panel = new DashboardColumns { MinimumColumnWidth = 260, MaximumColumns = 2, Gap = 18 };
        foreach (var height in heights) panel.Children.Add(new DesiredHeightElement(height));
        return panel;
    }

    private static void Layout(DashboardColumns panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
    }

    private static Rect[] Bounds(DashboardColumns panel) => panel.Children.Cast<FrameworkElement>()
        .Select(child => new Rect(VisualTreeHelper.GetOffset(child).X, VisualTreeHelper.GetOffset(child).Y,
            child.ActualWidth, child.ActualHeight)).ToArray();

    private sealed class DesiredHeightElement(double height) : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, height);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Dashboard layout test exceeded its bounded STA lifetime.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
