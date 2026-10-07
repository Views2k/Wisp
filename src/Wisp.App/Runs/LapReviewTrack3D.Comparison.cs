using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Wisp.App.Runs;

public sealed partial class LapReviewTrack3D
{
    public static readonly DependencyProperty ComparisonDataProperty = DependencyProperty.Register(nameof(ComparisonData),
        typeof(LapReviewPlotData), typeof(LapReviewTrack3D), new FrameworkPropertyMetadata(null, DataChanged));
    private static readonly DependencyPropertyKey IsComparisonPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsComparison),
        typeof(bool), typeof(LapReviewTrack3D), new PropertyMetadata(false));
    public static readonly DependencyProperty IsComparisonProperty = IsComparisonPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey FocusedLapPropertyKey = DependencyProperty.RegisterReadOnly(nameof(FocusedLap),
        typeof(int), typeof(LapReviewTrack3D), new PropertyMetadata(0));
    public static readonly DependencyProperty FocusedLapProperty = FocusedLapPropertyKey.DependencyProperty;

    public LapReviewPlotData? ComparisonData { get => (LapReviewPlotData?)GetValue(ComparisonDataProperty); set => SetValue(ComparisonDataProperty, value); }
    public bool IsComparison => (bool)GetValue(IsComparisonProperty);
    public int FocusedLap => (int)GetValue(FocusedLapProperty);
    public event Action<int>? ReferencePointChosen;

    private readonly ModelVisual3D _comparisonPath = new();
    private readonly TranslateTransform3D _primaryPosition = new(), _referencePosition = new();
    private LapReviewTrackScene? _comparisonScene;
    private LapReviewPlotData? _preparedComparison, _requestedComparison;
    private LapReviewTrackArrangement? _arrangement;
    private bool _resetOverview, _cameraMotionActive, _updatingCameraMotion;
    private long _cameraMotionStarted;
    private Point3D _motionStartTarget, _motionEndTarget;
    private double _motionStartZoom, _motionEndZoom;
    private LapReviewPlotData? EffectiveComparison => Data?.HasDistinctReference == true && ComparisonData is { Lap.Points.Length: > 1 } data ? data : null;
    internal int PreparedReferenceSegmentCount => _comparisonScene?.SegmentCount ?? 0;
    internal bool HasBothPreparedModels => _path.Content is not null && _comparisonPath.Content is not null;
    internal bool IsCameraMotionActive => _cameraMotionActive;
    private LapReviewTrackFit? CurrentOverviewFit => IsComparison && _arrangement is { } arrangement
        ? arrangement.ProjectedFit(Yaw, Pitch, Roll) : _arrangement?.Overview;

    private static bool SameOptionalScene(LapReviewPlotData? a, LapReviewPlotData? b) =>
        a is null ? b is null : b is not null && SameScene(a, b);

    public void FocusLap(int lap)
    {
        if (!IsReady || !IsComparison || _arrangement is not { } arrangement || lap is < 1 or > 2) return;
        var selected = arrangement.ProjectedFit(Yaw, Pitch, Roll, lap);
        SetValue(FocusedLapPropertyKey, lap);
        var aspect = ActualWidth / Math.Max(1, ActualHeight);
        AnimateCamera(arrangement.ProjectedFit(Yaw, Pitch, Roll, lap).Target,
            Math.Clamp(arrangement.ProjectedFit(Yaw, Pitch, Roll).Width(aspect) / selected.Width(aspect), 1.8, 50));
    }

    public void ShowAll()
    {
        if (_arrangement is not { } arrangement) return;
        SetValue(FocusedLapPropertyKey, 0);
        AnimateCamera(IsComparison ? arrangement.ProjectedFit(Yaw, Pitch, Roll).Target : arrangement.Overview.Target, 1);
    }

    private void AnimateCamera(Point3D target, double zoom)
    {
        StopCameraMotion();
        _motionStartTarget = _target; _motionEndTarget = target;
        _motionStartZoom = ZoomFactor; _motionEndZoom = zoom;
        _cameraMotionStarted = Environment.TickCount64;
        _cameraMotionActive = true;
        CompositionTarget.Rendering += RenderCameraMotion;
    }

    private void RenderCameraMotion(object? sender, EventArgs args)
    {
        var progress = Math.Clamp((Environment.TickCount64 - _cameraMotionStarted) / 180d, 0, 1);
        ApplyCameraMotion(1 - Math.Pow(1 - progress, 3));
        if (progress >= 1) StopCameraMotion();
    }

    private void ApplyCameraMotion(double fraction)
    {
        _updatingCameraMotion = true;
        try
        {
            _target = _motionStartTarget + (_motionEndTarget - _motionStartTarget) * fraction;
            SetCurrentValue(ZoomFactorProperty, _motionStartZoom + (_motionEndZoom - _motionStartZoom) * fraction);
            UpdateCamera();
        }
        finally { _updatingCameraMotion = false; }
    }

    private void StopCameraMotion()
    {
        if (!_cameraMotionActive) return;
        CompositionTarget.Rendering -= RenderCameraMotion;
        _cameraMotionActive = false;
    }

    internal void CompleteCameraMotionForTest()
    {
        if (!_cameraMotionActive) return;
        ApplyCameraMotion(1);
        StopCameraMotion();
    }

    internal Point ProjectReferencePoint(int index)
    {
        if (_comparisonScene is null || EffectiveComparison?.Lap is not { } lap || (uint)index >= (uint)lap.Points.Length) return new(double.NaN, double.NaN);
        return LapReviewCameraFrame.From(Yaw, Pitch, Roll).Project(_comparisonScene.Bounds.Normalize(lap.Points[index].Position) +
            (_arrangement?.ReferenceOffset ?? new()), _target, _camera.Width, RenderSize);
    }
}
