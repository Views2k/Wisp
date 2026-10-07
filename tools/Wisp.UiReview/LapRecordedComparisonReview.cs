using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal static class LapRecordedComparisonReview
{
    internal static int Run(IReadOnlyList<string> sourceFiles, string output, Func<ResourceDictionary> loadResources)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        if (sourceFiles.Count != 3) throw new ArgumentException("Select exactly three saved run files.", nameof(sourceFiles));
        var originals = sourceFiles.Select(Path.GetFullPath).ToArray();
        if (originals.Any(path => !string.Equals(Path.GetExtension(path), ".wisprun", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)))
            throw new ArgumentException("Select saved runs with GUID filenames.", nameof(sourceFiles));
        var ids = originals.Select(path => Guid.ParseExact(Path.GetFileNameWithoutExtension(path), "N").ToString("N")).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("Select three distinct saved runs.", nameof(sourceFiles));
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        Directory.CreateDirectory(output);
        var isolated = Path.Combine(output, "isolated-run-library"); Directory.CreateDirectory(isolated);
        var failures = new List<string>(); var proofs = new List<object>();
        var before = originals.Select(Hash).ToArray();
        for (var i = 0; i < originals.Length; i++) File.Copy(originals[i], Path.Combine(isolated, ids[i] + ".wisprun"), overwrite: false);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var receiver = new TelemetryUdpReceiver();
        var service = new RunRecordingService(receiver, isolated);
        RunsViewModel? model = null; RunsPage? page = null;
        var stage = "load-resources";
        var exceptionDetails = new List<object>();
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            stage = "construct-runs-page";
            var settings = new AppSettings { StartWithWindows = false, StartWithForza = false, AutomaticApplicationUpdateChecks = false };
            model = new RunsViewModel(service, settings, Dispatcher.CurrentDispatcher);
            page = new RunsPage { DataContext = model };
            var surface = new Border { Background = (Brush)application.FindResource("WindowBrush"), Child = page };
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            stage = "initialize-isolated-library";
            Await(model.InitializeAsync());
            Check(model.Library.Count == 3, "only-three-isolated-runs-loaded");
            stage = "select-run-A";
            model.SelectedRun = model.Library.Single(item => item.Id == Guid.ParseExact(ids[0], "N")); Ready();
            foreach (var id in ids.Skip(1))
            {
                stage = "compare-run-" + id;
                model.ComparisonChoice = model.Library.Single(item => item.Id == Guid.ParseExact(id, "N"));
                Check(model.CompareCommand.CanExecute(null), "compare-command-enabled-" + id);
                model.CompareCommand.Execute(null); Ready(); model.ShowSummary(); Ready();
                stage = "locate-lap-view-" + id;
                var expander = (Expander)page.FindName("LapReviewExpander"); expander.IsExpanded = true;
                Arrange(new(980, 750));
                var view = Descendants(expander).OfType<LapReviewView>().Single();
                var track = (LapReviewTrack3D)view.FindName("Track3D");
                var chosenReference = -1;
                track.ReferencePointChosen += index => chosenReference = index;
                var viewport = (FrameworkElement)view.FindName("MapViewport");
                var scrub = (FrameworkElement)view.FindName("ScrubBar");
                var review = model.LapReview;
                Check(review.Lap?.RunId == Guid.ParseExact(ids[0], "N") && review.Reference?.RunId == Guid.ParseExact(id, "N"), "actual-selected-pair-" + id);
                Check(review.Plot.Comparison?.CanCompare == false, "different-car-benchmark-unavailable-" + id);
                review.Is3D = true; review.SharedSpace = true;
                foreach (var size in new[] { new Size(720, 440), new Size(980, 750), new Size(1920, 1080) })
                {
                    var name = "recorded-" + id[..8] + "-" + size.Width + "x" + size.Height;
                    bindings.Phase = name;
                    stage = name + "/prepare-scene";
                    view.RequestedMapHeight = double.NaN; Arrange(size); Await(track.PrepareForTestAsync()); Pump();
                    track.ResetView();
                    var scroll = (ScrollViewer)page.FindName("RunsScroll");
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + scrub.TranslatePoint(new Point(), scroll).Y);
                    Arrange(size);
                    Check(track.IsReady && track.IsComparison && track.HasBothPreparedModels && track.PreparedReferenceSegmentCount > 0, name + "/two-real-models");
                    Check(LapReviewColorRange.From(track.Data!) == LapReviewColorRange.From(track.ComparisonData!), name + "/shared-color-range");
                    var bounds = LapReviewTrackBounds.From(track.Data!, track.ComparisonData!.Lap);
                    var aDistance = SpatialDistance(track.Data!, bounds); var bDistance = SpatialDistance(track.ComparisonData!, bounds);
                    Check(aDistance > 0 && bDistance > 0, name + "/same-xyz-normalization");
                    var slider = (Slider)view.FindName("LapCursorSlider");
                    var buildsBeforeScrub = track.SceneBuildCount;
                    ((RadioButton)view.FindName("ScrubBoth")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    slider.SetCurrentValue(RangeBase.ValueProperty, (double)review.MaximumScrubCursor); Pump();
                    Check(review.Cursor == review.MaximumCursor && track.ComparisonData!.Cursor == review.Reference!.Points.Length - 1,
                        name + "/both-reaches-end-of-each-recorded-lap");
                    ((RadioButton)view.FindName("ScrubB")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    slider.SetCurrentValue(RangeBase.ValueProperty, 80d); Pump();
                    Check(review.Cursor == review.MaximumCursor && track.ComparisonData!.Cursor == 80, name + "/B-slider-holds-A");
                    ((RadioButton)view.FindName("ScrubA")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    slider.SetCurrentValue(RangeBase.ValueProperty, 150d); Pump();
                    Check(review.Cursor == 150 && track.ComparisonData!.Cursor == 80, name + "/A-slider-holds-B");
                    ((RadioButton)view.FindName("ScrubBoth")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true); Pump();
                    slider.SetCurrentValue(RangeBase.ValueProperty, review.MaximumScrubCursor / 2d); Pump();
                    Check(track.SceneBuildCount == buildsBeforeScrub && review.Plot.Comparison?.CanCompare == false,
                        name + "/scrubbing-preserves-meshes-and-benchmark-validity");
                    var cameraChecks = new List<object>();
                    foreach (var focus in new[] { 0, 1, 2 })
                    {
                        track.ResetView();
                        if (focus != 0) { track.FocusLap(focus); track.CompleteCameraMotionForTest(); }
                        var camera = (OrthographicCamera)track.Children.OfType<Viewport3D>().Single().Camera;
                        var widthBefore = camera.Width; var zoomBefore = track.ZoomFactor;
                        var buildsBefore = track.SceneBuildCount;
                        var cursorBefore = review.Cursor; var referenceCursorBefore = track.ComparisonData!.Cursor;
                        track.Yaw += 70; track.Pitch = 65; track.Roll = 32;
                        var widthAfter = camera.Width;
                        Check(Math.Abs(widthAfter - widthBefore) < 1e-10 && track.ZoomFactor == zoomBefore,
                            name + "/rotation-preserves-scale-" + focus);
                        Check(review.Cursor == cursorBefore && track.ComparisonData!.Cursor == referenceCursorBefore &&
                            track.SceneBuildCount == buildsBefore, name + "/rotation-preserves-selection-and-models-" + focus);
                        cameraChecks.Add(new { focus, widthBefore, widthAfter, zoomBefore, zoomAfter = track.ZoomFactor });
                    }
                    track.ResetView();
                    stage = name + "/select-reference";
                    var cursorA = review.Cursor; var indexB = Math.Min(100, review.Reference!.Points.Length - 1);
                    track.Choose(track.ProjectReferencePoint(indexB)); track.CompleteCameraMotionForTest(); Pump();
                    Check(review.Cursor == cursorA && chosenReference >= 0 && track.ComparisonData?.Cursor == chosenReference && track.FocusedLap == 2, name + "/B-selection-independent");
                    track.ShowAll(); track.CompleteCameraMotionForTest(); Pump();
                    Check(track.FocusedLap == 0 && track.HasBothPreparedModels, name + "/overview-retains-both");
                    stage = name + "/resize";
                    var originalHeight = viewport.ActualHeight;
                    view.ResizeMapBy(150); Arrange(size);
                    Check(viewport.ActualHeight > originalHeight, name + "/resize-grows-map");
                    view.RequestedMapHeight = double.NaN; Arrange(size);
                    Check(Math.Abs(viewport.ActualHeight - originalHeight) < 1, name + "/automatic-height-restored");
                    Check(scrub.ActualWidth > 0 && scrub.ActualWidth <= view.ActualWidth + 1 &&
                        viewport.TranslatePoint(new Point(), view).Y >= scrub.TranslatePoint(new Point(0, scrub.ActualHeight), view).Y,
                        name + "/scrub-precedes-map-within-width");
                    stage = name + "/capture-page";
                    Capture(surface, size, name + ".png");
                    stage = name + "/export-map";
                    var png = view.CaptureMapImage();
                    Check(png.PixelWidth > 100 && png.PixelHeight > 100, name + "/PNG-has-map-and-legend");
                    if (size.Width == 980) Save(png, name + "-map.png");
                    proofs.Add(new
                    {
                        pair = new[] { ids[0], id },
                        cars = new[] { review.Lap!.CarOrdinal, review.Reference.CarOrdinal },
                        points = new[] { review.Lap.Points.Length, review.Reference.Points.Length },
                        size = new { width = size.Width, height = size.Height },
                        map = new { width = viewport.ActualWidth, height = viewport.ActualHeight },
                        segments = new[] { track.PreparedSegmentCount, track.PreparedReferenceSegmentCount },
                        benchmarkAvailable = review.Plot.Comparison?.CanCompare,
                        sharedBoundsSpan = bounds.Span,
                        aDistance,
                        bDistance,
                        cameraChecks,
                        png = new { width = png.PixelWidth, height = png.PixelHeight }
                    });
                }
            }
            Check(!receiver.IsRunning && !service.IsRecording, "no-receiver-or-recording");
            Check(application.Windows.Count == 0 && PresentationSource.FromVisual(surface) is null, "no-shown-window");

            void Arrange(Size size) { LapReviewUiChecks.Arrange(surface, size); Pump(); surface.UpdateLayout(); }
            void Ready()
            {
                var timer = Stopwatch.StartNew();
                do { Await(Task.Delay(10)); Pump(); }
                while ((model.IsBusy || model.IsPreparingCharts || model.LapReview.IsBusy) && timer.Elapsed < TimeSpan.FromSeconds(20));
                if (model.IsBusy || model.IsPreparingCharts || model.LapReview.IsBusy || model.HasError)
                    throw new InvalidOperationException("Recorded comparison did not finish.");
            }
        }
        catch (Exception error)
        {
            failures.Add(stage + "/" + error.GetType().Name);
            for (Exception? current = error; current is not null; current = current.InnerException)
                exceptionDetails.Add(new
                {
                    stage,
                    errorType = current.GetType().FullName,
                    methods = (new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? []).Select(frame => frame.GetMethod())
                        .Where(method => method is not null).Take(16).Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray(),
                    libraryCount = model?.Library.Count,
                    hasError = model?.HasError,
                    busy = model?.IsBusy,
                    chartsBusy = model?.IsPreparingCharts,
                    lapBusy = model?.LapReview.IsBusy
                });
        }
        finally
        {
            if (page is not null) page.DataContext = null;
            model?.Dispose(); Await(service.DisposeAsync().AsTask()); Await(receiver.DisposeAsync().AsTask());
            application.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            for (var i = 0; i < originals.Length; i++) Check(Hash(originals[i]) == before[i], "original-unchanged-" + ids[i]);
        }
        Check(bindings.TotalCount == 0, "binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "recorded-lap-comparison.json"), JsonSerializer.Serialize(new
        {
            method = "Three explicit saved runs copied to an isolated library. Actual RunsPage library selection and CompareCommand; detached software rendering. No displayed-frame or live-input performance claim.",
            sourceIds = ids,
            sourceHashes = before,
            proofs,
            failures,
            exceptionDetails,
            bindingDiagnosticCount = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Recorded comparison: {(failures.Count == 0 ? "PASS" : "FAIL")}; {proofs.Count} layouts; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void Check(bool condition, string code) { if (!condition) failures.Add(code); }
        void Capture(Visual visual, Size size, string name)
        {
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); Save(bitmap, name);
        }
        void Save(BitmapSource bitmap, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write); encoder.Save(file);
        }
    }

    private static double SpatialDistance(LapReviewPlotData data, LapReviewTrackBounds bounds)
    {
        var points = data.Lap!.Points; var a = points[0].Position; var b = points[points.Length / 2].Position;
        var original = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
        var normalized = (bounds.Normalize(b) - bounds.Normalize(a)).Length * bounds.Span;
        if (Math.Abs(original - normalized) > .01) throw new InvalidOperationException("Recorded position scale changed.");
        return original;
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
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
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            yield return item;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) queue.Enqueue(child);
            for (var i = 0; item is Visual && i < VisualTreeHelper.GetChildrenCount(item); i++) queue.Enqueue(VisualTreeHelper.GetChild(item, i));
        }
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
