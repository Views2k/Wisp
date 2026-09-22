using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wisp.App;

public sealed class DashboardRimEffect : FrameworkElement
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(OrbitSurfaceShape), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(OrbitSurfaceShape.Swept, Changed));
    public static readonly DependencyProperty TargetElementProperty = DependencyProperty.Register(
        nameof(TargetElement), typeof(FrameworkElement), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(null, TargetChanged));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(new CornerRadius(28), Changed));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty RimThicknessProperty = DependencyProperty.Register(
        nameof(RimThickness), typeof(Thickness), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(new Thickness(1), Changed));
    public static readonly DependencyProperty GlowOpacityProperty = DependencyProperty.Register(
        nameof(GlowOpacity), typeof(double), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(1d, Changed, CoerceOpacity));
    public static readonly DependencyProperty ParticlesEnabledProperty = DependencyProperty.Register(
        nameof(ParticlesEnabled), typeof(bool), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(true, Changed));
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(DashboardRimEffect),
        new FrameworkPropertyMetadata(true, Changed));

    private readonly DrawingVisual _visual = new();
    private readonly DashboardRimDrawing _drawing = new();
    private readonly AmbientBackdropClock _clock = new();
    private readonly AmbientParticleFrameGate _frames = new();
    private Window? _host;
    private ScrollViewer? _scroll;
    private bool _attached;
    private bool _rendering;
    private bool _redrawPending;
    private Rect _instrumentBounds;
    private double _targetScale = 1;
    private bool _targetAttached;
    private TranslateTransform? _translation;
    private RectangleGeometry? _viewportClip;

    public DashboardRimEffect()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(_visual, BitmapScalingMode.LowQuality);
        AddVisualChild(_visual);
        Loaded += LoadedEffect;
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += VisibilityChanged;
    }

    public OrbitSurfaceShape Shape { get => (OrbitSurfaceShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public FrameworkElement? TargetElement { get => (FrameworkElement?)GetValue(TargetElementProperty); set => SetValue(TargetElementProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Thickness RimThickness { get => (Thickness)GetValue(RimThicknessProperty); set => SetValue(RimThicknessProperty, value); }
    public double GlowOpacity { get => (double)GetValue(GlowOpacityProperty); set => SetValue(GlowOpacityProperty, value); }
    public bool ParticlesEnabled { get => (bool)GetValue(ParticlesEnabledProperty); set => SetValue(ParticlesEnabledProperty, value); }
    public bool IsAnimationEnabled { get => (bool)GetValue(IsAnimationEnabledProperty); set => SetValue(IsAnimationEnabledProperty, value); }
    internal bool HasRenderingSubscription => _rendering;
    internal bool HasLifecycleSubscriptions => _attached;
    internal bool HasTargetSubscription => _targetAttached;
    internal double SceneTimeSeconds => _clock.Seconds;
    internal Rect InstrumentBounds => TargetElement is not null ? _instrumentBounds :
        new Rect(12, 32, Math.Max(0, RenderSize.Width - 24), Math.Max(0, RenderSize.Height - 44));
    internal CornerRadius EffectiveCornerRadius => new(CornerRadius.TopLeft * _targetScale, CornerRadius.TopRight * _targetScale,
        CornerRadius.BottomRight * _targetScale, CornerRadius.BottomLeft * _targetScale);
    internal double EffectiveRimWidth => _targetScale * Math.Max(Math.Max(RimThickness.Left, RimThickness.Right), Math.Max(RimThickness.Top, RimThickness.Bottom));
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (TargetElement is not null) RefreshTarget();
        else Draw(new Rect(12, 32, Math.Max(0, finalSize.Width - 24), Math.Max(0, finalSize.Height - 44)));
        return finalSize;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Draw(InstrumentBounds);
        UpdatePlayback();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Draw(InstrumentBounds);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == OpacityProperty) UpdatePlayback();
    }

    private void LoadedEffect(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        AttachTarget();
        _host = Window.GetWindow(this);
        if (_host is not null)
        {
            _host.StateChanged += HostChanged;
            _host.IsVisibleChanged += HostVisibilityChanged;
            _host.Closed += HostClosed;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(TargetElement ?? this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { _scroll = scroll; break; }
        if (_scroll is not null) _scroll.ScrollChanged += Scrolled;
        SystemParameters.StaticPropertyChanged += EnvironmentChanged;
        RenderCapability.TierChanged += HostChanged;
        RefreshTarget();
        Draw(InstrumentBounds);
        UpdatePlayback();
    }

    private void Detach()
    {
        SetRunning(false);
        DetachTarget(TargetElement);
        if (!_attached) return;
        _attached = false;
        if (_host is not null)
        {
            _host.StateChanged -= HostChanged;
            _host.IsVisibleChanged -= HostVisibilityChanged;
            _host.Closed -= HostClosed;
            _host = null;
        }
        if (_scroll is not null) { _scroll.ScrollChanged -= Scrolled; _scroll = null; }
        SystemParameters.StaticPropertyChanged -= EnvironmentChanged;
        RenderCapability.TierChanged -= HostChanged;
    }

    private void HostChanged(object? sender, EventArgs e) => UpdatePlayback();
    private void HostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePlayback();
    private void HostClosed(object? sender, EventArgs e) => Detach();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attached)
        {
            if (IsVisible) { AttachTarget(); RefreshTarget(); }
            else DetachTarget(TargetElement);
        }
        UpdatePlayback();
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        var previousBounds = InstrumentBounds;
        RefreshTarget();
        if (previousBounds == InstrumentBounds) Draw(InstrumentBounds);
        UpdatePlayback();
    }
    private void EnvironmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) UpdatePlayback();
        else if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(UpdatePlayback));
    }

    private bool IsInViewport()
    {
        if (_scroll is null) return true;
        var element = TargetElement ?? this;
        // Accent resources can change after the dashboard tab leaves the visual tree.
        if (_scroll.FindCommonVisualAncestor(this) is null || !_scroll.IsAncestorOf(element)) return false;
        var bounds = element.TransformToAncestor(_scroll).TransformBounds(new Rect(element.RenderSize));
        return bounds.IntersectsWith(new Rect(_scroll.RenderSize));
    }

    private void UpdatePlayback() => SetRunning(_attached && IsVisible && _host?.IsVisible == true &&
        _host.WindowState != WindowState.Minimized && ParticlesEnabled && IsAnimationEnabled &&
        GlowOpacity > 0 && Opacity > 0 && Accent is SolidColorBrush { Color.A: > 0, Opacity: > 0 } &&
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast &&
        (RenderCapability.Tier >> 16) >= 1 && !DesignerProperties.GetIsInDesignMode(this) &&
        InstrumentBounds.Width > 0 && InstrumentBounds.Height > 0 && IsInViewport());

    private void SetRunning(bool running)
    {
        if (_rendering == running) return;
        _rendering = running;
        _clock.SetRunning(running, Timestamp());
        _frames.Reset();
        if (running) CompositionTarget.Rendering += RenderFrame;
        else CompositionTarget.Rendering -= RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame && _frames.ShouldDraw(frame.RenderingTime) && _clock.Advance(Timestamp()))
            Draw(InstrumentBounds);
    }

    private void Draw(Rect bounds)
    {
        using var context = _visual.RenderOpen();
        if (bounds.Width <= 0 || bounds.Height <= 0 || GlowOpacity <= 0 || Accent is not SolidColorBrush accent) return;
        if (_scroll is not null && !IsInViewport())
        {
            _redrawPending = true;
            return;
        }
        if (_scroll is not null)
        {
            var viewport = _scroll.TransformToVisual(this).TransformBounds(new Rect(_scroll.RenderSize));
            if (_scroll.VerticalOffset <= 0) { viewport.Y -= 32; viewport.Height += 32; }
            if (_viewportClip is null || _viewportClip.Rect != viewport)
            {
                _viewportClip = new RectangleGeometry(viewport);
                _viewportClip.Freeze();
            }
            context.PushClip(_viewportClip);
        }
        if (_translation is null || _translation.X != bounds.X || _translation.Y != bounds.Y)
        {
            _translation = new TranslateTransform(bounds.X, bounds.Y);
            _translation.Freeze();
        }
        context.PushTransform(_translation);
        _drawing.Draw(context, new Rect(bounds.Size), Shape, EffectiveCornerRadius, accent.Color, GlowOpacity * accent.Opacity,
            ParticlesEnabled, _clock.Seconds, VisualTreeHelper.GetDpi(this),
            EffectiveRimWidth);
        context.Pop();
        if (_scroll is not null) context.Pop();
        _redrawPending = false;
    }
    private static void TargetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var effect = (DashboardRimEffect)sender;
        effect.DetachTarget(e.OldValue as FrameworkElement);
        effect._instrumentBounds = Rect.Empty;
        effect._targetScale = 1;
        effect.AttachTarget();
        effect.RefreshTarget();
        effect.Draw(effect.InstrumentBounds);
        effect.UpdatePlayback();
    }
    private void AttachTarget()
    {
        if (!_attached || !IsVisible || _targetAttached || TargetElement is not { } target) return;
        target.LayoutUpdated += TargetLayoutUpdated;
        _targetAttached = true;
    }
    private void DetachTarget(FrameworkElement? target)
    {
        if (_targetAttached && target is not null) target.LayoutUpdated -= TargetLayoutUpdated;
        _targetAttached = false;
    }
    private void TargetLayoutUpdated(object? sender, EventArgs e) => RefreshTarget();
    internal void RefreshTarget()
    {
        if (TargetElement is not { } target) return;
        if (target.RenderSize.Width <= 0 || target.RenderSize.Height <= 0 || target.FindCommonVisualAncestor(this) is null)
        {
            _instrumentBounds = Rect.Empty;
            Draw(_instrumentBounds);
            UpdatePlayback();
            return;
        }
        var bounds = target.TransformToVisual(this).TransformBounds(new Rect(target.RenderSize));
        var scale = bounds.Width / target.RenderSize.Width;
        if (bounds == _instrumentBounds && Math.Abs(scale - _targetScale) < 0.0001 && !_redrawPending) return;
        _instrumentBounds = bounds;
        _targetScale = scale;
        Draw(bounds);
        UpdatePlayback();
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var effect = (DashboardRimEffect)sender;
        effect.Draw(effect.InstrumentBounds);
        effect.UpdatePlayback();
    }
    private static object CoerceOpacity(DependencyObject sender, object value) => double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0d;
    private static double Timestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
