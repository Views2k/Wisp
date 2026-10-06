using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;

namespace Wisp.UiReview;

internal static class LapReview3DPerformanceReview
{
    internal static int Run(string source, string output, Func<ResourceDictionary> loadResources)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = new List<string>();
        var measurements = new List<Measurement>();
        string? sourceSha256 = null;
        var sourceSamples = 0; var lapPoints = 0; var maximumPoints = 0; var realSegments = 0; var stressSegments = 0;
        double elevationSpan = 0;
        IReadOnlyDictionary<string, int> previewContacts = new Dictionary<string, int>();
        LapReviewTrack3D? track = null;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var path = Path.GetFullPath(source);
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".wisprun", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                throw new InvalidDataException("A saved run with its original library filename is required.");
            sourceSha256 = Hash(path);
            var load = new RunStore(Path.GetDirectoryName(path)!).LoadAsync(id);
            Await(load);
            var run = load.GetAwaiter().GetResult();
            sourceSamples = run.Samples.Length;
            LapReviewResult result = null!;
            Measure("recorded-lap-analysis", 1, () => result = LapReviewAnalysis.Build(run));
            var lap = result.Laps.OrderByDescending(item => item.IsComplete).ThenByDescending(item => item.Points.Length).FirstOrDefault();
            if (lap is null || lap.Points.Length < 2) throw new InvalidDataException("No reviewable recorded lap was found.");
            lapPoints = lap.Points.Length;
            elevationSpan = lap.Points.Max(point => point.Position.Y) - lap.Points.Min(point => point.Position.Y);
            var data = new LapReviewPlotData(lap, null, null, LapReviewChannel.Speed, SpeedUnit.MilesPerHour,
                0, 0, lap.Points.Length - 1);
            track = new LapReviewTrack3D { Data = data };
            var surface = new Border { Background = (Brush)application.FindResource("WindowBrush"), Child = track };
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            LapReviewUiChecks.Arrange(surface, new(1280, 720));
            Measure("recorded-3d-scene-preparation", 1, () => Await(track.PrepareForTestAsync()));
            Check(track.IsReady, "recorded-scene-ready");
            realSegments = track.PreparedSegmentCount;
            Check(realSegments is > 0 and < LapReviewTrackGeometry.MaximumPathPoints, "recorded-scene-bounded");
            var bounds = LapReviewTrackBounds.From(data);
            var normalizedSpan = lap.Points.Max(point => bounds.Normalize(point.Position).Y) -
                lap.Points.Min(point => bounds.Normalize(point.Position).Y);
            Check(Math.Abs(normalizedSpan * bounds.Span - elevationSpan) < .001, "recorded-elevation-scale-preserved");
            Capture(surface, "recorded-3d-default");

            var builds = track.SceneBuildCount;
            const int changes = 360;
            Measure("camera-property-updates-no-render", changes, () =>
            {
                for (var i = 0; i < changes; i++)
                {
                    track.Yaw = i - 180; track.Pitch = 30 + 15 * Math.Sin(i * Math.PI / 180);
                    track.Roll = 10 * Math.Sin(i * Math.PI / 90); track.ZoomFactor = 1 + .2 * Math.Sin(i * Math.PI / 180);
                }
            });
            Check(track.SceneBuildCount == builds, "camera-keeps-prepared-scene");
            Measure("cursor-property-updates-no-render", changes, () =>
            {
                for (var i = 0; i < changes; i++) track.Data = data with { Cursor = i * (lap.Points.Length - 1) / (changes - 1) };
            });
            Check(track.SceneBuildCount == builds, "cursor-keeps-prepared-scene");
            Check(track.ProjectPoint(lap.Points.Length / 2) is var p && double.IsFinite(p.X) && double.IsFinite(p.Y), "recorded-point-projects");
            track.Yaw = -65; track.Pitch = 18; track.Roll = 5; track.ZoomFactor = 1;
            Capture(surface, "recorded-3d-rotated");
            CaptureActualView(run, Path.GetDirectoryName(path)!);

