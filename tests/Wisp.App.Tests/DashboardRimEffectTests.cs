using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardRimEffectTests
{
    [Theory]
    [InlineData(OrbitSurfaceShape.Swept, 28)]
    [InlineData(OrbitSurfaceShape.Card, 8)]
    [InlineData(OrbitSurfaceShape.Card, 48)]
    public void MovingRimLeavesTheTelemetryInteriorTransparent(OrbitSurfaceShape shape, double radius) => OnSta(() =>
    {
        var renderer = new DashboardRimDrawing();
        var bounds = new Rect(12, 32, 1000, 300);
        var corners = new CornerRadius(radius);
        var interior = OrbitSurface.CreateGeometry(new Rect(32, 52, 960, 260), shape, corners);
        byte[]? previous = null;
        foreach (var seconds in new[] { 0d, .4, 1.2, 2 })
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
                renderer.Draw(context, bounds, shape, corners, Colors.Cyan, 1, true, seconds, new DpiScale(1, 1));
            var pixels = Pixels(visual);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
            for (var y = 60; y < 310; y += 20)
                for (var x = 40; x < 990; x += 20)
                    if (interior.FillContains(new Point(x + .5, y + .5)))
                        Assert.Equal(0, Alpha(pixels, x, y));
            if (previous is not null) Assert.False(previous.SequenceEqual(pixels));
            previous = pixels;
        }
        Assert.Equal(1, renderer.HaloBuildCount);
        Assert.Equal(1, renderer.AnchorBuildCount);
        Assert.InRange(renderer.CachedSparkBrushCount, 1, 256);
    });

    [Fact]
    public void VisibleParticlesTravelAwayFromTheRimBetweenFrames() => OnSta(() =>
    {
        var center = new Point(256, 256);
        var silhouette = new EllipseGeometry(center, 150, 150);
        silhouette.Freeze();
        var scene = new DashboardRimScene();
        scene.Update(silhouette, 1.3);
        var first = scene.Sparks.ToArray();
        scene.Update(silhouette, 1.3 + 1d / 60);
        var second = scene.Sparks.ToArray();
        Assert.Equal(DashboardRimScene.SparkCount, first.Length);
        Assert.Equal(first.Length, second.Length);
        var visible = 0;
        var outward = 0;
        for (var index = 0; index < first.Length; index++)
        {
            Assert.True(double.IsFinite(second[index].Position.X));
            Assert.True(double.IsFinite(second[index].Position.Y));
            Assert.True(double.IsFinite(second[index].Radius) && second[index].Radius > 0);
            Assert.InRange(second[index].Opacity, 0, 1);
            if (first[index].Opacity <= 0.04 || second[index].Opacity <= 0.04)
                continue;
            visible++;
            var previousDistance = (first[index].Position - center).Length;
            var nextDistance = (second[index].Position - center).Length;
            Assert.True(previousDistance >= 149.5 && nextDistance >= 149.5,
                "Visible emitted particles must stay outside the circular instrument.");
            if (nextDistance > previousDistance + 0.0001) outward++;
        }
        Assert.True(visible >= DashboardRimScene.SparkCount / 3, "The fixture needs enough visible particles to test their motion.");
        Assert.True(outward >= visible * 0.95,
            $"Expected visible particles to move away from the rim; {outward} of {visible} moved outward.");
        Assert.Equal(1, scene.AnchorBuildCount);
    });

    [Fact]
    public void AnimatedFilledPrimitivesAreDiscreteParticlesRatherThanRibbonPaths() => OnSta(() =>
    {
        var renderer = new DashboardRimDrawing();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            renderer.Draw(drawing, new Rect(12, 32, 1000, 300), OrbitSurfaceShape.Swept,
                new CornerRadius(28), Colors.Cyan, 1, true, 0.4, new DpiScale(1, 1));
        var filled = Drawings(visual.Drawing).OfType<GeometryDrawing>()
            .Where(drawing => drawing.Brush is not null).ToArray();
        Assert.NotEmpty(filled);
        Assert.All(filled, drawing => Assert.IsType<EllipseGeometry>(drawing.Geometry));
    });

    [Fact]
    public void CacheTracksGeometryAndAccentButNotAnimationTimeOrParticleVisibility() => OnSta(() =>
    {
        var renderer = new DashboardRimDrawing();
        var bounds = new Rect(12, 32, 1000, 300);
        void Draw(Color color, bool particles, double seconds, OrbitSurfaceShape shape = OrbitSurfaceShape.Swept)
        {
            var visual = new DrawingVisual();
            using var context = visual.RenderOpen();
            renderer.Draw(context, bounds, shape, new CornerRadius(28), color, 1, particles, seconds, new DpiScale(1, 1));
        }
        Draw(Colors.Cyan, false, 0);
        Assert.Equal(1, renderer.HaloBuildCount);
        Assert.Equal(0, renderer.AnchorBuildCount);
        Draw(Colors.Cyan, true, 0);
        Draw(Colors.Cyan, true, 1.2);
        Assert.Equal(1, renderer.HaloBuildCount);
        Assert.Equal(1, renderer.AnchorBuildCount);
        Draw(Colors.Gold, true, 1.2);
        Assert.Equal(2, renderer.HaloBuildCount);
        Assert.Equal(1, renderer.AnchorBuildCount);
        Draw(Colors.Gold, true, 1.2, OrbitSurfaceShape.Card);
        Assert.Equal(3, renderer.HaloBuildCount);
        Assert.Equal(2, renderer.AnchorBuildCount);
    });

    [Fact]
    public void GlowOffClearsTheRimWithoutChangingInstrumentBounds() => OnSta(() =>
    {
        var effect = CreateEffect();
        var original = Capture(effect);
        var bounds = effect.InstrumentBounds;
        Assert.Equal(new Rect(12, 32, 1000, 300), bounds);
        Assert.Contains(original.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Equal(0, Alpha(original, 512, 182));

        effect.GlowOpacity = 0;
        Assert.All(Capture(effect), value => Assert.Equal(0, value));
        Assert.Equal(bounds, effect.InstrumentBounds);
        effect.GlowOpacity = 1;
        Assert.Equal(original, Capture(effect));
        Assert.Equal(bounds, effect.InstrumentBounds);
    });

    [Fact]
    public void DisablingParticlesKeepsTheHaloAndPausedDrawingStaysStill() => OnSta(() =>
    {
        var effect = CreateEffect();
        effect.ParticlesEnabled = false;
        var halo = Capture(effect);
        Assert.Contains(halo.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Equal(0, Alpha(halo, 512, 182));
        var seconds = effect.SceneTimeSeconds;
        effect.IsAnimationEnabled = false;
        for (var index = 0; index < 3; index++)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            Assert.Equal(halo, Capture(effect));
            Assert.Equal(seconds, effect.SceneTimeSeconds);
            Assert.False(effect.HasRenderingSubscription);
        }
        effect.Accent = Brushes.HotPink;
        Assert.False(halo.SequenceEqual(Capture(effect)));
        effect.Accent = Brushes.Transparent;
        Assert.All(Capture(effect), value => Assert.Equal(0, value));
    });

    [Fact]
    public void UnshownLifecycleNeverStartsRenderingAndDetachesRepeatedly() => OnSta(() =>
    {
        var effect = CreateEffect();
        var host = new Window { Content = effect, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            for (var index = 0; index < 2; index++)
            {
                effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.True(effect.HasLifecycleSubscriptions);
                Assert.False(effect.HasRenderingSubscription);
                Assert.False(effect.IsHitTestVisible);
                Assert.False(effect.Focusable);
                effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.False(effect.HasLifecycleSubscriptions);
                Assert.False(effect.HasRenderingSubscription);
            }
        }
        finally
        {
            effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            host.Content = null;
            host.Close();
        }
    });

    [Fact]
    public void AnchoredRimFollowsTranslatedAndScaledTargetWithoutAffectingItsLayout() => OnSta(() =>
    {
        var target = new Border { Width = 400, Height = 100 };
        var effect = CreateEffect();
        effect.Width = 1024; effect.Height = 344;
        effect.CornerRadius = new CornerRadius(8, 12, 16, 20);
        effect.RimThickness = new Thickness(1, 2, 1, 2);
        effect.TargetElement = target;
        var canvas = new Canvas(); canvas.Children.Add(effect); canvas.Children.Add(target);
        Canvas.SetLeft(target, 70); Canvas.SetTop(target, 80);
        ArrangeCanvas();
        Assert.Equal(new Rect(70, 80, 400, 100), effect.InstrumentBounds);
        var targetSize = target.RenderSize;
        effect.GlowOpacity = 0; ArrangeCanvas();
        Assert.Equal(targetSize, target.RenderSize);
        effect.GlowOpacity = 1;
        Canvas.SetLeft(target, 90);
        target.LayoutTransform = new ScaleTransform(1.5, 1.5);
        ArrangeCanvas();
        Assert.Equal(new Rect(90, 80, 600, 150), effect.InstrumentBounds);
        Assert.Equal(new CornerRadius(12, 18, 24, 30), effect.EffectiveCornerRadius);
        Assert.Equal(3, effect.EffectiveRimWidth);
        Assert.Equal(targetSize, target.RenderSize);
        effect.TargetElement = null;
        effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        void ArrangeCanvas()
        {
            canvas.Measure(new Size(1024, 344)); canvas.Arrange(new Rect(0, 0, 1024, 344)); canvas.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            canvas.UpdateLayout();
            effect.RefreshTarget();
        }
    });

    [Fact]
    public void TargetSubscriptionsDetachEvenWhenTheControlWasNeverLoaded() => OnSta(() =>
    {
        var effect = CreateEffect();
        effect.TargetElement = new Border();
        Assert.False(effect.HasTargetSubscription);
        Assert.False(effect.HasLifecycleSubscriptions);
        effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(effect.HasTargetSubscription);
        effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(effect.IsVisible, effect.HasTargetSubscription);
        Assert.True(effect.HasLifecycleSubscriptions);
        effect.TargetElement = new Border();
        Assert.Equal(effect.IsVisible, effect.HasTargetSubscription);
        effect.TargetElement = null;
        Assert.False(effect.HasTargetSubscription);
        effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.False(effect.HasLifecycleSubscriptions);
        Assert.False(effect.HasRenderingSubscription);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedRimRefreshesItsClipWhenOnlyTheViewportHeightChanges(bool particlesEnabled) => OnSta(() =>
    {
        foreach (var resizeEffect in new[] { false, true })
        {
            var target = new Border { Width = 400, Height = 100 };
            var content = new Canvas { Height = 600 };
            Canvas.SetLeft(target, 100); Canvas.SetTop(target, 100);
            content.Children.Add(target);
            var scroll = new ScrollViewer
            {
                Content = content,
                Height = 180,
                VerticalAlignment = VerticalAlignment.Top,
                CanContentScroll = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            var effect = CreateEffect();
            effect.ParticlesEnabled = particlesEnabled;
            effect.TargetElement = target;
            var surface = new Grid();
            surface.Children.Add(effect); surface.Children.Add(scroll);
            try
            {
                ArrangeViewport(180);
                effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                effect.RefreshTarget();
                var bounds = effect.InstrumentBounds;
                var time = effect.SceneTimeSeconds;
                Assert.Equal(new Rect(100, 100, 400, 100), bounds);
                Assert.Equal(0, LowerHaloAlpha(Pixels(effect)));
                Assert.False(effect.HasRenderingSubscription);

                ArrangeViewport(320);
                Assert.Equal(bounds, effect.InstrumentBounds);
                Assert.True(LowerHaloAlpha(Pixels(effect)) > 0,
                    "Growing the paused viewport must reveal the previously clipped bottom halo.");
                Assert.Equal(time, effect.SceneTimeSeconds);

                ArrangeViewport(180);
                Assert.Equal(bounds, effect.InstrumentBounds);
                Assert.Equal(0, LowerHaloAlpha(Pixels(effect)));
                Assert.Equal(time, effect.SceneTimeSeconds);
                Assert.False(effect.HasRenderingSubscription);
            }
            finally
            {
                effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                effect.TargetElement = null;
                scroll.Content = null;
                surface.Children.Clear();
            }

            void ArrangeViewport(double height)
            {
                scroll.Height = height;
                var size = new Size(1024, resizeEffect ? height : 344);
                surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                surface.UpdateLayout();
            }
        }

        static int LowerHaloAlpha(byte[] pixels) => Enumerable.Range(201, 10)
            .Sum(y => Enumerable.Range(280, 40).Sum(x => (int)Alpha(pixels, x, y)));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AccentResourceChangesSurviveDetachedDashboardVisuals(bool reparentTarget, bool hideEffect) => OnSta(() =>
    {
        var target = new Border { Width = 400, Height = 100 };
        var content = new Canvas { Height = 600 };
        Canvas.SetLeft(target, 100); Canvas.SetTop(target, 100);
        content.Children.Add(target);
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var effect = CreateEffect();
        effect.TargetElement = target;
        var surface = new Grid();
        surface.Resources["AccentBrush"] = Brushes.Cyan;
        effect.SetResourceReference(DashboardRimEffect.AccentProperty, "AccentBrush");
        surface.Children.Add(scroll); surface.Children.Add(effect);
        try
        {
            ArrangeSurface();
            effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            effect.RefreshTarget();
            var original = Pixels(effect);
            Assert.Contains(original, value => value != 0);

            if (hideEffect) effect.Visibility = Visibility.Collapsed;
            if (reparentTarget) content.Children.Remove(target);
            else surface.Children.Remove(scroll);

            // Resource invalidation can precede the detached tab's next layout event.
            surface.Resources["AccentBrush"] = Brushes.Magenta;
            Assert.Same(Brushes.Magenta, effect.Accent);
            Assert.All(Pixels(effect), value => Assert.Equal(0, value));
            Assert.False(effect.HasRenderingSubscription);
            if (!hideEffect) effect.RefreshTarget();

            if (reparentTarget) content.Children.Add(target);
            else surface.Children.Insert(0, scroll);
            if (hideEffect) effect.Visibility = Visibility.Visible;
            ArrangeSurface();
            effect.RefreshTarget();
            var restored = Pixels(effect);
            Assert.Contains(restored, value => value != 0);
            Assert.False(original.SequenceEqual(restored));
            Assert.Same(Brushes.Magenta, effect.Accent);
        }
        finally
        {
            effect.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            effect.TargetElement = null;
            scroll.Content = null;
            surface.Children.Clear();
        }

        void ArrangeSurface()
        {
            var size = new Size(1024, 344);
            surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            surface.UpdateLayout();
        }
    });

    private static DashboardRimEffect CreateEffect() => new()
    {
        Accent = Brushes.Cyan,
        Shape = OrbitSurfaceShape.Swept,
        IsAnimationEnabled = false
    };

    private static byte[] Capture(DashboardRimEffect effect)
    {
        effect.Measure(new Size(1024, 344));
        effect.Arrange(new Rect(0, 0, 1024, 344));
        effect.UpdateLayout();
        return Pixels(effect);
    }

    private static byte[] Pixels(Visual visual)
    {
        var bitmap = new RenderTargetBitmap(1024, 344, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[1024 * 344 * 4];
        bitmap.CopyPixels(pixels, 1024 * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int x, int y) => pixels[(y * 1024 + x) * 4 + 3];

    private static IEnumerable<Drawing> Drawings(Drawing drawing)
    {
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Drawings(child))
                    yield return descendant;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Dashboard rim fixture timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
