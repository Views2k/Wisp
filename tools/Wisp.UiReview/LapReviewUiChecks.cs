using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Telemetry;

namespace Wisp.UiReview;

// Shared by the screenshot tool and the existing STA runtime-test stage.
internal static class LapReviewUiChecks
{
    internal static void Run(string unusedStoreDirectory, Action<bool, string> check,
        Action<string, FrameworkElement, Size>? capture = null)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var receiver = new TelemetryUdpReceiver(); // Never started; no listener or game inspection.
        var service = new RunRecordingService(receiver, unusedStoreDirectory);
        var settings = new AppSettings { StartWithWindows = false, StartWithForza = false, AutomaticApplicationUpdateChecks = false };
        var model = new RunsViewModel(service, settings, Dispatcher.CurrentDispatcher);
        var page = new RunsPage { DataContext = model };
        var surface = new Border
        {
            Padding = new Thickness(16),
            Background = Theme("WindowBrush"),
            DataContext = new DiagnosticsViewModel(settings),
            Child = page
        };
        try
        {
            VisualTreeHelper.SetRootDpi(surface, new DpiScale(1, 1));
            Await(model.ShowReviewAsync(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true)));
            Ready(model);
            var expander = Descendants(page).OfType<Expander>().Single(item => Equals(item.Header, "Lap review"));
            expander.IsExpanded = true;
            Arrange(surface, new(980, 750));
            var view = Descendants(expander).OfType<LapReviewView>().Single();
            var review = model.LapReview;
            var track = (LapReviewPlot)view.FindName("Track");
            var track3D = (LapReviewTrack3D)view.FindName("Track3D");
            var trace = (LapReviewPlot)view.FindName("Trace");
            var view2D = (RadioButton)view.FindName("View2D");
            var view3D = (RadioButton)view.FindName("View3D");
            var mapExportSurface = (FrameworkElement)view.FindName("MapExportSurface");
            var saveMap = (Button)view.FindName("SaveMapPng");
            var scroll = (ScrollViewer)page.FindName("RunsScroll");
            var combos = Descendants(view).OfType<ComboBox>().ToArray();
            var channel = combos.Single(item => AutomationProperties.GetName(item) == "Lap map and graph channel");
            var laps = combos.Single(item => AutomationProperties.GetName(item) == "Lap to review");
            var references = combos.Single(item => AutomationProperties.GetName(item) == "Reference lap");
            var emptyLapHint = (TextBlock)view.FindName("EmptyLapHint");
            var emptyReferenceHint = (TextBlock)view.FindName("EmptyReferenceHint");
            var cursorReadout = (Border)view.FindName("CursorReadout");
            var wheelReadings = (Expander)view.FindName("WheelReadings");
            var slider = Descendants(view).OfType<Slider>().Single(item => AutomationProperties.GetName(item) == "Lap position cursor");
            check(review.Lap?.IsComplete == true && review.Reference?.IsComplete == true, "completed-A-and-B");
            check(review.Plot.Comparison?.CanCompare == true, "comparison-ready");
            check(review.Metrics.Any(item => item.Reference != "—"), "reference-metrics-present");
            check(ReferenceEquals(laps.SelectedItem, review.Lap) && ReferenceEquals(references.SelectedItem, review.Reference), "lap-selector-bindings");
            check(laps.IsEnabled && references.IsEnabled && emptyLapHint.Visibility == Visibility.Collapsed &&
                emptyReferenceHint.Visibility == Visibility.Collapsed, "populated-lap-selectors-enabled-without-empty-hints");
            check(review.PinCommand.CanExecute(null) && !review.UnpinCommand.CanExecute(null), "benchmark-command-availability");
            check(!review.Is3D && view2D.IsChecked == true && track.Visibility == Visibility.Visible &&
                track3D.Visibility == Visibility.Collapsed, "existing-2d-default-preserved");
            check(review.Lap!.Points.Max(point => point.Position.Y) - review.Lap.Points.Min(point => point.Position.Y) > 30,
                "elevated-recording-fixture");
            check(new[] { LapReviewChannel.Speed, LapReviewChannel.Delta, LapReviewChannel.Throttle, LapReviewChannel.Brake,
                LapReviewChannel.Steering, LapReviewChannel.LateralG, LapReviewChannel.LongitudinalG, LapReviewChannel.CombinedG,
                LapReviewChannel.Rpm, LapReviewChannel.Gear, LapReviewChannel.TireTemperature, LapReviewChannel.SlipRatio,
                LapReviewChannel.SlipAngle, LapReviewChannel.Suspension }.All(original => review.Channels.Any(item => item.Channel == original)),
                "all-existing-channels-retained");

