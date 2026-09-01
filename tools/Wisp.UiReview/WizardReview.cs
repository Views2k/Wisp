using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wisp.App;

namespace Wisp.UiReview;

internal sealed record WizardReviewContext(SetupWindow Window, AppController Controller, int Step);
internal sealed record WizardReviewReport(
    int Step,
    bool SetupRequired,
    bool TestRunning,
    bool HasSuccessfulTest,
    bool? DataOutConfirmed,
    bool? DisplayModeConfirmed,
    bool? StockHudConfirmed,
    HeadingReview[] Headings,
    FooterControlReview[] FooterControls,
    bool HeadingsTruncated)
{
    public string ContrastBasis => "Actual TextBlock foreground; contrast estimates use solid ancestor/window backgrounds, not ambient/image pixels. Non-solid brushes remain unmeasured.";
    public int BlackForegroundCount => Headings.Count(heading => heading.BlackForeground);
    public int LowContrastCount => Headings.Count(heading => heading.LowContrast);
    public int UnexpectedClippingCount => Headings.Count(heading => heading.UnexpectedClipping);
    public bool FooterWithinViewport => FooterControls.Where(control => control.Required)
        .All(control => control.Found && control.Visible && control.WithinViewport && !control.ClippedByAncestor);
    public bool HasFindings => !SetupRequired || TestRunning || HasSuccessfulTest ||
        DataOutConfirmed != false || DisplayModeConfirmed != false || StockHudConfirmed != false ||
        HeadingsTruncated || BlackForegroundCount > 0 || LowContrastCount > 0 ||
        UnexpectedClippingCount > 0 || !FooterWithinViewport;
}

internal sealed record HeadingReview(
    string Name,
    int TextLength,
    Bounds? Bounds,
    string ForegroundBrush,
    string? Foreground,
    string? EstimatedBackground,
    double EffectiveOpacity,
    double? ContrastRatio,
    double RequiredContrast,
    bool BlackForeground,
    bool LowContrast,
    bool WithinViewport,
    bool InsideScrollViewer,
    bool ClippedByAncestor,
    bool UnexpectedClipping);

internal sealed record FooterControlReview(
    string Name, bool Required, bool Found, bool Visible, bool Enabled, Bounds? Bounds, bool WithinViewport, bool ClippedByAncestor);

internal static class WizardReview
{
    private const int MaximumHeadings = 48;
    private const double BoundsTolerance = 1.5;

    public static WizardReviewReport Inspect(
        FrameworkElement surface, IReadOnlyList<FrameworkElement> elements, WizardReviewContext context)
    {
        var candidates = elements.OfType<TextBlock>()
            .Where(text => ReviewDiagnostics.VisibleWithin(text, surface) &&
                           !string.IsNullOrWhiteSpace(text.Text) &&
                           (text.FontSize >= 18 ||
                            text.Name.Contains("Title", StringComparison.Ordinal) ||
                            text.Name.Contains("Heading", StringComparison.Ordinal) ||
                            text.Name == "StepLabel"))
            .ToArray();
        var headings = candidates.Take(MaximumHeadings)
            .Select((text, index) => InspectHeading(text, index, surface, context.Window.Background)).ToArray();
        var footer = new[] { "BackButton", "NextButton" }.Select(name =>
        {
            var element = elements.FirstOrDefault(element => element.Name == name);
            var bounds = element is null ? null : ReviewDiagnostics.RelativeBounds(element, surface);
            var clipped = element is not null && Ancestors(element, surface)
                .Where(ancestor => ancestor.ClipToBounds || ancestor.Clip is not null || ancestor is ScrollViewer)
                .Any(ancestor => !Fits(bounds, ClipBounds(ancestor, surface)));
            return new FooterControlReview(name, name == "NextButton" || context.Step > 0, element is not null,
                element is not null && ReviewDiagnostics.VisibleWithin(element, surface),
                element?.IsEnabled ?? false, bounds, Fits(bounds, SurfaceBounds(surface)), clipped);
        }).ToArray();
        return new WizardReviewReport(context.Step, context.Controller.Settings.RequiresSetup,
            context.Controller.SetupTelemetry.IsRunning, context.Controller.SetupTelemetry.SuccessfulEvidence is not null,
            Confirmed(context.Window, "DataOutConfirmation"), Confirmed(context.Window, "DisplayConfirmation"),
            Confirmed(context.Window, "StockHudConfirmation"), headings, footer, candidates.Length > MaximumHeadings);
    }

    private static bool? Confirmed(SetupWindow window, string name) =>
        window.FindName(name) is CheckBox checkbox ? checkbox.IsChecked : null;

