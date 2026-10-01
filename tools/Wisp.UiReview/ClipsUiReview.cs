using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal static class ClipsUiReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Action<FrameworkElement, int> setDpi)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var failures = new List<string>();
        var captures = new List<object>();
        var layouts = new List<object>();
        var boundsFailures = new List<object>();
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousContext = SynchronizationContext.Current;
        var phase = "initialize";
        var operation = "initialize";
        object? exceptionDiagnostic = null;
        var checks = 0;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            var libraryPath = Path.Combine(output, "synthetic-library-not-playable");
            Directory.CreateDirectory(libraryPath);
            File.WriteAllText(Path.Combine(libraryPath, "SYNTHETIC.txt"),
                "UI fixture only. The GUID.mp4 files and synthetic export copies contain placeholder bytes, not video. Do not play or import them. No captured media exists here.");
            var recorder = new FakeRecorder();
            var posters = new FakePosters();
            var fixtureContext = new DiagnosticsViewModel(new AppSettings
            {
                StartWithWindows = false,
                StartWithForza = false,
                AutomaticApplicationUpdateChecks = false
            });
            using var model = new ClipsViewModel(new ClipsSettings { StorageDirectory = libraryPath },
                recorder, Dispatcher.CurrentDispatcher, posters, libraryDirectory: libraryPath);
            var page = new ClipsPage { DataContext = model };
            var surface = new Border
            {
                Padding = new Thickness(16),
                Background = Theme("WindowBrush"),
                DataContext = fixtureContext,
                Child = page
            };
            try
            {
                Await(model.InitializeAsync());
                Layout(new(980, 750), 96);
                phase = "empty";
                Check(model.IsEmpty && model.Clips.Count == 0 && !model.HasSelection, "empty-state");
                Check(!model.SaveClipCommand.CanExecute(null) && !model.PreviousPageCommand.CanExecute(null) &&
                    !model.NextPageCommand.CanExecute(null), "empty-actions-disabled");
                CheckTimelineReset("initial-timeline-disabled");
                var audioToggle = (CheckBox)page.FindName("CaptureSystemAudioToggle");
                Check(audioToggle.IsChecked == false && audioToggle.Style is not null && audioToggle.IsEnabled,
                    "system-audio-default-off-and-themed");
                Capture("empty", surface, new(980, 750), 96);
                CheckSelectors();

                phase = "seed";
                var library = new ClipLibrary(libraryPath);
                var spec = new ClipRecordingSpec(60, 1080, 60, 75);
                for (var index = 0; index < 25; index++)
                {
                    var target = Await(library.ReserveSaveAsync(spec));
                    using (var file = new FileStream(target.MediaPath, FileMode.CreateNew, FileAccess.Write))
                        file.Write("WISP SYNTHETIC UI PLACEHOLDER"u8);
                    var bytes = new FileInfo(target.MediaPath).Length;
                    var entry = Await(library.CommitFinalizedAsync(target.Id,
                        new FinalizedClipMedia(bytes, 1920, 1080, 60, 0, 600_000_000, true)));
                    if (index < 3) Await(library.MarkViewedAsync(entry.Id));
                }
                Await(model.LoadPageAsync(0));
                model.SetGalleryActive(true);
                Await(model.ThumbnailCompletion);
                Check(model.Clips.Count == 25 && model.Clips.All(item => item.HasThumbnail), "25-generated-posters");
                Check(model.NewClipCount == 22 && !model.PreviousPageCommand.CanExecute(null) &&
                    !model.NextPageCommand.CanExecute(null), "single-page-and-unviewed-count");
                var settings = Descendants(page).OfType<Expander>().Single(item => Equals(item.Header, "Recording settings"));
                settings.IsExpanded = false;

                foreach (var dpi in new[] { 96, 144 })
                    foreach (var size in new[] { new Size(720, 440), new Size(980, 750), new Size(1464, 994) })
                    {
                        phase = $"gallery-{size.Width:0}-{dpi}";
                        Layout(size, dpi);
                        CheckGallery(phase);
                        ScrollTo((FrameworkElement)page.FindName("Gallery"));
                        Capture(phase, surface, size, dpi);
                    }

                // These are gallery-only width probes, not supported whole-window sizes.
                var gallery = (ItemsControl)page.FindName("Gallery");
                var columns = new HashSet<int>();
                foreach (var width in new[] { 220d, 390d, 565d, 740d, 915d })
                {
                    phase = $"gallery-width-{width:0}";
                    gallery.Width = width;
                    Layout(new(1464, 994), 96);
                    CheckGallery(phase);
                    columns.Add(page.GalleryColumns);
                }
                gallery.ClearValue(FrameworkElement.WidthProperty);
                Check(columns.SetEquals(new[] { 1, 2, 3, 4, 5 }), "all-five-gallery-column-counts");

                phase = "selected-player-layout";
                var unviewed = model.NewClipCount;
                var selectedPath = Await(model.SelectForPlaybackAsync(model.Clips[0]));
                Check(selectedPath is not null && model.HasSelection && model.CanExport, "selection-ready");
                Check(model.NewClipCount == unviewed, "selection-does-not-mark-viewed");
                Layout(new(980, 750), 96);
                var player = (ContentControl)page.FindName("PlayerHost");
                var playback = (Border)page.FindName("PlaybackSurface");
                Check(playback.Visibility == Visibility.Visible && player.Content is null &&
                    !Descendants(page).OfType<MediaElement>().Any(), "player-layout-without-decoder");
                Check(((Button)page.FindName("PlayPauseButton")).IsEnabled == false, "play-disabled-before-media-open");
                CheckFits(player, playback, "player-bounds");
                CheckTimelineReset("selected-timeline-awaits-media-open");
                var timeline = (Slider)page.FindName("PlaybackPosition");
                timeline.ApplyTemplate();
                Check(timeline.Style is not null && timeline.Focusable && timeline.IsTabStop &&
                    timeline.SmallChange == 1 && timeline.LargeChange == 10 &&
                    AutomationProperties.GetName(timeline).Length > 0, "timeline-themed-keyboard-accessibility");
                CheckFits(timeline, playback, "timeline-bounds");
                CheckFits((TextBlock)page.FindName("PlaybackTime"), playback, "timeline-label-bounds");
                foreach (var dpi in new[] { 96, 144 })
                    foreach (var size in new[] { new Size(720, 440), new Size(980, 750), new Size(1464, 994) })
                    {
                        phase = $"player-{size.Width:0}-{dpi}";
                        Layout(size, dpi);
                        ScrollTo(playback);
                        var scroll = (ScrollViewer)page.FindName("ClipsScroll");
                        var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                        Check(player.ActualHeight > 150 && player.ActualWidth > 500, "substantial-player-area");
                        Check(size.Height < 700 || player.ActualHeight > 400, "desktop-player-exceeds-old-height-cap");
                        var usedHeight = playback.ActualHeight + playback.Margin.Top + playback.Margin.Bottom;
                        var widthLimited = Math.Abs(player.ActualHeight - player.ActualWidth * 9 / 16) <= 1;
                        Check(widthLimited || Math.Abs(usedHeight - scroll.ViewportHeight) <= 1,
                            "player-uses-width-or-remaining-viewport-height");
                        CheckFits(playback, viewport, "whole-player-fits-viewport");
                        CheckFits(timeline, viewport, "timeline-visible-with-player");
                        CheckFits((WrapPanel)page.FindName("PlaybackControls"), viewport, "playback-controls-visible");
                        Check(player.HorizontalContentAlignment == HorizontalAlignment.Stretch &&
                            player.VerticalContentAlignment == VerticalAlignment.Stretch &&
                            player.Background is SolidColorBrush { Color: var color } && color == Colors.Black &&
                            !ScrollEdgeFade.GetIsEnabled(viewport) && viewport.OpacityMask is null,
                            "opaque-stretched-player-without-scroll-fade");
                        Capture(phase, surface, size, dpi);
                    }
                phase = "preview-export-state";
                var export = (Button)page.FindName("ExportClipButton");
                var exportStatus = (TextBlock)page.FindName("PreviewExportStatus");
                var playbackStatus = (TextBlock)page.FindName("PlaybackStatus");
                var exportDirectory = Path.Combine(output, "synthetic-export-not-playable");
                Await(model.SetStorageDirectoryAsync(exportDirectory));
                Await(model.ExportSelectedToFolderAsync()); Pump();
                Check(!model.CanExport && !export.IsEnabled && model.HasPreviewExportStatus &&
                    exportStatus.Text.StartsWith("Export successful.", StringComparison.Ordinal), "export-feedback-and-disabled-action");
                Await(model.SelectForPlaybackAsync(model.SelectedClip!));
                Check(model.CanExport && !model.HasPreviewExportStatus, "new-preview-allows-export-again");
                Await(model.ExportSelectedToFolderAsync()); Pump();
                Check(!export.IsEnabled && exportStatus.Text.Contains("no duplicate", StringComparison.Ordinal) &&
                    Directory.GetFiles(exportDirectory, "*.mp4").Length == 1, "repeated-export-keeps-one-fixture-copy");
                playbackStatus.Text = "This clip is taking too long to open. Choose it again to retry.";
                playbackStatus.Visibility = Visibility.Visible;
                foreach (var size in new[] { new Size(720, 440), new Size(980, 750), new Size(1464, 994) })
                {
                    phase = $"player-status-{size.Width:0}-144";
                    Layout(size, 144); ScrollTo(playback);
                    var scroll = (ScrollViewer)page.FindName("ClipsScroll");
                    var viewport = (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                    CheckFits(playback, viewport, "player-with-status-fits-viewport");
                    CheckFits(playbackStatus, viewport, "playback-status-readable");
                    CheckFits(exportStatus, viewport, "export-status-readable");
                    CheckFits((WrapPanel)page.FindName("PlaybackControls"), viewport, "controls-visible-with-status");
                    Check(player.ActualHeight > 0 && playbackStatus.Style is not null && exportStatus.Style is not null,
                        "status-keeps-video-and-theme");
                    Capture(phase, surface, size, 144);
                }
                // The detached page has no Loaded subscription; exercise the real close action.
                Descendants(playback).OfType<Button>().Single(item => Equals(item.Content, "Close player"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(playback.Visibility == Visibility.Collapsed && playbackStatus.Visibility == Visibility.Collapsed &&
                    exportStatus.Visibility == Visibility.Collapsed, "player-closes");
                // Exercise the real unload/reset handler without opening media.
                // These temporary control values do not establish playback state.
                timeline.Maximum = 90; timeline.Value = 7; timeline.IsEnabled = true;
                ((TextBlock)page.FindName("PlaybackTime")).Text = "0:07 / 1:30";
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                CheckTimelineReset("unopened-timeline-reset-on-unload");
                Check(player.Content is null && !Descendants(page).OfType<MediaElement>().Any(), "reset-does-not-open-media");

                phase = "buffering-settings";
                recorder.Publish(new(ClipRecorderState.Buffering, true, true, true, "Synthetic recorder is buffering."));
                settings.IsExpanded = true;
                Layout(new(720, 440), 96);
                ((ScrollViewer)page.FindName("ClipsScroll")).ScrollToTop(); Pump();
                var selectors = Descendants(page).OfType<ComboBox>().ToArray();
                Check(!model.CanEditSettings && selectors.Length == 3 && selectors.All(item => !item.IsEnabled) &&
                    !audioToggle.IsEnabled, "recording-settings-disabled");
                Check(model.SaveClipCommand.CanExecute(null), "save-enabled-for-ready-fake");
                Capture(phase, surface, new(720, 440), 96);

                phase = "pending-save-failure";
                Await(model.SaveClipAsync()); // The fake fails after the real library reservation.
                Check(recorder.SaveCalls == 1 && model.HasError && model.PendingText.Length > 0 &&
                    model.Clips.Count == 25, "failed-save-keeps-pending-and-gallery");
                recorder.Publish(new(ClipRecorderState.Error, false, true, false, "Synthetic save could not finish."));
                Layout(new(720, 440), 96);
                ((ScrollViewer)page.FindName("ClipsScroll")).ScrollToTop(); Pump();
                Capture(phase, surface, new(720, 440), 96);
                Check(Descendants(page).OfType<ComboBox>().All(item => item.IsEnabled), "settings-restored-after-stop");
                Check(PresentationSource.FromVisual(surface) is null &&
                    Descendants(page).OfType<MediaElement>().Count() == 0 &&
                    Descendants(page).OfType<ComboBox>().All(item =>
                        (item.Template.FindName("PART_Popup", item) as Popup)?.IsOpen == false), "no-presented-window-popup-or-player");
            }
            finally
            {
                model.SetGalleryActive(false);
                page.DataContext = null;
                surface.Child = null;
            }

            void Layout(Size size, int dpi)
            {
                setDpi(surface, dpi);
                Arrange(surface, size);
                setDpi(surface, dpi);
                Arrange(surface, size);
            }
            void CheckTimelineReset(string name)
            {
                var timeline = (Slider)page.FindName("PlaybackPosition");
                Check(!timeline.IsEnabled && timeline.Minimum == 0 && timeline.Maximum == 1 && timeline.Value == 0 &&
                    ((TextBlock)page.FindName("PlaybackTime")).Text == "0:00 / 0:00", name);
            }
            void ScrollTo(FrameworkElement child)
            {
                var scroll = (ScrollViewer)page.FindName("ClipsScroll");
                var content = (FrameworkElement)scroll.Content;
                scroll.ScrollToVerticalOffset(child.TransformToAncestor(content).Transform(new Point()).Y);
                Pump(); surface.UpdateLayout();
            }
            void CheckGallery(string name)
            {
                var gallery = (ItemsControl)page.FindName("Gallery");
                var grid = Descendants(gallery).OfType<UniformGrid>().Single();
                var cards = Descendants(gallery).OfType<Button>().Where(item => item.DataContext is ClipCardItem).ToArray();
                Check(cards.Length == 25 && grid.Columns == page.GalleryColumns && page.GalleryColumns is >= 1 and <= 5, "gallery-card-and-column-count");
                foreach (var card in cards)
                {
                    CheckFits(card, grid, "card-grid-bounds");
                    var content = Descendants(card).OfType<StackPanel>().First(item => item.Width == 144);
                    CheckFits(content, card, "card-content-bounds");
                    Check(card.Focusable && card.IsTabStop && AutomationProperties.GetName(card).Length > 0, "card-accessible");
                }
                var scroll = (ScrollViewer)page.FindName("ClipsScroll");
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "no-horizontal-scroll-overflow");
                layouts.Add(new
                {
                    name,
                    columns = page.GalleryColumns,
                    cards = cards.Length,
                    galleryWidth = gallery.ActualWidth,
                    gridWidth = grid.ActualWidth,
                    viewportWidth = scroll.ViewportWidth,
                    extentWidth = scroll.ExtentWidth
                });
            }
            void CheckSelectors()
            {
                phase = "selector-states";
                var combos = Descendants(page).OfType<ComboBox>().ToArray();
                Check(combos.Length == 3, "three-selectors");
                foreach (var combo in combos)
                {
                    combo.ApplyTemplate();
                    Check(combo.Style is not null && combo.Focusable && combo.IsTabStop &&
                        AutomationProperties.GetName(combo).Length > 0, "selector-theme-accessibility");
                    var border = combo.Template.FindName("ComboBorder", combo) as Border;
                    var root = combo.Template.FindName("ComboRoot", combo) as FrameworkElement;
                    var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
                    Check(border is not null && SameBrush(border.Background, Theme("InputBrush")), "selector-input-brush");
                    Check(popup?.Child is Border panel && SameBrush(panel.Background, Theme("PanelBrush")) &&
                        panel.CornerRadius.TopLeft > 0 && !popup.IsOpen, "closed-popup-theme");
                    Check(combo.Template.Triggers.OfType<Trigger>().Any(item => item.Property == UIElement.IsKeyboardFocusWithinProperty), "focus-trigger-present");
                    Check(combo.Template.Triggers.OfType<Trigger>().Any(item => item.Property == UIElement.IsMouseOverProperty), "hover-trigger-present");
                    combo.IsEnabled = false; Pump(); Check(root?.Opacity == .45, "disabled-selector-opacity");
                    combo.ClearValue(UIElement.IsEnabledProperty); Pump(); Check(root?.Opacity == 1, "enabled-selector-opacity");
                    var previous = combo.SelectedIndex;
                    var key = previous + 1 < combo.Items.Count ? Key.Down : Key.Up;
                    var expected = previous + (key == Key.Down ? 1 : -1);
                    combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new OffscreenSource(surface),
                        Environment.TickCount, key)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                    Pump(); Check(combo.SelectedIndex == expected, "selector-routed-key-selection");
                    combo.SelectedIndex = previous; Pump();
                }
                var item = new ComboBoxItem
                {
                    Content = "1080p · selected",
                    IsSelected = true,
                    Style = (Style)page.FindResource(typeof(ComboBoxItem))
                };
                item.Resources["ClipControlRadius"] = page.FindResource("ClipControlRadius");
                var itemSurface = new Border
                {
                    Background = Theme("PanelBrush"),
                    Padding = new Thickness(8),
                    DataContext = fixtureContext,
                    Child = item
                };
                Arrange(itemSurface, new(240, 56));
                var itemBorder = item.Template.FindName("ItemBorder", item) as Border;
                Check(itemBorder is not null && SameBrush(itemBorder.BorderBrush, Theme("AccentBrush")), "selected-item-theme");
                Check(item.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == ComboBoxItem.IsHighlightedProperty), "item-highlight-trigger");
                Capture("selected-popup-item-template", itemSurface, new(240, 56), 96);
                item.IsEnabled = false; Pump(); Check(itemBorder?.Opacity == .45, "disabled-item-theme");
                Capture("disabled-popup-item-template", itemSurface, new(240, 56), 96);
            }
        }
        catch (Exception error)
        {
            failures.Add(phase + "/" + error.GetType().Name);
            // Local artifact only: exception detail can include output paths.
            // Console output below remains fixed scalar counts.
            var detail = error.ToString();
            exceptionDiagnostic = new
            {
                phase,
                operation,
                type = error.GetType().Name,
                detail = detail[..Math.Min(detail.Length, 32_768)]
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            application.Shutdown();
        }
        if (bindings.TotalCount > 0) failures.Add("binding-diagnostics");
        using var assembly = File.OpenRead(typeof(ClipsPage).Assembly.Location);
        File.WriteAllText(Path.Combine(output, "clips-ui-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual detached ClipsPage and ClipsViewModel with a generated isolated library, fake recorder and frozen posters. No shown window, controller, real helper, capture, decoder, media playback or installed data.",
            limits = "Software rendering and routed keys only. Closed popup and item templates are inspected without showing a popup. Does not prove physical focus, hover, displayed smoothness or recording performance.",
            appAssemblySha256 = Convert.ToHexString(SHA256.HashData(assembly)),
            checks,
            captures,
            layouts,
            boundsFailures,
            failures,
            exceptionDiagnostic,
            bindingDiagnosticCount = bindings.TotalCount,
            placeholderMediaPlayable = false,
            liveServicesStarted = false,
            performanceVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Clips UI review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {checks} checks; {captures.Count} images; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void Check(bool condition, string name) { checks++; if (!condition) failures.Add(phase + "/" + name); }
        void CheckFits(FrameworkElement child, FrameworkElement parent, string name)
        {
            var bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
            var fits = bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1 &&
                bounds.Bottom <= parent.ActualHeight + 1 && bounds.Width > 0 && bounds.Height > 0;
            Check(fits, name);
            if (!fits && boundsFailures.Count < 64)
                boundsFailures.Add(new
                {
                    phase,
                    name,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    parentWidth = parent.ActualWidth,
                    parentHeight = parent.ActualHeight
                });
        }
        void Capture(string name, FrameworkElement element, Size size, int dpi)
        {
            bindings.Phase = name;
            operation = "render:" + name;
            var scale = dpi / 96d;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
                (int)Math.Ceiling(size.Height * scale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            operation = "write-image:" + name;
            using (var file = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
            operation = "inspect:" + name;
            var inspection = ReviewDiagnostics.Inspect(element, name + ".png", "synthetic-clips", "clips", name,
                dpi, bitmap.PixelWidth, bitmap.PixelHeight, 0);
            Check(inspection.Labels.OverflowCount == 0, name + "/text-overflow");
            captures.Add(new
            {
                name,
                dpi,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                labelsChecked = inspection.Labels.Checked,
                overflowCount = inspection.Labels.OverflowCount,
                labelOverflows = inspection.Labels.Overflows,
                inspection.VisualNodesVisited,
                inspection.VisualTreeTruncated
            });
            operation = "after-capture:" + name;
        }
    }

    private static void Arrange(FrameworkElement element, Size size)
    {
        element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout(); Pump();
        element.Measure(size); element.Arrange(new Rect(size)); element.UpdateLayout();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Synthetic UI operation exceeded its bound.");
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
    private static bool SameBrush(Brush? left, Brush? right) => ReferenceEquals(left, right) ||
        left is SolidColorBrush a && right is SolidColorBrush b && a.Color == b.Color;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current)) continue;
            if (seen.Count > 4096) throw new InvalidOperationException("UI tree exceeded the review bound.");
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Enqueue(child);
            for (var index = 0; current is Visual && index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed class OffscreenSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private sealed class FakeRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot { get; private set; } = new(ClipRecorderState.Disabled, false, true, false, "Synthetic recorder is off.");
        public event EventHandler? StateChanged;
        public int SaveCalls { get; private set; }
        public void Publish(ClipRecorderSnapshot snapshot) { Snapshot = snapshot; StateChanged?.Invoke(this, EventArgs.Empty); }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken token, bool showCaptureBorder = false) =>
            throw new InvalidOperationException("The detached review does not toggle a recorder.");
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); SaveCalls++;
            return Task.FromException<FinalizedClipMedia>(new IOException("Synthetic save failure."));
        }
    }
    private sealed class FakePosters : IClipThumbnailProvider
    {
        private readonly BitmapSource _image;
        internal FakePosters()
        {
            var pixels = new byte[320 * 180 * 4];
            for (var y = 0; y < 180; y++)
                for (var x = 0; x < 320; x++)
                {
                    var offset = (y * 320 + x) * 4;
                    pixels[offset] = (byte)(40 + x / 2); pixels[offset + 1] = (byte)(50 + y);
                    pixels[offset + 2] = (byte)(35 + ((x / 40 + y / 30) % 2) * 60); pixels[offset + 3] = 255;
                }
            _image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4); _image.Freeze();
        }
        public Task<BitmapSource?> LoadAsync(ClipEntry clip, string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<BitmapSource?>(_image); }
    }
}
