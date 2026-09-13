using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;

namespace Wisp.UiReview;

internal static class DashboardRimReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<object>();
        var benchmarks = new List<object>();
        var animationFrames = new List<string>();
        var activations = 0;
        var previousSynchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        MainWindow? window = null;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var fixture = Fixture.All.First(item => item.Name == "orbit-reference");
            var settings = fixture.CreateSettings();
            settings.DebugLoggingEnabled = false;
            settings.AnimatedBackground = false;
            settings.BackgroundParticlesEnabled = true;
            controller = new AppController(settings, new SettingsService(Path.Combine(output, "synthetic-settings.json")));
            fixture.Apply(controller.ViewModel, waiting: false);
            window = new MainWindow(controller);
            window.Activated += (_, _) => activations++;
            var surface = detachSurface(window, controller.ViewModel);
            var rim = Required<DashboardRimEffect>(window, "DashboardRim");
            var instrument = Required<OrbitSurface>(window, "OrbitInstrument");
            var metrics = Required<Panel>(window, "DashboardInstruments");
            var speed = Required<TextBlock>(window, "DashboardSpeedNumber");
            var tabs = Required<TabControl>(window, "RootTabs");
            tabs.SelectedIndex = 0;
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            var renderer = new DashboardRimDrawing();
            Arrange(surface, new Size(1440, 1000));
            rim.RefreshTarget();
            var expectedSpeed = speed.Text;
            var expectedStatus = controller.ViewModel.StatusText;
            var size = new Size(1440, 1000);

            foreach (var width in new[] { 1180, 1200, 1220, 1280, 1440 })
            {
                size = new Size(width, 1000);
                Arrange(surface, size);
                Capture($"layout-{width}", 0, saveShell: true);
            }

            size = new Size(1440, 1000);
            Arrange(surface, size);
            foreach (var (name, color) in new[] { ("cyan", Colors.Cyan), ("pink", Colors.HotPink), ("gold", Colors.Gold) })
            {
                var brush = new SolidColorBrush(color); brush.Freeze();
                surface.Resources["AccentBrush"] = brush;
                Arrange(surface, size);
                foreach (var seconds in new[] { 0d, .4, 1.2, 2 })
                    Capture($"{name}-{seconds:0.0}s", seconds, saveShell: seconds == 0);
            }

            var originalBounds = Bounds(instrument, surface);
            rim.GlowOpacity = 0;
            Arrange(surface, size);
            Check(!HasPixels(rim), "glow-off-still-draws");
            Check(Bounds(instrument, surface) == originalBounds, "glow-toggle-changes-metric-layout");
            Capture("glow-off", 0, saveShell: true);
            rim.GlowOpacity = 1;
            rim.ParticlesEnabled = false;
            Arrange(surface, size);
            Check(HasPixels(rim), "particles-off-removes-halo");
            Check(Bounds(instrument, surface) == originalBounds, "particle-toggle-changes-metric-layout");
            Capture("particles-off", 0, saveShell: true);
            rim.ParticlesEnabled = true;
            rim.IsAnimationEnabled = false;
            Arrange(surface, size);
            var pausedTime = rim.SceneTimeSeconds;
            var pausedPixels = PixelBytes(Render(rim, rim.RenderSize));
            Arrange(surface, size);
            Check(pausedTime == rim.SceneTimeSeconds && pausedPixels.SequenceEqual(PixelBytes(Render(rim, rim.RenderSize))), "paused-frame-changes");
            Capture("paused", 0, saveShell: true);

            size = new Size(980, 750);
            Arrange(surface, size);
            Check(instrument.Shape == OrbitSurfaceShape.Card && rim.Shape == instrument.Shape, "compact-shape-binding");
            foreach (var radius in new[] { 8d, 48 })
            {
                surface.Resources["OrbitCornerRadius"] = new CornerRadius(radius);
                Arrange(surface, size);
                Check(rim.CornerRadius == instrument.CornerRadius, "compact-radius-binding");
                Capture($"compact-radius-{radius:0}", .4, saveShell: true);
            }
            surface.Resources["OrbitCornerRadius"] = new CornerRadius(28);
            window.SetDashboardDisplayMode(true);
            size = new Size(1920, 1080);
            Arrange(surface, size);
            Capture("display-mode", 1.2, saveShell: true);
            Check(window.IsDashboardDisplayMode, "display-mode-not-applied");
            window.SetDashboardDisplayMode(false);
            size = new Size(1440, 1000);
            Arrange(surface, size);
            Capture("returned-to-window", 0, saveShell: true);

            CaptureMotion();
            foreach (var dpi in new[] { 1d, 1.5 })
                benchmarks.Add(Benchmark(new Rect(12, 32, 1200, 360), dpi));
            Check(activations == 0 && new WindowInteropHelper(window).Handle == IntPtr.Zero, "window-was-shown-or-activated");
            Check(!rim.HasLifecycleSubscriptions && !rim.HasRenderingSubscription, "detached-review-attached-live-events");

            void Capture(string name, double seconds, bool saveShell)
            {
                bindings.Phase = name;
                rim.RefreshTarget();
                CheckGeometry(name);
                DrawSample(seconds);
                var bitmap = Render(surface, size);
                var hero = HeroBounds(bitmap);
                if (hero.Width > 0 && hero.Height > 0)
                    Save(new CroppedBitmap(bitmap, hero), name + "-hero.png");
                else Check(false, name + "/hero-outside-shell");
                if (saveShell) Save(bitmap, name + "-shell.png");
                captures.Add(new
                {
                    name,
                    seconds,
                    width = size.Width,
                    height = size.Height,
                    shape = instrument.Shape.ToString(),
                    radius = rim.CornerRadius.TopLeft,
                    glow = rim.GlowOpacity,
                    particles = rim.ParticlesEnabled,
                    instrument = Rectangle(Bounds(instrument, surface)),
                    rim = new { x = hero.X, y = hero.Y, width = hero.Width, height = hero.Height },
                    status = expectedStatus,
                    speed = speed.Text,
                    metricCells = metrics.Children.Cast<FrameworkElement>().Select(item => Rectangle(Bounds(item, instrument))).ToArray()
                });
            }

            void DrawSample(double seconds)
            {
                var visual = (DrawingVisual)VisualTreeHelper.GetChild(rim, 0);
                using (var drawing = visual.RenderOpen())
                {
                    if (rim.Accent is SolidColorBrush accent)
                    {
                        var bounds = rim.InstrumentBounds;
                        drawing.PushTransform(new TranslateTransform(bounds.X, bounds.Y));
                        renderer.Draw(drawing, new Rect(bounds.Size), rim.Shape, rim.EffectiveCornerRadius, accent.Color,
                            rim.GlowOpacity * accent.Opacity, rim.ParticlesEnabled, seconds, VisualTreeHelper.GetDpi(rim), rim.EffectiveRimWidth);
                        drawing.Pop();
                    }
                }
            }

            Int32Rect HeroBounds(BitmapSource bitmap)
            {
                var hero = Bounds(instrument, surface);
                hero = new Rect(hero.X - 36, hero.Y - 36, hero.Width + 72, hero.Height + 72);
                var left = Math.Max(0, (int)Math.Floor(hero.Left));
                var top = Math.Max(0, (int)Math.Floor(hero.Top));
                var right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(hero.Right));
                var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(hero.Bottom));
                return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            }

            void CaptureMotion()
            {
                bindings.Phase = "particle-motion-sequence";
                var accent = new SolidColorBrush(Colors.Cyan); accent.Freeze();
                surface.Resources["AccentBrush"] = accent;
                rim.GlowOpacity = 1;
                rim.ParticlesEnabled = true;
                Arrange(surface, size);
                rim.RefreshTarget();
                CheckGeometry(bindings.Phase);
                var originalInstrument = Bounds(instrument, surface);
                var originalTime = rim.SceneTimeSeconds;
                byte[]? firstFrame = null;
                var motionObserved = false;
                for (var frame = 0; frame < 48; frame++)
                {
                    DrawSample(frame / 24d);
                    var bitmap = Render(surface, size);
                    var hero = HeroBounds(bitmap);
                    if (hero.Width <= 0 || hero.Height <= 0)
                    {
                        Check(false, "particle-motion-sequence/hero-outside-shell");
                        break;
                    }
                    var crop = new CroppedBitmap(bitmap, hero);
                    var name = $"particle-motion-{frame:000}.png";
                    Save(crop, name);
                    animationFrames.Add(name);
                    if (frame == 0) firstFrame = PixelBytes(crop);
                    else if (frame == 24) motionObserved = firstFrame is not null && !firstFrame.SequenceEqual(PixelBytes(crop));
                }
                Check(animationFrames.Count == 48 && motionObserved, "particle-motion-sequence/frames-do-not-change");
                Check(Bounds(instrument, surface) == originalInstrument && rim.SceneTimeSeconds == originalTime,
                    "particle-motion-sequence/changes-layout-or-live-clock");
                WriteMotionPreview(output, animationFrames);
            }

            void CheckGeometry(string phase)
            {
                var expected = Bounds(instrument, surface);
                var actual = rim.TransformToAncestor(surface).TransformBounds(rim.InstrumentBounds);
                Check(Near(expected, actual), phase + "/effect-misses-instrument-contour");
                Check(metrics.Children.Count == 5, phase + "/dashboard-metric-count-changed");
                Check(speed.Text == expectedSpeed && !string.IsNullOrWhiteSpace(speed.Text), phase + "/speed-changed");
                var status = Descendants(metrics.Children[4]).OfType<TextBlock>().FirstOrDefault(item => item.Text == expectedStatus);
                Check(status is not null && VisibleInSurface(status, surface) && status.ActualWidth > 0 && status.ActualHeight > 0,
                    phase + "/fifth-status-cell-missing");
                for (var index = 0; index < metrics.Children.Count; index++)
                {
                    var child = (FrameworkElement)metrics.Children[index];
                    var bounds = Bounds(child, instrument);
                    Check(bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= instrument.ActualWidth + .5 &&
                        bounds.Bottom <= instrument.ActualHeight + .5, phase + "/metric-outside-instrument-" + index);
                }
                Check(!rim.HasRenderingSubscription, phase + "/unexpected-animation-subscription");
            }
            void Save(BitmapSource image, string name)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
            }
            void Check(bool condition, string name) { if (!condition) failures.Add(name); }
        }
        catch (Exception exception)
        {
            failures.Add(bindings.Phase + "/" + exception.GetType().Name);
        }
        finally
        {
            if (window?.FindName("DashboardRim") is DashboardRimEffect finalRim) finalRim.TargetElement = null;
            window?.Close();
            if (controller is not null) Await(controller.DisposeAsync().AsTask());
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousSynchronization);
        }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "dashboard-rim-review.json"), JsonSerializer.Serialize(new
        {
            method = "Detached actual MainWindow with synthetic telemetry and output-local settings. Native-resolution software PNGs and a local HTML frame player; no shown window, game access, live service or focus changes. Time samples call only the rim drawing helper; they do not advance the live control clock.",
            measurement = "CPU preparation plus DrawingContext command recording; one cold frame, 60 warming frames, 300 measured frames. Excludes GPU/compositor/display delivery. Layout and pixel checks do not establish gameplay performance.",
            motionPreview = new { file = "particle-motion.html", samplesPerSecond = 24, durationSeconds = 2, frames = animationFrames, note = "Finite sampled preview of actual WPF output. Browser playback is not a measurement of app frame delivery." },
            captures,
            benchmarks,
            failures,
            ownWindowActivations = activations,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Dashboard rim review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} cases; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static object Benchmark(Rect bounds, double dpi)
    {
        var renderer = new DashboardRimDrawing(); var visual = new DrawingVisual();
        void Draw(double seconds)
        {
            using var drawing = visual.RenderOpen();
            renderer.Draw(drawing, bounds, OrbitSurfaceShape.Swept, new CornerRadius(28), Colors.Cyan, 1, true, seconds, new DpiScale(dpi, dpi));
        }
        var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp(); Draw(0);
        var coldMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        for (var frame = 1; frame <= 60; frame++) Draw(frame / 60d);
        var haloBuildsBefore = renderer.HaloBuildCount; var anchorBuildsBefore = renderer.AnchorBuildCount;
        var times = new double[300]; before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < times.Length; frame++)
        {
            start = Stopwatch.GetTimestamp(); Draw((frame + 61) / 60d);
            times[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        var warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(times);
        return new
        {
            dpi = dpi * 96,
            coldMilliseconds,
            coldBytes,
            frames = times.Length,
            meanMilliseconds = times.Average(),
            p95Milliseconds = times[(int)(times.Length * .95) - 1],
            maximumMilliseconds = times[^1],
            warmAllocatedBytes = warmBytes,
            allocatedBytesPerFrame = warmBytes / (double)times.Length,
            haloRebuilds = renderer.HaloBuildCount - haloBuildsBefore,
            anchorRebuilds = renderer.AnchorBuildCount - anchorBuildsBefore,
            renderer.CachedSparkBrushCount
        };
    }

    private static void WriteMotionPreview(string output, IReadOnlyList<string> frames)
    {
        var html = """
            <!doctype html>
            <html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Wisp rim particle motion</title>
            <style>
            :root{color-scheme:dark;font:16px system-ui,sans-serif;background:#0d141b;color:#e9f1f6}
            body{margin:0;padding:24px;max-width:1480px;margin-inline:auto}h1{font-size:24px;margin:0 0 8px}
            p{color:#b4c2cd;line-height:1.5;max-width:900px}.viewport{overflow:auto;border:1px solid #304450;background:#10171f}
            #frame{display:block;max-width:100%;height:auto}button,input{accent-color:#54e6db}button{font:inherit;background:#1c303b;color:inherit;border:1px solid #56717f;border-radius:6px;padding:8px 16px;cursor:pointer}
            button:focus-visible,input:focus-visible,a:focus-visible{outline:2px solid #54e6db;outline-offset:3px}.controls{display:flex;flex-wrap:wrap;align-items:center;gap:14px;margin:18px 0}
            #scrub{flex:1;min-width:180px;max-width:700px}details{margin-top:24px}.sheet{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin-top:12px}.sheet img{width:100%;display:block}.sheet a{color:#b4c2cd;text-decoration:none;font-size:14px}.sheet span{display:block;padding:5px 0}
            @media(max-width:700px){body{padding:14px}.sheet{grid-template-columns:1fr}}
            </style>
            <h1>Wisp rim particle motion</h1>
            <p>Two seconds of actual WPF-rendered frames. The dashboard stays still while particles move. Use the slider to inspect individual frames or open a reference image at its original size.</p>
            <div class="viewport"><img id="frame" alt="Wisp dashboard with its oval particle effect"></div>
            <div class="controls"><button id="play" type="button" disabled>Play two seconds</button><label for="scrub">Frame</label><input id="scrub" type="range" min="0" max="47" value="0" step="1" disabled><output id="time" for="scrub">0.00 s</output></div>
            <p>48 samples at 24 samples/sec; playback stops at the end. This is an appearance and motion preview, not a measurement of the live app’s rendering speed.</p>
            <details><summary>Reference frames</summary><div id="sheet" class="sheet"></div></details>
            <script>
            const files=__FRAMES__,fps=24,img=document.querySelector('#frame'),play=document.querySelector('#play'),scrub=document.querySelector('#scrub'),time=document.querySelector('#time'),sheet=document.querySelector('#sheet');
            let running=false,start=0,request=0,current=0;
            function show(index){current=Math.min(files.length-1,Math.max(0,index));img.src=files[current];scrub.value=current;time.value=(current/fps).toFixed(2)+' s';}
            function stop(){running=false;cancelAnimationFrame(request);play.textContent='Play two seconds';}
            function tick(now){const index=Math.floor((now-start)*fps/1000);show(index);if(index>=files.length){stop();return;}request=requestAnimationFrame(tick);}
            play.addEventListener('click',()=>{if(running){stop();return;}running=true;start=performance.now();play.textContent='Pause';show(0);request=requestAnimationFrame(tick);});
            scrub.addEventListener('input',()=>{stop();show(Number(scrub.value));});
            document.addEventListener('visibilitychange',()=>{if(document.hidden)stop();});
            for(const index of [0,9,19,28,38,47]){if(!files[index])continue;const link=document.createElement('a'),picture=document.createElement('img'),caption=document.createElement('span');link.href=files[index];link.target='_blank';link.rel='noopener';picture.src=files[index];picture.alt='Rim particles at '+(index/fps).toFixed(2)+' seconds';caption.textContent=(index/fps).toFixed(2)+' s';link.append(picture,caption);sheet.append(link);}
            if(files.length){scrub.max=files.length-1;show(0);Promise.all(files.map(src=>new Promise((resolve,reject)=>{const frame=new Image();frame.onload=resolve;frame.onerror=reject;frame.src=src;}))).then(()=>{play.disabled=false;scrub.disabled=false;}).catch(()=>{time.value='A frame could not be loaded.';});}
            </script></html>
            """;
        File.WriteAllText(Path.Combine(output, "particle-motion.html"), html.Replace("__FRAMES__", JsonSerializer.Serialize(frames), StringComparison.Ordinal));
    }

    private static T Required<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException("Missing rim-review element.");
    private static Rect Bounds(FrameworkElement child, Visual parent) => child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
    private static object Rectangle(Rect bounds) => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
    private static bool Near(Rect first, Rect second) => Math.Abs(first.X - second.X) < .5 && Math.Abs(first.Y - second.Y) < .5 &&
        Math.Abs(first.Width - second.Width) < .5 && Math.Abs(first.Height - second.Height) < .5;
    private static bool VisibleInSurface(FrameworkElement element, FrameworkElement surface)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && visual.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, surface)) return true;
        }
        return false;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static RenderTargetBitmap Render(Visual visual, Size size)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); return bitmap;
    }
    private static byte[] PixelBytes(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static bool HasPixels(FrameworkElement element) => PixelBytes(Render(element, element.RenderSize)).Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0);
    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }
    private static void Await(Task task)
    {
        while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        task.GetAwaiter().GetResult();
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
