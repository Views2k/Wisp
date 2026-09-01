using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal sealed class ScrollCheckReport
{
    public string Fixture { get; init; } = string.Empty;
    public int? Dpi { get; init; }
    public int WarmupSteps => ScrollReview.WarmupSteps;
    public int MaximumMeasuredStepsPerVariant => ScrollReview.MeasuredSteps;
    public int CooperativeBudgetSeconds => 30;
    public bool OffscreenOnly => Presentation is null;
    public string TimingScope => Presentation is null
        ? "ScrollToVerticalOffset plus synchronous UpdateLayout, warmed; same-thread allocations exclude report/snapshot construction. No rasterization or compositor presentation is timed."
        : "Monotonic Stopwatch timestamps at unique CompositionTarget.Rendering callbacks in an independent visible host; per-step synchronous scroll/layout timing and allocations. These callbacks are not GPU present timestamps.";
    public string Limitations => Presentation is null
        ? "Synthetic fixed data and detached visuals isolate layout/transform churn. A non-scrolling tab is explicitly skipped. No claim of GPU frame-rate, live telemetry load, or user-visible lag severity; synchronous WPF calls cannot be forcibly interrupted."
        : "Synthetic fixed data; monitor-DPI presentation, no screen capture or OS input. Callback gaps measure application/compositor scheduling, not GPU completion. The 30-second watchdog terminates only this review process if WPF is unresponsive; a hard termination may leave no final report.";
    public List<ScrollComparison> Comparisons { get; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScrollPresentationReport? Presentation { get; set; }
    public bool HasFindings => Presentation is not null ? Presentation.HasFindings :
        Comparisons.Count != 8 || Comparisons.Any(comparison => !comparison.Equivalent ||
            comparison.Direct.HasFindings || comparison.Decorator.HasFindings);
}

internal sealed record ScrollComparison(string Tab, string Viewport, string ProductionContentType,
    bool ProductionHasDecorator, string FirstVariant, ScrollVariant Direct, ScrollVariant Decorator,
    bool Equivalent, double MaximumGeometryDifference);

internal sealed record ScrollVariant(string Variant, bool HasScrollableContent, int MeasuredSteps,
    int ActualOffsetChanges, int OffsetMismatches, int TransformReferenceReplacements,
    long AllocatedBytes, double TotalLayoutMilliseconds, double MedianLayoutMilliseconds,
    double P95LayoutMilliseconds, double MaximumLayoutMilliseconds, double MaximumStableGeometryDrift,
    ScrollGeometry[] Anchors)
{
    public bool HasFindings => HasScrollableContent && (MeasuredSteps != ScrollReview.MeasuredSteps ||
        ActualOffsetChanges != MeasuredSteps || OffsetMismatches != 0) || MaximumStableGeometryDrift > ScrollReview.GeometryTolerance;
}

internal sealed record ScrollGeometry(double VerticalOffset, double ExtentWidth, double ExtentHeight,
    double ViewportWidth, double ViewportHeight, double ScaleX, double ScaleY,
    Bounds ViewboxBounds, Bounds ContentBounds);

internal static class ScrollReview
{
    internal const int WarmupSteps = 16;
    internal const int MeasuredSteps = 120;
    internal const double GeometryTolerance = 0.001;
    private static readonly (string Name, int Width, int Height)[] Viewports =
        [("compact", 720, 440), ("baseline", 980, 750)];
    private static readonly string[] Tabs = ["dashboard", "appearance", "diagnostics", "setup"];

    public static void Run(MainWindow sourceWindow, FrameworkElement surface, TabControl tabs,
        BindingTrace bindings, CancellationToken cancellationToken, int dpi,
        Action<FrameworkElement, int> setOffscreenDpi, ScrollCheckReport report)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(report.CooperativeBudgetSeconds));
        var token = budget.Token;
        surface.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(surface, KeyboardNavigationMode.None);
        foreach (var viewport in Viewports)
            for (var tab = 0; tab < Tabs.Length; tab++)
            {
                token.ThrowIfCancellationRequested();
                AssertOffscreen(sourceWindow, surface);
                tabs.SelectedIndex = tab;
                if (tabs.Items[tab] is not TabItem { Content: ScrollViewer scroll })
                {
                    throw new InvalidOperationException("The main-tab scroll contract changed.");
                }

                var originalContent = scroll.Content;
                var originalDecorator = originalContent as Decorator;
                var viewbox = originalContent as Viewbox ?? originalDecorator?.Child as Viewbox
                    ?? throw new InvalidOperationException("The page no longer has one outer Viewbox.");
                var size = new Size(viewport.Width, viewport.Height);
                var temporaryDecorator = new Decorator();
                ScrollVariant? direct = null;
                ScrollVariant? decorated = null;
                var directFirst = (report.Comparisons.Count & 1) == 0;
                try
                {
                    scroll.Content = null;
                    if (originalDecorator is not null && !ReferenceEquals(originalDecorator, viewbox))
                    {
                        originalDecorator.Child = null;
                    }

                    // Alternate order by case to reduce consistent first-variant/JIT bias.
                    foreach (var useDecorator in directFirst ? new[] { false, true } : new[] { true, false })
                    {
                        token.ThrowIfCancellationRequested();
                        scroll.Content = null;
                        temporaryDecorator.Child = null;
                        if (useDecorator)
                        {
                            temporaryDecorator.Child = viewbox;
                            scroll.Content = temporaryDecorator;
                        }
                        else
                        {
                            scroll.Content = viewbox;
                        }

                        var variant = useDecorator ? "decorator-viewbox" : "direct-viewbox";
                        bindings.Phase = $"scroll-check/{Tabs[tab]}/{viewport.Name}/{variant}";
                        for (var pass = 0; pass < 2; pass++)
                        {
                            setOffscreenDpi(surface, dpi);
                            surface.Measure(size);
                            surface.Arrange(new Rect(size));
                            surface.UpdateLayout();
                            surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle,
                                token, TimeSpan.FromSeconds(2));
                        }

                        var result = MeasureVariant(sourceWindow, surface, scroll, viewbox, variant, token);
                        if (useDecorator)
                            decorated = result;
                        else
                            direct = result;
                    }
                }
                finally
                {
                    scroll.Content = null;
                    temporaryDecorator.Child = null;
                    if (originalDecorator is not null && !ReferenceEquals(originalDecorator, viewbox))
                        originalDecorator.Child = viewbox;
                    scroll.Content = originalContent;
                }

                if (direct is null || decorated is null)
                    throw new InvalidOperationException("The scroll comparison did not complete.");
                var difference = direct.Anchors.Zip(decorated.Anchors,
                    (left, right) => GeometryDifference(left, right, compensateOffset: false)).Max();
                report.Comparisons.Add(new ScrollComparison(Tabs[tab], viewport.Name,
                    originalContent.GetType().Name, originalDecorator is not null && !ReferenceEquals(originalDecorator, viewbox),
                    directFirst ? "direct-viewbox" : "decorator-viewbox", direct, decorated,
                    direct.HasScrollableContent == decorated.HasScrollableContent && difference <= GeometryTolerance, difference));
                Console.WriteLine($"Scroll {viewport.Name}/{Tabs[tab]}: direct {direct.TransformReferenceReplacements}/{direct.ActualOffsetChanges} transform/offset changes, " +
                    $"decorator {decorated.TransformReferenceReplacements}/{decorated.ActualOffsetChanges}; " +
                    $"layout {direct.TotalLayoutMilliseconds:F3}/{decorated.TotalLayoutMilliseconds:F3} ms; geometry delta {difference:F6} DIP.");
            }
        AssertOffscreen(sourceWindow, surface);
    }

    private static ScrollVariant MeasureVariant(MainWindow sourceWindow, FrameworkElement surface,
        ScrollViewer scroll, Viewbox viewbox, string variant, CancellationToken token)
    {
        if (VisualTreeHelper.GetChildrenCount(viewbox) != 1 ||
            VisualTreeHelper.GetChild(viewbox, 0) is not ContainerVisual container ||
            viewbox.Child is not FrameworkElement content)
            throw new InvalidOperationException("The Viewbox visual contract changed.");

        Move(scroll, surface, 0);
        var top = Snapshot(surface, scroll, viewbox, content, container);
        var range = Math.Max(0, scroll.ScrollableHeight);
        var hasScroll = range > GeometryTolerance;
        if (hasScroll)
        {
            for (var step = 0; step < WarmupSteps; step++)
            {
                token.ThrowIfCancellationRequested();
                Move(scroll, surface, TargetOffset(step, range));
            }
            Move(scroll, surface, 0);
        }

        var elapsed = new double[hasScroll ? MeasuredSteps : 0];
        var transformChanges = 0;
        var offsetChanges = 0;
        var offsetMismatches = 0;
        long allocated = 0;
        var drift = 0d;
        for (var step = 0; step < elapsed.Length; step++)
        {
            token.ThrowIfCancellationRequested();
            var requested = TargetOffset(step, range);
            var previousOffset = scroll.VerticalOffset;
            var previousTransform = container.Transform;
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var timestamp = Stopwatch.GetTimestamp();
            Move(scroll, surface, requested);
            var elapsedTicks = Stopwatch.GetTimestamp() - timestamp;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            elapsed[step] = elapsedTicks * 1000d / Stopwatch.Frequency;
            if (!ReferenceEquals(previousTransform, container.Transform))
                transformChanges++;
            if (Math.Abs(previousOffset - scroll.VerticalOffset) > GeometryTolerance)
                offsetChanges++;
            if (Math.Abs(requested - scroll.VerticalOffset) > GeometryTolerance)
                offsetMismatches++;
            drift = Math.Max(drift, GeometryDifference(top,
                Snapshot(surface, scroll, viewbox, content, container), compensateOffset: true));
        }

        var anchors = new ScrollGeometry[3];
        for (var anchor = 0; anchor < anchors.Length; anchor++)
        {
            token.ThrowIfCancellationRequested();
            Move(scroll, surface, range * anchor / 2);
            anchors[anchor] = Snapshot(surface, scroll, viewbox, content, container);
        }
        AssertOffscreen(sourceWindow, surface);
        Array.Sort(elapsed);
        return new ScrollVariant(variant, hasScroll, elapsed.Length, offsetChanges, offsetMismatches,
            transformChanges, allocated, elapsed.Sum(), Percentile(elapsed, 0.5), Percentile(elapsed, 0.95),
            elapsed.Length == 0 ? 0 : elapsed[^1], drift, anchors);
    }

    private static double TargetOffset(int step, double range) => range * (step % 24 + 1) / 24;

    private static void Move(ScrollViewer scroll, FrameworkElement surface, double offset)
    {
        scroll.ScrollToVerticalOffset(offset);
        surface.UpdateLayout();
    }

    private static ScrollGeometry Snapshot(FrameworkElement surface, ScrollViewer scroll, Viewbox viewbox,
        FrameworkElement content, ContainerVisual container)
    {
        var transform = container.Transform.Value;
        return new ScrollGeometry(scroll.VerticalOffset, scroll.ExtentWidth, scroll.ExtentHeight,
            scroll.ViewportWidth, scroll.ViewportHeight, transform.M11, transform.M22,
            GetBounds(viewbox, surface), GetBounds(content, surface));
    }

    private static Bounds GetBounds(FrameworkElement element, FrameworkElement surface)
    {
        var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        return new Bounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static double GeometryDifference(ScrollGeometry left, ScrollGeometry right, bool compensateOffset)
    {
        var leftOffset = compensateOffset ? left.VerticalOffset : 0;
        var rightOffset = compensateOffset ? right.VerticalOffset : 0;
        double[] differences =
        [
            compensateOffset ? 0 : Math.Abs(left.VerticalOffset - right.VerticalOffset),
            Math.Abs(left.ExtentWidth - right.ExtentWidth), Math.Abs(left.ExtentHeight - right.ExtentHeight),
            Math.Abs(left.ViewportWidth - right.ViewportWidth), Math.Abs(left.ViewportHeight - right.ViewportHeight),
            Math.Abs(left.ScaleX - right.ScaleX), Math.Abs(left.ScaleY - right.ScaleY),
            Math.Abs(left.ViewboxBounds.X - right.ViewboxBounds.X),
            Math.Abs(left.ViewboxBounds.Y + leftOffset - right.ViewboxBounds.Y - rightOffset),
            Math.Abs(left.ViewboxBounds.Width - right.ViewboxBounds.Width), Math.Abs(left.ViewboxBounds.Height - right.ViewboxBounds.Height),
            Math.Abs(left.ContentBounds.X - right.ContentBounds.X),
            Math.Abs(left.ContentBounds.Y + leftOffset - right.ContentBounds.Y - rightOffset),
            Math.Abs(left.ContentBounds.Width - right.ContentBounds.Width), Math.Abs(left.ContentBounds.Height - right.ContentBounds.Height)
        ];
        if (differences.Any(value => !double.IsFinite(value)))
            throw new InvalidOperationException("Scroll geometry must remain finite.");
        return differences.Max();
    }

    private static double Percentile(double[] sorted, double fraction) => sorted.Length == 0
        ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];

    private static void AssertOffscreen(MainWindow sourceWindow, FrameworkElement surface)
    {
        if (new WindowInteropHelper(sourceWindow).Handle != IntPtr.Zero ||
            PresentationSource.FromVisual(surface) is not null)
            throw new InvalidOperationException("Offscreen scroll isolation was lost.");
    }
}
