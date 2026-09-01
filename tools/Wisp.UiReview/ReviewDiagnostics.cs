using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;

namespace Wisp.UiReview;

internal sealed class ReviewReport
{
    public int SchemaVersion => 1;
    public bool PresentationMode { get; init; }
    public string Scope { get; init; } = "matrix";
    public string Method => Scope == "native-lifetime-check"
        ? "Four real native controls with synthetic frames in an independent nonactivating host; automatic minimize/restore/collapse/resume/close; no controller, production window, settings, or capture"
        : Scope == "scroll-check"
        ? PresentationMode
            ? "Detached MainWindow.Content in an independent input-blocked host; automatic direct/wrapped Appearance scrolling, monotonic compositor-callback gaps; no controller startup, source-window Show, or input simulation"
            : "Detached MainWindow.Content; bounded warmed programmatic scroll/layout A/B, direct Viewbox versus temporary Decorator; no window, GPU timing, controller startup, or input simulation"
        : Scope == "wizard"
        ? PresentationMode
            ? "Detached SetupWindow.Content in a bounded display-only host; tool-only step selection, no setup test/completion or production startup"
            : "Detached SetupWindow.Content, explicit root DPI, RenderTargetBitmap; tool-only step selection, no live window, setup test/completion, or production startup"
        : PresentationMode
        ? "Detached MainWindow.Content in a bounded display-only host Window; no Wisp application startup or screen capture"
        : "Detached MainWindow.Content, explicit root DPI, RenderTargetBitmap; no live window or application startup";
    public string ShaderLimitations => "RenderTargetBitmap uses software rendering, which does not support PS 3.0 shaders; unprocessed white input rectangles are not GPU output. Capability flags and synthetic presentation are not proof of GPU or game parity.";
    public bool SyntheticOnly => true;
    public string Telemetry { get; init; } = string.Empty;
    public string? AppAssemblySha256 { get; set; }
    public string? AppResourceSourceSha256 { get; set; }
    public LogoResourceReport LogoResource { get; set; } = new(false, 0, null);
    public int SuppressedStartupNotifications { get; set; }
    public string? FatalError { get; set; }
    public string? FatalInnerError { get; set; }
    public string? FatalPhase { get; set; }
    public int BindingMessageCount { get; set; }
    public bool BindingMessagesTruncated { get; set; }
    public BindingMessage[] BindingMessages { get; set; } = [];
    public RendererReport? Renderer { get; set; }
    public PresentationReport? Presentation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScrollCheckReport? ScrollCheck { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeLifetimeCheckReport? NativeLifetimeCheck { get; set; }
    public List<CaptureReport> Captures { get; } = [];
}

internal sealed class PresentationReport
{
    public string Title { get; init; } = "Wisp UI review - synthetic preview";
    public int AutoCloseSeconds { get; init; } = 120;
    public bool DisplayOnly => true;
    public bool Shown { get; set; }
    public bool ContentRendered { get; set; }
    public bool AutoClosed { get; set; }
    public double DurationSeconds { get; set; }
    public string? HwndTargetRenderMode { get; set; }
    public CaptureReport? Surface { get; set; }
}

internal sealed record RendererReport(int Tier, bool PixelShader3HardwareSupported, bool PixelShader3SoftwareSupported,
    string ProcessRenderMode);
internal sealed record CaptureReport(string? Image, string Fixture, string Tab, string Viewport,
    int Dpi, int PixelWidth, int PixelHeight, double ActualDpiX, double ActualDpiY,
    Bounds? SurfaceBounds, LogoReport Logo, PreviewStateReport? PreviewState,
    PreviewReport[] Previews, ElementBounds[] NamedElements,
    ScrollReport[] ScrollViewers, LabelReviewReport Labels, BindingFailure[] BindingFailures, int VisualNodesVisited,
    bool VisualTreeTruncated, int NewBindingMessages)
{
    public WizardReviewReport? Wizard { get; init; }
}
internal sealed record Bounds(double X, double Y, double Width, double Height);
internal sealed record ElementBounds(string Name, string Type, bool Visible, Bounds? Bounds, double LocalWidth, double LocalHeight);
internal sealed record LogoResourceReport(bool Found, long ByteLength, string? Sha256);
internal sealed record LogoReport(bool Found, bool Visible, bool Decoded, int PixelWidth, int PixelHeight, Bounds? Bounds);
internal sealed record PreviewStateReport(bool HasLiveTelemetry, bool IsPreviewLive,
    bool NativeFrameSpeedAvailable, bool PreviewFrameSpeedAvailable, int PreviewSpeed, bool PreviewIsElectric);
internal sealed record PreviewReport(string Name, bool Visible, string CenterReference,
    Bounds? PreviewSurfaceBounds, Bounds? HostBounds, Bounds? DescendantBounds,
    double? CenterOffsetX, double? CenterOffsetY, bool? ContentFitsPreviewSurface, ElementBounds[] Controls);
internal sealed record ScrollReport(Bounds? Bounds, double ViewportWidth, double ViewportHeight,
    double ExtentWidth, double ExtentHeight, double HorizontalOffset, double VerticalOffset);
internal sealed record BindingFailure(string Name, string Type, string Property, string Status);
internal sealed record LabelReviewReport(int Checked, int Skipped, int OverflowCount, bool Truncated, LabelOverflow[] Overflows);
internal sealed record LabelOverflow(string Name, int TextLength, Bounds? Bounds,
    double AvailableWidth, double AvailableHeight, double RequiredWidth, double RequiredHeight, string Trimming);
internal sealed record BindingMessage(string Phase, string Severity, int? Code, string Category,
    string? BindingPath, string? TargetProperty);

internal static class ReviewDiagnostics
{
    private const int MaximumVisualNodes = 4096;

