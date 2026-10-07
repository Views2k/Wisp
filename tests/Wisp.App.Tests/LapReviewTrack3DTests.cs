using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewTrack3DTests
{
    [Fact]
    public void HiddenViewDoesNotPrepareMeshes() => OnSta(async () =>
    {
        var track = Track();
        Assert.Equal(0, track.SceneBuildCount); Assert.False(track.IsPreparing);
        Assert.False(track.IsReady);
        await Task.CompletedTask;
    });

    [Fact]
    public void CursorAndCameraUpdatesReuseThePreparedGeometry() => OnSta(async () =>
    {
        var track = Track();
        await track.PrepareForTestAsync();
        Assert.True(track.IsReady); Assert.Equal(1, track.SceneBuildCount);
        var original = track.ProjectPoint(1);
        for (var i = 0; i < 100; i++) track.Data = track.Data! with { Cursor = i % 3 };
        track.Yaw = 90; track.Pitch = 45; track.Roll = 15; track.ZoomBy(2); track.PanBy(30, 20);
        Assert.NotEqual(original, track.ProjectPoint(1));
        Assert.Equal(1, track.SceneBuildCount); Assert.False(track.IsPreparing);
        track.ResetView();
        Assert.Equal(original, track.ProjectPoint(1));
    });

    [Fact]
    public void ExplicitZoomOutRequestsOverviewButResetAndCameraBindingDoNot() => OnSta(async () =>
    {
        var track = Track(); await track.PrepareForTestAsync();
        var requests = 0; track.ZoomOutAtOverview += (_, _) => requests++;
        track.ZoomBy(4); track.ZoomBy(.5);
        Assert.Equal(0, requests);
        track.ZoomBy(.5);
        Assert.Equal(1, requests);
        track.ZoomBy(.9);
        Assert.Equal(2, requests);
        track.ZoomFactor = .4; track.ResetView();
        track.ZoomBy(double.NaN); track.ZoomBy(0); track.ZoomBy(1);
        Assert.Equal(2, requests);
        Assert.Equal(1, track.SceneBuildCount);
    });

    [Fact]
    public void ScrollingOverAnUnfocusedMapBubblesWithoutShrinkingTheTrack() => OnSta(async () =>
    {
        var track = Track();
        var parent = new Border { Child = track };
        parent.Measure(new Size(800, 400)); parent.Arrange(new Rect(0, 0, 800, 400));
        await track.PrepareForTestAsync();
        Assert.False(track.IsKeyboardFocusWithin);
        var point = track.ProjectPoint(1); var bubbled = 0;
        parent.MouseWheel += (_, _) => bubbled++;
        for (var i = 0; i < 12; i++)
        {
            var input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent };
            track.RaiseEvent(input);
            Assert.False(input.Handled);
        }
        Assert.Equal(12, bubbled); Assert.Equal(1, track.ZoomFactor);
        Assert.Equal(point, track.ProjectPoint(1)); Assert.Equal(1, track.SceneBuildCount);
    });

    [Fact]
    public void ComparisonRangeAndPathChangesRebuildWhileMatchedCursorUpdatesDoNot() => OnSta(async () =>
    {
        var track = Track();
        var reference = Data([new(0, 0, 0), new(40, 30, 50), new(80, 35, 40)]).Lap! with { RunId = Guid.NewGuid() };
        track.Data = track.Data! with { Reference = reference, Comparison = new([], 1, true, string.Empty) };
        await track.PrepareForTestAsync();
        track.Data = track.Data! with { ColorRangeOverride = new(0, 100, true) };
        await track.PrepareForTestAsync();
        Assert.Equal(2, track.SceneBuildCount);
        track.Data = track.Data! with { ShowReferencePath = false };
        await track.PrepareForTestAsync();
        Assert.Equal(3, track.SceneBuildCount);
        for (var i = 0; i < 100; i++) track.Data = track.Data! with { Cursor = i % 3 };
        Assert.Equal(3, track.SceneBuildCount); Assert.True(track.IsReady);
    });

    [Fact]
    public void RapidChannelReturnCancelsTheStaleBuildAndRestoresTheCachedScene() => OnSta(async () =>
    {
        var track = Track(); var original = track.Data!;
        await track.PrepareForTestAsync();
        track.Data = original with { Channel = LapReviewChannel.Brake };
        var pending = track.PrepareForTestAsync();
        track.Data = original;
        await pending;
        Assert.True(track.IsReady); Assert.Equal(1, track.SceneBuildCount);
        Assert.Equal(2, track.PreparedSegmentCount);
    });

    [Fact]
    public void EmptyLapNavigationAndNonNavigationKeysDoNotSelectInvalidPoints() => OnSta(async () =>
    {
        var track = new LapReviewTrack3D { Data = Data([]) };
        var chosen = new List<int>(); track.PointChosen += chosen.Add;
        foreach (var key in new[] { Key.Left, Key.Down, Key.Right, Key.Up, Key.Home, Key.End, Key.A })
            Assert.False(SendKey(track, key).Handled);
        Assert.Empty(chosen);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(Key.Left, 0, 0)]
    [InlineData(Key.Right, 2, 2)]
    [InlineData(Key.Up, 0, 1)]
    [InlineData(Key.Down, 2, 1)]
    [InlineData(Key.Home, 2, 0)]
    [InlineData(Key.End, 0, 2)]
    public void KeyboardSelectionMatchesTwoDimensionalMapBehavior(Key key, int cursor, int expected) => OnSta(async () =>
    {
        var track = Track(); track.Data = track.Data! with { Cursor = cursor };
        var chosen = new List<int>(); track.PointChosen += chosen.Add;
        Assert.True(SendKey(track, key).Handled); Assert.Equal(expected, Assert.Single(chosen));
        await Task.CompletedTask;
    });

    [Fact]
    public void ChannelWheelAndSectionChangesRebuildButClearingDataRemovesTheScene() => OnSta(async () =>
    {
        var track = Track(); await track.PrepareForTestAsync();
        track.Data = track.Data! with { Channel = LapReviewChannel.TireTemperature, Wheel = 2, SectionStart = 1 };
        await track.PrepareForTestAsync();
        Assert.Equal(2, track.SceneBuildCount);
        track.Data = null;
        Assert.False(track.IsReady); Assert.False(track.IsPreparing);
        Assert.Equal("No recorded lap positions", track.StatusText);
    });

    [Fact]
    public void InvalidCameraValuesCannotPoisonTheProjection() => OnSta(async () =>
    {
        var track = Track(); await track.PrepareForTestAsync();
        track.Yaw = double.NaN; track.Pitch = double.PositiveInfinity; track.Roll = double.NegativeInfinity;
        track.ZoomFactor = 0; track.PanBy(double.NaN, 3); track.ZoomBy(double.PositiveInfinity);
        var point = track.ProjectPoint(1);
        Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y));
        Assert.Equal(.2, track.ZoomFactor); Assert.Equal(LapReviewTrackFit.DefaultPitch, track.Pitch);
        track.Yaw = 180; track.Roll = -180;
        Assert.Equal(180, track.Yaw); Assert.Equal(-180, track.Roll);
    });

    [Fact]
    public void RenderedTubeColorMatchesTheLegendInsteadOfAddingItsFrontAndBackFaces() => OnSta(async () =>
    {
        var track = new LapReviewTrack3D { Data = Data([new(-100, 0, 0), new(0, 0, 0), new(100, 0, 0)]) };
        var surface = new Border { Background = Brushes.Black, Child = track };
        surface.Measure(new Size(800, 400)); surface.Arrange(new Rect(0, 0, 800, 400));
        await track.PrepareForTestAsync();
        track.Yaw = 0; track.Pitch = 90; track.Roll = 0;
        var bitmap = new RenderTargetBitmap(800, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var center = track.ProjectPoint(1);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(center.X), (int)Math.Round(center.Y), 1, 1), pixel, 4, 0);
        var expected = ((SolidColorBrush)LapReviewPalette.GetBrush(.5)).Color;
        Assert.InRange(Math.Abs(pixel[0] - expected.B), 0, 2);
        Assert.InRange(Math.Abs(pixel[1] - expected.G), 0, 2);
        Assert.InRange(Math.Abs(pixel[2] - expected.R), 0, 2);
        Assert.Equal(255, pixel[3]);
    });

    [Fact]
    public void ContactAnnotationStaysAboveAnOccludingTrackAndFollowsTheCameraWithoutRebuilding() => OnSta(async () =>
    {
        // The raised return segment passes directly above the contact in a top view.
        // Coloring by elevation proves the red foreground track really covers that position.
        var data = Data([new(-100, 0, 0), new(0, 0, 0), new(100, 0, 0), new(100, 60, 0), new(-100, 60, 0)]) with
        { Channel = LapReviewChannel.Elevation, Contacts = [new(1, 1, 100, LapReviewContactKind.PossibleContact)] };
        var track = new LapReviewTrack3D { Data = data, ShowContacts = false };
        var surface = new Border { Background = Brushes.Black, Child = track };
        surface.Measure(new Size(800, 400)); surface.Arrange(new Rect(0, 0, 800, 400));
        await track.PrepareForTestAsync();
        track.Yaw = 0; track.Pitch = 90; track.Roll = 0;
        var originalAnchor = track.ProjectPoint(1);
        AssertPixel(surface, originalAnchor, ((SolidColorBrush)LapReviewPalette.GetBrush(1)).Color);
        track.ShowContacts = true;
        AssertPixel(surface, originalAnchor, ((SolidColorBrush)LapReviewPalette.ContactBrush).Color);

        track.Yaw = 35; track.Pitch = 48; track.Roll = 14; track.ZoomBy(1.4); track.PanBy(22, -16);
        var movedAnchor = track.ProjectPoint(1);
        Assert.NotEqual(originalAnchor, movedAnchor);
        AssertPixel(surface, movedAnchor, ((SolidColorBrush)LapReviewPalette.ContactBrush).Color);
        track.Data = data with { Cursor = 1 };
        AssertPixel(surface, movedAnchor, ((SolidColorBrush)LapReviewPalette.ContactBrush).Color);
        track.Data = data with { Contacts = null };
        track.ResetView(); track.Yaw = 0; track.Pitch = 90;
        AssertPixel(surface, track.ProjectPoint(1), ((SolidColorBrush)LapReviewPalette.GetBrush(1)).Color);
        track.Data = data;
        AssertPixel(surface, track.ProjectPoint(1), ((SolidColorBrush)LapReviewPalette.ContactBrush).Color);
        Assert.Equal(1, track.SceneBuildCount);
        Assert.True(track.IsReady); Assert.False(track.IsPreparing);
    });

    private static void AssertPixel(FrameworkElement surface, Point anchor, Color expected)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(anchor.X), (int)Math.Round(anchor.Y), 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - expected.B), 0, 2);
        Assert.InRange(Math.Abs(pixel[1] - expected.G), 0, 2);
        Assert.InRange(Math.Abs(pixel[2] - expected.R), 0, 2);
        Assert.Equal(255, pixel[3]);
    }

    [Fact]
    public void InterpolatedCursorMovesWithoutMovingContactsOrRebuildingGeometry() => OnSta(async () =>
    {
        var data = Data([new(-100, 0, 0), new(0, 0, 0), new(100, 0, 0)]) with
        { Cursor = 1, Contacts = [new(1, 1, 100, LapReviewContactKind.PossibleContact)] };
        var track = new LapReviewTrack3D { Data = data };
        var surface = new Border { Background = Brushes.Black, Child = track };
        surface.Measure(new Size(800, 400)); surface.Arrange(new Rect(0, 0, 800, 400));
        await track.PrepareForTestAsync();
        track.Yaw = 0; track.Pitch = 90; track.Roll = 0;
        var sample = track.ProjectPoint(1);
        var interpolated = sample + (track.ProjectPoint(2) - sample) * .4;
        track.Data = data with { CursorPositionOverride = new(40, 0, 0) };
        AssertPixel(surface, interpolated, Colors.White);
        AssertPixel(surface, sample, ((SolidColorBrush)LapReviewPalette.ContactBrush).Color);
        AssertPixel(surface, new(sample.X + 10, sample.Y), ((SolidColorBrush)LapReviewPalette.GetBrush(.5)).Color);
        for (var i = 0; i < 100; i++) track.Data = data with { CursorPositionOverride = new(i, 0, 0) };
        Assert.Equal(1, track.SceneBuildCount); Assert.True(track.IsReady);
    });

    [Fact]
    public void ComparisonModelsShareOneSceneAndClickingEitherTrackFocusesWithoutRemovingTheOther() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        Assert.True(track.IsComparison); Assert.True(track.HasBothPreparedModels);
        Assert.Equal(2, track.SceneBuildCount); Assert.Equal(3, track.PreparedReferenceSegmentCount);
        var overviewWidth = Enumerable.Range(0, 4).Max(i => track.ProjectReferencePoint(i).X) - Enumerable.Range(0, 4).Min(i => track.ProjectReferencePoint(i).X);
        Assert.True(Enumerable.Range(0, 4).Max(i => track.ProjectPoint(i).X) < Enumerable.Range(0, 4).Min(i => track.ProjectReferencePoint(i).X));
        var primary = new List<int>(); var reference = new List<int>();
        track.PointChosen += primary.Add; track.ReferencePointChosen += reference.Add;
        track.Choose(track.ProjectReferencePoint(1));
        Assert.Equal(1, Assert.Single(reference)); Assert.Empty(primary); Assert.Equal(2, track.FocusedLap);
        Assert.True(track.IsCameraMotionActive); track.CompleteCameraMotionForTest();
        var focusedWidth = Enumerable.Range(0, 4).Max(i => track.ProjectReferencePoint(i).X) - Enumerable.Range(0, 4).Min(i => track.ProjectReferencePoint(i).X);
        Assert.True(focusedWidth > overviewWidth * 1.15);
        Assert.True(track.HasBothPreparedModels); Assert.Equal(2, track.SceneBuildCount);
        Assert.True(SendKey(track, Key.Right).Handled); Assert.Equal(1, reference[^1]);
        track.ShowAll(); track.CompleteCameraMotionForTest();
        track.Choose(track.ProjectPoint(2)); track.CompleteCameraMotionForTest();
        Assert.Equal(2, Assert.Single(primary)); Assert.Equal(1, track.FocusedLap);
        Assert.True(track.HasBothPreparedModels); Assert.Equal(2, track.SceneBuildCount);
    });

    [Fact]
    public void DistinctLapsRemainVisibleWhenBenchmarkComparisonIsUnavailable() => OnSta(async () =>
    {
        var track = ComparisonTrack();
        var primary = track.Data!.Lap! with { CarOrdinal = 3141, IsComplete = true };
        var reference = track.ComparisonData!.Lap! with { CarOrdinal = 1229, IsComplete = true };
        var comparison = LapReviewAnalysis.Compare(primary, reference, TestContext.Current.CancellationToken);
        Assert.False(comparison.CanCompare);
        track.Data = track.Data! with { Lap = primary, Reference = reference, Comparison = comparison };
        track.ComparisonData = track.ComparisonData! with { Lap = reference, Reference = primary, Comparison = null, SectionStart = -1, SectionEnd = -1 };
        Assert.False(track.Data.HasDistinctReference);
        Assert.True(track.Data.HasDistinctMapReference);
        await track.PrepareForTestAsync();
        Assert.True(track.IsComparison); Assert.True(track.HasBothPreparedModels);
        Assert.Equal(2, track.SceneBuildCount); Assert.Equal(3, track.PreparedReferenceSegmentCount);
        Assert.True(Enumerable.Range(0, 4).Max(i => track.ProjectPoint(i).X) < Enumerable.Range(0, 4).Min(i => track.ProjectReferencePoint(i).X));
        var chosen = -1; track.ReferencePointChosen += index => chosen = index;
        track.Choose(track.ProjectReferencePoint(2)); track.CompleteCameraMotionForTest();
        Assert.Equal(2, chosen); Assert.Equal(2, track.FocusedLap);
        Assert.True(track.HasBothPreparedModels); Assert.Equal(2, track.SceneBuildCount);
    });

    [Fact]
    public void RotatedOverviewFitsBothTracksAndCursorUpdatesDoNotPrepareNewMeshes() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        for (var i = 0; i < 100; i++)
        {
            track.Data = track.Data! with { Cursor = i % 4 };
            track.ComparisonData = track.ComparisonData! with { Cursor = (i + 1) % 4 };
        }
        Assert.Equal(2, track.SceneBuildCount);
        track.Yaw = 123; track.Pitch = 47; track.Roll = 78; track.ZoomBy(3); track.PanBy(400, -250);
        track.ShowAll(); track.CompleteCameraMotionForTest();
        for (var i = 0; i < 4; i++)
            foreach (var point in new[] { track.ProjectPoint(i), track.ProjectReferencePoint(i) })
            {
                Assert.InRange(point.X, 0, track.ActualWidth);
                Assert.InRange(point.Y, 0, track.ActualHeight);
            }
        Assert.Equal(0, track.FocusedLap); Assert.Equal(123, track.Yaw); Assert.Equal(78, track.Roll);
        track.FocusLap(2); track.CompleteCameraMotionForTest(); track.ZoomBy(.1);
        Assert.Equal(0, track.FocusedLap); Assert.True(track.HasBothPreparedModels);
        Assert.Equal(2, track.SceneBuildCount);
    });

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void RotationPreservesCameraScaleAndLapPosition(bool comparison, int focusedLap) => OnSta(async () =>
    {
        var track = comparison ? ComparisonTrack() : Track();
        await track.PrepareForTestAsync();
        if (focusedLap != 0) { track.FocusLap(focusedLap); track.CompleteCameraMotionForTest(); }
        track.ZoomBy(1.3); track.PanBy(12, -7);
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        var width = camera.Width; var zoom = track.ZoomFactor;
        var target = camera.Position + camera.LookDirection * 4;
        var sceneBuilds = track.SceneBuildCount;
        var selections = 0;
        track.PointChosen += _ => selections++;
        track.ReferencePointChosen += _ => selections++;
        foreach (var angle in new[] { -80d, 0d, 45d, 90d, 150d })
        {
            track.Yaw = angle; track.Pitch = angle / 2; track.Roll = angle / 3;
            Assert.Equal(width, camera.Width, 10);
            Assert.Equal(zoom, track.ZoomFactor);
            Assert.True((camera.Position + camera.LookDirection * 4 - target).Length < 1e-10);
        }
        Assert.Equal(focusedLap, track.FocusedLap);
        Assert.Equal(0, selections); Assert.Equal(sceneBuilds, track.SceneBuildCount);
    });

    [Fact]
    public void ExplicitFocusAndOverviewAfterRotationStartWithoutAScaleJump() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        track.Yaw = 78; track.Pitch = 55; track.Roll = 32;
        var width = camera.Width;
        track.FocusLap(2);
        Assert.Equal(width, camera.Width, 10);
        track.CompleteCameraMotionForTest();
        track.Yaw = -42; track.Roll = -15;
        width = camera.Width;
        track.ShowAll();
        Assert.Equal(width, camera.Width, 10);
        track.CompleteCameraMotionForTest();
        for (var i = 0; i < 4; i++)
            foreach (var point in new[] { track.ProjectPoint(i), track.ProjectReferencePoint(i) })
            {
                Assert.InRange(point.X, 0, track.ActualWidth);
                Assert.InRange(point.Y, 0, track.ActualHeight);
            }
        width = camera.Width;
        track.Yaw += 37;
        track.ZoomBy(.8);
        Assert.Equal(width / .8, camera.Width, 10);
        Assert.Equal(2, track.SceneBuildCount);
    });

    [Theory]
    [InlineData(.2)]
    [InlineData(50)]
    public void FramingFromZoomLimitsDoesNotJumpBeforeAnimation(double zoom) => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        foreach (var focus in new[] { 1, 2, 0 })
        {
            track.Yaw = 85; track.Pitch = 5; track.Roll = 90; track.ZoomFactor = zoom;
            var width = camera.Width; var position = camera.Position;
            if (focus == 0) track.ShowAll(); else track.FocusLap(focus);
            Assert.Equal(width, camera.Width, 10); Assert.Equal(position, camera.Position);
            track.CompleteCameraMotionForTest();
            Assert.True(double.IsFinite(camera.Width) && camera.Width > 0);
            Assert.InRange(track.ZoomFactor, .2, 50);
        }
        for (var i = 0; i < 4; i++)
            foreach (var point in new[] { track.ProjectPoint(i), track.ProjectReferencePoint(i) })
            {
                Assert.InRange(point.X, 0, track.ActualWidth);
                Assert.InRange(point.Y, 0, track.ActualHeight);
            }
    });

    [Fact]
    public void PressingTheMapStopsFocusAnimationBeforeDragging() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        track.FocusLap(2); Assert.True(track.IsCameraMotionActive);
        typeof(LapReviewTrack3D).GetMethod("ApplyCameraMotion", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(track, [.5d]);
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        var width = camera.Width; var position = camera.Position;
        var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.MouseDownEvent };
        track.RaiseEvent(input);
        Assert.True(input.Handled); Assert.False(track.IsCameraMotionActive);
        track.CompleteCameraMotionForTest();
        Assert.Equal(width, camera.Width); Assert.Equal(position, camera.Position);
        track.Yaw += 75; track.Pitch = 67; track.Roll = 20;
        Assert.Equal(width, camera.Width);
        if (track.IsMouseCaptured) track.ReleaseMouseCapture();
    });

    [Fact]
    public void FocusAnimationDoesNotOvershootScaleWhenTheFittedAxisChanges() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        var animate = typeof(LapReviewTrack3D).GetMethod("ApplyCameraMotion", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!;
        foreach (var yaw in new[] { -80d, 0d, 50d, 90d })
            foreach (var pitch in new[] { 5d, 55d })
                foreach (var roll in new[] { 0d, 60d })
                    foreach (var focus in new[] { 1, 2 })
                    {
                        track.ResetView(); track.Yaw = yaw; track.Pitch = pitch; track.Roll = roll;
                        var start = camera.Width;
                        track.FocusLap(focus);
                        var widths = new List<double> { start };
                        foreach (var fraction in new[] { .25, .5, .75 })
                        {
                            animate.Invoke(track, [fraction]); widths.Add(camera.Width);
                        }
                        track.CompleteCameraMotionForTest(); widths.Add(camera.Width);
                        var end = camera.Width;
                        for (var i = 1; i < widths.Count; i++)
                        {
                            Assert.InRange(widths[i], Math.Min(start, end) - 1e-10, Math.Max(start, end) + 1e-10);
                            Assert.True(end >= start ? widths[i] >= widths[i - 1] - 1e-10 : widths[i] <= widths[i - 1] + 1e-10);
                        }
                    }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ZoomingOutOfARotatedFocusedLapKeepsEachScaleStepAndRevealsBoth(int focus) => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        track.FocusLap(focus); track.CompleteCameraMotionForTest();
        track.ZoomBy(4); track.Yaw += 87; track.Pitch = 70; track.Roll = 55;
        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
        var overviewEvents = 0; track.ZoomOutAtOverview += (_, _) => overviewEvents++;
        for (var step = 0; step < 50 && track.FocusedLap != 0; step++)
        {
            var expectedWidth = camera.Width * track.ZoomFactor / Math.Clamp(track.ZoomFactor * .8, .2, 50);
            track.ZoomBy(.8);
            Assert.Equal(expectedWidth, camera.Width, 10);
            Assert.InRange(track.ZoomFactor, .2, 50);
            Assert.Equal(track.FocusedLap == 0 ? 1 : 0, overviewEvents);
        }
        Assert.Equal(0, track.FocusedLap); Assert.Equal(1, overviewEvents);
        for (var i = 0; i < 4; i++)
            foreach (var point in new[] { track.ProjectPoint(i), track.ProjectReferencePoint(i) })
            {
                Assert.InRange(point.X, 0, track.ActualWidth);
                Assert.InRange(point.Y, 0, track.ActualHeight);
            }
        Assert.Equal(2, track.SceneBuildCount);
    });

    [Fact]
    public void SelectingAnotherPointOnTheFocusedLapPreservesManualCameraAdjustments() => OnSta(async () =>
    {
        var track = ComparisonTrack(); await track.PrepareForTestAsync();
        track.Choose(track.ProjectPoint(0));
        Assert.Equal(1, track.FocusedLap); Assert.True(track.IsCameraMotionActive);
        track.CompleteCameraMotionForTest();
        track.Yaw += 12; track.Pitch = 35; track.Roll = 8;
        track.ZoomBy(2); track.PanBy(18, -7);
        var zoom = track.ZoomFactor; var projection = track.ProjectPoint(1);
        track.Choose(projection);
        Assert.Equal(1, track.FocusedLap); Assert.False(track.IsCameraMotionActive);
        Assert.Equal(zoom, track.ZoomFactor); Assert.Equal(projection, track.ProjectPoint(1));
        track.Choose(track.ProjectReferencePoint(1));
        Assert.Equal(2, track.FocusedLap); Assert.True(track.IsCameraMotionActive);
        track.CompleteCameraMotionForTest();
        Assert.True(track.HasBothPreparedModels); Assert.Equal(2, track.SceneBuildCount);
    });

    [Fact]
    public void RemovingComparisonAndUnloadingCancelCameraMotionAndKeepPrimaryGeometryReusable() => OnSta(async () =>
    {
        var track = ComparisonTrack(); var other = track.ComparisonData;
        await track.PrepareForTestAsync();
        track.FocusLap(2); Assert.True(track.IsCameraMotionActive);
        track.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(track.IsCameraMotionActive);
        track.ComparisonData = null; await track.PrepareForTestAsync();
        Assert.False(track.IsComparison); Assert.False(track.HasBothPreparedModels);
        Assert.True(track.IsReady); Assert.Equal(2, track.SceneBuildCount);
        track.ComparisonData = other; await track.PrepareForTestAsync();
        Assert.True(track.IsComparison); Assert.True(track.HasBothPreparedModels); Assert.Equal(3, track.SceneBuildCount);
        track.FocusLap(1); track.Data = null;
        Assert.False(track.IsCameraMotionActive); Assert.False(track.IsReady);
    });

    private static LapReviewTrack3D ComparisonTrack()
    {
        var first = Data([new(-100, 0, 0), new(-50, 15, 30), new(0, 30, 70), new(100, 5, 0)]);
        var second = Data([new(-100, 1, 0), new(-40, 20, 35), new(0, 35, 75), new(100, 10, 0)]);
        second = second with { Lap = second.Lap! with { RunId = Guid.NewGuid() } };
        var track = new LapReviewTrack3D
        {
            Data = first with { Reference = second.Lap, Comparison = new([], 1, true, string.Empty), ShowReferencePath = false },
            ComparisonData = second with { Reference = first.Lap, Comparison = new([], 1, true, string.Empty), ShowReferencePath = false }
        };
        track.Measure(new Size(800, 400)); track.Arrange(new Rect(0, 0, 800, 400));
        return track;
    }

    private static LapReviewTrack3D Track()
    {
        var track = new LapReviewTrack3D { Data = Data([new(0, 0, 0), new(20, 10, 30), new(60, 15, 10)]) };
        track.Measure(new Size(800, 400)); track.Arrange(new Rect(0, 0, 800, 400));
        return track;
    }
    private static LapReviewPlotData Data(LapPosition[] positions) => new(new LapReviewLap
    {
        Points = positions.Select((position, i) => new LapReviewPoint(i, i, i, i, position, i == 0,
            new RunSample { State = RunTestData.State() })).ToArray()
    }, null, null, LapReviewChannel.Speed, SpeedUnit.MilesPerHour, 0, 0, positions.Length - 1);

    private static KeyEventArgs SendKey(LapReviewTrack3D track, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenSource(track), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent };
        track.RaiseEvent(input); return input;
    }
    private sealed class OffscreenSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static void OnSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var task = action();
                if (!task.IsCompleted)
                {
                    _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                    Dispatcher.Run();
                }
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "3D map check did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
