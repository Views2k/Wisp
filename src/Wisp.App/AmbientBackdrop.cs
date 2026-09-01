using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wisp.App;

public sealed class AmbientBackdrop : FrameworkElement
{
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(true, OnAnimationChanged));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(0.78, OnIntensityChanged, CoerceIntensity));

    private static readonly Brush[] FarParticleBrushes = CreateParticlePalette(AmbientParticleLayer.Far);
    private static readonly Brush[] MiddleParticleBrushes = CreateParticlePalette(AmbientParticleLayer.Middle);
    private static readonly Brush[] NearParticleBrushes = CreateParticlePalette(AmbientParticleLayer.Near);
    private static readonly Brush BackgroundBrush = CreateBackground();

    private readonly DrawingVisual _background = new();
    private readonly DrawingVisual _foreground = new();
    private readonly AmbientBackdropScene _scene = new();
    private readonly AmbientBackdropClock _clock = new();
    private readonly AmbientBackdropPointer _pointer = new();
    private readonly DispatcherTimer _timer;
    private readonly bool _designMode;
    private Window? _host;
    private Size _viewport;
    private bool _loaded;
    private bool _environmentAttached;
    private bool _tickAttached;
    private bool _pointerAttached;
    private bool _initialized;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _designMode = DesignerProperties.GetIsInDesignMode(this);
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1d / AmbientBackdropClock.FramesPerSecond)
        };
        AddVisualChild(_background);
        AddVisualChild(_foreground);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        _initialized = true;
        ApplyIntensity();
    }

    public bool IsAnimationEnabled
    {
        get => (bool)GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public double Intensity
    {
        get => (double)GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    internal bool IsAnimationRunning => _clock.IsRunning;
    internal bool HasAnimationTickSubscription => _tickAttached;
    internal bool HasEnvironmentSubscriptions => _environmentAttached;
    internal bool HasPointerSubscriptions => _pointerAttached;
    internal bool HasHostWindow => _host is not null;
    internal double SceneTimeSeconds => _clock.Seconds;

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _background,
        1 => _foreground,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        UpdateAnimationState();
        DrawBackground();
        DrawScene(_clock.Seconds);
        return finalSize;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_initialized && e.Property == OpacityProperty)
            UpdateAnimationState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SetHost(Window.GetWindow(this));
        if (!_environmentAttached && !_designMode)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            RenderCapability.TierChanged += OnRenderingTierChanged;
            _environmentAttached = true;
        }
        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        _loaded = false;
        SetAnimationRunning(false);
        SetHost(null);
        if (_environmentAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            RenderCapability.TierChanged -= OnRenderingTierChanged;
            _environmentAttached = false;
        }
    }

    private void SetHost(Window? host)
    {
        if (ReferenceEquals(_host, host))
            return;
        if (_host is not null)
        {
            _host.Activated -= OnHostChanged;
            _host.Deactivated -= OnHostChanged;
            _host.StateChanged -= OnHostChanged;
            _host.IsVisibleChanged -= OnVisibilityChanged;
            _host.Closed -= OnHostClosed;
            _host.PreviewMouseMove -= OnHostPointerMoved;
            _host.MouseLeave -= OnHostPointerLeft;
            _pointerAttached = false;
        }
        _pointer.Reset();
        _host = host;
        if (_host is not null)
        {
            _host.Activated += OnHostChanged;
            _host.Deactivated += OnHostChanged;
            _host.StateChanged += OnHostChanged;
            _host.IsVisibleChanged += OnVisibilityChanged;
            _host.Closed += OnHostClosed;
            _host.PreviewMouseMove += OnHostPointerMoved;
            _host.MouseLeave += OnHostPointerLeft;
            _pointerAttached = true;
        }
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (_host?.IsActive != true)
            _pointer.Leave();
        UpdateAnimationState();
    }
    private void OnHostClosed(object? sender, EventArgs e) => Detach();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateAnimationState();
    private void OnRenderingTierChanged(object? sender, EventArgs e) => RefreshEnvironment();

    private void OnHostPointerMoved(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null || _viewport.Width <= 0 || _viewport.Height <= 0)
            return;
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X >= _viewport.Width || point.Y >= _viewport.Height)
        {
            _pointer.Leave();
            return;
        }
        _pointer.Move(point.X / _viewport.Width, point.Y / _viewport.Height);
    }

    private void OnHostPointerLeft(object sender, MouseEventArgs e) => _pointer.Leave();

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
            RefreshEnvironment();
    }

    private void RefreshEnvironment()
    {
        if (Dispatcher.CheckAccess())
            UpdateAnimationState();
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateAnimationState));
    }

    private AmbientBackdropPlaybackState PlaybackState() => new()
    {
        IsLoaded = _loaded,
        IsVisible = IsVisible,
        HasHost = _host is not null,
        HostIsVisible = _host?.IsVisible == true,
        HostIsActive = _host?.IsActive == true,
        HostIsMinimized = _host?.WindowState == WindowState.Minimized,
        IsAnimationEnabled = IsAnimationEnabled,
        ClientAreaAnimation = SystemParameters.ClientAreaAnimation,
        HighContrast = SystemParameters.HighContrast,
        RenderingTier = RenderCapability.Tier >> 16,
        HasViewport = _viewport.Width > 0 && _viewport.Height > 0,
        IsDesignMode = _designMode,
        Intensity = Intensity * Opacity
    };

    private void UpdateAnimationState() => SetAnimationRunning(PlaybackState().CanAnimate);

    private void SetAnimationRunning(bool running)
    {
        if (running)
        {
            if (_tickAttached)
                return;
            _clock.SetRunning(true, Timestamp());
            _timer.Tick += OnTick;
            _tickAttached = true;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (_tickAttached)
            {
                _timer.Tick -= OnTick;
                _tickAttached = false;
            }
            _clock.SetRunning(false, Timestamp());
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!PlaybackState().CanAnimate)
        {
            SetAnimationRunning(false);
            return;
        }
        if (_clock.Advance(Timestamp()))
        {
            _pointer.Advance(_clock.LastStepSeconds);
            DrawScene(_clock.Seconds);
        }
    }

    private void DrawBackground()
    {
        using var drawing = _background.RenderOpen();
        if (_viewport.Width <= 0 || _viewport.Height <= 0)
            return;
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(_viewport));
    }

    private void DrawScene(double seconds)
    {
        _scene.Update(
            _viewport.Width,
            _viewport.Height,
            seconds,
            _pointer.Position,
            _pointer.Activity);
        using var drawing = _foreground.RenderOpen();
        foreach (var particle in _scene.Particles)
        {
            var palette = particle.Layer switch
            {
                AmbientParticleLayer.Far => FarParticleBrushes,
                AmbientParticleLayer.Middle => MiddleParticleBrushes,
                _ => NearParticleBrushes
            };
            drawing.DrawEllipse(palette[particle.Shade], null, Point(particle.Position),
                particle.Radius, particle.Radius);
        }
    }

    private void ApplyIntensity()
    {
        _background.Opacity = 1;
        _foreground.Opacity = Intensity;
    }

    private static void OnAnimationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((AmbientBackdrop)sender).UpdateAnimationState();

    private static void OnIntensityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AmbientBackdrop)sender;
        backdrop.ApplyIntensity();
        backdrop.UpdateAnimationState();
    }

    private static object CoerceIntensity(DependencyObject sender, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0d;

    private static Point Point(AmbientPoint point) => new(point.X, point.Y);
    private static double Timestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static Brush CreateBackground() => Freeze(new RadialGradientBrush
    {
        MappingMode = BrushMappingMode.RelativeToBoundingBox,
        Center = new Point(1.20, 0.42),
        GradientOrigin = new Point(1.20, 0.42),
        RadiusX = 1.55,
        RadiusY = 1.35,
        ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation,
        GradientStops = new GradientStopCollection
        {
            new(Color.FromRgb(12, 23, 26), 0),
            new(Color.FromRgb(11, 20, 24), 0.35),
            new(Color.FromRgb(10, 16, 21), 0.70),
            new(Color.FromRgb(9, 13, 18), 1)
        }
    });

    private static Brush[] CreateParticlePalette(AmbientParticleLayer layer)
    {
        var brushes = new Brush[AmbientBackdropScene.PaletteSize];
        for (var index = 0; index < brushes.Length; index++)
        {
            var light = index / (double)(brushes.Length - 1);
            var color = layer switch
            {
                AmbientParticleLayer.Far => Color.FromArgb(
                    (byte)(8 + light * 26), 64, 102, 99),
                AmbientParticleLayer.Middle => Color.FromArgb(
                    (byte)(12 + light * 48), 105, 130, 129),
                _ => Color.FromArgb(
                    (byte)(20 + light * 74), 138, 153, 153)
            };
            brushes[index] = layer == AmbientParticleLayer.Near
                ? Freeze(new SolidColorBrush(color))
                : Freeze(new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    GradientStops = new GradientStopCollection
                    {
                        new(color, 0),
                        new(Color.FromArgb((byte)(color.A * 0.58), color.R, color.G, color.B), 0.52),
                        new(Color.FromArgb(0, color.R, color.G, color.B), 1)
                    }
                });
        }
        return brushes;
    }

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