            // Dense interpolation of this recorded geometry exercises the accepted size limit;
            // it is a synthetic stress case, not another measured gameplay recording.
            LapReviewLap dense = null!;
            Measure("synthetic-dense-fixture", 1, () => dense = Densify(lap));
            maximumPoints = dense.Points.Length;
            Measure("synthetic-180000-point-scene-preparation", 1, () =>
            {
                track.Data = data with { Lap = dense, Cursor = 0, SectionEnd = dense.Points.Length - 1 };
                Await(track.PrepareForTestAsync());
            });
            stressSegments = track.PreparedSegmentCount;
            Check(track.IsReady && maximumPoints == LapReviewAnalysis.MaximumPoints, "maximum-source-size-ready");
            Check(stressSegments is > 0 and < LapReviewTrackGeometry.MaximumPathPoints, "maximum-scene-bounded");
            builds = track.SceneBuildCount;
            Measure("maximum-cursor-updates-no-render", changes, () =>
            {
                var denseData = track.Data!;
                for (var i = 0; i < changes; i++) track.Data = denseData with { Cursor = i * (maximumPoints - 1) / (changes - 1) };
            });
            Check(track.SceneBuildCount == builds, "maximum-cursor-keeps-prepared-scene");
            Capture(surface, "synthetic-180000-point-3d");
            Check(PresentationSource.FromVisual(surface) is null && application.Windows.Count == 0, "no-presentation-window");
            Check(Hash(path) == sourceSha256, "original-run-unchanged");
        }
        catch (Exception error) { failures.Add(error.GetType().Name); }
        finally
        {
            if (track is not null) { track.Data = null; Await(track.PrepareForTestAsync()); }
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "lap-review-3d-performance.json"), JsonSerializer.Serialize(new
        {
            method = "Read-only stored lap analysis and actual WPF 3D scene. Detached 1280x720 software rendering and the production Lap Review view at 1280x900; its map PNG uses the production bounded 2x export. No game, window, controller, UDP listener or settings access. Camera/cursor timings measure CPU property updates, not rendered or displayed FPS. Allocation deltas are process-wide managed allocations. The 180000-point case is dense interpolation of recorded geometry, not gameplay.",
            sourceSha256,
            sourceSamples,
            lapPoints,
            elevationSpanMeters = elevationSpan,
            realSegments,
            maximumPoints,
            stressSegments,
            previewContactEvidence = new
            {
                countsByKind = previewContacts,
                note = "Only production contact analysis and original stored run markers are used. No synthetic contact markers are added. PossibleContact entries are estimates, not confirmed collisions."
            },
            measurements,
            failures,
            bindingDiagnosticCount = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Lap review 3D: {(failures.Count == 0 ? "PASS" : "FAIL")}; {lapPoints} recorded points; {maximumPoints} synthetic stress points; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void Check(bool condition, string code) { if (!condition) failures.Add(code); }
        void Measure(string name, int operations, Action action)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew(); action(); timer.Stop();
            measurements.Add(new(name, operations, timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - allocated));
        }
        void Capture(FrameworkElement surface, string name)
        {
            LapReviewUiChecks.Arrange(surface, new(1280, 720));
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            Measure(name + "-software-render", 1, () => bitmap.Render(surface));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew, FileAccess.Write);
            encoder.Save(file);
        }
        void CaptureActualView(RecordedRun run, string runDirectory)
        {
            var settings = new AppSettings { StartWithWindows = false, StartWithForza = false, AutomaticApplicationUpdateChecks = false };
            using var model = new LapReviewViewModel(settings, new RunStore(runDirectory),
                () => throw new InvalidOperationException("The preview must not save settings."));
            var view = new LapReviewView { DataContext = model };
            // Lap Review normally inherits its selector templates from RunsPage.
            // Reuse those exact resources for this detached production preview.
            view.Resources.MergedDictionaries.Add(new RunsPage().Resources);
            var scroll = new ScrollViewer
            {
                Content = view,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var preview = new Border
            {
                Padding = new Thickness(16),
                Background = (Brush)application.FindResource("WindowBrush"),
                Child = scroll
            };
            var size = new Size(1280, 900);
            VisualTreeHelper.SetRootDpi(preview, new DpiScale(1, 1));
            var actualTrack = (LapReviewTrack3D)view.FindName("Track3D");
            try
            {
                Measure("production-view-lap-analysis", 1, () =>
                {
                    model.SetRuns(run, null);
                    var timeout = Stopwatch.StartNew();
                    do { Await(Task.Delay(10)); } while (model.IsBusy && timeout.Elapsed < TimeSpan.FromSeconds(20));
                    if (model.IsBusy || !model.HasLap) throw new InvalidOperationException("The recorded lap preview did not become ready.");
                });
                model.Is3D = true;
                LapReviewUiChecks.Arrange(preview, size);
                Measure("production-view-3d-preparation", 1, () => Await(actualTrack.PrepareForTestAsync()));
                actualTrack.ResetView();
                LapReviewUiChecks.Arrange(preview, size);
                Check(actualTrack.IsReady, "production-map-ready");
                Check(ReferenceEquals(actualTrack.Data?.Lap, model.Lap), "production-map-uses-selected-recorded-lap");
                previewContacts = (model.Plot.Contacts ?? Array.Empty<LapReviewContact>())
                    .GroupBy(contact => contact.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count());

                BitmapSource map = null!;
                Measure("production-map-export-render", 1, () => map = view.CaptureMapImage());
                Check(map.IsFrozen && map.PixelWidth > 0 && map.PixelWidth <= 3840 && map.PixelHeight > 0 && map.PixelHeight <= 2160,
                    "production-map-export-frozen-and-bounded");
                Await(RunImageExporter.WriteAsync(map, Path.Combine(output, "actual-map-export.png")));

                var toolbar = view.FindName("MapToolbar") as FrameworkElement ?? (FrameworkElement)view.FindName("MapExportSurface");
                scroll.ScrollToVerticalOffset(Math.Max(0, toolbar.TranslatePoint(new Point(), view).Y - 10));
                LapReviewUiChecks.Arrange(preview, size);
                var fullView = new RenderTargetBitmap(2560, 1800, 192, 192, PixelFormats.Pbgra32);
                Measure("production-view-software-render", 1, () => fullView.Render(preview));
                fullView.Freeze();
                Await(RunImageExporter.WriteAsync(fullView, Path.Combine(output, "actual-lap-review-3d.png")));
                Check(PresentationSource.FromVisual(preview) is null && application.Windows.Count == 0,
                    "production-preview-has-no-window");
            }
            finally
            {
                view.DataContext = null;
                actualTrack.Data = null;
                Await(actualTrack.PrepareForTestAsync());
            }
        }
    }

    private static LapReviewLap Densify(LapReviewLap lap)
    {
        var points = new LapReviewPoint[LapReviewAnalysis.MaximumPoints];
        var previous = -1;
        for (var i = 0; i < points.Length; i++)
        {
            var position = i * (lap.Points.Length - 1d) / (points.Length - 1);
            var index = Math.Min((int)position, lap.Points.Length - 2);
            var t = position - index;
            var a = lap.Points[index]; var b = lap.Points[index + 1];
            points[i] = a with
            {
                SampleIndex = i,
                Position = new((float)Mix(a.Position.X, b.Position.X, t), (float)Mix(a.Position.Y, b.Position.Y, t), (float)Mix(a.Position.Z, b.Position.Z, t)),
                DistanceMeters = Mix(a.DistanceMeters, b.DistanceMeters, t),
                LapSeconds = Mix(a.LapSeconds, b.LapSeconds, t),
                RunSeconds = Mix(a.RunSeconds, b.RunSeconds, t),
                BreakBefore = i == 0 || index != previous && a.BreakBefore
            };
            previous = index;
        }
        return lap with { Points = points };
    }
    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private sealed record Measurement(string Name, int Operations, double TotalMilliseconds, long ManagedAllocatedBytes);
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
