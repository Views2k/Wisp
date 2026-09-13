using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wisp.App;

public sealed class AmbientBackdrop : FrameworkElement
{
    public static readonly DependencyProperty IsAnimationEnabledProperty = DependencyProperty.Register(
        nameof(IsAnimationEnabled), typeof(bool), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(true, OnAnimationChanged));

    public static readonly DependencyProperty IntensityProperty = DependencyProperty.Register(
        nameof(Intensity), typeof(double), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(1d, OnIntensityChanged, CoerceIntensity));

    public static readonly DependencyProperty ParticlesOnlyProperty = DependencyProperty.Register(
        nameof(ParticlesOnly), typeof(bool), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(false, OnPresentationChanged));

    public static readonly DependencyProperty ParticleColorProperty = DependencyProperty.Register(
        nameof(ParticleColor), typeof(Color), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(Colors.White, OnParticleColorChanged));

    public static readonly DependencyProperty IsPointerInteractionEnabledProperty = DependencyProperty.Register(
        nameof(IsPointerInteractionEnabled), typeof(bool), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(true, OnPointerInteractionChanged));

    internal const int ParticleFramesPerSecond = 60;

    private readonly DrawingVisual _frame = new();
    private readonly AmbientParticleSprites _sprites = new();
    private readonly AmbientParticleFrameGate _particleFrames = new();
    private AmbientBackdropRasterizer? _rasterizer;
    private WriteableBitmap? _bitmap;
    private readonly AmbientBackdropScene _scene = new();
    private readonly AmbientBackdropClock _clock = new();
    private readonly AmbientBackdropPointer _pointer = new();
    private DispatcherTimer _timer;
    private readonly bool _designMode;
    private Window? _host;
    private Size _viewport;
    private bool _loaded;
    private bool _environmentAttached;
    private bool _tickAttached;
    private bool _compositionAttached;
    private bool _pointerAttached;
    private bool _initialized;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(_frame, BitmapScalingMode.LowQuality);
        _designMode = DesignerProperties.GetIsInDesignMode(this);
        _timer = CreateTimer();
        AddVisualChild(_frame);
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

    public bool ParticlesOnly
    {
        get => (bool)GetValue(ParticlesOnlyProperty);
        set => SetValue(ParticlesOnlyProperty, value);
    }

    public Color ParticleColor
    {
        get => (Color)GetValue(ParticleColorProperty);
        set => SetValue(ParticleColorProperty, value);
    }

    public bool IsPointerInteractionEnabled
    {
        get => (bool)GetValue(IsPointerInteractionEnabledProperty);
        set => SetValue(IsPointerInteractionEnabledProperty, value);
    }

