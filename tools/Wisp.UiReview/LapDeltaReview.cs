using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Laps;
using Wisp.App.NativeRendering;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class LapDeltaReview
{
    internal static int Run(string output, Func<ResourceDictionary> resources)
    {
        if (Process.GetProcesses().Any(p => p.ProcessName.StartsWith("Forza", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Close Forza before the isolated Lap Delta window checks.");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = resources() };
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var settings = new AppSettings { StartWithWindows = false, StartWithForza = false, AutomaticApplicationUpdateChecks = false };
        var now = DateTimeOffset.UtcNow;
        SetupCompletion.Save(settings, SetupPreferences.FromSettings(settings) with
        { DataOutConfirmed = true, DisplayModeConfirmed = true, StockHudConfirmed = true },
            new SetupTelemetryEvidence(settings.UdpPort, 12, 12, TimeSpan.FromMilliseconds(550), now), _ => { }, now);
        var controller = new AppController(settings, _ => { }, new NoStartup(), runsDirectory: Path.Combine(output, "synthetic-runs"));
        try
        {
            foreach (var legacy in new[] { false, true })
            {
                ControlPanelWindow panel = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                try
                {
                    panel.ShowActivated = false;
                    panel.ShowInTaskbar = false;
                    panel.Show();
                    PumpUntil(() => panel.IsLoaded, 3000);
                    Check(panel.IsLoaded, "color-panel-loaded-" + legacy);
                    var control = (LapDeltaSettingsControl)panel.FindName("LapDeltaSettings");
                    Check(control is not null, "settings-present-" + legacy);
                    var timing = (ComboBox)control!.FindName("TimingSelector");
                    timing.ApplyTemplate();
                    Check(ReferenceEquals(timing.Style, control.Resources["LapReferenceComboStyle"]), "themed-timing-selector-" + legacy);
                    Check(ReferenceEquals(timing.ItemContainerStyle, control.Resources["LapReferenceItemStyle"]), "themed-timing-items-" + legacy);
                    timing.SelectedValue = LapTimingMode.TimeAttack;
                    Check(settings.LapTimingMode == LapTimingMode.TimeAttack, "time-attack-selection-" + legacy);
                    var timingPreset = HudPreset.Capture(settings, "Time Attack");
                    var timingRestored = new AppSettings();
                    timingPreset.ApplyTo(timingRestored);
                    Check(timingRestored.LapTimingMode == LapTimingMode.TimeAttack, "timing-profile-roundtrip-" + legacy);
                    timing.SelectedValue = LapTimingMode.GameLaps;
                    var selector = (ComboBox)control!.FindName("ReferenceSelector");
                    selector.ApplyTemplate();
                    Check(ReferenceEquals(selector.Style, control.Resources["LapReferenceComboStyle"]), "themed-selector-" + legacy);
                    Check(ReferenceEquals(selector.ItemContainerStyle, control.Resources["LapReferenceItemStyle"]), "themed-items-" + legacy);
                    var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
                    Check(ReferenceEquals(((Border)popup.Child).Background, selector.FindResource("PanelBrush")), "themed-popup-" + legacy);
                    selector.IsEnabled = false;
                    Check(((Grid)selector.Template.FindName("ComboRoot", selector)).Opacity == .45, "disabled-style-" + legacy);
                    selector.IsEnabled = true;
                    selector.SelectedValue = LapDeltaReference.PreviousLap;
                    Check(settings.LapDeltaReference == LapDeltaReference.PreviousLap, "reference-selection-" + legacy);
                    ((CheckBox)control.FindName("EnabledToggle")).IsChecked = true;
                    ((Slider)control.FindName("ScaleSlider")).Value = 1.25;
                    ((CheckBox)control.FindName("BarToggle")).IsChecked = false;
                    Check(settings.LapDeltaEnabled && settings.LapDeltaScale == 1.25 && !settings.LapDeltaShowBar, "settings-apply-" + legacy);
                    ((CheckBox)control.FindName("MapToggle")).IsChecked = true;
                    ((Slider)control.FindName("MapScaleSlider")).Value = 2.5;
                    Check(control.FindName("AheadColor") is null && control.FindName("TrackColor") is null,
                        "colors-removed-from-gauges-" + legacy);
                    var targets = (ListBox)panel.FindName("ColorTargetSelector");
                    var colorEditor = (ColorWheelEditor)panel.FindName("ColorEditor");
                    var resetColor = (Button)panel.FindName("ResetLapColorButton");
                    string?[] StoredColors() => [settings.LapDeltaAheadColor, settings.LapDeltaBehindColor,
                        settings.LapMapTrackColor, settings.LapMapCarColor, settings.LapMapBackgroundColor];
                    var chosen = new[] { Colors.Lime, Colors.Magenta, Colors.Cyan, Colors.Orange, Color.FromArgb(80, 12, 24, 36) };
                    for (var index = 0; index < chosen.Length; index++)
                    {
                        var before = StoredColors();
                        targets.SelectedIndex = 17 + index;
                        Check(resetColor.Visibility == Visibility.Visible && colorEditor.MinimumOpacity == (index == 4 ? 0 : .25),
                            "lap-color-editor-policy-" + legacy + "-" + index);
                        colorEditor.SelectedColor = chosen[index];
                        var after = StoredColors();
                        Check(after[index] == ColorCustomization.ToHex(chosen[index]) &&
                            before.Where((_, i) => i != index).SequenceEqual(after.Where((_, i) => i != index)),
                            "lap-color-edit-isolated-" + legacy + "-" + index);
                        targets.SelectedIndex = 0;
                        Check(resetColor.Visibility == Visibility.Collapsed, "lap-reset-hidden-for-other-target-" + legacy + "-" + index);
                        targets.SelectedIndex = 17 + index;
                        Check(colorEditor.SelectedColor == chosen[index], "lap-color-selection-retained-" + legacy + "-" + index);
                        resetColor.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Check(StoredColors()[index] is null &&
                            before.Where((_, i) => i != index).SequenceEqual(StoredColors().Where((_, i) => i != index)),
                            "lap-color-reset-isolated-" + legacy + "-" + index);
                        colorEditor.SelectedColor = chosen[index];
                    }
                    Check(settings.LapMapEnabled && settings.LapMapScale == 2.5 && settings.LapMapTrackColor == "#FF00FFFF" &&
                        settings.LapMapCarColor == "#FFFFA500" && settings.LapDeltaAheadColor == "#FF00FF00", "map-and-color-controls-" + legacy);
                    var preset = HudPreset.Capture(settings, "Lap review");
                    var restored = new AppSettings();
                    preset.ApplyTo(restored);
                    Check(restored.LapDeltaEnabled && restored.LapDeltaReference == LapDeltaReference.PreviousLap &&
                        restored.LapDeltaScale == 1.25 && !restored.LapDeltaShowBar, "profile-roundtrip-" + legacy);
                    Check(restored.LapMapEnabled && restored.LapMapScale == 2.5 && restored.LapMapTrackColor == settings.LapMapTrackColor &&
                        restored.LapMapCarColor == settings.LapMapCarColor && restored.LapDeltaAheadColor == settings.LapDeltaAheadColor,
                        "map-colors-profile-" + legacy);
                    var serialized = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                    Check(serialized.LapMapScale == 2.5 && serialized.LapMapCarColor == settings.LapMapCarColor, "map-settings-save-" + legacy);
                    if (legacy) continue;
                    // Render the real settings control without showing the control-panel window.
                    var preview = new LapDeltaSettingsControl { Width = 480 };
                    preview.Initialize(controller);
                    preview.Measure(new Size(480, double.PositiveInfinity));
                    preview.Arrange(new Rect(new Point(), preview.DesiredSize));
                    preview.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(480, (int)Math.Ceiling(preview.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(preview);
                    Save(bitmap, Path.Combine(output, "lap-settings.png"));
                    colorEditor.Width = 480;
                    colorEditor.Measure(new Size(480, double.PositiveInfinity));
                    colorEditor.Arrange(new Rect(new Point(), colorEditor.DesiredSize));
                    colorEditor.UpdateLayout();
                    var colorsBitmap = new RenderTargetBitmap(480, (int)Math.Ceiling(colorEditor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    colorsBitmap.Render(colorEditor);
                    Save(colorsBitmap, Path.Combine(output, "lap-colors.png"));
                }
                finally { panel.Close(); }
            }

            var host = new Window { Width = 320, Height = 112, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                var handle = new WindowInteropHelper(host).EnsureHandle();
                var textures = LapDeltaHudLayer.LoadTextures();
                Check(ReferenceEquals(textures, LapDeltaHudLayer.LoadTextures()), "cached-artwork");
                foreach (var cpu in new[] { false, true })
                {
                    using var device = DirectCompositionDevice.Create(handle, 640, 224, cpu);
                    foreach (var texture in textures) device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                    var readings = new[] { LapDeltaReading.Waiting, new(LapDeltaStatus.RecordingLap), new(LapDeltaStatus.RejoinReference),
                        new(LapDeltaStatus.Comparing, -.35, 60), new(LapDeltaStatus.Comparing, .62, 60), new(LapDeltaStatus.Comparing, 123.45, 60) };
                    for (var i = 0; i < readings.Length; i++)
                    {
                        var commands = new List<DirectCompositionDrawCommand>();
                        LapDeltaHudLayer.Append(commands, readings[i], LapDeltaReference.SessionBest, true);
                        Check(commands.Count <= 16, "bounded-draws-" + cpu + "-" + i);
                        var data = commands.ToArray();
                        for (var j = 0; j < data.Length; j++)
                        {
                            data[j].OriginX *= 2; data[j].OriginY *= 2;
                            data[j].AxisXX *= 2; data[j].AxisXY *= 2; data[j].AxisYX *= 2; data[j].AxisYY *= 2;
                        }
                        device.RenderForCapture(data, data.Length);
                        var pixels = device.CaptureBgra();
                        Check(pixels.Where((_, index) => index % 4 == 3).Count(a => a != 0) > 10000, "native-pixels-" + cpu + "-" + i);
                        Save(BitmapSource.Create(640, 224, 192, 192, PixelFormats.Pbgra32, null, pixels, 640 * 4),
                            Path.Combine(output, $"lap-{(cpu ? "warp" : "gpu")}-{i}.png"));
                    }
                }
            }
            finally { host.Close(); }

            var mapHost = new Window { Width = 360, Height = 360, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                var points = Enumerable.Range(0, 361).Select(i => new LapPosition((float)(180 * Math.Cos(i * Math.PI / 180)),
                    0, (float)(120 * Math.Sin(i * Math.PI / 180)))).ToArray();
                var outline = new LapTrackOutline(Array.AsReadOnly(points), true);
                var artwork = TrackMapHudLayer.CreateArtwork(outline);
                foreach (var offTrackZ in new[] { -500f, 500f })
                {
                    var car = new LapPosition(50, 0, offTrackZ);
                    var expanded = artwork.Bounds.Include(car);
                    var commands = new List<DirectCompositionDrawCommand>();
                    TrackMapHudLayer.Append(commands, artwork, new(outline, car, Stopwatch.GetTimestamp()),
                        new(0, 255, 255), new(255, 165, 0), new(8, 12, 17));
                    var texture = commands[2];
                    foreach (var position in new[] { points[0], points[42], points[270] })
                    {
                        var source = artwork.Bounds.Project(position);
                        var target = expanded.Project(position);
                        var x = texture.OriginX + (8 + 496 * source.X) / 512 * texture.AxisXX;
                        var y = texture.OriginY + (8 + 496 * source.Y) / 512 * texture.AxisYY;
                        Check(Math.Abs(x - (28 + 304 * target.X)) < .001 && Math.Abs(y - (40 + 304 * target.Y)) < .001,
                            "off-track-texture-alignment-" + offTrackZ + "-" + position.X + "-" + position.Z);
                    }
                    var projectedCar = expanded.Project(car);
                    Check(Math.Abs(commands[^1].OriginY + 9 - (40 + 304 * projectedCar.Y)) < .001,
                        "off-track-marker-alignment-" + offTrackZ);
                }
                foreach (var cpu in new[] { false, true })
                {
                    using var device = DirectCompositionDevice.Create(new WindowInteropHelper(mapHost).EnsureHandle(), 720, 720, cpu);
                    foreach (var texture in artwork.Textures) device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                    var commands = new List<DirectCompositionDrawCommand>();
                    TrackMapHudLayer.Append(commands, artwork, new(outline, points[42], Stopwatch.GetTimestamp()),
                        new(0, 255, 255), new(255, 165, 0), new(8, 12, 17, 175));
                    Check(commands.Count == 6, "six-map-draws-" + cpu);
                    var data = commands.ToArray();
                    for (var i = 0; i < data.Length; i++)
                    {
                        data[i].OriginX *= 2; data[i].OriginY *= 2;
                        data[i].AxisXX *= 2; data[i].AxisXY *= 2; data[i].AxisYX *= 2; data[i].AxisYY *= 2;
                    }
                    device.RenderForCapture(data, data.Length);
                    var pixels = device.CaptureBgra();
                    Check(pixels.Where((_, i) => i % 4 == 3).Count(a => a != 0) > 10000, "map-native-pixels-" + cpu);
                    Save(BitmapSource.Create(720, 720, 192, 192, PixelFormats.Pbgra32, null, pixels, 720 * 4),
                        Path.Combine(output, $"map-{(cpu ? "warp" : "gpu")}.png"));
                }
            }
            finally { mapHost.Close(); }

            controller.SetLapDeltaSettings(true, LapDeltaReference.SessionBest, true, 1);
            controller.InitializeLapDeltaWindow();
            var window = controller.LapDeltaOverlay!;
            window.SetTelemetryVisible(true, 1);
            var view = (LapDeltaView)((Viewbox)window.Content).Child;
            PumpUntil(() => HudNativeHost.IsPresented(view), 5000);
            Check(HudNativeHost.IsPresented(view) && HudNativeHost.IsWpfContentSuppressed(view) &&
                HudNativeHost.PresentationHandle(window) != IntPtr.Zero, "actual-native-host");
            foreach (var unlocked in new[] { true, false, true, false })
            {
                window.SetEditMode(unlocked);
                PumpUntil(() => HudNativeHost.IsPresented(view), 2000);
                Check(HudNativeHost.IsWpfContentSuppressed(view), "native-during-edit-" + unlocked);
            }
            window.ApplyAppearance(2, .65);
            window.SetTelemetryVisible(false, .65);
            Check(!window.IsVisible, "hidden-with-telemetry");
            window.SetTelemetryVisible(true, .65);
            PumpUntil(() => HudNativeHost.IsPresented(view), 5000);
            Check(window.Width == 640 && HudNativeHost.IsPresented(view), "resize-and-resume");
            window.SetEnabled(false);
            Check(!window.IsVisible, "disabled-hidden");
            controller.SetLapMapSettings(true, 1.5);
            var mapWindow = controller.LapMapOverlay!;
            mapWindow.SetTelemetryVisible(true, 1);
            var mapView = (TrackMapView)((Viewbox)mapWindow.Content).Child;
            PumpUntil(() => HudNativeHost.IsPresented(mapView), 5000);
            Check(HudNativeHost.IsPresented(mapView) && HudNativeHost.IsWpfContentSuppressed(mapView) && mapWindow.Width == 540,
                "actual-native-map-host");
            controller.LapDelta.Observe(MapSample());
            PumpUntil(() => controller.LapDelta.LatestMap is not null, 3000);
            Check(controller.LapDelta.LatestMap is not null, "direct-map-telemetry");
            var liveSnapshot = TrackMapHudLayer.Capture(mapView);
            var playback = liveSnapshot.CreatePlayback();
            playback.Update(liveSnapshot, Stopwatch.GetTimestamp());
            _ = playback.Build(Stopwatch.GetTimestamp());
            Check(playback.CanReuse(Stopwatch.GetTimestamp()), "map-pending-frame-current");
            controller.LapDelta.Observe(MapSample() with { IsRaceOn = false });
            PumpUntil(() => controller.LapDelta.LatestMap is { IsRecording: false }, 3000);
            Check(controller.LapDelta.LatestMap is { IsRecording: false }, "inactive-map-state");
            Check(!playback.CanReuse(Stopwatch.GetTimestamp()), "inactive-rejects-pending-learning-frame");
            var idleCommands = playback.Build(Stopwatch.GetTimestamp());
            Check(idleCommands[1].TextureId == 95_004, "inactive-map-uses-start-lap-label");

            controller.ResetLapDeltaSession();
            Check(!playback.CanReuse(Stopwatch.GetTimestamp()), "reset-rejects-pending-map-frame");
            var firstCapture = TrackMapHudLayer.Capture(mapView);
            Check(ReferenceEquals(firstCapture.Textures, TrackMapHudLayer.Capture(mapView).Textures), "map-textures-cached");
            mapView.TrackColor = "#FFFF0000";
            Check(ReferenceEquals(firstCapture.Textures, TrackMapHudLayer.Capture(mapView).Textures), "map-color-reuses-artwork");
            Check(mapWindow.GetDisplayKey() != window.GetDisplayKey(), "independent-placement-keys");
            mapWindow.SetEditMode(true);
            mapWindow.SetEditMode(false);
            Check(HudNativeHost.IsWpfContentSuppressed(mapView), "native-map-edit-lock");
            mapWindow.SetEnabled(false);
            Check(!mapWindow.IsVisible, "map-disable");
            File.WriteAllText(Path.Combine(output, "lap-delta-review.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            application.Shutdown();
        }
    }

    private static void PumpUntil(Func<bool> ready, int milliseconds)
    {
        var start = Stopwatch.GetTimestamp();
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (ready()) return;
            Thread.Sleep(10);
        } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds);
    }
    private static VehicleState MapSample() => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = 100,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        ReceivedTimestamp = Stopwatch.GetTimestamp(),
        CarOrdinal = 42,
        Drivetrain = DrivetrainType.RearWheelDrive,
        Lap = new(new LapPosition(100, 0, 100), .1f, 0, .1f, 0, 1),
        GroundSpeedMetersPerSecond = 16,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 3000,
        EngineMaximumRpm = 7000,
        Gear = TransmissionGear.Third,
        Steering = 0,
        Accelerator = 100,
        Brake = 0
    };
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class NoStartup : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
