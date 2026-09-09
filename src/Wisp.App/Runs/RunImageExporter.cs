using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

internal sealed record RunImageSnapshot(
    string RunA, string? RunB, string Interval, string Context,
    RunFinding[] Findings, RunMetric[] Metrics, RunPlotPanel[] Charts,
    RunAlternativePlotPanel[] AlternativeCharts, double StartSeconds, double EndSeconds);

internal static class RunImageExporter
{
    internal const int ImageWidth = 1280;
    internal const int MaximumImageHeight = 4096;
    private static readonly Brush Background = Frozen(0x13, 0x1B, 0x20);
    private static readonly Brush Surface = Frozen(0x1D, 0x29, 0x30);
    private static readonly Brush Accent = RunComparisonColors.RunA;
    private static readonly Brush Ink = Frozen(0xED, 0xF3, 0xF6);
    private static readonly Brush Muted = Frozen(0xB0, 0xBF, 0xC8);

    // Only this bounded offscreen layout runs on the dispatcher. Encoding and disk I/O do not.
    internal static BitmapSource Render(RunImageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(snapshot.StartSeconds) || !double.IsFinite(snapshot.EndSeconds) ||
            snapshot.EndSeconds < snapshot.StartSeconds || snapshot.Charts.Length > 4 ||
            snapshot.AlternativeCharts.Length > 4 || snapshot.Metrics.Length > 16 || snapshot.Findings.Length > 32)
            throw new ArgumentException("The report is too large or its interval is invalid.", nameof(snapshot));

        var body = new StackPanel { Margin = new Thickness(48, 36, 48, 32) };
        body.Children.Add(Label("WISP  /  RUN REVIEW", 18, Accent, 28, FontWeights.SemiBold));
        body.Children.Add(Label(snapshot.RunB is null ? "Recorded run" : "Run comparison", 34, Ink, 48, FontWeights.SemiBold));
        body.Children.Add(Label("A · " + snapshot.RunA, 20, RunComparisonColors.RunA, 56));
        if (snapshot.RunB is not null) body.Children.Add(Label("B · " + snapshot.RunB, 20, RunComparisonColors.RunB, 56));
        body.Children.Add(Label(snapshot.Interval, 17, Accent, 52));
        body.Children.Add(Label(snapshot.Context, 15, Muted, 80));

        var metrics = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = snapshot.Metrics.Length == 9 ? 3 : 4,
            Margin = new Thickness(0, 20, 0, 12)
        };
        foreach (var metric in snapshot.Metrics)
        {
            var content = new StackPanel();
            content.Children.Add(Label(metric.Label, 13, Muted, 40));
            content.Children.Add(Label(metric.Value, 22, Ink, 64, FontWeights.SemiBold));
            if (metric.Comparison is not null) content.Children.Add(Label("B · " + metric.Comparison, 13, RunComparisonColors.RunB, 48));
            metrics.Children.Add(new Border
            {
                Background = Surface,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(14),
                Child = content
            });
        }
        body.Children.Add(metrics);
        if (snapshot.RunB is not null && snapshot.Metrics.Any(metric => metric.Comparison is not null))
            body.Children.Add(Label("Metric differences in brackets are B minus A.", 13, Muted, 40));

        foreach (var finding in snapshot.Findings.Take(3))
        {
            var item = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            item.Children.Add(Label(finding.Title, 18, Ink, 54, FontWeights.SemiBold));
            item.Children.Add(Label(finding.Detail, 15, Muted, 92));
            body.Children.Add(item);
        }
        body.Children.Add(Label("Selected graphs", 21, Ink, 36, FontWeights.SemiBold, new Thickness(0, 26, 0, 12)));
        foreach (var panel in snapshot.Charts)
        {
            body.Children.Add(ChartBorder(new RunChartView
            {
                Panel = panel,
                StartSeconds = snapshot.StartSeconds,
                EndSeconds = snapshot.EndSeconds,
                CursorSeconds = snapshot.StartSeconds,
                AccentBrush = Accent,
                TextBrush = Ink,
                MutedBrush = Muted,
                Height = 285,
                Focusable = false,
                IsHitTestVisible = false,
                RenderOffscreen = true
            }));
        }
        foreach (var panel in snapshot.AlternativeCharts)
        {
            body.Children.Add(ChartBorder(new RunAlternativePlotView
            {
                Panel = panel,
                AccentBrush = Accent,
                TextBrush = Ink,
                MutedBrush = Muted,
                Height = 330,
                Focusable = false,
                IsHitTestVisible = false,
                ShowInteractionHints = false
            }));
        }
        if (snapshot.Charts.Length > 0)
            body.Children.Add(Label("Time is in seconds. Legend values are recorded samples at the interval start; gaps stay empty.", 13, Muted, 48));
        body.Children.Add(Label("wispoverlay.com", 14, Accent, 28, margin: new Thickness(0, 18, 0, 0)));

        var root = new Border { Width = ImageWidth, Background = Background, Child = body };
        root.Measure(new Size(ImageWidth, double.PositiveInfinity));
        var height = (int)Math.Ceiling(root.DesiredSize.Height);
        if (height is <= 0 or > MaximumImageHeight)
            throw new InvalidOperationException("This report is too tall to export. Choose a smaller graph group.");
        root.Arrange(new Rect(0, 0, ImageWidth, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(ImageWidth, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    internal static Task WriteAsync(BitmapSource image, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsFrozen) throw new ArgumentException("The report image must be frozen before saving.", nameof(image));
        var destination = Path.GetFullPath(path);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".wisp-image-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);
    }

    private static Border ChartBorder(FrameworkElement chart) => new()
    {
        Background = Surface,
        Padding = new Thickness(20),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(0, 0, 0, 14),
        Child = chart
    };

    private static TextBlock Label(string text, double size, Brush color, double maximumHeight,
        FontWeight? weight = null, Thickness? margin = null) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            Foreground = color,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = maximumHeight,
            Margin = margin ?? new Thickness(0, 2, 0, 2)
        };

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
