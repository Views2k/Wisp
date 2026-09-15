using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App;
using Wisp.Core;
using Wisp.Telemetry;

namespace Wisp.UiReview;

internal static class SupplementaryGaugeReview
{
    internal static void Run(string output,
        Func<Window, object?, FrameworkElement> detachSurface, Action<FrameworkElement, int> setDpi,
        List<string> captures, List<string> failures, List<object> geometry)
    {
        var variants = new (string Name, double[] Scales)[]
        {
            ("default-100", [1, 1, 1, 1]),
            ("mixed-sizes", [.75, 1.25, 1.5, .5]),
            ("minimum-50", [.5, .5, .5, .5]),
            ("maximum-200", [2, 2, 2, 2])
        };
        foreach (var variant in variants)
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                var name = $"supplementary-{variant.Name}-{dpi}dpi";
                var settings = Fixture.All.First(item => item.Name == "orbit-reference").CreateSettings();
                settings.LayoutMode = HudLayoutMode.Native;
                settings.NativeGaugeMode = NativeGaugeMode.Analogue;
                settings.BoostGaugeEnabled = settings.TireTemperatureGaugeEnabled = true;
                settings.PowerGaugeEnabled = settings.TorqueGaugeEnabled = true;
                settings.BoostGaugeAttached = settings.TireTemperatureGaugeAttached = true;
                settings.PowerGaugeAttached = settings.TorqueGaugeAttached = true;
                settings.BoostGaugeScale = variant.Scales[0];
                settings.TireTemperatureGaugeScale = variant.Scales[1];
                settings.PowerGaugeScale = variant.Scales[2];
                settings.TorqueGaugeScale = variant.Scales[3];
                settings.OverlayWidthScale = settings.OverlayHeightScale = 1;
                settings.BackgroundParticlesEnabled = settings.AnimatedBackground = false;
                settings.AutomaticApplicationUpdateChecks = false;
                settings.StartWithWindows = settings.StartWithForza = false;
                var controller = new AppController(settings, _ => { }, new NoStartupRegistration(),
                    runsDirectory: Path.Combine(output, name + "-runs"));
                OverlayWindow? overlay = null;
                try
                {
                    ApplySample(controller.ViewModel);
                    overlay = new OverlayWindow(controller);
                    var overlaySurface = detachSurface(overlay, controller.ViewModel);
                    // The authored root normally fades in with telemetry. The detached
                    // review has no window, so make that same root opaque for capture.
                    overlaySurface.SetCurrentValue(UIElement.OpacityProperty, 1d);
                    var panel = (FrameworkElement)overlay.FindName("RootPanel");
                    var size = new Size(panel.Width, panel.Height);
                    setDpi(overlaySurface, dpi);
                    Arrange(overlaySurface, size);
                    var overlayGauges = new FrameworkElement[]
                    {
                    (FrameworkElement)overlay.FindName("AttachedAnalogBoost"),
                    (FrameworkElement)overlay.FindName("AttachedAnalogTireTemperature"),
                    (FrameworkElement)overlay.FindName("AttachedPowerGauge"),
                    (FrameworkElement)overlay.FindName("AttachedTorqueGauge")
                    };
                    Verify(name + "-overlay", overlaySurface, size, overlayGauges, variant.Scales, failures, geometry);
                    Save(name + "-overlay.png", overlaySurface, size);

                    var preview = new SupplementaryAnalogGaugePreview { DataContext = controller.ViewModel };
                    preview.Resources.MergedDictionaries.Add(overlay.Resources);
                    var previewSize = new Size(preview.Width, preview.Height);
                    setDpi(preview, dpi);
                    Arrange(preview, previewSize);
                    var previewGauges = preview.Children.OfType<FrameworkElement>().ToArray();
                    Verify(name + "-preview", preview, previewSize, previewGauges, variant.Scales, failures, geometry);
                    Save(name + "-preview.png", preview, previewSize);
                }
                finally
                {
                    overlay?.Close();
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                void Save(string filename, FrameworkElement surface, Size size)
                {
                    var bitmap = PowerTorqueShaderCapture.RenderSurface(surface, size, dpi);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, filename));
                    encoder.Save(stream);
                    captures.Add(filename);
                }
            }
    }

    private static void ApplySample(DiagnosticsViewModel model)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            CarOrdinal = 1335,
            NumCylinders = 8,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            Drivetrain = DrivetrainType.RearWheelDrive,
            WheelRotationRadiansPerSecond = new(88, 88, 88, 88),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            Steering = 0,
            Accelerator = 160,
            Brake = 0,
            GameTimestampMilliseconds = 12000,
            EngineRpm = 4500,
            EngineMaximumRpm = 8000,
            PowerWatts = 318_413,
            TorqueNm = 507,
            BoostPressurePsi = 24,
            TireTemperatureFahrenheit = new(176, 178, 176, 178),
            GroundSpeedMetersPerSecond = 30,
            Gear = TransmissionGear.Fourth
        };
        model.Update(state, new IndicatedSpeed(30, 67, true, false, "Review fixture"),
            new CalibrationResult(.34, null, 1, 120, true, string.Empty, true, RollingRadii.Uniform(.34)),
            new ReceiverStatistics(1200, 0, 60, PacketParseError.None, null), TimeSpan.Zero,
            SpeedUnit.MilesPerHour, 60, refreshDiagnostics: true, updateGForce: false);
    }

    private static void Verify(string name, FrameworkElement surface, Size size,
        FrameworkElement[] gauges, double[] scales, List<string> failures, List<object> geometry)
    {
        if (gauges.Length != 4) throw new InvalidOperationException("Expected four authored supplementary controls.");
        var measured = gauges.Select((gauge, index) => MeasureRim(gauge, surface, scales[index])).ToArray();
        void Near(double a, double b, string reason)
        {
            if (Math.Abs(a - b) > .08) failures.Add(name + "/" + reason + $" ({a:0.###} vs {b:0.###})");
        }
        Near(measured[0].CenterX, measured[1].CenterX, "left-column-centers");
        Near(measured[2].CenterX, measured[3].CenterX, "right-column-centers");
        Near(measured[0].CenterY, measured[2].CenterY, "upper-row-centers");
        Near(measured[1].CenterY, measured[3].CenterY, "lower-row-centers");
        for (var i = 0; i < measured.Length; i++)
        {
            var rim = measured[i];
            Near(rim.Radius / scales[i], measured[0].Radius / scales[0], $"gauge-{i}-normalized-rim");
            Near(rim.Radius, 136 * .43 * scales[i], $"gauge-{i}-rendered-size");
            if (rim.CenterX - rim.Radius < -.08 || rim.CenterY - rim.Radius < -.08 ||
                rim.CenterX + rim.Radius > size.Width + .08 || rim.CenterY + rim.Radius > size.Height + .08)
                failures.Add(name + $"/gauge-{i}-rim-clipped");
            var content = gauges[i].TransformToAncestor(surface).TransformBounds(VisualTreeHelper.GetDrawing(gauges[i]).Bounds);
            if (content.Left < -.08 || content.Top < -.08 || content.Right > size.Width + .08 || content.Bottom > size.Height + .08)
                failures.Add(name + $"/gauge-{i}-artwork-clipped");
            for (var j = i + 1; j < measured.Length; j++)
            {
                var other = measured[j];
                var distance = new Vector(rim.CenterX - other.CenterX, rim.CenterY - other.CenterY).Length;
                if (distance < rim.Radius + other.Radius)
                    failures.Add(name + $"/rim-overlap-{i}-{j}");
            }
        }
        geometry.Add(new { name, size.Width, size.Height, gauges = measured });
    }

    // Read the circle from the real rendered track geometry, including each
    // DrawingGroup and visual transform. Equal control boxes alone are not proof.
    private static Rim MeasureRim(FrameworkElement gauge, FrameworkElement surface, double scale)
    {
        if (gauge.Visibility != Visibility.Visible) throw new InvalidOperationException("A review gauge is not visible.");
        var drawing = VisualTreeHelper.GetDrawing(gauge) ?? throw new InvalidOperationException("Gauge did not render.");
        var candidates = GeometryDrawings(drawing, Matrix.Identity);
        var track = candidates.First(item => item.Drawing.Geometry is StreamGeometry && item.Drawing.Pen is not null &&
            item.Transform.TransformBounds(item.Drawing.Geometry.Bounds).Width > gauge.ActualWidth * .7 &&
            item.Transform.TransformBounds(item.Drawing.Geometry.Bounds).Height > gauge.ActualHeight * .7);
        var path = PathGeometry.CreateFromGeometry(track.Drawing.Geometry);
        var figure = path.Figures.Single();
        var arc = figure.Segments.OfType<ArcSegment>().Single();
        if (Math.Abs(arc.Size.Width - arc.Size.Height) > .001)
            throw new InvalidOperationException("The authored track is not circular.");
        var start = figure.StartPoint;
        var end = arc.Point;
        var chord = end - start;
        var radius = arc.Size.Width;
        var halfChord = chord.Length / 2;
        var height = Math.Sqrt(radius * radius - halfChord * halfChord);
        var sign = arc.IsLargeArc == (arc.SweepDirection == SweepDirection.Clockwise) ? -1 : 1;
        var localCenter = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2) +
                          new Vector(-chord.Y, chord.X) / chord.Length * height * sign;
        var toSurface = gauge.TransformToAncestor(surface);
        Point Map(Point p) => toSurface.Transform(track.Transform.Transform(p));
        var center = Map(localCenter);
        var radiusX = (Map(localCenter + new Vector(radius, 0)) - center).Length;
        var radiusY = (Map(localCenter + new Vector(0, radius)) - center).Length;
        if (Math.Abs(radiusX - radiusY) > .001)
            throw new InvalidOperationException("The rendered gauge is stretched.");
        return new Rim(gauge.GetType().Name, scale, center.X, center.Y, radiusX);
    }

    private static IEnumerable<(GeometryDrawing Drawing, MatrixTransform Transform)> GeometryDrawings(Drawing drawing, Matrix parent)
    {
        if (drawing is DrawingGroup group)
        {
            var matrix = group.Transform?.Value ?? Matrix.Identity;
            matrix.Append(parent);
            foreach (var child in group.Children)
                foreach (var geometry in GeometryDrawings(child, matrix)) yield return geometry;
        }
        else if (drawing is GeometryDrawing geometry) yield return (geometry, new MatrixTransform(parent));
    }

    private static void Arrange(FrameworkElement surface, Size size)
    {
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        surface.UpdateLayout();
    }

    private sealed record Rim(string Control, double Scale, double CenterX, double CenterY, double Radius);

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
}