            foreach (var (name, size) in new[] { ("normal", new Size(980, 750)), ("compact", new Size(720, 440)) })
            {
                model.ShowSummary(); Arrange(surface, size);
                ScrollTo(expander); Capture("controls");
                foreach (var combo in combos) CheckCombo(combo);
                var originalChannel = review.Channel;
                channel.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                Pump();
                SendKey(channel, Key.Down, surface); Pump();
                check(channel.SelectedIndex == 1 && review.Channel.Channel == LapReviewChannel.Delta, name + "/combo-keyboard-selection");
                channel.SetCurrentValue(Selector.SelectedItemProperty, originalChannel); Pump();

                slider.SetCurrentValue(RangeBase.ValueProperty, 120d); Pump();
                check(review.Cursor == 120 && track.Data?.Cursor == 120 && trace.Data?.Cursor == 120, name + "/shared-slider-cursor");
                SendKey(track, Key.Right, surface); Pump();
                check(review.Cursor == 121 && trace.Data?.Cursor == 121, name + "/map-arrow-cursor");
                SendKey(trace, Key.End, surface); Pump();
                check(review.Cursor == review.MaximumCursor, name + "/graph-end-cursor");
                SendKey(track, Key.Home, surface); Pump();
                check(review.Cursor == 0, name + "/map-home-cursor");

                review.Cursor = 125; review.SectionStartCommand.Execute(null); Ready(model);
                review.Cursor = 265; review.SectionEndCommand.Execute(null); Ready(model);
                check(review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265, name + "/section-commands");
                check(review.Metrics.Count > 10 && review.Metrics.Any(item => item.Reference != "—"), name + "/section-statistics");
                review.Channel = review.Channels.First(item => item.Channel == LapReviewChannel.Speed); Pump();
                ScrollTo(track); Capture("map-speed-section");
                VerifyMapImage("2d", track);
                ScrollTo(trace); Capture("graph-speed-section");
                ScrollTo(cursorReadout); Capture("cursor-readout");
                check(Descendants(cursorReadout).OfType<TextBlock>().Any(item => item.Text == review.CursorDetails?.Speed), name + "/cursor-speed-binding");
                check(Descendants(cursorReadout).OfType<TextBlock>().All(item => item.FontFamily.Source != "Consolas"), name + "/cursor-readout-uses-ui-font");
                wheelReadings.IsExpanded = true; Arrange(surface, size); Capture("cursor-wheel-readings");
                check(Descendants(wheelReadings).OfType<TextBlock>().Any(item => item.Text == review.CursorDetails?.Temperatures.FrontLeft), name + "/cursor-wheel-binding");
                wheelReadings.IsExpanded = false; Arrange(surface, size);
                check(cursorReadout.TranslatePoint(new Point(cursorReadout.ActualWidth, 0), surface).X <= size.Width + 1, name + "/cursor-readout-within-width");
                review.Channel = review.Channels.First(item => item.Channel == LapReviewChannel.Brake); Pump();
                ScrollTo(track); Capture("map-brake-section");
                var selectedLap = review.Lap; var selectedReference = review.Reference;
                var selectedChannel = review.Channel; var selectedCursor = review.Cursor;
                view3D.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(); Arrange(surface, size);
                Await(track3D.PrepareForTestAsync()); Pump();
                check(review.Is3D && view3D.IsChecked == true && track.Visibility == Visibility.Collapsed &&
                    track3D.Visibility == Visibility.Visible, name + "/3d-mode-binding");
                check(track3D.IsReady && track3D.PreparedSegmentCount > 0, name + "/3d-scene-ready");
                check(ReferenceEquals(review.Lap, selectedLap) && ReferenceEquals(review.Reference, selectedReference) &&
                    ReferenceEquals(review.Channel, selectedChannel) && review.Cursor == selectedCursor &&
                    review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265, name + "/3d-keeps-review-state");
                check(ReferenceEquals(track3D.Data?.Lap, review.Lap) && ReferenceEquals(track3D.Data?.Reference, review.Reference) &&
                    track3D.Data?.Cursor == review.Cursor && track3D.Data?.Channel == review.Channel.Channel,
                    name + "/3d-shares-recorded-data");
                var camera = (track3D.Yaw, track3D.Pitch, track3D.Roll, track3D.ZoomFactor);
                track3D.Yaw = camera.Yaw + 35; track3D.Pitch = camera.Pitch + 8; track3D.Roll = camera.Roll + 12;
                track3D.ZoomBy(1.2); track3D.PanBy(12, -8); Pump();
                check(track3D.Yaw != camera.Yaw && track3D.Pitch != camera.Pitch && track3D.Roll != camera.Roll &&
                    track3D.ZoomFactor != camera.ZoomFactor, name + "/3d-axis-and-zoom-controls");
                slider.SetCurrentValue(RangeBase.ValueProperty, 180d); Pump();
                check(review.Cursor == 180 && track3D.Data?.Cursor == 180 && trace.Data?.Cursor == 180,
                    name + "/3d-shared-slider-cursor");
                SendKey(track3D, Key.Right, surface); Pump();
                check(review.Cursor == 181 && trace.Data?.Cursor == 181, name + "/3d-arrow-cursor");
                foreach (var choice in review.Channels)
                {
                    channel.SetCurrentValue(Selector.SelectedItemProperty, choice); Pump(); Await(track3D.PrepareForTestAsync());
                    check(track3D.IsReady && track3D.Data?.Channel == choice.Channel && trace.Data?.Channel == choice.Channel,
                        name + "/3d-channel-" + choice.Channel);
                }
                review.Channel = review.Channels.First(item => item.Channel == LapReviewChannel.TireTemperature);
                review.SelectedWheel = 3; Pump();
                check(track3D.Data?.Wheel == 3 && trace.Data?.Wheel == 3, name + "/3d-retains-wheel-selection");
                review.SelectedWheel = 0; review.Channel = selectedChannel; Pump(); Await(track3D.PrepareForTestAsync());
                check(saveMap.IsEnabled && mapExportSurface.ActualWidth > 100 && mapExportSurface.ActualHeight > 100,
                    name + "/map-export-surface-ready");
                track3D.ResetView(); Pump();
                check(track3D.Yaw == camera.Yaw && track3D.Pitch == camera.Pitch && track3D.Roll == camera.Roll &&
                    track3D.ZoomFactor == camera.ZoomFactor, name + "/3d-reset-camera");
                ScrollTo(mapExportSurface); Capture("map-3d-brake-section");
                VerifyMapImage("3d", track3D);
                view2D.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(); Arrange(surface, size);
                check(!review.Is3D && track.Visibility == Visibility.Visible && track3D.Visibility == Visibility.Collapsed &&
                    track.Data?.Cursor == review.Cursor && review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265,
                    name + "/2d-restores-with-shared-state");
                var metrics = Descendants(view).OfType<ItemsControl>().Single(item => ReferenceEquals(item.ItemsSource, review.Metrics));
                ScrollTo(metrics); Capture("section-metrics");
                check(track.ActualWidth > 100 && trace.ActualWidth > 100, name + "/plots-have-layout");
                check(track.Focusable && trace.Focusable && slider.Focusable && slider.IsTabStop, name + "/cursor-controls-keyboard-reachable");
                check(combos.All(item => item.TranslatePoint(new Point(item.ActualWidth, 0), surface).X <= size.Width + 1), name + "/selectors-within-width");

                review.ShowGraphsCommand.Execute(null); Ready(model);
                check(model.HasSelection && Math.Abs(model.SelectionStart - review.Lap!.Points[125].RunSeconds) < .001 &&
                    Math.Abs(model.SelectionEnd - review.Lap.Points[265].RunSeconds) < .001, name + "/section-opens-in-existing-graphs");
                model.ShowSummary(); Ready(model);
                review.WholeLapCommand.Execute(null); Ready(model);
                check(review.Plot.SectionStart == 0 && review.Plot.SectionEnd == review.MaximumCursor, name + "/whole-lap-command");

                void ScrollTo(FrameworkElement element)
                {
                    Arrange(surface, size);
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + element.TranslatePoint(new Point(), scroll).Y);
                    Arrange(surface, size);
                    if (ReferenceEquals(element, track))
                    {
                        var bounds = track.TransformToAncestor(scroll).TransformBounds(new Rect(track.RenderSize));
                        check(bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5, name + "/whole-map-fits-aligned-viewport");
                        check(name == "normal" ? Math.Abs(track.ActualHeight - 360) < .5 : track.ActualHeight is >= 180 and < 360,
                            name + "/responsive-map-height");
                    }
                }
                void Capture(string stage) { Arrange(surface, size); capture?.Invoke(name + "-" + stage, surface, size); }
                void VerifyMapImage(string mode, FrameworkElement map)
                {
                    Arrange(surface, size);
                    var bitmap = view.CaptureMapImage();
                    var code = name + "/" + mode + "-png-";
                    check(bitmap.IsFrozen && bitmap.PixelWidth is > 0 and <= 3840 && bitmap.PixelHeight is > 0 and <= 2160,
                        code + "frozen-and-bounded");
                    var pixels = Pixels(bitmap);
                    var corners = CornerAlphas(pixels, bitmap);
                    var opaque = corners.Length == 4 && corners.All(alpha => alpha == 255);
                    check(opaque, code + "opaque-corners-without-layout-offset");
                    if (!opaque) Console.WriteLine($"{code}corner-alpha=[{string.Join(",", corners)}]; pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}; surface={mapExportSurface.RenderSize.Width:R}x{mapExportSurface.RenderSize.Height:R}");
                    var mapBounds = map.TransformToAncestor(mapExportSurface).TransformBounds(new Rect(map.RenderSize));
                    var legend = Descendants(mapExportSurface).OfType<System.Windows.Shapes.Rectangle>()
                        .Single(item => ReferenceEquals(item.Fill, LapReviewPalette.LegendBrush));
                    var legendBounds = legend.TransformToAncestor(mapExportSurface).TransformBounds(new Rect(legend.RenderSize));
                    check(ColorfulPixels(pixels, bitmap, mapBounds, mapExportSurface.RenderSize) > 50, code + "contains-map-line");
                    check(ColorfulPixels(pixels, bitmap, legendBounds, mapExportSurface.RenderSize) > 150, code + "contains-color-legend");
                    if (name != "normal") return;

                    var parent = Path.GetDirectoryName(Path.GetFullPath(unusedStoreDirectory))!;
                    var scratch = Path.Combine(parent, "lap-map-png-check-" + Guid.NewGuid().ToString("N"));
                    var destination = Path.Combine(scratch, "map.png");
                    Directory.CreateDirectory(scratch);
                    try
                    {
                        Await(RunImageExporter.WriteAsync(bitmap, destination));
                        BitmapSource restored;
                        using (var file = File.OpenRead(destination))
                        {
                            var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            check(decoder.Frames.Count == 1, code + "single-frame");
                            restored = decoder.Frames[0];
                        }
                        check(restored.PixelWidth == bitmap.PixelWidth && restored.PixelHeight == bitmap.PixelHeight &&
                            SamePixels(Pixels(restored), pixels), code + "saved-pixels-round-trip");
                        var before = Hash(destination);
                        var rejected = false;
                        try { Await(RunImageExporter.WriteAsync(bitmap, destination)); }
                        catch (IOException) { rejected = true; }
                        check(rejected && Hash(destination) == before, code + "existing-image-kept");
                    }
                    finally
                    {
                        if (File.Exists(destination)) File.Delete(destination);
                        Directory.Delete(scratch, recursive: false);
                    }
                }
            }

            review.SetRuns(null, null); Ready(model); Arrange(surface, new(720, 440));
            check(!review.HasLap && !review.PinCommand.CanExecute(null) && !review.SectionStartCommand.CanExecute(null), "empty-review-disables-commands");
            check(!channel.IsEnabled && !slider.IsEnabled, "empty-review-disables-channel-and-cursor");
            check(!view2D.IsEnabled && !view3D.IsEnabled && !saveMap.IsEnabled, "empty-review-disables-map-tools");
            check(!laps.HasItems && !references.HasItems && !laps.IsEnabled && !references.IsEnabled, "empty-lap-selectors-disabled");
            check(emptyLapHint.Visibility == Visibility.Visible && emptyReferenceHint.Visibility == Visibility.Visible &&
                !emptyLapHint.IsHitTestVisible && !emptyReferenceHint.IsHitTestVisible, "empty-lap-selectors-show-passive-hints");
            check(((Popup)laps.Template.FindName("PART_Popup", laps)).IsOpen == false &&
                ((Popup)references.Template.FindName("PART_Popup", references)).IsOpen == false, "empty-lap-popups-stay-closed");
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + expander.TranslatePoint(new Point(), scroll).Y);
            Arrange(surface, new(720, 440)); capture?.Invoke("compact-empty-disabled", surface, new(720, 440));
            Await(model.ShowReviewAsync(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true)));
            Ready(model); Arrange(surface, new(720, 440));
            check(laps.IsEnabled && references.IsEnabled && emptyLapHint.Visibility == Visibility.Collapsed &&
                emptyReferenceHint.Visibility == Visibility.Collapsed, "lap-selectors-recover-when-laps-load");
            check(!receiver.IsRunning && PresentationSource.FromVisual(surface) is null, "no-listener-or-presentation-window");
            check(!Directory.Exists(unusedStoreDirectory), "in-memory-fixtures-never-access-run-storage");
        }
        finally
        {
            page.DataContext = null;
            model.Dispose();
            Await(service.DisposeAsync().AsTask()); Await(receiver.DisposeAsync().AsTask());
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        void CheckCombo(ComboBox combo)
        {
            combo.ApplyTemplate();
            check(combo.Style is not null && combo.Focusable && combo.IsTabStop && !string.IsNullOrEmpty(AutomationProperties.GetName(combo)), "combo-themed-and-accessible");
            var border = combo.Template.FindName("ComboBorder", combo) as Border;
            var root = combo.Template.FindName("ComboRoot", combo) as FrameworkElement;
            var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
            check(border is not null && SameBrush(border.Background, Theme("InputBrush")), "combo-input-theme");
            check(popup?.Child is Border panel && SameBrush(panel.Background, Theme("PanelBrush")) && panel.CornerRadius.TopLeft > 0, "popup-panel-theme");
            check(FocusTriggers.HasKeyboardOnly(combo.Template, UIElement.IsKeyboardFocusWithinProperty), "combo-visible-keyboard-focus-trigger");
            var foreground = combo.Foreground;
            combo.SetCurrentValue(UIElement.IsEnabledProperty, false); Pump();
            check(root?.Opacity == .45 && SameBrush(combo.Foreground, foreground), "combo-disabled-theme");
            combo.SetCurrentValue(UIElement.IsEnabledProperty, true); Pump();
            check(root?.Opacity == 1, "combo-enabled-restoration");
            var item = new ComboBoxItem { Content = "Selected lap", Style = (Style)page.FindResource(typeof(ComboBoxItem)), IsSelected = true };
            Arrange(item, new(220, 40));
            var itemBorder = item.Template.FindName("ItemBorder", item) as Border;
            check(itemBorder is not null && SameBrush(itemBorder.BorderBrush, Theme("AccentBrush")), "selected-popup-item-theme");
            check(item.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == ComboBoxItem.IsHighlightedProperty), "popup-item-highlight-trigger");
            item.IsEnabled = false; Pump();
            check(itemBorder?.Opacity == .45, "disabled-popup-item-theme");
            check(popup?.IsOpen == false, "popup-not-shown");
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        if (bitmap.Format != PixelFormats.Pbgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static int ColorfulPixels(byte[] pixels, BitmapSource bitmap, Rect region, Size surface)
    {
        var scaleX = bitmap.PixelWidth / surface.Width; var scaleY = bitmap.PixelHeight / surface.Height;
        var left = Math.Clamp((int)Math.Ceiling(region.Left * scaleX), 0, bitmap.PixelWidth);
        var right = Math.Clamp((int)Math.Floor(region.Right * scaleX), 0, bitmap.PixelWidth);
        var top = Math.Clamp((int)Math.Ceiling(region.Top * scaleY), 0, bitmap.PixelHeight);
        var bottom = Math.Clamp((int)Math.Floor(region.Bottom * scaleY), 0, bitmap.PixelHeight);
        var count = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var at = (y * bitmap.PixelWidth + x) * 4;
                var maximum = Math.Max(pixels[at], Math.Max(pixels[at + 1], pixels[at + 2]));
                var minimum = Math.Min(pixels[at], Math.Min(pixels[at + 1], pixels[at + 2]));
                if (pixels[at + 3] >= 200 && maximum - minimum > 70) count++;
            }
        return count;
    }
    private static byte[] CornerAlphas(byte[] pixels, BitmapSource bitmap)
    {
        if (bitmap.PixelWidth < 4 || bitmap.PixelHeight < 4) return [];
        // The one-pixel inset avoids fractional-DPI edge antialiasing while
        // detecting layout offsets which leave unpainted image margins.
        var result = new List<byte>(4);
        foreach (var y in new[] { 1, bitmap.PixelHeight - 2 })
            foreach (var x in new[] { 1, bitmap.PixelWidth - 2 })
                result.Add(pixels[(y * bitmap.PixelWidth + x) * 4 + 3]);
        return result.ToArray();
    }
    private static bool SamePixels(byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length) return false;
        for (var i = 0; i < actual.Length; i += 4)
        {
            if (actual[i + 3] != expected[i + 3]) return false;
            // PNG stores straight alpha; WPF's premultiplied antialiasing can
            // round a translucent channel by one when converting back.
            var tolerance = expected[i + 3] == 255 ? 0 : 1;
            for (var channel = 0; channel < 3; channel++)
                if (Math.Abs(actual[i + channel] - expected[i + channel]) > tolerance) return false;
        }
        return true;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    internal static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout(); Pump();
        surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
    private static void Ready(RunsViewModel model)
    {
        var timeout = Stopwatch.StartNew();
        do { Await(Task.Delay(10)); } while ((model.IsBusy || model.IsPreparingCharts || model.LapReview.IsBusy) && timeout.Elapsed < TimeSpan.FromSeconds(8));
        Pump();
        if (model.IsBusy || model.IsPreparingCharts || model.LapReview.IsBusy || model.HasError)
            throw new InvalidOperationException("Synthetic lap review did not settle.");
    }
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
    private static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
    private static bool SameBrush(Brush? a, Brush? b) => ReferenceEquals(a, b) || a is SolidColorBrush x && b is SolidColorBrush y && x.Color == y.Color;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>(); var visited = new HashSet<DependencyObject>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Enqueue(child);
            for (var i = 0; current is Visual && i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
    }
    private static void SendKey(UIElement element, Key key, Visual root) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        new OffscreenSource(root), Environment.TickCount, key)
    { RoutedEvent = Keyboard.KeyDownEvent });
    private sealed class OffscreenSource(Visual visual) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = visual;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
