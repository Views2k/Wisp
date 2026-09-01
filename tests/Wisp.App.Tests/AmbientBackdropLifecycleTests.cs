using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AmbientBackdropLifecycleTests
{
    private static AmbientBackdropPlaybackState Active => new()
    {
        IsLoaded = true,
        IsVisible = true,
        HasHost = true,
        HostIsVisible = true,
        HostIsActive = true,
        IsAnimationEnabled = true,
        ClientAreaAnimation = true,
        RenderingTier = 2,
        HasViewport = true,
        Intensity = 0.78
    };

    [Fact]
    public void EveryLifecycleAndAccessibilityGateMustAllowAnimation()
    {
        Assert.True(Active.CanAnimate);
        var disabled = new[]
        {
            Active with { IsLoaded = false },
            Active with { IsVisible = false },
            Active with { HasHost = false },
            Active with { HostIsVisible = false },
            Active with { HostIsActive = false },
            Active with { HostIsMinimized = true },
            Active with { IsAnimationEnabled = false },
            Active with { ClientAreaAnimation = false },
            Active with { HighContrast = true },
            Active with { RenderingTier = 0 },
            Active with { RenderingTier = 1 },
            Active with { HasViewport = false },
            Active with { IsDesignMode = true },
            Active with { Intensity = 0 },
            Active with { Intensity = double.NaN }
        };
        Assert.All(disabled, state => Assert.False(state.CanAnimate));
        Assert.False(default(AmbientBackdropPlaybackState).CanAnimate);
    }

    [Fact]
    public void PausingPreservesTheSceneWithoutCatchingUpOrJumpingOnResume()
    {
        var clock = new AmbientBackdropClock();
        Assert.False(clock.Advance(100));
        Assert.Equal(0, clock.LastStepSeconds);
        clock.SetRunning(true, 100);
        Assert.True(clock.Advance(100.05));
        Assert.Equal(0.05, clock.Seconds, 8);
        Assert.Equal(0.05, clock.LastStepSeconds, 8);

        clock.SetRunning(false, 101);
        Assert.False(clock.IsRunning);
        Assert.Equal(0.05, clock.Seconds, 8);
        Assert.Equal(0, clock.LastStepSeconds);
        Assert.False(clock.Advance(10_000));
        clock.SetRunning(true, 20_000);
        Assert.Equal(0.05, clock.Seconds, 8);
        Assert.True(clock.Advance(20_000.025));
        Assert.Equal(0.075, clock.Seconds, 8);
        Assert.Equal(0.025, clock.LastStepSeconds, 8);
    }

    [Fact]
    public void ClockIgnoresInvalidTimesAndBoundsDelayedFrames()
    {
        var clock = new AmbientBackdropClock();
        clock.SetRunning(true, 10);
        Assert.False(clock.Advance(double.NaN));
        Assert.False(clock.Advance(double.PositiveInfinity));
        Assert.False(clock.Advance(9));
        Assert.False(clock.Advance(10));
        Assert.Equal(0, clock.LastStepSeconds);
        Assert.True(clock.Advance(100));
        Assert.Equal(AmbientBackdropClock.MaximumStepSeconds, clock.Seconds);
        Assert.Equal(AmbientBackdropClock.MaximumStepSeconds, clock.LastStepSeconds);
        clock.SetRunning(true, 200);
        Assert.True(clock.Advance(100.025));
        Assert.Equal(0.125, clock.Seconds, 8);
        Assert.Equal(0.025, clock.LastStepSeconds, 8);
        Assert.Equal(24, AmbientBackdropClock.FramesPerSecond);
    }

    [Fact]
    public void PointerFollowAndReturnAreBoundedAndAllocationFree()
    {
        var pointer = new AmbientBackdropPointer();
        pointer.Move(2, -1);
        for (var index = 0; index < 240; index++)
            pointer.Advance(1d / AmbientBackdropClock.FramesPerSecond);
        Assert.InRange(pointer.Position.X, 0.999, 1);
        Assert.InRange(pointer.Position.Y, 0, 0.001);
        Assert.InRange(pointer.Activity, 0.999, 1);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            pointer.Advance(1d / AmbientBackdropClock.FramesPerSecond);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0L, allocated);

        pointer.Leave();
        for (var index = 0; index < 240; index++)
            pointer.Advance(1d / AmbientBackdropClock.FramesPerSecond);
        Assert.Equal(0, pointer.Activity);
        pointer.Reset();
        Assert.Equal(new AmbientPoint(0.5, 0.5), pointer.Position);
    }

    [Fact]
    public void OffscreenControlDrawsCompleteFrameZeroWithoutCompositeEffectsOrTimers() => OnSta(() =>
    {
        var control = new AmbientBackdrop();
        control.Measure(new Size(1280, 800));
        control.Arrange(new Rect(0, 0, 1280, 800));

        Assert.False(control.IsAnimationRunning);
        Assert.False(control.HasAnimationTickSubscription);
        Assert.False(control.HasPointerSubscriptions);
        Assert.Equal(0, control.SceneTimeSeconds);
        Assert.False(control.IsHitTestVisible);
        Assert.False(control.Focusable);
        Assert.True(control.ClipToBounds);
        Assert.Null(control.Effect);
        Assert.Null(control.OpacityMask);
        Assert.Null(control.CacheMode);
        Assert.Equal(2, VisualTreeHelper.GetChildrenCount(control));
        var background = Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(control, 0));
        var backgroundDrawing = Assert.IsType<DrawingGroup>(background.Drawing);
        Assert.Single(backgroundDrawing.Children);
        Assert.Equal(1, background.Opacity);
        Assert.Null(background.OpacityMask);
        var scene = Assert.IsType<DrawingVisual>(VisualTreeHelper.GetChild(control, 1));
        var drawing = Assert.IsType<DrawingGroup>(scene.Drawing);
        Assert.False(drawing.Bounds.IsEmpty);
        Assert.InRange(drawing.Bounds.Left, 0, 1280 * 0.12);
        Assert.InRange(drawing.Bounds.Right, 1280 * 0.88, 1280);
        Assert.Equal(0.78, scene.Opacity, 3);
        Assert.Null(scene.OpacityMask);
        Assert.Equal(AmbientBackdropScene.ParticleCount, drawing.Children.Count);
    });

    [Fact]
    public void RepeatedUnloadDetachesTimerEnvironmentAndPointerSubscriptions() => OnSta(() =>
    {
        var control = new AmbientBackdrop();
        var host = new Window { Content = control, ShowActivated = false, ShowInTaskbar = false };
        control.Measure(new Size(1280, 800));
        control.Arrange(new Rect(0, 0, 1280, 800));
        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.True(control.HasEnvironmentSubscriptions);
                Assert.True(control.HasPointerSubscriptions);
                Assert.True(control.HasHostWindow);
                Assert.False(control.IsAnimationRunning);
                StartTimer(control);
                StartTimer(control);
                Assert.True(control.HasAnimationTickSubscription);

                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.False(control.HasAnimationTickSubscription);
                Assert.False(control.IsAnimationRunning);
                Assert.False(control.HasEnvironmentSubscriptions);
                Assert.False(control.HasPointerSubscriptions);
                Assert.False(control.HasHostWindow);
                Assert.Equal(0, control.SceneTimeSeconds);
            }
        }
        finally
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
        }
    });

    [Fact]
    public void OptOutStopsAnAttachedTimerAndIntensityIsCoerced() => OnSta(() =>
    {
        var control = new AmbientBackdrop();
        StartTimer(control);
        control.IsAnimationEnabled = false;
        Assert.False(control.HasAnimationTickSubscription);
        Assert.False(control.IsAnimationRunning);
        control.Intensity = 3;
        Assert.Equal(1, control.Intensity);
        control.Intensity = -1;
        Assert.Equal(0, control.Intensity);
        control.Intensity = double.NaN;
        Assert.Equal(0, control.Intensity);
        Assert.Equal(0, control.SceneTimeSeconds);
    });

    private static void StartTimer(AmbientBackdrop control) =>
        typeof(AmbientBackdrop).GetMethod("SetAnimationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, new object[] { true });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Backdrop STA check timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
