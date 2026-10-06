using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wisp.App.DebugLogging;

namespace Wisp.App.Runs;

public sealed class LapReviewTrack3D : Grid
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(LapReviewPlotData), typeof(LapReviewTrack3D),
        new FrameworkPropertyMetadata(null, DataChanged));
    public static readonly DependencyProperty YawProperty = AngleProperty(nameof(Yaw), 0, false);
    public static readonly DependencyProperty PitchProperty = AngleProperty(nameof(Pitch), LapReviewTrackFit.DefaultPitch, true);
    public static readonly DependencyProperty RollProperty = AngleProperty(nameof(Roll), 0, false);
    public static readonly DependencyProperty ZoomFactorProperty = DependencyProperty.Register(nameof(ZoomFactor), typeof(double), typeof(LapReviewTrack3D),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, CameraChanged,
            (_, value) => double.IsFinite((double)value) ? Math.Clamp((double)value, .2, 50) : 1d));
    public static readonly DependencyProperty ShowContactsProperty = DependencyProperty.Register(nameof(ShowContacts), typeof(bool), typeof(LapReviewTrack3D),
        new FrameworkPropertyMetadata(true));
    private static readonly DependencyPropertyKey IsPreparingPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsPreparing), typeof(bool), typeof(LapReviewTrack3D), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPreparingProperty = IsPreparingPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey StatusTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(StatusText), typeof(string), typeof(LapReviewTrack3D), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StatusTextProperty = StatusTextPropertyKey.DependencyProperty;

    public LapReviewPlotData? Data { get => (LapReviewPlotData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Yaw { get => (double)GetValue(YawProperty); set => SetValue(YawProperty, value); }
    public double Pitch { get => (double)GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    public double Roll { get => (double)GetValue(RollProperty); set => SetValue(RollProperty, value); }
    public double ZoomFactor { get => (double)GetValue(ZoomFactorProperty); set => SetValue(ZoomFactorProperty, value); }
    public bool ShowContacts { get => (bool)GetValue(ShowContactsProperty); set => SetValue(ShowContactsProperty, value); }
    public bool IsPreparing => (bool)GetValue(IsPreparingProperty);
    public string StatusText => (string)GetValue(StatusTextProperty);
    public bool IsReady => _scene is not null && _path.Content is not null && Data is { } data &&
        SameScene(_preparedData, data) && _preparedContacts == ShowContacts && !IsPreparing && string.IsNullOrEmpty(StatusText);
    public event Action<int>? PointChosen;

    private readonly Viewport3D _viewport = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly OrthographicCamera _camera = new() { NearPlaneDistance = .01, FarPlaneDistance = 20 };
    private readonly ModelVisual3D _path = new();
    private readonly ModelVisual3D _cursor = new();
    private readonly TranslateTransform3D _cursorPosition = new();
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private LapReviewTrackScene? _scene;
    private LapReviewPlotData? _preparedData, _requestedData;
    private CancellationTokenSource? _preparation;
    private Task _preparationTask = Task.CompletedTask;
    private bool _preparedContacts, _requestedContacts;
    private Point3D _target;
    private Point _dragStart, _dragPrevious;
    private bool _dragMoved;
    private MouseButton _dragButton;
    private bool _testPreparation;
    internal int SceneBuildCount { get; private set; }
    internal int PreparedSegmentCount => _scene?.SegmentCount ?? 0;

    public LapReviewTrack3D()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        _viewport.Camera = _camera;
        var ambient = new AmbientLight(Colors.White); ambient.Freeze();
        _viewport.Children.Add(new ModelVisual3D { Content = ambient });
        _viewport.Children.Add(_path);
        _cursor.Content = LapReviewTrackGeometry.CursorModel();
        _cursor.Transform = _cursorPosition;
        _viewport.Children.Add(_cursor);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Children.Add(_viewport);
        Children.Add(_status);
        Loaded += (_, _) => RefreshScene();
        Unloaded += (_, _) => CancelPreparation();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshScene(); else CancelPreparation(); };
        SizeChanged += (_, _) => UpdateCamera();
        UpdateCamera();
    }

    public void ResetView()
    {
        _target = _scene?.Fit.Target ?? new();
        SetCurrentValue(YawProperty, _scene?.Fit.Yaw ?? 0d);
        SetCurrentValue(PitchProperty, LapReviewTrackFit.DefaultPitch);
        SetCurrentValue(RollProperty, 0d);
        SetCurrentValue(ZoomFactorProperty, 1d);
        UpdateCamera();
    }

    public void ZoomBy(double factor)
    {
        if (double.IsFinite(factor) && factor > 0) SetCurrentValue(ZoomFactorProperty, ZoomFactor * factor);
    }

    public void PanBy(double horizontalPixels, double verticalPixels)
    {
        if (!double.IsFinite(horizontalPixels) || !double.IsFinite(verticalPixels)) return;
        var frame = LapReviewCameraFrame.From(Yaw, Pitch, Roll);
        var scale = _camera.Width / Math.Max(1, ActualWidth);
        var target = _target - frame.Right * horizontalPixels * scale + frame.Up * verticalPixels * scale;
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Z)) return;
        _target = target;
        UpdateCamera();
    }

    private static DependencyProperty AngleProperty(string name, double initial, bool pitch) => DependencyProperty.Register(name, typeof(double), typeof(LapReviewTrack3D),
        new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, CameraChanged,
            (_, value) => CoerceAngle((double)value, initial, pitch)));
    private static double CoerceAngle(double value, double initial, bool pitch)
    {
        if (!double.IsFinite(value)) return initial;
        if (pitch) return Math.Clamp(value, -90, 90);
        var normalized = value % 360;
        return normalized > 180 ? normalized - 360 : normalized < -180 ? normalized + 360 : normalized;
    }
    private static void CameraChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((LapReviewTrack3D)sender).UpdateCamera();
    private static void DataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var track = (LapReviewTrack3D)sender;
        var previous = (LapReviewPlotData?)args.OldValue; var next = (LapReviewPlotData?)args.NewValue;
        if (!ReferenceEquals(previous?.Lap, next?.Lap))
        {
            track._scene = null; track._preparedData = null;
            track.ResetView();
        }
        track.RefreshScene();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ShowContactsProperty) RefreshScene();
        if (e.Property == FocusCues.ShowKeyboardFocusProperty) InvalidateVisual();
    }

    private static bool SameScene(LapReviewPlotData? a, LapReviewPlotData b) => a is not null &&
        ReferenceEquals(a.Lap, b.Lap) && ReferenceEquals(a.Reference, b.Reference) && ReferenceEquals(a.Comparison, b.Comparison) &&
        ReferenceEquals(a.Contacts, b.Contacts) && a.Channel == b.Channel && a.SpeedUnit == b.SpeedUnit && a.TorqueUnit == b.TorqueUnit &&
        a.SectionStart == b.SectionStart && a.SectionEnd == b.SectionEnd && a.Wheel == b.Wheel && a.TemperatureUnit == b.TemperatureUnit;

    private void RefreshScene()
    {
        if ((!IsLoaded || !IsVisible) && !_testPreparation) return;
        if (Data is not { Lap.Points.Length: > 1 } data)
        {
            CancelPreparation(); _scene = null; _preparedData = null; _path.Content = null; _cursor.Content = null;
            SetStatus("No recorded lap positions", false); return;
        }
        if (_scene is not null && SameScene(_preparedData, data) && _preparedContacts == ShowContacts)
        { CancelPreparation(); _path.Content = _scene.Model; SetStatus(string.Empty, false); UpdateCursor(); return; }
        if (_preparation is not null && SameScene(_requestedData, data) && _requestedContacts == ShowContacts) return;
        CancelPreparation();
        var cancellation = new CancellationTokenSource(); _preparation = cancellation;
        _requestedData = data; _requestedContacts = ShowContacts;
        _path.Content = null; _cursor.Content = null;
        SetStatus("Preparing 3D map…", true);
        _preparationTask = PrepareAsync(data, ShowContacts, cancellation);
    }

    private async Task PrepareAsync(LapReviewPlotData data, bool showContacts, CancellationTokenSource cancellation)
    {
        try
        {
            var scene = await Task.Run(() => LapReviewTrackGeometry.Build(data, showContacts, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_preparation, cancellation)) return;
            var firstScene = _scene is null;
            _scene = scene; _preparedData = data; _preparedContacts = showContacts;
            if (firstScene) ResetView();
            _path.Content = scene.Model; SceneBuildCount++;
            SetStatus(string.Empty, false); UpdateCursor(); UpdateCamera();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_preparation, cancellation)) return;
            _scene = null; _preparedData = null; _path.Content = null; _cursor.Content = null;
            HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.LapReviewFailed, exception.HResult);
            SetStatus("The 3D map could not be prepared. Switch to 2D or reopen this lap.", false);
        }
        finally
        {
            if (ReferenceEquals(_preparation, cancellation)) { _preparation = null; _requestedData = null; }
            cancellation.Dispose();
        }
    }

    private void CancelPreparation()
    {
        _preparation?.Cancel(); _preparation = null; _requestedData = null;
        if (IsPreparing) SetStatus(string.Empty, false);
    }

    private void SetStatus(string message, bool preparing)
    {
        SetValue(IsPreparingPropertyKey, preparing); SetValue(StatusTextPropertyKey, message);
        _status.Text = message; _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCursor()
    {
        if (_scene is null || Data is not { Lap.Points.Length: > 0 } data) { _cursor.Content = null; return; }
        var position = data.Lap.Points[Math.Clamp(data.Cursor, 0, data.Lap.Points.Length - 1)].Position;
        if (!LapReviewTrackBounds.IsFinite(position)) { _cursor.Content = null; return; }
        _cursor.Content ??= LapReviewTrackGeometry.CursorModel();
        var p = _scene.Bounds.Normalize(position);
        _cursorPosition.OffsetX = p.X; _cursorPosition.OffsetY = p.Y; _cursorPosition.OffsetZ = p.Z;
    }

    private void UpdateCamera()
    {
        if (_camera is null) return;
        var frame = LapReviewCameraFrame.From(Yaw, Pitch, Roll);
        _camera.Position = _target - frame.Forward * 4;
        _camera.LookDirection = frame.Forward;
        _camera.UpDirection = frame.Up;
        _camera.Width = (_scene?.Fit.Width(ActualWidth / Math.Max(1, ActualHeight)) ?? 1.5) / ZoomFactor;
    }

    internal Task PrepareForTestAsync()
    {
        _testPreparation = true;
        RefreshScene();
        return _preparationTask;
    }

    internal Point ProjectPoint(int index)
    {
        if (_scene is null || Data?.Lap is not { } lap || (uint)index >= (uint)lap.Points.Length) return new(double.NaN, double.NaN);
        return LapReviewCameraFrame.From(Yaw, Pitch, Roll).Project(_scene.Bounds.Normalize(lap.Points[index].Position), _target, _camera.Width, RenderSize);
    }

    private void Choose(Point click)
    {
        if (!IsReady || Data?.Lap is not { } lap || _scene is null) return;
        var frame = LapReviewCameraFrame.From(Yaw, Pitch, Roll);
        var closest = double.PositiveInfinity; var index = -1;
        for (var i = 0; i < lap.Points.Length; i++)
        {
            if (!LapReviewTrackBounds.IsFinite(lap.Points[i].Position)) continue;
            var p = frame.Project(_scene.Bounds.Normalize(lap.Points[i].Position), _target, _camera.Width, RenderSize);
            var distance = (p - click).LengthSquared;
            if (distance < closest) { closest = distance; index = i; }
        }
        if (index >= 0) PointChosen?.Invoke(index);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not MouseButton.Left and not MouseButton.Right and not MouseButton.Middle) return;
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        _dragStart = _dragPrevious = e.GetPosition(this); _dragMoved = false; _dragButton = e.ChangedButton;
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;
        var current = e.GetPosition(this); var delta = current - _dragPrevious;
        _dragMoved |= (current - _dragStart).LengthSquared > 9;
        if (!_dragMoved) return;
        _dragPrevious = current;
        if (_dragButton is MouseButton.Middle or MouseButton.Right || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) PanBy(delta.X, delta.Y);
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) SetCurrentValue(RollProperty, Roll + delta.X * .4);
        else { SetCurrentValue(YawProperty, Yaw - delta.X * .35); SetCurrentValue(PitchProperty, Pitch + delta.Y * .35); }
        e.Handled = true;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsMouseCaptured || e.ChangedButton != _dragButton) return;
        if (!_dragMoved && e.ChangedButton == MouseButton.Left) Choose(e.GetPosition(this));
        ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomBy(Math.Pow(1.15, e.Delta / 120d)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.R) { ResetView(); e.Handled = true; return; }
        if (e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.2); e.Handled = true; return; }
        if (e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1 / 1.2); e.Handled = true; return; }
        if (Data?.Lap is not { Points.Length: > 0 } lap) return;
        var next = e.Key switch { Key.Left or Key.Down => Data.Cursor - 1, Key.Right or Key.Up => Data.Cursor + 1, Key.Home => 0, Key.End => lap.Points.Length - 1, _ => -1 };
        if (next < 0 && e.Key is not Key.Left and not Key.Down) return;
        PointChosen?.Invoke(Math.Clamp(next, 0, lap.Points.Length - 1)); e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (IsKeyboardFocused && FocusCues.GetShowKeyboardFocus(this) && ActualWidth > 2 && ActualHeight > 2)
            dc.DrawRectangle(null, new Pen(TryFindResource("TextBrush") as Brush ?? Brushes.White, 1), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }
}
