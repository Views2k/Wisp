using System.Windows;
using System.Windows.Controls;

namespace Wisp.App;

/// <summary>Equal-width telemetry groups that reflow instead of shrinking their text.</summary>
public sealed class DashboardColumns : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(DashboardColumns),
        new FrameworkPropertyMetadata(260d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number > 0);
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(DashboardColumns),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int number && number > 0);
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(DashboardColumns),
        new FrameworkPropertyMetadata(18d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number >= 0);

    public double MinimumColumnWidth { get => (double)GetValue(MinimumColumnWidthProperty); set => SetValue(MinimumColumnWidthProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    internal static int ColumnCount(double width, double minimum, double gap, int maximum, int count) =>
        Math.Max(1, Math.Min(Math.Min(maximum, Math.Max(1, count)),
            double.IsFinite(width) ? (int)Math.Max(1, Math.Floor((width + gap) / (minimum + gap))) : maximum));

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        if (children.Length == 0) return default;
        var columns = ColumnCount(availableSize.Width, MinimumColumnWidth, Gap, MaximumColumns, children.Length);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : columns * (MinimumColumnWidth + Gap) - Gap;
        var cellWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var height = 0d;
        for (var row = 0; row < children.Length; row += columns)
        {
            var rowHeight = 0d;
            for (var column = row; column < Math.Min(row + columns, children.Length); column++)
            {
                children[column].Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, children[column].DesiredSize.Height);
            }
            height += rowHeight + (row == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToArray();
        var columns = ColumnCount(finalSize.Width, MinimumColumnWidth, Gap, MaximumColumns, children.Length);
        var cellWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var top = 0d;
        for (var row = 0; row < children.Length; row += columns)
        {
            var rowHeight = children.Skip(row).Take(columns).Max(child => child.DesiredSize.Height);
            for (var column = 0; column < Math.Min(columns, children.Length - row); column++)
                children[row + column].Arrange(new Rect(column * (cellWidth + Gap), top, cellWidth, rowHeight));
            top += rowHeight + Gap;
        }
        return finalSize;
    }
}