    public static RendererReport ProbeRenderer() => new(RenderCapability.Tier >> 16,
        RenderCapability.IsPixelShaderVersionSupported(3, 0),
        RenderCapability.IsPixelShaderVersionSupportedInSoftware(3, 0), RenderOptions.ProcessRenderMode.ToString());

    public static LogoResourceReport ProbeLogoResource()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("/Wisp;component/Assets/Wisp-logo.png", UriKind.Relative));
            if (resource is null)
            {
                return new(false, 0, null);
            }

            using var stream = resource.Stream;
            if (stream.Length is <= 0 or > 4 * 1024 * 1024)
            {
                return new(false, stream.Length, null);
            }

            return new(true, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            return new(false, 0, null);
        }
    }

    public static CaptureReport Inspect(FrameworkElement surface, string? image, string fixture,
        string tab, string viewport, int dpi, int pixelWidth, int pixelHeight, int newBindingMessages,
        WizardReviewContext? wizard = null)
    {
        var pending = new Queue<DependencyObject>();
        var elements = new List<FrameworkElement>();
        var visuals = new List<Visual>();
        pending.Enqueue(surface);
        var visited = 0;
        var truncated = false;
        while (pending.Count > 0 && visited < MaximumVisualNodes)
        {
            var current = pending.Dequeue();
            visited++;
            if (current is FrameworkElement element)
            {
                elements.Add(element);
            }

            if (current is Visual visual)
            {
                visuals.Add(visual);
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                if (visited + pending.Count >= MaximumVisualNodes)
                {
                    truncated = true;
                    break;
                }

                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        var logo = elements.OfType<Image>().FirstOrDefault(element =>
                       element.Name.Contains("Logo", StringComparison.OrdinalIgnoreCase) ||
                       element.Source is BitmapImage bitmap &&
                       bitmap.UriSource?.OriginalString.Contains("Wisp-logo.png", StringComparison.OrdinalIgnoreCase) == true)
                   ?? elements.OfType<Image>().FirstOrDefault(element =>
                       VisibleWithin(element, surface) && element.Width <= 64 &&
                       RelativeBounds(element, surface) is { Y: >= 0 and < 55 });
        var logoBitmap = logo?.Source as BitmapSource;
        var logoReport = new LogoReport(logo is not null,
            logo is not null && VisibleWithin(logo, surface) && logo.ActualWidth > 0 && logo.ActualHeight > 0,
            logoBitmap is { PixelWidth: > 0, PixelHeight: > 0 },
            logoBitmap?.PixelWidth ?? 0, logoBitmap?.PixelHeight ?? 0,
            logo is null ? null : RelativeBounds(logo, surface));

        var previews = elements.Where(element => element.Name.EndsWith("LayoutPreview", StringComparison.Ordinal))
            .Take(4).Select(element => InspectPreview(element, surface, elements, visuals)).ToArray();
        var named = elements.Where(element => element.Name.Length > 0).Take(96)
            .Select(element => Describe(element, surface)).ToArray();
        var scrolls = elements.OfType<ScrollViewer>().Where(element => VisibleWithin(element, surface)).Take(8)
            .Select(element => new ScrollReport(RelativeBounds(element, surface), Round(element.ViewportWidth),
                Round(element.ViewportHeight), Round(element.ExtentWidth), Round(element.ExtentHeight),
                Round(element.HorizontalOffset), Round(element.VerticalOffset))).ToArray();
        var failures = new List<BindingFailure>();
        foreach (var element in elements)
        {
            var localValues = element.GetLocalValueEnumerator();
            while (localValues.MoveNext() && failures.Count < 64)
            {
                var property = localValues.Current.Property;
                var expression = BindingOperations.GetBindingExpressionBase(element, property);
                if (expression is not null && (expression.HasError || expression.Status is
                        BindingStatus.PathError or BindingStatus.UpdateTargetError or BindingStatus.UpdateSourceError))
                {
                    failures.Add(new(element.Name, element.GetType().Name, property.Name, expression.Status.ToString()));
                }
            }
        }

        PreviewStateReport? previewState = null;
        if (surface.DataContext is DiagnosticsViewModel viewModel)
        {
            var preview = viewModel.NativePreviewFrame;
            previewState = new PreviewStateReport(viewModel.HasLiveTelemetry, viewModel.IsPreviewLive,
                viewModel.NativeGaugeFrame.SpeedAvailable, preview.SpeedAvailable, preview.Speed, preview.IsElectric);
        }
        else if (wizard is null)
        {
            throw new InvalidOperationException("The review surface lost its fixture data context.");
        }
        var actualDpi = VisualTreeHelper.GetDpi(surface);
        return new CaptureReport(image, fixture, tab, viewport, dpi, pixelWidth, pixelHeight,
            Round(actualDpi.PixelsPerInchX), Round(actualDpi.PixelsPerInchY),
            ToBounds(new Rect(surface.RenderSize)), logoReport, previewState, previews, named, scrolls,
            InspectLabels(elements, surface),
            failures.ToArray(), visited, truncated || pending.Count > 0, newBindingMessages)
        {
            Wizard = wizard is null ? null : WizardReview.Inspect(surface, elements, wizard)
        };
    }

    private static PreviewReport InspectPreview(FrameworkElement host, FrameworkElement surface,
        IReadOnlyList<FrameworkElement> elements, IReadOnlyList<Visual> visuals)
    {
        var visible = VisibleWithin(host, surface);
        var hostBounds = RelativeBounds(host, surface);
        var previewSurface = elements.FirstOrDefault(element => element.Name == "HudPreviewSurface") ?? host;
        var previewSurfaceBounds = RelativeBounds(previewSurface, surface);
        Bounds? contentBounds = null;
        if (visible)
        {
            var descendants = Rect.Empty;
            foreach (var visual in visuals.Where(visual => IsWithin(visual, host) && VisibleWithin(visual, surface)))
            {
                var ownBounds = VisualTreeHelper.GetContentBounds(visual);
                if (!ownBounds.IsEmpty)
                {
                    descendants.Union(visual.TransformToAncestor(surface).TransformBounds(ownBounds));
                }
            }

            contentBounds = ToBounds(descendants);
        }

        var controls = elements.Where(element => IsWithin(element, host) && element is UserControl &&
                VisibleWithin(element, surface)).Take(8).Select(element => Describe(element, surface)).ToArray();
        double? dx = null;
        double? dy = null;
        bool? fits = null;
        if (previewSurfaceBounds is { } outer && contentBounds is { } inner)
        {
            dx = Round(inner.X + inner.Width / 2 - outer.X - outer.Width / 2);
            dy = Round(inner.Y + inner.Height / 2 - outer.Y - outer.Height / 2);
            fits = inner.X >= outer.X - 1 && inner.Y >= outer.Y - 1 &&
                   inner.X + inner.Width <= outer.X + outer.Width + 1 &&
                   inner.Y + inner.Height <= outer.Y + outer.Height + 1;
        }

        return new(host.Name, visible, previewSurface.Name, previewSurfaceBounds, hostBounds, contentBounds, dx, dy, fits, controls);
    }

    private static ElementBounds Describe(FrameworkElement element, FrameworkElement surface) =>
        new(element.Name, element.GetType().Name, VisibleWithin(element, surface), RelativeBounds(element, surface),
            Round(element.ActualWidth), Round(element.ActualHeight));

    private static LabelReviewReport InspectLabels(IEnumerable<FrameworkElement> elements, FrameworkElement surface)
    {
        var checkedCount = 0;
        var skipped = 0;
        var overflowCount = 0;
        var overflows = new List<LabelOverflow>();
        foreach (var text in elements.OfType<TextBlock>())
        {
            if (!VisibleWithin(text, surface) || string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            var bounds = RelativeBounds(text, surface);
            if (bounds is null || bounds.X + bounds.Width <= 0 || bounds.Y + bounds.Height <= 0 ||
                bounds.X >= surface.ActualWidth || bounds.Y >= surface.ActualHeight)
            {
                continue;
            }

            var width = text.ActualWidth - text.Padding.Left - text.Padding.Right;
            var height = text.ActualHeight - text.Padding.Top - text.Padding.Bottom;
            if (width <= 0 || height <= 0 || text.Text.Length > 4096 || text.Inlines.Count > 1 ||
                text.Inlines.FirstInline is not Run run ||
                run.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue ||
                run.ReadLocalValue(TextElement.FontWeightProperty) != DependencyProperty.UnsetValue)
            {
                skipped++;
                continue;
            }

            var formatted = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text),
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            if (text.TextWrapping != TextWrapping.NoWrap)
            {
                formatted.MaxTextWidth = width;
            }

            if (double.IsFinite(text.LineHeight) && text.LineHeight > 0)
            {
                formatted.LineHeight = text.LineHeight;
            }

            checkedCount++;
            // Wrap-boundary spaces have advance width but paint no glyphs outside the label.
            if (formatted.Width <= width + 1.5 && formatted.Height <= height + 1.5)
            {
                continue;
            }

            overflowCount++;
            if (overflows.Count < 64)
            {
                overflows.Add(new(text.Name, text.Text.Length, bounds, Round(width), Round(height),
                    Round(formatted.Width), Round(formatted.Height), text.TextTrimming.ToString()));
            }
        }

        return new(checkedCount, skipped, overflowCount, overflowCount > overflows.Count, overflows.ToArray());
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool VisibleWithin(DependencyObject element, DependencyObject surface)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: <= 0 })
            {
                return false;
            }

            if (ReferenceEquals(current, surface))
            {
                return true;
            }
        }

        return false;
    }

    internal static Bounds? RelativeBounds(FrameworkElement element, FrameworkElement surface)
    {
        try
        {
            var rectangle = new Rect(element.RenderSize);
            return ToBounds(ReferenceEquals(element, surface)
                ? rectangle : element.TransformToAncestor(surface).TransformBounds(rectangle));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Bounds? ToBounds(Rect rectangle) =>
        rectangle.IsEmpty || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
        !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)
            ? null : new(Round(rectangle.X), Round(rectangle.Y), Round(rectangle.Width), Round(rectangle.Height));

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 3) : 0;
}

