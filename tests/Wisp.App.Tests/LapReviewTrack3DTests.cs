using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
