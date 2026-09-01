using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal sealed class ScrollPresentationReport
{
    public int AutoCloseSeconds => 20;
    public int HardProcessLimitSeconds => 30;
    public int WarmupSecondsPerVariant => 1;
    public int MeasuredSecondsPerVariant => 6;
    public int MaximumSamplesPerVariant => 4096;
    public double ScrollDipsPerSecond => 360;
    public string Order => "direct-viewbox, decorator-viewbox";
    public string ProductionContentType { get; init; } = string.Empty;
    public bool ProductionHasDecorator { get; init; }
    public double ActualDpiX { get; set; }
    public double ActualDpiY { get; set; }
    public bool ContentRendered { get; set; }
    public bool AutoClosed { get; set; }
    public bool SourceWindowHandleStayedZero { get; set; } = true;
    public int DuplicateRenderingCallbacks { get; set; }
    public int OutOfOrderRenderingCallbacks { get; set; }
    public string CloseReason { get; set; } = "not-shown";
    public double DurationSeconds { get; set; }
    public List<ScrollPresentationVariant> Variants { get; } = [];
    public bool Completed => CloseReason == "completed" && Variants.Count == 2 && Variants.All(variant => variant.Completed);
    public bool HasFindings => !Completed || !ContentRendered || !SourceWindowHandleStayedZero ||
        OutOfOrderRenderingCallbacks != 0 || Variants.Any(variant => variant.HasFindings);
}

internal sealed class ScrollPresentationVariant
{
    public string Variant { get; init; } = string.Empty;
    public bool Completed { get; set; }
    public double DurationSeconds { get; set; }
    public double ScrollableHeight { get; init; }
    public double ExtentHeight { get; init; }
    public double ViewportHeight { get; init; }
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }
    public int OffsetMismatches { get; set; }
    public int ExtentOrScaleChanges { get; set; }
    public List<ScrollPresentationSample> Samples { get; } = [];
    public int UniqueRenderingCallbacks => Samples.Count;
    public int ActualOffsetChanges => Samples.Count(sample => sample.OffsetChanged);
    public int TransformReferenceReplacements => Samples.Count(sample => sample.TransformReferenceReplaced);
    public int GapsOver33Milliseconds => Samples.Count(sample => sample.IntervalMilliseconds > 33);
    public int GapsOver50Milliseconds => Samples.Count(sample => sample.IntervalMilliseconds > 50);
    public double MedianIntervalMilliseconds => Percentile(0.5);
    public double P95IntervalMilliseconds => Percentile(0.95);
    public double MaximumIntervalMilliseconds => Samples.Select(sample => sample.IntervalMilliseconds ?? 0).DefaultIfEmpty().Max();
    public double TotalLayoutMilliseconds => Samples.Sum(sample => sample.LayoutMilliseconds);
    public long AllocatedBytes => Samples.Sum(sample => sample.AllocatedBytes);
    public double MinimumActualOffset => Samples.Select(sample => sample.ActualOffset).DefaultIfEmpty().Min();
    public double MaximumActualOffset => Samples.Select(sample => sample.ActualOffset).DefaultIfEmpty().Max();
    public bool HasFindings => !Completed || Samples.Count < 2 || ActualOffsetChanges == 0 ||
        OffsetMismatches != 0 || ExtentOrScaleChanges != 0;

    private double Percentile(double fraction)
    {
        var sorted = Samples.Where(sample => sample.IntervalMilliseconds is not null)
            .Select(sample => sample.IntervalMilliseconds!.Value).Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    }
}

internal sealed record ScrollPresentationSample(double ElapsedMilliseconds, double? IntervalMilliseconds,
    double RenderingElapsedMilliseconds, double RequestedOffset, double ActualOffset, bool OffsetChanged,
    bool TransformReferenceReplaced, double LayoutMilliseconds, long AllocatedBytes);