internal sealed class BindingTrace : TraceListener
{
    private const int MaximumMessages = 128;
    private readonly object _gate = new();
    private readonly StringBuilder _line = new();
    private readonly List<BindingMessage> _messages = [];
    private readonly TraceSource _source = PresentationTraceSources.DataBindingSource;
    private readonly SourceLevels _previousLevel;
    private readonly TraceListener[] _previousListeners;
    private int _totalCount;
    private bool _disposed;

    public BindingTrace()
    {
        _previousLevel = _source.Switch.Level;
        _previousListeners = _source.Listeners.Cast<TraceListener>().ToArray();
        _source.Listeners.Clear();
        _source.Listeners.Add(this);
        _source.Switch.Level = SourceLevels.Warning;
    }

    public string Phase { get; set; } = "initialize";
    public int TotalCount { get { lock (_gate) { return _totalCount; } } }
    public bool Truncated => TotalCount > MaximumMessages;
    public BindingMessage[] Messages { get { lock (_gate) { return _messages.ToArray(); } } }

    public override void Write(string? message)
    {
        lock (_gate)
        {
            if (message is not null && _line.Length < 2048)
            {
                _line.Append(message.AsSpan(0, Math.Min(message.Length, 2048 - _line.Length)));
            }
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            Write(message);
            var text = _line.ToString();
            _line.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            _totalCount++;
            if (_messages.Count == MaximumMessages)
            {
                return;
            }

            // Keep identifiers/categories only; never write raw binding values, paths, URLs, or exception text.
            var codeText = Match(text, @"(?:Error|Warning):\s*(\d+)");
            var category = text.Contains("path error", StringComparison.OrdinalIgnoreCase) ? "path-error" :
                text.Contains("Cannot find source", StringComparison.OrdinalIgnoreCase) ? "source-not-found" :
                text.Contains("convert", StringComparison.OrdinalIgnoreCase) ? "conversion-error" : "binding-diagnostic";
            _messages.Add(new(Phase, text.Contains("Error:", StringComparison.Ordinal) ? "error" : "warning",
                int.TryParse(codeText, out var code) ? code : null, category,
                Match(text, @"BindingExpression:Path=([A-Za-z0-9_.\[\]-]+)"),
                Match(text, @"target property is '([A-Za-z0-9_]+)'")));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _source.Listeners.Remove(this);
            _source.Listeners.AddRange(_previousListeners);
            _source.Switch.Level = _previousLevel;
        }

        base.Dispose(disposing);
    }

    private static string? Match(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return match.Success ? match.Groups[1].Value : null;
    }
}