    private static HeadingReview InspectHeading(
        TextBlock text, int index, FrameworkElement surface, Brush? windowBackground)
    {
        var bounds = ReviewDiagnostics.RelativeBounds(text, surface);
        var insideViewport = Fits(bounds, SurfaceBounds(surface));
        var ancestors = Ancestors(text, surface).ToArray();
        var insideScroll = ancestors.Any(ancestor => ancestor is ScrollViewer);
        var opacity = text.Foreground?.Opacity ?? 1;
        foreach (var element in ancestors.Prepend(text))
        {
            opacity *= element.Opacity;
        }

        var foreground = text.Foreground is SolidColorBrush foregroundBrush ? foregroundBrush.Color : (Color?)null;
        var background = EstimateBackground(text, ancestors, windowBackground);
        var requiredContrast = text.FontSize >= 24 ||
                               text.FontSize >= 18.67 && text.FontWeight.ToOpenTypeWeight() >= 700 ? 3d : 4.5d;
        double? contrast = foreground is { } ink && background is { } paper
            ? Math.Round(Contrast(Blend(ink, opacity, paper), paper), 3)
            : null;
        var clippedByAncestor = false;
        var horizontalClipping = !FitsHorizontally(bounds, SurfaceBounds(surface));
        foreach (var ancestor in ancestors)
        {
            if (!ancestor.ClipToBounds && ancestor.Clip is null && ancestor is not ScrollViewer)
            {
                continue;
            }

            var clipBounds = ClipBounds(ancestor, surface);
            clippedByAncestor |= !Fits(bounds, clipBounds);
            horizontalClipping |= !FitsHorizontally(bounds, clipBounds);
        }

        var black = foreground is { A: >= 128, R: <= 32, G: <= 32, B: <= 32 };
        var unexpectedClipping = horizontalClipping || (!insideViewport || clippedByAncestor) && !insideScroll;
        var name = text.Name.Length > 0 ? text.Name :
            (ancestors.FirstOrDefault(element => element.Name.Length > 0)?.Name ?? "unnamed") + "/text-" + index;
        return new HeadingReview(name, text.Text.Length, bounds, text.Foreground?.GetType().Name ?? "none",
            foreground is { } color ? Hex(color) : null, background is { } fill ? Hex(fill) : null,
            Math.Round(opacity, 3), contrast, requiredContrast, black, contrast is { } measured && measured < requiredContrast,
            insideViewport, insideScroll, clippedByAncestor, unexpectedClipping);
    }

    private static IEnumerable<FrameworkElement> Ancestors(DependencyObject element, FrameworkElement surface)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement ancestor)
            {
                yield return ancestor;
            }

            if (ReferenceEquals(current, surface))
            {
                yield break;
            }
        }
    }

    private static Color? EstimateBackground(TextBlock text, FrameworkElement[] ancestors, Brush? windowBackground)
    {
        if (windowBackground is not SolidColorBrush { Color.A: 255, Opacity: 1 } windowBrush)
        {
            return null;
        }

        var color = windowBrush.Color;
        foreach (var element in ancestors.Reverse().Append(text))
        {
            var background = element switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                TextBlock block => block.Background,
                _ => null
            };
            if (background is null)
            {
                continue;
            }

            if (background is not SolidColorBrush solid)
            {
                return null;
            }

            color = Blend(solid.Color, solid.Opacity * element.Opacity, color);
        }

        return color;
    }

    private static Bounds? ClipBounds(FrameworkElement ancestor, FrameworkElement surface)
    {
        if (ancestor.Clip is not { } clip)
        {
            return ReviewDiagnostics.RelativeBounds(ancestor, surface);
        }

        try
        {
            var transformed = ReferenceEquals(ancestor, surface)
                ? clip.Bounds : ancestor.TransformToAncestor(surface).TransformBounds(clip.Bounds);
            return new Bounds(transformed.X, transformed.Y, transformed.Width, transformed.Height);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Bounds SurfaceBounds(FrameworkElement surface) => new(0, 0, surface.ActualWidth, surface.ActualHeight);
    private static bool Fits(Bounds? inner, Bounds? outer) =>
        FitsHorizontally(inner, outer) && inner is not null && outer is not null &&
        inner.Height > 0 && inner.Y >= outer.Y - BoundsTolerance &&
        inner.Y + inner.Height <= outer.Y + outer.Height + BoundsTolerance;
    private static bool FitsHorizontally(Bounds? inner, Bounds? outer) =>
        inner is not null && outer is not null && inner.Width > 0 &&
        inner.X >= outer.X - BoundsTolerance && inner.X + inner.Width <= outer.X + outer.Width + BoundsTolerance;

    private static Color Blend(Color foreground, double opacity, Color background)
    {
        var alpha = Math.Clamp(opacity * foreground.A / 255d, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color first, Color second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    private static double Linear(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
