using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wisp.App.DebugLogging;

namespace Wisp.App.Runs;

public sealed partial class LapReviewTrack3D : Grid
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
        SameScene(_preparedData, data) && SameOptionalScene(_preparedComparison, EffectiveComparison) &&
        (EffectiveComparison is null || _comparisonScene is not null && _comparisonPath.Content is not null) &&
        !IsPreparing && string.IsNullOrEmpty(StatusText);
    public event Action<int>? PointChosen;
    public event EventHandler? FocusRequested;
    public event EventHandler? ZoomOutAtOverview;

    private readonly Viewport3D _viewport = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly OrthographicCamera _camera = new() { NearPlaneDistance = .01, FarPlaneDistance = 20 };
    private readonly ModelVisual3D _path = new();
    private readonly ContactOverlay _contactOverlay;
    private readonly TextBlock _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private LapReviewTrackScene? _scene;
    private LapReviewPlotData? _preparedData, _requestedData;
    private CancellationTokenSource? _preparation;
    private Task _preparationTask = Task.CompletedTask;
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
        _path.Transform = _primaryPosition;
        _comparisonPath.Transform = _referencePosition;
        _viewport.Children.Add(_comparisonPath);
        _contactOverlay = new ContactOverlay(this) { IsHitTestVisible = false, ClipToBounds = true };
        _contactOverlay.SetResourceReference(ContactOverlay.OutlineProperty, "InputBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Children.Add(_viewport);
        Children.Add(_contactOverlay);
        Children.Add(_status);
        Loaded += (_, _) => RefreshScene();
        Unloaded += (_, _) => { CancelPreparation(); StopCameraMotion(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshScene(); else { CancelPreparation(); StopCameraMotion(); } };
        SizeChanged += (_, _) => UpdateCamera();
        UpdateCamera();
    }

    public void ResetView()
    {
        StopCameraMotion();
        _updatingCameraMotion = true;
        _target = _arrangement?.Overview.Target ?? _scene?.Fit.Target ?? new();
        SetCurrentValue(YawProperty, _arrangement?.Overview.Yaw ?? _scene?.Fit.Yaw ?? 0d);
        SetCurrentValue(PitchProperty, LapReviewTrackFit.DefaultPitch);
        SetCurrentValue(RollProperty, 0d);
        SetCurrentValue(ZoomFactorProperty, 1d);
        if (IsComparison && _arrangement is { } arrangement) _target = arrangement.ProjectedFit(Yaw, Pitch, Roll).Target;
        _updatingCameraMotion = false;
        SetValue(FocusedLapPropertyKey, 0);
        UpdateCamera();
    }

    public void ZoomBy(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        StopCameraMotion();
        var previous = ZoomFactor;
        var next = Math.Clamp(previous * factor, .2, 50);
        if (factor < 1 && IsComparison && _arrangement is { } arrangement)
        {
            var fraction = previous > 1 ? Math.Clamp((next - 1) / (previous - 1), 0, 1) : 0;
            var overview = arrangement.ProjectedFit(Yaw, Pitch, Roll).Target;
            _target = overview + (_target - overview) * fraction;
            if (next <= 1) SetValue(FocusedLapPropertyKey, 0);
        }
        SetCurrentValue(ZoomFactorProperty, next);
        UpdateCamera();
        if (factor < 1 && ZoomFactor <= 1) ZoomOutAtOverview?.Invoke(this, EventArgs.Empty);
    }

    public void PanBy(double horizontalPixels, double verticalPixels)
    {
        if (!double.IsFinite(horizontalPixels) || !double.IsFinite(verticalPixels)) return;
        StopCameraMotion();
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
    private static void CameraChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var track = (LapReviewTrack3D)sender;
        if (!track._updatingCameraMotion) track.StopCameraMotion();
        track.UpdateCamera();
    }
    private static void DataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var track = (LapReviewTrack3D)sender;
        var previous = (LapReviewPlotData?)args.OldValue; var next = (LapReviewPlotData?)args.NewValue;
        if (track.IsReady)
        {
            track.UpdateCursor();
            return;
        }
        track.StopCameraMotion();
        if (!ReferenceEquals(previous?.Lap, next?.Lap))
        {
            if (args.Property == DataProperty) { track._scene = null; track._preparedData = null; }
            else { track._comparisonScene = null; track._preparedComparison = null; }
            track._resetOverview = true;
        }
        track.RefreshScene();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ShowContactsProperty) _contactOverlay?.InvalidateVisual();
        if (e.Property == FocusCues.ShowKeyboardFocusProperty) InvalidateVisual();
    }

    private static bool SameScene(LapReviewPlotData? a, LapReviewPlotData b) => a is not null &&
        ReferenceEquals(a.Lap, b.Lap) && ReferenceEquals(a.Reference, b.Reference) && ReferenceEquals(a.Comparison, b.Comparison) &&
        a.Channel == b.Channel && a.SpeedUnit == b.SpeedUnit && a.TorqueUnit == b.TorqueUnit &&
        a.ShowReferencePath == b.ShowReferencePath && a.ColorRangeOverride == b.ColorRangeOverride &&
        a.SectionStart == b.SectionStart && a.SectionEnd == b.SectionEnd && a.Wheel == b.Wheel && a.TemperatureUnit == b.TemperatureUnit;

    private void RefreshScene()
    {
        _contactOverlay.InvalidateVisual();
        if (EffectiveComparison is null) { SetValue(IsComparisonPropertyKey, false); SetValue(FocusedLapPropertyKey, 0); }
        if ((!IsLoaded || !IsVisible) && !_testPreparation) return;
        if (Data is not { Lap.Points.Length: > 1 } data)
        {
            CancelPreparation(); _scene = _comparisonScene = null; _preparedData = _preparedComparison = null;
            _arrangement = null; _path.Content = _comparisonPath.Content = null;
            SetValue(IsComparisonPropertyKey, false); SetValue(FocusedLapPropertyKey, 0);
            SetStatus("No recorded lap positions", false); return;
        }
        var other = EffectiveComparison;
        if (_scene is not null && SameScene(_preparedData, data) && SameOptionalScene(_preparedComparison, other))
        {
            CancelPreparation(); _path.Content = _scene.Model; _comparisonPath.Content = other is null ? null : _comparisonScene?.Model;
            SetStatus(string.Empty, false); UpdateCursor(); return;
        }
        if (_preparation is not null && SameScene(_requestedData, data) && SameOptionalScene(_requestedComparison, other)) return;
        CancelPreparation();
        var cancellation = new CancellationTokenSource(); _preparation = cancellation;
        _requestedData = data;
        _requestedComparison = other;
        _path.Content = _comparisonPath.Content = null;
        SetStatus("Preparing 3D map…", true);
        _preparationTask = PrepareAsync(data, other, cancellation);
    }

    private async Task PrepareAsync(LapReviewPlotData data, LapReviewPlotData? other, CancellationTokenSource cancellation)
    {
        try
        {
            var cachedA = SameScene(_preparedData, data) ? _scene : null;
            var cachedB = other is not null && SameScene(_preparedComparison, other) ? _comparisonScene : null;
            var result = await Task.Run(() =>
            {
                var bounds = LapReviewTrackBounds.From(data, other?.Lap);
                var buildA = cachedA?.Bounds != bounds;
                var buildB = other is not null && cachedB?.Bounds != bounds;
                var scene = buildA ? LapReviewTrackGeometry.Build(data, bounds, cancellation.Token) : cachedA!;
                var comparison = other is null ? null : buildB ? LapReviewTrackGeometry.Build(other, bounds, cancellation.Token) : cachedB;
                return (scene, comparison, arrangement: LapReviewTrackArrangement.Create(data, other, bounds), builds: (buildA ? 1 : 0) + (buildB ? 1 : 0));
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_preparation, cancellation)) return;
            var reset = _arrangement is null || _resetOverview;
            _scene = result.scene; _comparisonScene = result.comparison;
            _preparedData = data; _preparedComparison = other; _arrangement = result.arrangement;
            _primaryPosition.OffsetX = _arrangement.PrimaryOffset.X; _primaryPosition.OffsetY = _arrangement.PrimaryOffset.Y; _primaryPosition.OffsetZ = _arrangement.PrimaryOffset.Z;
            _referencePosition.OffsetX = _arrangement.ReferenceOffset.X; _referencePosition.OffsetY = _arrangement.ReferenceOffset.Y; _referencePosition.OffsetZ = _arrangement.ReferenceOffset.Z;
            _path.Content = _scene.Model; _comparisonPath.Content = _comparisonScene?.Model; SceneBuildCount += result.builds;
            SetValue(IsComparisonPropertyKey, other is not null);
            _resetOverview = false;
            if (reset) ResetView();
            SetStatus(string.Empty, false); UpdateCursor(); UpdateCamera();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_preparation, cancellation)) return;
            _scene = _comparisonScene = null; _preparedData = _preparedComparison = null; _path.Content = _comparisonPath.Content = null;
            _arrangement = null; SetValue(IsComparisonPropertyKey, false);
            HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.LapReviewFailed, exception.HResult);
            SetStatus("The 3D map could not be prepared. Switch to 2D or reopen this lap.", false);
        }
        finally
        {
            if (ReferenceEquals(_preparation, cancellation)) { _preparation = null; _requestedData = _requestedComparison = null; }
            cancellation.Dispose();
        }
    }

    private void CancelPreparation()
    {
        _preparation?.Cancel(); _preparation = null; _requestedData = _requestedComparison = null;
        if (IsPreparing) SetStatus(string.Empty, false);
    }

    private void SetStatus(string message, bool preparing)
    {
        SetValue(IsPreparingPropertyKey, preparing); SetValue(StatusTextPropertyKey, message);
        _status.Text = message; _status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _contactOverlay.InvalidateVisual();
    }

    private void UpdateCursor() => _contactOverlay.InvalidateVisual();

    private void UpdateCamera()
    {
        if (_camera is null) return;
        var frame = LapReviewCameraFrame.From(Yaw, Pitch, Roll);
        _camera.Position = _target - frame.Forward * 4;
        _camera.LookDirection = frame.Forward;
        _camera.UpDirection = frame.Up;
        _camera.Width = (CurrentOverviewFit?.Width(ActualWidth / Math.Max(1, ActualHeight)) ?? _scene?.Fit.Width(ActualWidth / Math.Max(1, ActualHeight)) ?? 1.5) / ZoomFactor;
        _contactOverlay?.InvalidateVisual();
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
        return LapReviewCameraFrame.From(Yaw, Pitch, Roll).Project(_scene.Bounds.Normalize(lap.Points[index].Position) + (_arrangement?.PrimaryOffset ?? new()), _target, _camera.Width, RenderSize);
    }

    internal void Choose(Point click)
    {
        if (!IsReady || Data?.Lap is not { } lap || _scene is null) return;
        var frame = LapReviewCameraFrame.From(Yaw, Pitch, Roll);
        var closest = double.PositiveInfinity; var index = -1; var selectedLap = 1;
        void Search(Wisp.Core.Runs.LapReviewLap candidate, LapReviewTrackScene scene, Vector3D offset, int number)
        {
            for (var i = 0; i < candidate.Points.Length; i++)
            {
                if (!LapReviewTrackBounds.IsFinite(candidate.Points[i].Position)) continue;
                var p = frame.Project(scene.Bounds.Normalize(candidate.Points[i].Position) + offset, _target, _camera.Width, RenderSize);
                var distance = (p - click).LengthSquared;
                if (distance < closest) { closest = distance; index = i; selectedLap = number; }
            }
        }
        Search(lap, _scene, _arrangement?.PrimaryOffset ?? new(), 1);
        if (_comparisonScene is { } referenceScene && EffectiveComparison?.Lap is { } reference)
            Search(reference, referenceScene, _arrangement?.ReferenceOffset ?? new(), 2);
        if (index < 0) return;
        if (selectedLap == 2) ReferencePointChosen?.Invoke(index); else PointChosen?.Invoke(index);
        if (IsComparison && selectedLap != FocusedLap) FocusLap(selectedLap);
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
        if (!_dragMoved && e.ChangedButton == MouseButton.Left)
        {
            Choose(e.GetPosition(this));
            if (IsReady) FocusRequested?.Invoke(this, EventArgs.Empty);
        }
        ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomBy(Math.Pow(1.15, e.Delta / 120d)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.R) { ResetView(); e.Handled = true; return; }
        if (e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.2); e.Handled = true; return; }
        if (e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1 / 1.2); e.Handled = true; return; }
        var active = FocusedLap == 2 ? EffectiveComparison : Data;
        if (active?.Lap is not { Points.Length: > 0 } lap) return;
        var next = e.Key switch { Key.Left or Key.Down => active.Cursor - 1, Key.Right or Key.Up => active.Cursor + 1, Key.Home => 0, Key.End => lap.Points.Length - 1, _ => -1 };
        if (next < 0 && e.Key is not Key.Left and not Key.Down) return;
        var selected = Math.Clamp(next, 0, lap.Points.Length - 1);
        if (FocusedLap == 2) ReferencePointChosen?.Invoke(selected); else PointChosen?.Invoke(selected);
        e.Handled = true;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (IsKeyboardFocused && FocusCues.GetShowKeyboardFocus(this) && ActualWidth > 2 && ActualHeight > 2)
            dc.DrawRectangle(null, new Pen(TryFindResource("TextBrush") as Brush ?? Brushes.White, 1), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }

    private sealed class ContactOverlay(LapReviewTrack3D owner) : FrameworkElement
    {
        internal static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(nameof(Outline), typeof(Brush), typeof(ContactOverlay),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
        private Brush Outline => (Brush)GetValue(OutlineProperty);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (!owner.IsReady || owner._scene is not { } scene || owner.Data is not { } data) return;
            DrawLap(dc, data, scene, owner._arrangement?.PrimaryOffset ?? new(), owner.IsComparison ? "A" : null, owner._arrangement?.PrimaryFit);
            if (owner._comparisonScene is { } referenceScene && owner.EffectiveComparison is { } reference)
                DrawLap(dc, reference, referenceScene, owner._arrangement?.ReferenceOffset ?? new(), "B", owner._arrangement?.ReferenceFit);
        }

        private void DrawLap(DrawingContext dc, LapReviewPlotData data, LapReviewTrackScene scene, Vector3D offset, string? label, LapReviewTrackFit? fit)
        {
            if (data.Lap is not { } lap) return;
            var camera = LapReviewCameraFrame.From(owner.Yaw, owner.Pitch, owner.Roll);
            Point? ProjectPosition(Wisp.Core.LapPosition position)
            {
                if (!LapReviewTrackBounds.IsFinite(position)) return null;
                var point = camera.Project(scene.Bounds.Normalize(position) + offset, owner._target, owner._camera.Width, RenderSize);
                return point.X < -10 || point.Y < -10 || point.X > ActualWidth + 10 || point.Y > ActualHeight + 10 ? null : point;
            }
            Point? Project(int index)
            {
                return (uint)index >= (uint)lap.Points.Length ? null : ProjectPosition(lap.Points[index].Position);
            }
            if (Project(0) is { } start)
            {
                dc.DrawEllipse(null, new Pen(Outline, 4), start, 6, 6);
                dc.DrawEllipse(null, new Pen(LapReviewPalette.StartBrush, 2), start, 6, 6);
            }
            var cursorAtSample = (uint)data.Cursor < (uint)lap.Points.Length &&
                (data.CursorPositionOverride is null || data.CursorPositionOverride == lap.Points[data.Cursor].Position);
            var cursorContact = cursorAtSample && owner.ShowContacts && data.Contacts?.Any(contact => contact.PointIndex == data.Cursor) == true;
            var cursorPosition = data.CursorPositionOverride is { } position ? ProjectPosition(position) : Project(data.Cursor);
            if (cursorPosition is { } cursor)
                dc.DrawEllipse(cursorContact ? null : Brushes.White,
                    new Pen(cursorContact ? Brushes.White : Outline, 1.5), cursor, cursorContact ? 10 : 4, cursorContact ? 10 : 4);
            if (owner.ShowContacts && data.Contacts is { } contacts)
                foreach (var contact in contacts)
                    if (Project(contact.PointIndex) is { } anchor)
                        LapReviewPalette.DrawContact(dc, anchor, Outline, cursorAtSample && contact.PointIndex == data.Cursor);
            if (label is not null && fit is { } labelFit)
            {
                var initialFrame = LapReviewCameraFrame.From(labelFit.Yaw, LapReviewTrackFit.DefaultPitch, 0);
                var labelPosition = labelFit.Target + initialFrame.Up * (labelFit.VerticalSpan / 2 + .025);
                var anchor = camera.Project(labelPosition, owner._target, owner._camera.Width, RenderSize);
                if (anchor.X < 0 || anchor.X > ActualWidth || anchor.Y < -10 || anchor.Y > ActualHeight + 10) return;
                anchor.Y = Math.Clamp(anchor.Y, 12, Math.Max(12, ActualHeight - 12));
                var brush = owner.TryFindResource("TextBrush") as Brush ?? Brushes.White;
                var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawRoundedRectangle(Outline, null, new Rect(anchor.X - 11, anchor.Y - 11, 22, 22), 5, 5);
                dc.DrawText(text, new Point(anchor.X - text.Width / 2, anchor.Y - text.Height / 2));
            }
        }
    }
}
