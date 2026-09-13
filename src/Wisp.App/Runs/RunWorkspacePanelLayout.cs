using System.Windows;
using System.Windows.Controls;

namespace Wisp.App.Runs;

public sealed class RunWorkspacePanelLayout : Panel
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(RunWorkspaceWidth), typeof(RunWorkspacePanelLayout),
        new FrameworkPropertyMetadata(RunWorkspaceWidth.Full,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(RunWorkspacePanelLayout),
        new FrameworkPropertyMetadata(350d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width >= 100);
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(RunWorkspacePanelLayout),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count is >= 1 and <= 3);
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(RunWorkspacePanelLayout),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double gap && double.IsFinite(gap) && gap >= 0);
    public static readonly DependencyProperty FillRowsProperty = DependencyProperty.Register(
        nameof(FillRows), typeof(bool), typeof(RunWorkspacePanelLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static RunWorkspaceWidth GetSpan(DependencyObject element) => (RunWorkspaceWidth)element.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject element, RunWorkspaceWidth value) => element.SetValue(SpanProperty, value);
    public double MinimumColumnWidth { get => (double)GetValue(MinimumColumnWidthProperty); set => SetValue(MinimumColumnWidthProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool FillRows { get => (bool)GetValue(FillRowsProperty); set => SetValue(FillRowsProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MinimumColumnWidth;
        return new Size(width, Layout(width, arrange: false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(Math.Max(0, finalSize.Width), arrange: true);
        return finalSize;
    }

    private double Layout(double width, bool arrange)
    {
        if (FillRows) return LayoutFilledRows(width, arrange);
        var columns = Math.Clamp((int)Math.Floor((width + Gap) / (MinimumColumnWidth + Gap)), 1, MaximumColumns);
        var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var column = 0;
        var top = 0d;
        var rowHeight = 0d;
        var hasChildren = false;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                if (arrange) child.Arrange(new Rect());
                else child.Measure(new Size());
                continue;
            }
            var span = GetSpan(child) switch
            {
                RunWorkspaceWidth.Compact => 1,
                RunWorkspaceWidth.Wide => Math.Min(2, columns),
                _ => columns
            };
            if (column > 0 && column + span > columns)
            {
                top += rowHeight + Gap;
                column = 0;
                rowHeight = 0;
            }
            var childWidth = columnWidth * span + Gap * (span - 1);
            if (arrange) child.Arrange(new Rect(column * (columnWidth + Gap), top, childWidth, child.DesiredSize.Height));
            else child.Measure(new Size(childWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            column += span;
            hasChildren = true;
        }
        return hasChildren ? top + rowHeight : 0;
    }

    private double LayoutFilledRows(double width, bool arrange)
    {
        var columns = Math.Clamp((int)Math.Floor((width + Gap) / (MinimumColumnWidth + Gap)), 1, MaximumColumns);
        var row = new List<(UIElement Child, int Span)>(columns);
        var occupied = 0;
        var top = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                if (arrange) child.Arrange(new Rect());
                else child.Measure(new Size());
                continue;
            }
            var span = GetSpan(child) switch
            {
                RunWorkspaceWidth.Compact => 1,
                RunWorkspaceWidth.Wide => Math.Min(2, columns),
                _ => columns
            };
            if (occupied > 0 && occupied + span > columns) FinishRow();
            row.Add((child, span));
            occupied += span;
        }
        FinishRow();
        return Math.Max(0, top - Gap);

        void FinishRow()
        {
            if (row.Count == 0) return;
            // Keep the chosen reading order. An incomplete row shares its spare width;
            // measuring at that final width also gives wrapped labels the correct height.
            var columnWidth = Math.Max(0, (width - Gap * (occupied - 1)) / occupied);
            var rowHeight = 0d;
            foreach (var (child, span) in row)
            {
                if (!arrange) child.Measure(new Size(columnWidth * span + Gap * (span - 1), double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            if (arrange)
            {
                var left = 0d;
                foreach (var (child, span) in row)
                {
                    var childWidth = columnWidth * span + Gap * (span - 1);
                    child.Arrange(new Rect(left, top, childWidth, rowHeight));
                    left += childWidth + Gap;
                }
            }
            top += rowHeight + Gap;
            row.Clear();
            occupied = 0;
        }
    }
}
