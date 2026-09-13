using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Wisp.App;

public static class ScrollEdgeFade
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollEdgeFade), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(FadeState), typeof(ScrollEdgeFade));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    internal static void Refresh(ScrollContentPresenter presenter) =>
        (presenter.GetValue(StateProperty) as FadeState)?.Update();

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs change)
    {
        if (element is not ScrollContentPresenter presenter) return;
        if (presenter.GetValue(StateProperty) is FadeState previous) previous.Dispose();
        presenter.ClearValue(StateProperty);
        if (!(bool)change.NewValue) return;
        var state = new FadeState(presenter);
        presenter.SetValue(StateProperty, state);
        state.Initialize();
    }

    private sealed class FadeState(ScrollContentPresenter presenter)
    {
        private ScrollViewer? _owner;
        private Brush? _mask;
        private MaskShape? _shape;
        private bool _attached;

        internal void Initialize()
        {
            presenter.Loaded += Loaded;
            presenter.Unloaded += Unloaded;
            if (presenter.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();

        private ScrollViewer? FindOwner()
        {
            if (presenter.TemplatedParent is ScrollViewer templated) return templated;
            for (DependencyObject? item = VisualTreeHelper.GetParent(presenter); item is not null; item = VisualTreeHelper.GetParent(item))
                if (item is ScrollViewer viewer) return viewer;
            return null;
        }

        private void Attach()
        {
            if (_attached) return;
            _owner = FindOwner();
            if (_owner is null) return;
            _attached = true;
            _owner.ScrollChanged += Scrolled;
            presenter.SizeChanged += Resized;
            SystemParameters.StaticPropertyChanged += EnvironmentChanged;
            Update();
        }

        private void Detach()
        {
            ClearMask();
            if (!_attached) return;
            _attached = false;
            if (_owner is not null) _owner.ScrollChanged -= Scrolled;
            presenter.SizeChanged -= Resized;
            SystemParameters.StaticPropertyChanged -= EnvironmentChanged;
            _owner = null;
        }

        internal void Dispose()
        {
            Detach();
            presenter.Loaded -= Loaded;
            presenter.Unloaded -= Unloaded;
            ClearMask();
        }

        private void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (ReferenceEquals(args.OriginalSource, _owner)) Update();
        }
        private void Resized(object sender, SizeChangedEventArgs args) => Update();
        private void EnvironmentChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(SystemParameters.HighContrast)) return;
            if (presenter.Dispatcher.CheckAccess()) Update();
            else if (!presenter.Dispatcher.HasShutdownStarted)
                presenter.Dispatcher.BeginInvoke(new Action(() => { if (_attached) Update(); }));
        }

        internal void Update()
        {
            if (!GetIsEnabled(presenter)) { ClearMask(); return; }
            var owner = _owner ?? FindOwner();
            if (owner is null || SystemParameters.HighContrast || IsEditor(owner) ||
                presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0)
            {
                ClearMask();
                return;
            }
            // Keep the mask on the viewport presenter. Masking content itself moves
            // the fade with the content; masking the viewer also fades its scrollbar.
            if (presenter.OpacityMask is not null && !ReferenceEquals(presenter.OpacityMask, _mask)) return;
            const double tolerance = 0.001;
            var vertical = owner.ScrollableHeight > tolerance && owner.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
            var horizontal = owner.ScrollableWidth > tolerance && owner.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
            var shape = new MaskShape(presenter.ActualWidth, presenter.ActualHeight,
                horizontal && owner.HorizontalOffset > tolerance,
                vertical && owner.VerticalOffset > tolerance,
                horizontal && owner.HorizontalOffset < owner.ScrollableWidth - tolerance,
                vertical && owner.VerticalOffset < owner.ScrollableHeight - tolerance);
            if (!shape.Left && !shape.Top && !shape.Right && !shape.Bottom) { ClearMask(); return; }
            if (_shape == shape && ReferenceEquals(presenter.OpacityMask, _mask)) return;
            _shape = shape;
            _mask = CreateMask(shape);
            presenter.SetCurrentValue(UIElement.OpacityMaskProperty, _mask);
        }

        private void ClearMask()
        {
            if (_mask is not null && ReferenceEquals(presenter.OpacityMask, _mask))
                presenter.SetCurrentValue(UIElement.OpacityMaskProperty, null);
            _mask = null;
            _shape = null;
        }
    }

    private static bool IsEditor(ScrollViewer owner) => owner.TemplatedParent is TextBoxBase or PasswordBox;

    private static Brush CreateMask(MaskShape shape)
    {
        var vertical = AxisMask(shape.Height, shape.Top, shape.Bottom, true);
        var horizontal = AxisMask(shape.Width, shape.Left, shape.Right, false);
        if (!shape.Left && !shape.Right) return vertical;
        if (!shape.Top && !shape.Bottom) return horizontal;
        var group = new DrawingGroup { OpacityMask = horizontal };
        using (var drawing = group.Open()) drawing.DrawRectangle(vertical, null, new Rect(0, 0, shape.Width, shape.Height));
        group.Freeze();
        var mask = new DrawingBrush(group)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, shape.Width, shape.Height),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, shape.Width, shape.Height),
            Stretch = Stretch.Fill
        };
        mask.Freeze();
        return mask;
    }

    private static Brush AxisMask(double length, bool start, bool end, bool vertical)
    {
        var depth = Math.Min(24, length * 0.2) / length;
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, length) : new Point(length, 0)
        };
        foreach (var (fraction, opacity) in new[] { (0d, 0d), (0.25, 0.15625), (0.5, 0.5), (0.75, 0.84375), (1d, 1d) })
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round((start ? opacity : 1) * 255), 255, 255, 255), depth * fraction));
        foreach (var (fraction, opacity) in new[] { (0d, 1d), (0.25, 0.84375), (0.5, 0.5), (0.75, 0.15625), (1d, 0d) })
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round((end ? opacity : 1) * 255), 255, 255, 255), 1 - depth + depth * fraction));
        brush.Freeze();
        return brush;
    }

    private readonly record struct MaskShape(double Width, double Height, bool Left, bool Top, bool Right, bool Bottom);
}
