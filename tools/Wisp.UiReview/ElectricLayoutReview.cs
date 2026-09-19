using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal static class ElectricLayoutReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var bindings = new BindingTrace();
        var captures = new List<object>();
        var failures = new List<string>();
        var phase = "initialize";
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            foreach (var scale in new[] { 1d, .75 })
                // Detached WPF trees do not reproduce HWND fractional-DPI layout.
                // Loaded-window regressions and geometry tests cover display scaling.
                foreach (var dpi in new[] { 96 })
                {
                    var label = $"ev-wrap-{(scale == 1 ? "default" : "75-percent")}-{dpi}dpi";
                    phase = bindings.Phase = label;
                    var settings = CreateSettings(scale);
                    var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                        runsDirectory: Path.Combine(output, label + "-runs"));
                    OverlayWindow? overlay = null;
                    try
                    {
                        ApplySample(controller.ViewModel);
                        overlay = new OverlayWindow(controller);
                        overlay.SetElectricPowertrain(true);
                        var surface = detachSurface(overlay, controller.ViewModel);
                        setDpi(surface, dpi);
                        overlay.ApplyLayout(HudLayoutMode.Native, NativeGaugeMode.Analogue, 1, 1, 1);
                        ApplySample(controller.ViewModel);
                        // ApplyLayout intentionally hides an unshown live HUD.
                        // Override that only after its normal layout/visibility work.
                        surface.BeginAnimation(UIElement.OpacityProperty, null);
                        surface.SetCurrentValue(UIElement.OpacityProperty, 1d);
                        var panel = (Grid)overlay.FindName("RootPanel");
                        var size = new Size(panel.Width, panel.Height);
                        Arrange(surface, size, dpi, setDpi);
                        RequireDetached(overlay, surface);
                        var main = ((Grid)overlay.FindName("NativeElectricAnalogPanel"))
                            .Children.OfType<NativeElectricAnalogSpeedometer>().Single();
                        RequireCompleteSample(main);
                        var gauges = new FrameworkElement[]
                        {
                        (FrameworkElement)overlay.FindName("AttachedAnalogTireTemperature"),
                        (FrameworkElement)overlay.FindName("AttachedPowerGauge"),
                        (FrameworkElement)overlay.FindName("AttachedTorqueGauge")
                        };
                        var geometry = Inspect(label + "-overlay", panel, main, gauges, failures);
                        if (((FrameworkElement)overlay.FindName("AttachedAnalogBoost")).Visibility != Visibility.Collapsed)
                            failures.Add(label + "/boost-visible-in-ev");
                        Save(surface, size, dpi, label + "-overlay.png", "Actual EV overlay", scale, geometry);

                        foreach (var legacy in new[] { false, true })
                        {
                            var appearance = legacy ? "legacy" : "modern";
                            phase = bindings.Phase = label + "-" + appearance;
                            settings.UseLegacyInterface = legacy;
                            ControlPanelWindow window = legacy ? new LegacyMainWindow(controller) : new MainWindow(controller);
                            try
                            {
                                var page = detachSurface(window, controller.ViewModel);
                                ((TabControl)window.FindName("RootTabs")).SelectedIndex = 2;
                                Arrange(page, new Size(1464, 994), dpi, setDpi);
                                RequireDetached(window, page);
                                var supplementary = (SupplementaryAnalogGaugePreview)window.FindName("NativeSupplementaryGaugePreview");
                                var preview = (Grid)supplementary.Parent;
                                if (preview.Parent is not Panel parent)
                                    throw new InvalidOperationException("The authored Appearance preview container changed.");
                                // Keep the actual page's controls and inherited resources;
                                // only detach the capture rectangle from the surrounding form.
                                parent.Children.Remove(preview);
                                preview.DataContext = null;
                                preview.Resources.MergedDictionaries.Add(page.Resources);
                                setDpi(preview, dpi);
                                // A real HWND supplies its DPI before Loaded refreshes
                                // the preview. Detached capture uses the existing
                                // DataContextChanged refresh after setting that DPI.
                                preview.DataContext = controller.ViewModel;
                                ApplySample(controller.ViewModel);
                                preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                var previewSize = preview.DesiredSize;
                                Arrange(preview, previewSize, dpi, setDpi);
                                var previewMain = preview.Children.OfType<NativeElectricAnalogSpeedometer>().Single();
                                RequireCompleteSample(previewMain);
                                var previewGauges = new FrameworkElement[]
                                {
                                supplementary.Children.OfType<AnalogTireTemperatureGaugeView>().Single(),
                                supplementary.Children.OfType<PowerTorqueGaugeView>().Single(g => !g.IsTorque),
                                supplementary.Children.OfType<PowerTorqueGaugeView>().Single(g => g.IsTorque)
                                };
                                var previewGeometry = Inspect(label + "-" + appearance, preview, previewMain, previewGauges, failures);
                                Compare(label + "-" + appearance, geometry, previewGeometry, failures);
                                if (supplementary.Children.OfType<AnalogBoostGaugeView>().Single().Visibility != Visibility.Collapsed)
                                    failures.Add(label + "/" + appearance + "-boost-visible-in-ev");
                                Save(preview, previewSize, dpi, label + "-" + appearance + "-appearance.png",
                                    (legacy ? "Legacy" : "Modern") + " Appearance preview", scale, previewGeometry);
                            }
                            finally { window.Close(); }
                        }
                    }
                    finally
                    {
                        overlay?.Close();
                        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
        }
        catch (Exception error)
        {
            failures.Add(phase + "/" + error.GetType().Name);
            if (error.InnerException is { } inner) failures.Add(phase + "/inner-" + inner.GetType().Name);
        }
        finally { application.Shutdown(); }
        if (bindings.TotalCount > 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "ev-wrap-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual detached EV overlay and modern/legacy Appearance preview controls at 96 DPI; synthetic sample only. Fractional-DPI layout is covered by loaded-window and geometry regressions, not these captures. Native needle shader pixels read back through WARP. No displayed window, live services, production settings, or gameplay performance measurement.",
            captures,
            failures,
            bindingDiagnosticCount = bindings.TotalCount
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"EV wrap review: {captures.Count} offscreen captures, {failures.Count} findings; see ev-wrap-review.json. Synthetic layout only.");
        return failures.Count == 0 ? 0 : 1;

        void Save(FrameworkElement surface, Size size, int dpi, string file, string title, double scale, GeometryReport geometry)
        {
            var bitmap = CaptureWithNeedles(surface, size, dpi);
            var labelled = AddCaption(bitmap, size, dpi, $"{title} · synthetic sample · {scale:P0} gauges · {dpi} DPI");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(labelled));
            using var destination = new FileStream(Path.Combine(output, file), FileMode.CreateNew, FileAccess.Write);
            encoder.Save(destination);
            captures.Add(new { file, scale, dpi, geometry });
        }
    }

    private static AppSettings CreateSettings(double scale)
    {
        var settings = Fixture.All.First(item => item.Name == "orbit-reference").CreateSettings();
        settings.LayoutMode = HudLayoutMode.Native;
        settings.NativeGaugeMode = NativeGaugeMode.Analogue;
        settings.GearDisplayMode = GearDisplayMode.Automatic;
        settings.BoostGaugeEnabled = settings.BoostGaugeAttached = true;
        settings.TireTemperatureGaugeEnabled = settings.TireTemperatureGaugeAttached = true;
        settings.PowerGaugeEnabled = settings.PowerGaugeAttached = true;
        settings.TorqueGaugeEnabled = settings.TorqueGaugeAttached = true;
        settings.BoostGaugeScale = settings.TireTemperatureGaugeScale = scale;
        settings.PowerGaugeScale = settings.TorqueGaugeScale = scale;
        settings.GForceEnabled = settings.GForceAttached = true;
        settings.GForceGaugeScale = 1;
        settings.OverlayWidthScale = settings.OverlayHeightScale = 1;
        settings.OverlayOpacity = 1;
        settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
        settings.AutomaticApplicationUpdateChecks = false;
        settings.StartWithWindows = settings.StartWithForza = false;
        settings.DebugLoggingEnabled = false;
        settings.SetupCompletion = new SetupCompletionRecord
        {
            Version = SetupCompletionRecord.CurrentVersion,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ValidatedUdpPort = settings.UdpPort,
            ValidatedPackets = SetupCompletionRecord.MinimumPackets,
            MovingPackets = SetupCompletionRecord.MinimumMovingPackets,
            ValidatedElapsedMilliseconds = SetupCompletionRecord.MinimumElapsedMilliseconds,
            DataOutConfirmed = true,
            DisplayModeConfirmed = true,
            StockHudConfirmed = true
        };
        return settings;
    }

    private static void ApplySample(DiagnosticsViewModel model)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            CarOrdinal = 2,
            NumCylinders = 0,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ReceivedTimestamp = Stopwatch.GetTimestamp(),
            Drivetrain = DrivetrainType.AllWheelDrive,
            WheelRotationRadiansPerSecond = new(88, 88, 88, 88),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = .4f,
            LongitudinalAccelerationMetersPerSecondSquared = .8f,
            Steering = 0,
            Accelerator = 160,
            Brake = 0,
            GameTimestampMilliseconds = 12000,
            EngineRpm = 4500,
            EngineMaximumRpm = 8000,
            PowerWatts = 318_413,
            TorqueNm = 507,
            BoostPressurePsi = 0,
            TireTemperatureFahrenheit = new(210, 214, 224, 226),
            GroundSpeedMetersPerSecond = 30,
            Gear = TransmissionGear.Second
        };
        var native = NativeHudSnapshot.Unavailable(carOrdinal: state.CarOrdinal) with
        {
            Available = true,
            Status = NativeAssistProviderStatus.Ready,
            NativeNeedleAngleDegrees = NativeGaugeGeometry.ElectricAnalogNeedleAngle(67, 240),
            NativeNeedleBlurAmount = 0,
            NativeRegenFillAmount = 0,
            NativePowerFillAmount = .64,
            NativeRegenPowerRatio = .3,
            NativeElectricMaximumSpeed = 240,
            NativeGaugeObservedTimestamp = Stopwatch.GetTimestamp(),
            ElectricGearState = new(true, 1, -1, -1, -1, true),
            DisplayedSpeedState = new(true, 0, 6, 7, false, false, true, SpeedUnit.MilesPerHour)
        };
        model.Update(state, new IndicatedSpeed(30, 67, true, false, "Review fixture"),
            new CalibrationResult(.34, null, 1, 120, true, string.Empty, true, RollingRadii.Uniform(.34)),
            native,
            new ReceiverStatistics(1200, 0, 60, PacketParseError.None, null), TimeSpan.Zero,
            SpeedUnit.MilesPerHour, 60, refreshDiagnostics: true, updateGForce: true);
        if (!model.NativePreviewFrame.IsElectric || !model.NativeGaugeFrame.IsElectric)
            throw new InvalidOperationException("The isolated sample did not select an EV.");
    }

    private static void RequireCompleteSample(NativeElectricAnalogSpeedometer control)
    {
        if (!control.Frame.IsElectric || !control.Frame.SpeedAvailable ||
            ((Image)control.FindName("GearImage")).Source is null ||
            ((UIElement)control.FindName("GearImage")).Visibility != Visibility.Visible ||
            ((UIElement)control.FindName("PowerBarPanel")).Visibility != Visibility.Visible ||
            ((UIElement)control.FindName("Needle")).Visibility != Visibility.Visible)
            throw new InvalidOperationException("The detached EV sample did not populate gear, power bar and needle artwork.");
    }

    private static GeometryReport Inspect(string name, FrameworkElement surface,
        NativeElectricAnalogSpeedometer speedometer, FrameworkElement[] gauges, List<string> failures)
    {
        var center = speedometer.TransformToAncestor(surface).Transform(new Point(182.5, 200.5));
        var dial = (Image)speedometer.FindName("DialImage");
        var dialBounds = dial.TransformToAncestor(surface).TransformBounds(new Rect(dial.RenderSize));
        var results = new List<GaugeGeometry>();
        var labels = new[] { "tire", "power", "torque" };
        for (var index = 0; index < gauges.Length; index++)
        {
            var gauge = gauges[index];
            if (gauge.Visibility != Visibility.Visible) failures.Add(name + "/" + labels[index] + "-hidden");
            var bounds = gauge.TransformToAncestor(surface).TransformBounds(new Rect(gauge.RenderSize));
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var clearance = (point - center).Length - dialBounds.Width / 345 * 145.5 -
                bounds.Width * .43 - bounds.Width / 136 * 1.1;
            if (clearance < 3.9) failures.Add(name + "/" + labels[index] + "-speedometer-overlap");
            if (!new Rect(surface.RenderSize).Contains(bounds)) failures.Add(name + "/" + labels[index] + "-clipped");
            results.Add(new(labels[index], bounds.X, bounds.Y, bounds.Width, bounds.Height, point.X, point.Y, clearance));
        }
        if (!(results[0].CenterY < results[1].CenterY && results[1].CenterY < results[2].CenterY))
            failures.Add(name + "/tire-power-torque-order");
        for (var i = 0; i < results.Count; i++)
            for (var j = i + 1; j < results.Count; j++)
            {
                var a = results[i]; var b = results[j];
                var distance = new Vector(a.CenterX - b.CenterX, a.CenterY - b.CenterY).Length;
                if (distance < (a.Width + b.Width) * (.43 + 1.1 / 136) + 3.9)
                    failures.Add(name + "/" + a.Name + "-" + b.Name + "-overlap");
            }
        return new(surface.RenderSize.Width, surface.RenderSize.Height, center.X, center.Y, results.ToArray());
    }

    private static void Compare(string name, GeometryReport overlay, GeometryReport preview, List<string> failures)
    {
        if (Math.Abs(overlay.DialCenterX - preview.DialCenterX) > .08 ||
            Math.Abs(overlay.DialCenterY - preview.DialCenterY) > .08)
            failures.Add(name + "/preview-speedometer-offset");
        for (var i = 0; i < overlay.Gauges.Length; i++)
        {
            var a = overlay.Gauges[i]; var b = preview.Gauges[i];
            if (Math.Abs(a.X - b.X) > .08 || Math.Abs(a.Y - b.Y) > .08 ||
                Math.Abs(a.Width - b.Width) > .08 || Math.Abs(a.Height - b.Height) > .08)
                failures.Add(name + "/preview-" + a.Name + "-differs-from-overlay");
        }
    }

    private static void Arrange(FrameworkElement surface, Size size, int dpi, Action<FrameworkElement, int> setDpi)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            setDpi(surface, dpi);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            surface.UpdateLayout();
            surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            surface.UpdateLayout();
        }
    }

    private static void RequireDetached(Window window, FrameworkElement surface)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero || PresentationSource.FromVisual(surface) is not null)
            throw new InvalidOperationException("The offscreen review unexpectedly acquired a presentation surface.");
    }

    private static BitmapSource AddCaption(BitmapSource bitmap, Size size, int dpi, string caption)
    {
        const double padding = 16;
        const double heading = 38;
        var targetSize = new Size(size.Width + padding * 2, size.Height + padding + heading);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 30, 37)), null, new Rect(targetSize));
            dc.DrawText(new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(194, 208, 219)), dpi / 96d), new Point(padding, 10));
            dc.DrawImage(bitmap, new Rect(padding, heading, size.Width, size.Height));
        }
        var result = new RenderTargetBitmap((int)Math.Ceiling(targetSize.Width * dpi / 96),
            (int)Math.Ceiling(targetSize.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    // RenderTargetBitmap cannot execute the PS3 needle effects. Use the same
    // bounded hidden-WARP readback pattern as PowerTorqueShaderCapture, selecting
    // the electric material where the actual authored child requests it.
    private static BitmapSource CaptureWithNeedles(FrameworkElement surface, Size size, int dpi)
    {
        var originals = new List<(Canvas Parent, int Index, NativeAnalogNeedleVisual Needle, Image Image)>();
        try
        {
            foreach (var needle in Descendants(surface).OfType<NativeAnalogNeedleVisual>().ToArray())
            {
                if (!VisibleWithin(needle, surface)) continue;
                if (VisualTreeHelper.GetParent(needle) is not Canvas parent)
                    throw new InvalidOperationException("The authored needle canvas changed.");
                var transform = needle.TransformToAncestor(surface);
                var origin = transform.Transform(new Point());
                var scaleX = (transform.Transform(new Point(1, 0)) - origin).Length;
                var scaleY = (transform.Transform(new Point(0, 1)) - origin).Length;
                var width = Math.Max(1, (int)Math.Ceiling(needle.ActualWidth * scaleX * dpi / 96));
                var height = Math.Max(1, (int)Math.Ceiling(needle.ActualHeight * scaleY * dpi / 96));
                var image = new Image
                {
                    Source = CaptureNeedle(width, height, needle.BlurAmount, needle.IsElectricMaterial),
                    Width = needle.Width,
                    Height = needle.Height,
                    Margin = needle.Margin,
                    Opacity = needle.Opacity,
                    Visibility = needle.Visibility,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    RenderTransform = needle.RenderTransform.CloneCurrentValue(),
                    RenderTransformOrigin = needle.RenderTransformOrigin,
                    LayoutTransform = needle.LayoutTransform.CloneCurrentValue()
                };
                Canvas.SetLeft(image, Canvas.GetLeft(needle)); Canvas.SetTop(image, Canvas.GetTop(needle));
                Canvas.SetRight(image, Canvas.GetRight(needle)); Canvas.SetBottom(image, Canvas.GetBottom(needle));
                Panel.SetZIndex(image, Panel.GetZIndex(needle));
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var index = parent.Children.IndexOf(needle);
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, image);
                originals.Add((parent, index, needle, image));
            }
            surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
            var pixelWidth = checked((int)Math.Ceiling(size.Width * dpi / 96));
            var pixelHeight = checked((int)Math.Ceiling(size.Height * dpi / 96));
            if (pixelWidth is < 1 or > 4096 || pixelHeight is < 1 or > 4096)
                throw new InvalidOperationException("The EV review surface is outside the bounded capture size.");
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            foreach (var original in originals)
            {
                original.Parent.Children.Remove(original.Image);
                original.Parent.Children.Insert(original.Index, original.Needle);
            }
            surface.UpdateLayout();
        }
    }

    private static readonly Dictionary<(int Width, int Height, double Blur, bool Electric), BitmapSource> Needles = [];

    private static BitmapSource CaptureNeedle(int width, int height, double blur, bool electric)
    {
        if (!double.IsFinite(blur) || width is < 1 or > 2048 || height is < 1 or > 2048)
            throw new InvalidOperationException("The needle shader capture is outside its bounded size.");
        var key = (width, height, blur, electric);
        if (Needles.TryGetValue(key, out var cached)) return cached;
        var window = CreateWindowEx(0x08200080, "STATIC", "Wisp EV layout pixel review", 0x80000000,
            -32000, -32000, width, height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using var device = DirectCompositionDevice.Create(window, width, height, cpuRendering: true);
            var command = AnalogHudScene.Quad(0, new AnalogHudRect(0, 0, width, height),
                shader: electric ? DirectCompositionShader.ElectricNeedle : DirectCompositionShader.Needle);
            command.ParameterX = (float)blur;
            device.RenderForCapture([command], 1);
            var pixels = device.CaptureBgra();
            var visible = 0;
            for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) visible++;
            if (visible == 0 || visible == width * height)
                throw new InvalidOperationException("Native needle readback was blank or opaque.");
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze();
            if (Needles.Count < 24) Needles.Add(key, bitmap);
            return bitmap;
        }
        finally { DestroyWindow(window); }
    }

    private static bool VisibleWithin(DependencyObject child, FrameworkElement surface)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(current, surface)) return true;
        }
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record GaugeGeometry(string Name, double X, double Y, double Width, double Height,
        double CenterX, double CenterY, double DialClearance);
    private sealed record GeometryReport(double Width, double Height, double DialCenterX, double DialCenterY, GaugeGeometry[] Gauges);
    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
