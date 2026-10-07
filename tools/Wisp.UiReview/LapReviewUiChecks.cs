using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
            var mapViewport = (FrameworkElement)view.FindName("MapViewport");
            var scrubBar = (FrameworkElement)view.FindName("ScrubBar");
            var showBoth = (Button)view.FindName("ShowBothMaps");
            var sharedSpace = (CheckBox)view.FindName("SharedSpaceToggle");
            var resetCamera = (Button)view.FindName("ResetCamera");
            var cameraControls = (FrameworkElement)view.FindName("CameraControls");
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
            var lapSettings = Descendants(view).OfType<Expander>().Single(item => Equals(item.Header, "Lap settings and benchmark"));
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
            check(!review.SharedSpace && sharedSpace.IsChecked == false, "shared-space-is-opt-in");
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
                CheckComparisonLayout(page, model, surface, size, name, check, capture);
                lapSettings.IsExpanded = true;
                ScrollTo(laps); Capture("controls");
                foreach (var combo in combos) CheckCombo(combo);
                var originalChannel = review.Channel;
                channel.SetCurrentValue(Selector.SelectedIndexProperty, 0);
                Pump();
                SendKey(channel, Key.Down, surface); Pump();
                check(channel.SelectedIndex == 1 && review.Channel.Channel == LapReviewChannel.Delta, name + "/combo-keyboard-selection");
                channel.SetCurrentValue(Selector.SelectedItemProperty, originalChannel); Pump();
                lapSettings.IsExpanded = false;
                CheckMapAndScrubber(); Capture("map-and-scrubber");

                slider.SetCurrentValue(RangeBase.ValueProperty, 120d); Pump();
                check(review.Cursor == 120 && track.Data?.Cursor == 120 && trace.Data?.Cursor == 120, name + "/shared-slider-cursor");
                SendKey(track, Key.Right, surface); Pump();
                check(review.Cursor == 121 && trace.Data?.Cursor == 121, name + "/map-arrow-cursor");
                SendKey(trace, Key.End, surface); Pump();
                check(review.Cursor == review.MaximumCursor, name + "/graph-end-cursor");
                SendKey(track, Key.Home, surface); Pump();
                check(review.Cursor == 0, name + "/map-home-cursor");

                var beforeScrub = view.ScrubUpdates;
                view.BeginScrub();
                for (var cursor = 60; cursor < 120; cursor++) slider.SetCurrentValue(RangeBase.ValueProperty, (double)cursor);
                check(review.Cursor == 0 && view.ScrubUpdates == beforeScrub, name + "/drag-coalesces-before-frame");
                view.FlushScrub();
                check(review.Cursor == 119 && view.ScrubUpdates == beforeScrub + 1, name + "/drag-flushes-latest-position");
                slider.SetCurrentValue(RangeBase.ValueProperty, 120d);
                view.EndScrub(); Pump();
                check(review.Cursor == 120 && trace.Data?.Cursor == 120 && (int)slider.Value == 120,
                    name + "/drag-end-keeps-final-position");

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
                check(!review.SharedSpace && !review.IsMapComparison && !track3D.IsComparison && track3D.ComparisonData is null &&
                    track3D.Data is { ShowReferencePath: true, HasDistinctReference: true } originalMap &&
                    ReferenceEquals(originalMap.Reference, selectedReference) && showBoth.Visibility == Visibility.Collapsed,
                    name + "/normal-3d-preserves-original-map-and-reference");
                check(sharedSpace.IsEnabled && sharedSpace.Visibility == Visibility.Visible && review.CanUseSharedSpace,
                    name + "/shared-space-toggle-available-for-comparison");
                CheckMapAndScrubber(); Capture("map-3d-normal");
                VerifyMapImage("3d-normal", track3D);
                var normalData = track3D.Data!;
                var normalRange = LapReviewColorRange.From(normalData);
                sharedSpace.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(); Arrange(surface, size);
                Await(track3D.PrepareForTestAsync()); Pump();
                check(review.SharedSpace && review.IsMapComparison && sharedSpace.IsChecked == true,
                    name + "/shared-space-toggle-activates-comparison");
                check(review.HasMapComparison && track3D.IsComparison && track3D.PreparedReferenceSegmentCount > 0 && track3D.FocusedLap == 0,
                    name + "/3d-comparison-opens-both-tracks-in-one-scene");
                CheckMapAndScrubber();
                check(Descendants(mapViewport).OfType<Viewport3D>().Count() == 1 &&
                    Descendants(mapViewport).OfType<LapReviewTrack3D>().Count() == 1,
                    name + "/3d-comparison-has-one-continuous-viewport");
                check(ReferenceEquals(review.Lap, selectedLap) && ReferenceEquals(review.Reference, selectedReference) &&
                    ReferenceEquals(review.Channel, selectedChannel) && review.Cursor == selectedCursor &&
                    review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265, name + "/3d-keeps-review-state");
                check(ReferenceEquals(track3D.Data?.Lap, review.Lap) && ReferenceEquals(track3D.Data?.Reference, review.Reference) &&
                    track3D.Data?.Cursor == review.Cursor && track3D.Data?.Channel == review.Channel.Channel,
                    name + "/3d-shares-recorded-data");
                check(ReferenceEquals(track3D.ComparisonData?.Lap, review.Reference) && ReferenceEquals(track3D.ComparisonData?.Reference, review.Lap) &&
                    track3D.Data?.ShowReferencePath == false && track3D.ComparisonData?.ShowReferencePath == false,
                    name + "/3d-each-track-colors-its-own-lap");
                CheckSharedColorScale();
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
                    channel.SetCurrentValue(Selector.SelectedItemProperty, choice); Pump();
                    Await(track3D.PrepareForTestAsync());
                    check(track3D.IsReady && track3D.Data?.Channel == choice.Channel &&
                        track3D.ComparisonData?.Channel == choice.Channel && trace.Data?.Channel == choice.Channel,
                        name + "/3d-channel-" + choice.Channel);
                    CheckSharedColorScale();
                }
                review.Channel = review.Channels.First(item => item.Channel == LapReviewChannel.TireTemperature);
                review.SelectedWheel = 3; Pump();
                check(track3D.Data?.Wheel == 3 && track3D.ComparisonData?.Wheel == 3 && trace.Data?.Wheel == 3, name + "/3d-retains-wheel-selection");
                review.SelectedWheel = 0; review.Channel = selectedChannel; Pump();
                Await(track3D.PrepareForTestAsync());
                check(saveMap.IsEnabled && mapExportSurface.ActualWidth > 100 && mapExportSurface.ActualHeight > 100,
                    name + "/map-export-surface-ready");
                track3D.ResetView(); track3D.CompleteCameraMotionForTest(); Pump();
                check(track3D.Yaw == camera.Yaw && track3D.Pitch == camera.Pitch && track3D.Roll == camera.Roll &&
                    track3D.ZoomFactor == camera.ZoomFactor, name + "/3d-reset-camera");
                ScrollTo(mapExportSurface); Capture("map-3d-brake-section");
                VerifyMapImage("3d-overview", track3D);
                var sceneBuilds = track3D.SceneBuildCount;
                var segmentCounts = (track3D.PreparedSegmentCount, track3D.PreparedReferenceSegmentCount);
                var overviewSize = track3D.RenderSize;
                var overviewA = ProjectedTrackBounds(reference: false);
                var overviewB = ProjectedTrackBounds(reference: true);
                check(overviewA.Width > 10 && overviewB.Width > 10 &&
                    (overviewA.Right <= overviewB.Left || overviewB.Right <= overviewA.Left),
                    name + "/3d-overview-projects-tracks-side-by-side");
                track3D.FocusLap(1); track3D.CompleteCameraMotionForTest(); Arrange(surface, size);
                check(track3D.FocusedLap == 1 && track3D.RenderSize == overviewSize && showBoth.Visibility == Visibility.Visible &&
                    ProjectedTrackBounds(reference: false).Width > overviewA.Width * 1.15 &&
                    CameraBindingsMatch(), name + "/focus-A-moves-shared-camera");
                CheckBothGeometriesRetained();
                ScrollTo(scrubBar); Capture("map-3d-focus-A");
                VerifyMapImage("3d-focus-A", track3D);
                showBoth.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); track3D.CompleteCameraMotionForTest(); Arrange(surface, size);
                check(track3D.FocusedLap == 0 &&
                    showBoth.Visibility == Visibility.Visible, name + "/show-both-restores-overview");
                CheckBothGeometriesRetained();

                track3D.FocusLap(2); track3D.CompleteCameraMotionForTest(); Arrange(surface, size);
                check(track3D.FocusedLap == 2 && track3D.RenderSize == overviewSize &&
                    ProjectedTrackBounds(reference: true).Width > overviewB.Width * 1.15 &&
                    CameraBindingsMatch(), name + "/focus-B-moves-shared-camera");
                CheckBothGeometriesRetained();
                var referenceCursor = track3D.ComparisonData!.Cursor;
                var nextReference = Math.Clamp(referenceCursor + 1, 0, review.Reference!.Points.Length - 1);
                var expectedCursor = track3D.ComparisonData.Comparison?.Points.ElementAtOrDefault(nextReference)?.ReferencePointIndex;
                SendKey(track3D, Key.Right, surface); Pump();
                check(expectedCursor is { } matched && review.Cursor == matched && trace.Data?.Cursor == matched,
                    name + "/focused-reference-arrow-selects-corresponding-A-position");
                ScrollTo(scrubBar); Capture("map-3d-focus-B");
                VerifyMapImage("3d-focus-B", track3D);
                var zoomOut = Descendants(cameraControls).OfType<Button>().Single(item => Equals(item.Content, "Zoom out"));
                for (var zoom = 0; zoom < 12 && track3D.FocusedLap != 0; zoom++)
                { zoomOut.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); track3D.CompleteCameraMotionForTest(); }
                Arrange(surface, size);
                check(track3D.FocusedLap == 0, name + "/zoom-out-reveals-both-tracks");
                CheckBothGeometriesRetained();
                track3D.FocusLap(2); track3D.CompleteCameraMotionForTest();
                track3D.Yaw += 28; track3D.Pitch += 7; track3D.Roll = 11; track3D.ZoomBy(1.3);
                resetCamera.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); track3D.CompleteCameraMotionForTest(); Pump();
                check(track3D.FocusedLap == 0 && track3D.Yaw == camera.Yaw && track3D.Pitch == camera.Pitch &&
                    track3D.Roll == camera.Roll && track3D.ZoomFactor == camera.ZoomFactor,
                    name + "/reset-restores-whole-scene-camera");
                CheckMapAndScrubber(); Capture("map-3d-comparison-overview");
                var cursorBeforeNormal = review.Cursor;
                sharedSpace.SetCurrentValue(ToggleButton.IsCheckedProperty, false); Pump(); Arrange(surface, size);
                Await(track3D.PrepareForTestAsync()); Pump();
                check(!review.SharedSpace && !review.IsMapComparison && !track3D.IsComparison &&
                    track3D.ComparisonData is null && track3D.Data is { ShowReferencePath: true, HasDistinctReference: true } restoredMap &&
                    ReferenceEquals(restoredMap.Lap, selectedLap) && ReferenceEquals(restoredMap.Reference, selectedReference) &&
                    LapReviewColorRange.From(restoredMap) == normalRange &&
                    review.Cursor == cursorBeforeNormal && review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265,
                    name + "/shared-space-off-restores-original-3d-with-state");
                CheckMapAndScrubber(); Capture("map-3d-normal-restored");
                VerifyMapImage("3d-normal-restored", track3D);
                var automaticHeight = mapViewport.ActualHeight;
                var geometryBeforeResize = track3D.SceneBuildCount;
                var grip = (Thumb)view.FindName("MapResizeGrip");
                grip.RaiseEvent(new DragDeltaEventArgs(0, 160) { RoutedEvent = Thumb.DragDeltaEvent });
                grip.RaiseEvent(new DragDeltaEventArgs(0, 40) { RoutedEvent = Thumb.DragDeltaEvent });
                Arrange(surface, size);
                check(Math.Abs(mapViewport.ActualHeight - Math.Clamp(automaticHeight + 200, 180, 1400)) < .1 &&
                    track3D.SceneBuildCount == geometryBeforeResize, name + "/track-resize-accumulates-without-rebuilding-geometry");
                VerifyMapImage("3d-taller", track3D);
                SendKey(grip, Key.Home, surface); Arrange(surface, size);
                check(double.IsNaN(view.RequestedMapHeight) && Math.Abs(mapViewport.ActualHeight - automaticHeight) < .1,
                    name + "/track-size-keyboard-reset");
                view2D.SetCurrentValue(ToggleButton.IsCheckedProperty, true); Pump(); Arrange(surface, size);
                check(!review.Is3D && track.Visibility == Visibility.Visible && track3D.Visibility == Visibility.Collapsed &&
                    track.Data?.Cursor == review.Cursor &&
                    review.Plot.SectionStart == 125 && review.Plot.SectionEnd == 265,
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
                        check(mapViewport.ActualHeight is >= 60 and <= 640 && track.ActualHeight >= 60 &&
                            track.ActualHeight <= mapViewport.ActualHeight, name + "/responsive-map-keeps-renderable-height");
                    }
                }
                void CheckMapAndScrubber()
                {
                    scroll.ScrollToHome();
                    Arrange(surface, size);
                    var sliderBounds = slider.TransformToAncestor(scroll).TransformBounds(new Rect(slider.RenderSize));
                    var mapsBounds = mapViewport.TransformToAncestor(scroll).TransformBounds(new Rect(mapViewport.RenderSize));
                    var visibleTogether = scroll.VerticalOffset <= .5 && sliderBounds.Top >= -.5 &&
                        sliderBounds.Bottom <= scroll.ViewportHeight + .5 && mapsBounds.Top >= sliderBounds.Bottom &&
                        mapsBounds.Bottom <= scroll.ViewportHeight + .5;
                    check(visibleTogether, name + "/slider-and-map-visible-at-scroll-top");
                    if (!visibleTogether)
                        Console.WriteLine($"{name}/map-at-top: offset={scroll.VerticalOffset:0.##}; viewport={scroll.ViewportHeight:0.##}; slider={sliderBounds.Top:0.##}..{sliderBounds.Bottom:0.##}; maps={mapsBounds.Top:0.##}..{mapsBounds.Bottom:0.##}; map-height={mapViewport.ActualHeight:0.##}; mode={(review.Is3D ? "3d" : "2d")}");
                }
                void CheckSharedColorScale()
                {
                    var a = track3D.Data; var b = track3D.ComparisonData;
                    check(a is not null && b is not null && a.ColorRangeOverride is { } shared && b.ColorRangeOverride == shared &&
                        LapReviewColorRange.From(a) == LapReviewColorRange.From(b), name + "/A-B-share-value-color-range-" + review.Channel.Channel);
                }
                bool CameraBindingsMatch()
                {
                    var controls = Descendants(cameraControls).OfType<Slider>().ToArray();
                    return new[] { (Name: "3D camera yaw", Property: nameof(LapReviewTrack3D.Yaw), Value: track3D.Yaw),
                        (Name: "3D camera pitch", Property: nameof(LapReviewTrack3D.Pitch), Value: track3D.Pitch),
                        (Name: "3D camera roll", Property: nameof(LapReviewTrack3D.Roll), Value: track3D.Roll) }
                        .All(axis => controls.SingleOrDefault(control => AutomationProperties.GetName(control) == axis.Name) is { } slider &&
                            slider.GetBindingExpression(RangeBase.ValueProperty) is { } binding &&
                            ReferenceEquals(binding.DataItem, track3D) && binding.ParentBinding.Path?.Path == axis.Property &&
                            binding.ParentBinding.Mode == BindingMode.TwoWay && Math.Abs(slider.Value - axis.Value) < 1e-9);
                }
                Rect ProjectedTrackBounds(bool reference)
                {
                    var points = (reference ? track3D.ComparisonData : track3D.Data)!.Lap!.Points;
                    var bounds = Rect.Empty;
                    for (var index = 0; index < points.Length; index++)
                    {
                        var point = reference ? track3D.ProjectReferencePoint(index) : track3D.ProjectPoint(index);
                        if (double.IsFinite(point.X) && double.IsFinite(point.Y)) bounds.Union(point);
                    }
                    return bounds;
                }
                void CheckBothGeometriesRetained()
                {
                    check(track3D.SceneBuildCount == sceneBuilds &&
                        (track3D.PreparedSegmentCount, track3D.PreparedReferenceSegmentCount) == segmentCounts &&
                        ReferenceEquals(track3D.Data?.Lap, review.Lap) && ReferenceEquals(track3D.ComparisonData?.Lap, review.Reference),
                        name + "/camera-focus-retains-both-prepared-tracks");
                }
                void Capture(string stage) { Arrange(surface, size); capture?.Invoke(name + "-" + stage, surface, size); }
                void VerifyMapImage(string mode, params FrameworkElement[] maps)
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
                    var legend = Descendants(mapExportSurface).OfType<System.Windows.Shapes.Rectangle>()
                        .Single(item => ReferenceEquals(item.Fill, LapReviewPalette.LegendBrush));
                    var legendBounds = legend.TransformToAncestor(mapExportSurface).TransformBounds(new Rect(legend.RenderSize));
                    var mapPixelsPassed = true;
                    foreach (var map in maps)
                    {
                        var mapBounds = map.TransformToAncestor(mapExportSurface).TransformBounds(new Rect(map.RenderSize));
                        var count = ColorfulPixels(pixels, bitmap, mapBounds, mapExportSurface.RenderSize);
                        var passed = count > 50;
                        mapPixelsPassed &= passed;
                        check(passed, code + "contains-map-line-" + map.Name);
                        if (!passed)
                            Console.WriteLine($"{code}map={map.Name}; colorful-pixels={count}; bounds={mapBounds.X:R},{mapBounds.Y:R},{mapBounds.Width:R},{mapBounds.Height:R}");
                    }
                    if (mode == "3d-overview")
                    {
                        foreach (var reference in new[] { false, true })
                        {
                            var projectedBounds = ProjectedTrackBounds(reference);
                            projectedBounds.Inflate(3, 3);
                            var sceneBounds = track3D.TransformToAncestor(mapExportSurface).TransformBounds(projectedBounds);
                            var count = ColorfulPixels(pixels, bitmap, sceneBounds, mapExportSurface.RenderSize);
                            mapPixelsPassed &= count > 50;
                            check(count > 50, code + (reference ? "contains-reference-track" : "contains-current-track"));
                            if (count <= 50)
                                Console.WriteLine($"{code}track={(reference ? "B" : "A")}; colorful-pixels={count}; projected={sceneBounds}");
                        }
                    }
                    var legendPixels = ColorfulPixels(pixels, bitmap, legendBounds, mapExportSurface.RenderSize);
                    check(legendPixels > 150, code + "contains-color-legend");
                    if (!mapPixelsPassed || legendPixels <= 150 || !opaque)
                    {
                        var descendantBounds = VisualTreeHelper.GetDescendantBounds(mapExportSurface);
                        var clip = VisualTreeHelper.GetClip(mapExportSurface)?.Bounds;
                        Console.WriteLine($"{code}legend-pixels={legendPixels}; legend={legendBounds.X:R},{legendBounds.Y:R},{legendBounds.Width:R},{legendBounds.Height:R}; bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight}; dpi={bitmap.DpiX:R},{bitmap.DpiY:R}; surface={mapExportSurface.ActualWidth:R}x{mapExportSurface.ActualHeight:R}; descendants={descendantBounds}; clip={clip}; scroll={scroll.VerticalOffset:R}; map-height={track.ActualHeight:R}");
                        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(unusedStoreDirectory))!;
                        Directory.CreateDirectory(outputDirectory);
                        var fileName = $"lap-map-failure-{name}-{mode}-{Guid.NewGuid():N}.png";
                        Await(RunImageExporter.WriteAsync(bitmap, Path.Combine(outputDirectory, fileName)));
                        Console.WriteLine($"{code}exact-export-bitmap={fileName}");
                    }
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

    private static void CheckComparisonLayout(RunsPage page, RunsViewModel model, FrameworkElement surface,
        Size size, string name, Action<bool, string> check, Action<string, FrameworkElement, Size>? capture)
    {
        var editor = (Expander)page.FindName("RunComparisonControls");
        var button = (Button)page.FindName("RunComparisonButton");
        var selector = (ComboBox)page.FindName("ComparisonRunSelector");
        var hint = (TextBlock)page.FindName("ComparisonHint");
        var summaryScroll = (ScrollViewer)page.FindName("RunsScroll");
        var graphScroll = (ScrollViewer)page.FindName("GraphScroll");
        var lapReview = (Expander)page.FindName("LapReviewExpander");
        var selectedA = model.SelectedRun?.Id;
        var selectedB = model.ComparisonChoice?.Id;

        foreach (var graphs in new[] { false, true })
        {
            if (graphs) model.ShowGraphs(); else model.ShowSummary();
            Ready(model); Arrange(surface, size);
            var scroll = graphs ? graphScroll : summaryScroll;
            var host = (StackPanel)page.FindName(graphs ? "RunGraphContent" : "RunSummaryContent");
            var code = name + (graphs ? "/graph-comparison/" : "/lap-comparison/");
            scroll.ScrollToVerticalOffset(Math.Min(130, scroll.ScrollableHeight)); Arrange(surface, size);
            var returnOffset = scroll.VerticalOffset;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Arrange(surface, size);
            check(editor.IsExpanded && ReferenceEquals(editor.Parent, host) && ReferenceEquals(host.Children[0], editor), code + "single-editor-in-active-content");
            // Text boxes and popup lists have their own template scroll hosts. Only a
            // scroll viewer wrapping the editor's actual controls splits this workspace.
            var editorControls = Descendants(editor).OfType<FrameworkElement>()
                .Where(element => element is Button or TextBox or CheckBox or ComboBox || ReferenceEquals(element, hint));
            check(ReferenceEquals(scroll.Content, host) && NearestScrollViewer(editor) == scroll &&
                editorControls.All(element => NearestScrollViewer(element) == scroll),
                code + "editor-has-no-nested-scroll-region");
            check(Descendants(page).OfType<Expander>().Count(item => item.Name == "RunComparisonControls") == 1,
                code + "one-comparison-editor");
            check(scroll.VerticalOffset <= .5 && IsInViewport(selector, scroll), code + "open-shows-run-selector");
            check(Descendants(editor).OfType<Button>().Any(item => Equals(item.Content, "Compare selected")) &&
                Descendants(editor).OfType<TextBox>().Count() == 2, code + "all-existing-comparison-controls-retained");
            var editorBounds = editor.TransformToAncestor(host).TransformBounds(new Rect(editor.RenderSize));
            var hintBounds = hint.TransformToAncestor(host).TransformBounds(new Rect(hint.RenderSize));
            check(hint.ActualHeight > 0 && hint.Text == model.ComparisonNote &&
                hintBounds.Top >= editorBounds.Top && hintBounds.Bottom <= editorBounds.Bottom,
                code + "full-hint-measured-inside-editor");
            if (!graphs)
            {
                var lapBounds = lapReview.TransformToAncestor(host).TransformBounds(new Rect(lapReview.RenderSize));
                check(lapBounds.Top >= editorBounds.Bottom, code + "editor-and-lap-review-do-not-overlap");
            }
            if (editor.ActualHeight <= scroll.ViewportHeight)
                check(IsInViewport(editor, scroll), code + "complete-editor-fits-viewport");
            // Short windows use the same page scrollbar to reach the lower controls and hint.
            scroll.ScrollToVerticalOffset(Math.Max(0, hintBounds.Bottom - scroll.ViewportHeight + 24));
            Arrange(surface, size);
            check(IsInViewport(hint, scroll), code + "full-hint-reachable-in-page-scroll");
            capture?.Invoke(name + (graphs ? "-graph-comparison-controls" : "-lap-comparison-controls"), surface, size);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Arrange(surface, size);
            check(!editor.IsExpanded && Math.Abs(scroll.VerticalOffset - Math.Min(returnOffset, scroll.ScrollableHeight)) < .5,
                code + "close-restores-reading-position");
            check(model.SelectedRun?.Id == selectedA && model.ComparisonChoice?.Id == selectedB &&
                model.IsGraphWorkspaceOpen == graphs, code + "navigation-keeps-run-selection-and-workspace");
        }
        model.ShowSummary(); Ready(model); Arrange(surface, size);
        check(ReferenceEquals(editor.Parent, page.FindName("RunSummaryContent")) && !editor.IsExpanded,
            name + "/comparison-editor-returns-to-summary");
        summaryScroll.ScrollToHome(); Arrange(surface, size);

        static bool IsInViewport(FrameworkElement element, ScrollViewer scroll)
        {
            var bounds = element.TransformToAncestor(scroll).TransformBounds(new Rect(element.RenderSize));
            return bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5 &&
                bounds.Left >= -.5 && bounds.Right <= scroll.ViewportWidth + .5;
        }

        static ScrollViewer? NearestScrollViewer(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll) return scroll;
            return null;
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