    internal bool IsAnimationRunning => _clock.IsRunning;
    internal bool HasAnimationTickSubscription => _tickAttached;
    internal bool HasEnvironmentSubscriptions => _environmentAttached;
    internal bool HasPointerSubscriptions => _pointerAttached;
    internal bool HasHostWindow => _host is not null;
    internal double SceneTimeSeconds => _clock.Seconds;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _frame,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        UpdateAnimationState();
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
            DetachPointer();
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
            UpdatePointerSubscription();
        }
    }

    private void UpdatePointerSubscription()
    {
        DetachPointer();
        _pointer.Reset();
        if (_host is not null && IsPointerInteractionEnabled && !ParticlesOnly)
        {
            _host.PreviewMouseMove += OnHostPointerMoved;
            _host.MouseLeave += OnHostPointerLeft;
            _pointerAttached = true;
        }
    }

    private void DetachPointer()
    {
        if (_host is not null && _pointerAttached)
        {
            _host.PreviewMouseMove -= OnHostPointerMoved;
            _host.MouseLeave -= OnHostPointerLeft;
        }
        _pointerAttached = false;
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
        Intensity = Intensity * Opacity * (ParticlesOnly ? ParticleColor.A / 255d : 1)
    };

    private void UpdateAnimationState() => SetAnimationRunning(PlaybackState().CanAnimate);

    private void SetAnimationRunning(bool running)
    {
        if (running)
        {
            if (_tickAttached)
                return;
            _clock.SetRunning(true, Timestamp());
            _particleFrames.Reset();
            if (ParticlesOnly)
            {
                CompositionTarget.Rendering += OnCompositionRendering;
                _compositionAttached = true;
            }
            else
            {
                _timer.Tick += OnTick;
                _timer.Start();
            }
            _tickAttached = true;
        }
        else
        {
            _timer.Stop();
            if (_tickAttached)
            {
                _timer.Tick -= OnTick;
                if (_compositionAttached)
                    CompositionTarget.Rendering -= OnCompositionRendering;
                _compositionAttached = false;
                _tickAttached = false;
            }
            _clock.SetRunning(false, Timestamp());
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (!PlaybackState().CanAnimate)
        {
            SetAnimationRunning(false);
            return;
        }
        if (e is RenderingEventArgs frame && _particleFrames.ShouldDraw(frame.RenderingTime))
            OnTick(sender, e);
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
            if (_pointerAttached)
                _pointer.Advance(_clock.LastStepSeconds);
            DrawScene(_clock.Seconds);
        }
    }

    private void DrawScene(double seconds)
    {
        if (_viewport.Width <= 0 || _viewport.Height <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (ParticlesOnly)
        {
            _scene.Update(_viewport.Width, _viewport.Height, seconds);
            using var particles = _frame.RenderOpen();
            _sprites.Draw(particles, _scene.Particles, _viewport, ParticleColor, Intensity, dpi);
            return;
        }
        var width = Math.Ceiling(_viewport.Width * dpi.DpiScaleX);
        var height = Math.Ceiling(_viewport.Height * dpi.DpiScaleY);
        var maximumPixels = AmbientBackdropRasterizer.MaximumPixels;
        var scale = Math.Min(1, Math.Sqrt(maximumPixels / (width * height)));
        var pixelWidth = Math.Max(1, (int)Math.Floor(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Floor(height * scale));
        if (_rasterizer is null || _rasterizer.Width != pixelWidth || _rasterizer.Height != pixelHeight)
        {
            _rasterizer = new AmbientBackdropRasterizer(pixelWidth, pixelHeight);
            _bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32, null);
        }
        _scene.Update(
            pixelWidth,
            pixelHeight,
            seconds,
            _pointer.Position,
            _pointer.Activity);
        _rasterizer.Render(_scene.Particles, Intensity, ParticleColor);
        _bitmap!.WritePixels(new Int32Rect(0, 0, pixelWidth, pixelHeight), _rasterizer.Pixels, pixelWidth * 4, 0);
        using var drawing = _frame.RenderOpen();
        drawing.DrawImage(_bitmap, new Rect(_viewport));
    }

    private void ApplyIntensity()
    {
        DrawScene(_clock.Seconds);
    }

    private static void OnAnimationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((AmbientBackdrop)sender).UpdateAnimationState();

    private DispatcherTimer CreateTimer() => new(DispatcherPriority.Render, Dispatcher)
    {
        Interval = TimeSpan.FromSeconds(1d / AmbientBackdropClock.FramesPerSecond)
    };

    private static void OnPresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AmbientBackdrop)sender;
        backdrop.SetAnimationRunning(false);
        backdrop._timer = backdrop.CreateTimer();
        backdrop._rasterizer = null;
        backdrop._bitmap = null;
        backdrop.UpdatePointerSubscription();
        backdrop.DrawScene(backdrop._clock.Seconds);
        backdrop.UpdateAnimationState();
    }

    private static void OnParticleColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AmbientBackdrop)sender;
        if (!backdrop.ParticlesOnly)
            return;
        backdrop.DrawScene(backdrop._clock.Seconds);
        backdrop.UpdateAnimationState();
    }

    private static void OnPointerInteractionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AmbientBackdrop)sender;
        backdrop.UpdatePointerSubscription();
        backdrop.DrawScene(backdrop._clock.Seconds);
    }

    private static void OnIntensityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var backdrop = (AmbientBackdrop)sender;
        backdrop.ApplyIntensity();
        backdrop.UpdateAnimationState();
    }

    private static object CoerceIntensity(DependencyObject sender, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0d;

    private static double Timestamp() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