internal static class ScrollPresentation
{
    public static void Run(MainWindow sourceWindow, FrameworkElement surface, TabControl tabs, Fixture fixture,
        ReviewReport report, BindingTrace bindings, CancellationToken cancellationToken)
    {
        if (report.ScrollCheck is null || tabs.Items[1] is not TabItem { Content: ScrollViewer scroll })
            throw new InvalidOperationException("The scroll presentation contract changed.");
        tabs.SelectedIndex = 1;
        var originalContent = scroll.Content;
        var originalDecorator = originalContent is Decorator and not Viewbox ? (Decorator)originalContent : null;
        var viewbox = originalContent as Viewbox ?? originalDecorator?.Child as Viewbox
            ?? throw new InvalidOperationException("Appearance must contain one outer Viewbox.");
        if (VisualTreeHelper.GetChildrenCount(viewbox) != 1 ||
            VisualTreeHelper.GetChild(viewbox, 0) is not ContainerVisual container)
            throw new InvalidOperationException("The Viewbox visual contract changed.");

        var run = new ScrollPresentationReport
        {
            ProductionContentType = originalContent.GetType().Name,
            ProductionHasDecorator = originalDecorator is not null
        };
        report.ScrollCheck.Presentation = run;
        var presentation = new PresentationReport
        {
            Title = "Wisp UI review - synthetic scroll A/B",
            AutoCloseSeconds = run.AutoCloseSeconds
        };
        report.Presentation = presentation;
        var bindingStart = bindings.TotalCount;
        bindings.Phase = fixture.Name + "/scroll-presentation/initialize";
        var frame = new DispatcherFrame();
        var host = new Window
        {
            Title = presentation.Title,
            Width = 980,
            Height = 750,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = sourceWindow.Background,
            FontFamily = sourceWindow.FontFamily,
            FontSize = sourceWindow.FontSize,
            FontStyle = sourceWindow.FontStyle,
            FontWeight = sourceWindow.FontWeight,
            FontStretch = sourceWindow.FontStretch,
            Foreground = sourceWindow.Foreground,
            FlowDirection = sourceWindow.FlowDirection,
            Content = surface
        };
        surface.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(surface, KeyboardNavigationMode.None);
        var temporaryDecorator = new Decorator();
        ScrollPresentationVariant? current = null;
        var wrapped = false;
        var warming = true;
        var phaseStarted = 0L;
        var lastAdvance = 0L;
        long? lastMeasured = null;
        var renderingStart = TimeSpan.Zero;
        var lastRenderingTime = TimeSpan.MinValue;
        var renderingAttached = false;
        var inRendering = false;
        var targetOffset = 0d;
        var direction = 1;
        var elapsed = new Stopwatch();

        void Close(string reason)
        {
            if (run.CloseReason is "not-shown" or "running")
                run.CloseReason = reason;
            host.Close();
        }

        void VerifyIndependentHost()
        {
            if (new WindowInteropHelper(sourceWindow).Handle != IntPtr.Zero)
            {
                run.SourceWindowHandleStayedZero = false;
                throw new InvalidOperationException("The production source window acquired a handle.");
            }
            if (PresentationSource.FromVisual(surface) is not HwndSource)
                throw new InvalidOperationException("The independent presentation host was lost.");
        }

        void SetVariant(bool useDecorator, long now)
        {
            scroll.Content = null;
            temporaryDecorator.Child = null;
            if (useDecorator)
            {
                temporaryDecorator.Child = viewbox;
                scroll.Content = temporaryDecorator;
            }
            else
                scroll.Content = viewbox;
            wrapped = useDecorator;
            warming = true;
            current = null;
            targetOffset = 0;
            direction = 1;
            lastAdvance = now;
            phaseStarted = now;
            lastMeasured = null;
            scroll.ScrollToVerticalOffset(0);
            surface.UpdateLayout();
            bindings.Phase = fixture.Name + "/scroll-presentation/" + (wrapped ? "decorator" : "direct") + "/warmup";
        }

        double Advance(long now)
        {
            var seconds = lastAdvance == 0 ? 0 : (now - lastAdvance) / (double)Stopwatch.Frequency;
            lastAdvance = now;
            targetOffset = Math.Clamp(targetOffset + direction * seconds * run.ScrollDipsPerSecond,
                0, scroll.ScrollableHeight);
            if (targetOffset >= scroll.ScrollableHeight)
                direction = -1;
            else if (targetOffset <= 0)
                direction = 1;
            return targetOffset;
        }

        void OnRendering(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs rendering || inRendering)
                return;
            if (rendering.RenderingTime == lastRenderingTime)
            {
                run.DuplicateRenderingCallbacks++;
                return;
            }
            if (rendering.RenderingTime < lastRenderingTime)
            {
                run.OutOfOrderRenderingCallbacks++;
                return;
            }
            lastRenderingTime = rendering.RenderingTime;
            inRendering = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = Stopwatch.GetTimestamp();
                var seconds = (now - phaseStarted) / (double)Stopwatch.Frequency;
                if (warming)
                {
                    scroll.ScrollToVerticalOffset(Advance(now));
                    surface.UpdateLayout();
                    if (seconds < run.WarmupSecondsPerVariant)
                        return;
                    VerifyIndependentHost();
                    if (scroll.ScrollableHeight <= ScrollReview.GeometryTolerance)
                        throw new InvalidOperationException("Appearance did not have scrollable content.");
                    scroll.ScrollToVerticalOffset(0);
                    surface.UpdateLayout();
                    var matrix = container.Transform.Value;
                    current = new ScrollPresentationVariant
                    {
                        Variant = wrapped ? "decorator-viewbox" : "direct-viewbox",
                        ScrollableHeight = scroll.ScrollableHeight,
                        ExtentHeight = scroll.ExtentHeight,
                        ViewportHeight = scroll.ViewportHeight,
                        ScaleX = matrix.M11,
                        ScaleY = matrix.M22
                    };
                    run.Variants.Add(current);
                    warming = false;
                    phaseStarted = lastAdvance = now;
                    renderingStart = rendering.RenderingTime;
                    targetOffset = 0;
                    direction = 1;
                    bindings.Phase = fixture.Name + "/scroll-presentation/" + current.Variant + "/measure";
                    return;
                }

                if (current is null)
                    throw new InvalidOperationException("A measured scroll phase was not initialized.");
                var requested = Advance(now);
                var previousOffset = scroll.VerticalOffset;
                var previousTransform = container.Transform;
                var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                var layoutStart = Stopwatch.GetTimestamp();
                scroll.ScrollToVerticalOffset(requested);
                surface.UpdateLayout();
                var layoutTicks = Stopwatch.GetTimestamp() - layoutStart;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                var actual = scroll.VerticalOffset;
                current.Samples.Add(new ScrollPresentationSample(seconds * 1000,
                    lastMeasured is { } previous ? (now - previous) * 1000d / Stopwatch.Frequency : null,
                    (rendering.RenderingTime - renderingStart).TotalMilliseconds, requested, actual,
                    Math.Abs(actual - previousOffset) > ScrollReview.GeometryTolerance,
                    !ReferenceEquals(previousTransform, container.Transform), layoutTicks * 1000d / Stopwatch.Frequency, allocated));
                lastMeasured = now;
                if (Math.Abs(actual - requested) > ScrollReview.GeometryTolerance)
                    current.OffsetMismatches++;
                var scale = container.Transform.Value;
                if (Math.Abs(scroll.ExtentHeight - current.ExtentHeight) > ScrollReview.GeometryTolerance ||
                    Math.Abs(scroll.ViewportHeight - current.ViewportHeight) > ScrollReview.GeometryTolerance ||
                    Math.Abs(scale.M11 - current.ScaleX) > ScrollReview.GeometryTolerance ||
                    Math.Abs(scale.M22 - current.ScaleY) > ScrollReview.GeometryTolerance)
                    current.ExtentOrScaleChanges++;
                current.DurationSeconds = seconds;
                if (seconds >= run.MeasuredSecondsPerVariant || current.Samples.Count >= run.MaximumSamplesPerVariant)
                {
                    current.Completed = seconds >= run.MeasuredSecondsPerVariant;
                    Console.WriteLine($"Scroll {current.Variant}: {current.UniqueRenderingCallbacks} unique callbacks, " +
                        $"{current.ActualOffsetChanges} offset changes, {current.TransformReferenceReplacements} transform replacements, " +
                        $"gaps >33ms {current.GapsOver33Milliseconds}, >50ms {current.GapsOver50Milliseconds}.");
                    if (wrapped)
                        Close(current.Completed ? "completed" : "sample-limit");
                    else
                        SetVariant(true, Stopwatch.GetTimestamp());
                }
            }
            catch (Exception exception)
            {
                report.FatalError = exception.GetType().Name;
                report.FatalInnerError = exception.InnerException?.GetType().Name;
                report.FatalPhase = bindings.Phase;
                Close("error");
            }
            finally
            {
                inRendering = false;
            }
        }

        host.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            if (e.Key == Key.Escape)
                Close("escape");
        };
        host.PreviewKeyUp += (_, e) => e.Handled = true;
        host.PreviewTextInput += (_, e) => e.Handled = true;
        host.PreviewMouseDown += (_, e) => e.Handled = true;
        host.PreviewMouseUp += (_, e) => e.Handled = true;
        host.PreviewMouseWheel += (_, e) => e.Handled = true;
        host.Closed += (_, _) =>
        {
            if (run.CloseReason == "running")
                run.CloseReason = "closed-early";
            frame.Continue = false;
        };
        var autoClose = new DispatcherTimer(DispatcherPriority.Send, host.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(run.AutoCloseSeconds)
        };
        autoClose.Tick += (_, _) =>
        {
            autoClose.Stop();
            run.AutoClosed = presentation.AutoClosed = true;
            Close("auto-close");
        };
        host.ContentRendered += (_, _) =>
        {
            if (run.ContentRendered)
                return;
            try
            {
                VerifyIndependentHost();
                var source = (HwndSource)PresentationSource.FromVisual(surface);
                var dpi = VisualTreeHelper.GetDpi(surface);
                run.ActualDpiX = dpi.PixelsPerInchX;
                run.ActualDpiY = dpi.PixelsPerInchY;
                presentation.HwndTargetRenderMode = source.CompositionTarget?.RenderMode.ToString();
                presentation.Surface = ReviewDiagnostics.Inspect(surface, null, fixture.Name, "appearance",
                    "scroll-presentation", (int)Math.Round(dpi.PixelsPerInchX),
                    checked((int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
                    checked((int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)), bindings.TotalCount - bindingStart);
                run.ContentRendered = presentation.ContentRendered = true;
                report.Renderer = ReviewDiagnostics.ProbeRenderer();
                phaseStarted = lastAdvance = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
                renderingAttached = true;
                Console.WriteLine($"Scroll presentation ready; input blocked, no activation requested, automatic A/B then close. " +
                    $"WPF tier {report.Renderer.Tier}; target {presentation.HwndTargetRenderMode}; " +
                    $"{run.AutoCloseSeconds}s auto-close/{run.HardProcessLimitSeconds}s own-process watchdog. Callback intervals are not GPU present timestamps.");
            }
            catch (Exception exception)
            {
                report.FatalError = exception.GetType().Name;
                report.FatalInnerError = exception.InnerException?.GetType().Name;
                report.FatalPhase = bindings.Phase;
                Close("error");
            }
        };

        // This independent watchdog affects only the current review process, never
        // Wisp/the game/another PID. It also bounds a blocked synchronous WPF call.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("Scroll presentation exceeded the hard limit; terminating only this review process (124).");
            Environment.Exit(124);
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            scroll.Content = null;
            if (originalDecorator is not null)
                originalDecorator.Child = null;
            SetVariant(false, Stopwatch.GetTimestamp());
            elapsed.Start();
            autoClose.Start();
            watchdog.Change(TimeSpan.FromSeconds(run.HardProcessLimitSeconds), Timeout.InfiniteTimeSpan);
            run.CloseReason = "running";
            host.Show();
            presentation.Shown = true;
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            autoClose.Stop();
            if (renderingAttached)
                CompositionTarget.Rendering -= OnRendering;
            host.Close();
            host.Content = null;
            scroll.Content = null;
            temporaryDecorator.Child = null;
            if (originalDecorator is not null)
                originalDecorator.Child = viewbox;
            scroll.Content = originalContent;
            presentation.DurationSeconds = run.DurationSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3);
            if (new WindowInteropHelper(sourceWindow).Handle != IntPtr.Zero)
                run.SourceWindowHandleStayedZero = false;
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
