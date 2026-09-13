using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Wisp.App.Drift;
using Wisp.App.NativeRendering;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class DriftGaugeReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var failures = new List<string>();
        var captures = new List<object>();
        var assets = DriftGaugeVisuals.LoadOnUiThread();
        if (!ReferenceEquals(assets, DriftGaugeVisuals.LoadOnUiThread())) failures.Add("artwork-textures-are-not-cached");
        var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
        var size = new Size(DriftGaugeVisuals.DesignWidth, DriftGaugeVisuals.DesignHeight);
        var readings = new (string Name, DriftAngleReading Reading)[]
        {
            ("below-target", new(18, DriftGuidanceState.BelowTarget)),
            ("on-target", new(40, DriftGuidanceState.OnTarget)),
            ("left-on-target", new(-40, DriftGuidanceState.OnTarget)),
            ("above-range", new(135, DriftGuidanceState.AboveTarget)),
            ("left-above-range", new(-135, DriftGuidanceState.AboveTarget)),
            ("low-speed", new(null, DriftGuidanceState.LowSpeed)),
            ("unavailable", new(null, DriftGuidanceState.Unavailable)),
            ("reversing", new(null, DriftGuidanceState.Reversing))
        };
        var gauge = assets.Single(texture => texture.Id == 2);
        var gaugePixels = gauge.Pixels.Span;
        const double rulerGapX = 24 + 7.5 / 180 * 572;
        const double rulerGapY = 33;
        var gaugeX = (int)Math.Round(rulerGapX * gauge.Width / size.Width);
        var gaugeY = (int)Math.Round(rulerGapY * gauge.Height / size.Height);
        for (var y = gaugeY; y <= gaugeY + 1; y++)
            for (var x = gaugeX - 1; x <= gaugeX + 1; x++)
                if (gaugePixels[y * gauge.Stride + x * 4 + 3] != 0)
                    failures.Add("static-gauge/horizontal-baseline-between-ticks");
        foreach (var darkMode in new[] { false, true })
            foreach (var dpi in new[] { 96, 144 })
                foreach (var sample in readings)
                {
                    var phase = sample.Name + (darkMode ? "-dark" : "") + "-" + dpi;
                    var scale = dpi / 96d;
                    var presentation = new DriftGaugePresentation((int)(size.Width * scale), (int)(size.Height * scale),
                        (float)scale, (float)scale, 1, true, 40, 10, darkMode);
                    var count = DriftGaugeVisuals.Build(commands, presentation, sample.Reading);
                    Check(darkMode ? commands[0].TextureId == 4 && commands.Take(count).Any(command => command.TextureId == 5)
                        : commands.Take(count).All(command => command.TextureId < 4), "localized-halo-mode");
                    if (darkMode)
                    {
                        var transparentCommands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
                        var transparentCount = DriftGaugeVisuals.Build(transparentCommands, presentation with { DarkMode = false }, sample.Reading);
                        Check(commands.Take(count).Where(command => command.TextureId < 4).SequenceEqual(transparentCommands.Take(transparentCount)),
                            "dark-mode-changes-foreground-artwork");
                    }
                    Check(count is > 3 and < DriftGaugeVisuals.MaximumCommands, "command-count");
                    for (var index = 0; index < count; index++)
                    {
                        var command = commands[index];
                        Check(assets.Any(texture => texture.Id == command.TextureId), "unknown-texture");
                        Check(float.IsFinite(command.OriginX) && float.IsFinite(command.OriginY) &&
                              command.OriginX >= -.1 && command.OriginY >= -.1 &&
                              command.OriginX + command.AxisXX <= presentation.Width + .1 &&
                              command.OriginY + command.AxisYY <= presentation.Height + .1, "command-clipped");
                    }
                    if (sample.Reading.SignedDegrees is { } angle)
                    {
                        var marker = commands.Take(count).Single(command => command.TextureId == 1 &&
                            Math.Abs(command.AxisXX / scale - 3) < .01 && Math.Abs(command.AxisYY / scale - 34) < .01);
                        var expected = 24 + (Math.Clamp(angle, -90, 90) + 90) / 180 * 572;
                        Check(Math.Abs(marker.OriginX / scale + 1.5 - expected) < .01, "marker-position");
                        Check(Math.Abs(marker.AxisXX / scale - 3) < .01, "marker-width");
                        if (Math.Abs(angle) > 90)
                            Check(marker.TintR == 1 && marker.TintG == 0 && Math.Abs(marker.TintB - 136 / 255f) < .001, "over-range-color");
                    }
                    else
                        Check(commands.Take(count).All(command => command.TextureId != 6 &&
                            !(command.TextureId == 1 && Math.Abs(command.AxisXX / scale - 3) < .01 && Math.Abs(command.AxisYY / scale - 34) < .01)),
                            "unavailable-state-invents-marker");
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen())
                        DriftGaugeVisuals.Draw(drawing, size, sample.Reading, 40, 10, darkMode);
                    VisualTreeHelper.SetRootDpi(visual, new DpiScale(scale, scale));
                    var bitmap = new RenderTargetBitmap(presentation.Width, presentation.Height, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var pixels = new byte[presentation.Width * presentation.Height * 4];
                    bitmap.CopyPixels(pixels, presentation.Width * 4, 0);
                    var nontransparent = 0;
                    var touchedEdge = false;
                    for (var y = 0; y < presentation.Height; y++)
                        for (var x = 0; x < presentation.Width; x++)
                        {
                            if (pixels[(y * presentation.Width + x) * 4 + 3] == 0) continue;
                            nontransparent++;
                            if (x == 0 || y == 0 || x == presentation.Width - 1 || y == presentation.Height - 1) touchedEdge = true;
                        }
                    Check(nontransparent > 1500 * scale * scale, "empty-gauge-artwork");
                    Check(!touchedEdge, "artwork-touches-image-edge");
                    var gapIndex = ((int)Math.Round(rulerGapY * scale) * presentation.Width + (int)Math.Round(rulerGapX * scale)) * 4;
                    var gapAlpha = pixels[gapIndex + 3];
                    Check(gapAlpha == 0, "mode-has-ruler-line-or-panel-between-ticks");
                    foreach (var clear in new[] { new Point(10, 54), new Point(610, 54), new Point(310, 2), new Point(450, 104) })
                    {
                        var clearIndex = ((int)Math.Round(clear.Y * scale) * presentation.Width + (int)Math.Round(clear.X * scale)) * 4;
                        Check(pixels[clearIndex + 3] == 0, "enclosing-container-remains");
                    }
                    var haloIndex = ((int)Math.Round(22 * scale) * presentation.Width + (int)Math.Round((24 + 10d / 180 * 572 + 2.5) * scale)) * 4;
                    Check(darkMode ? pixels[haloIndex + 3] >= 24 : pixels[haloIndex + 3] == 0, "tick-localized-halo");
                    var filename = "drift-" + phase + ".png";
                    Save(bitmap, filename);
                    foreach (var sky in new[] { false, true })
                    {
                        var background = new DrawingVisual();
                        using (var drawing = background.RenderOpen())
                        {
                            drawing.DrawRectangle(sky ? Brushes.White : new SolidColorBrush(Color.FromRgb(24, 29, 35)), null, new Rect(size));
                            drawing.DrawImage(bitmap, new Rect(size));
                        }
                        VisualTreeHelper.SetRootDpi(background, new DpiScale(scale, scale));
                        var preview = new RenderTargetBitmap(presentation.Width, presentation.Height, dpi, dpi, PixelFormats.Pbgra32);
                        preview.Render(background);
                        Save(preview, (sky ? "sky-preview-" : "preview-") + filename);
                        if (!sky) continue;
                        var skyPixels = new byte[pixels.Length];
                        preview.CopyPixels(skyPixels, presentation.Width * 4, 0);
                        Check(skyPixels[gapIndex] == 255 && skyPixels[gapIndex + 1] == 255 && skyPixels[gapIndex + 2] == 255,
                            "mode-obscures-sky-between-ticks");
                        if (darkMode)
                            Check(skyPixels[haloIndex] < 235 && skyPixels[haloIndex + 1] < 235 && skyPixels[haloIndex + 2] < 235,
                                "localized-halo-does-not-contrast-white-sky");
                    }
                    captures.Add(new { phase, filename, darkMode, presentation.Width, presentation.Height, commandCount = count, nontransparent, touchedEdge, gapAlpha });

                    void Check(bool condition, string code)
                    {
                        if (!condition) failures.Add(phase + "/" + code);
                    }
                    void Save(BitmapSource image, string file)
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using var stream = File.Create(Path.Combine(output, file)); encoder.Save(stream);
                    }
                }
        ReviewZoneArtwork(output, assets, captures, failures);
        ReviewBlackBacking(output, captures, failures);
        var windowChecks = CheckWindowFrame(output, loadResources, failures);
        File.WriteAllText(Path.Combine(output, "drift-gauge-review.json"), JsonSerializer.Serialize(new
        {
            input = "Static exact-scene artwork checks with transparent, localized-halo and adjustable black-backing modes. Angle and percentage legibility checks, plus dark-scene and white-sky previews. No HWND, renderer device, services, or desktop capture.",
            captures,
            windowChecks,
            failures,
            textureBytes = assets.Sum(texture => texture.Pixels.Length)
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Drift gauge review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} state/DPI captures; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;
    }

    private static List<object> CheckWindowFrame(string output, Func<ResourceDictionary> loadResources,
        List<string> failures)
    {
        if (Application.Current is not null)
            throw new InvalidOperationException("The drift window review needs its own resource-only application.");
        var application = new ResourceOnlyApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppController? controller = null;
        DriftGaugeWindow? window = null;
        var checks = new List<object>();
        try
        {
            application.Resources = loadResources();
            controller = new AppController(new AppSettings
            {
                StartWithWindows = false,
                StartWithForza = false,
                AutomaticApplicationUpdateChecks = false
            }, _ => { }, new NoStartupRegistration(), runsDirectory: Path.Combine(output, "synthetic-runs"));
            window = new DriftGaugeWindow(controller);
            window.SetEnabled(true);
            var content = window.Content as FrameworkElement ??
                throw new InvalidOperationException("The drift window has no renderable content.");
            // Detach actual window content so an unshown parent's visibility cannot hide a frame.
            window.Content = null;
            foreach (var editMode in new[] { false, true, false })
            {
                window.SetEditMode(editMode);
                content.Measure(new Size(620, 108));
                content.Arrange(new Rect(0, 0, 620, 108));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(620, 108, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var pixels = new byte[620 * 108 * 4];
                bitmap.CopyPixels(pixels, 620 * 4, 0);
                var alphaPixels = pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha != 0);
                var hasHandle = new WindowInteropHelper(window).Handle != IntPtr.Zero;
                if (alphaPixels != 0) failures.Add("actual-window/frame-visible-" + editMode);
                if (editMode && content.Visibility != Visibility.Visible)
                    failures.Add("actual-window/unlocked-content-not-visible");
                if (window.Cursor != (editMode ? Cursors.SizeAll : Cursors.Arrow))
                    failures.Add("actual-window/edit-cursor-" + editMode);
                if (window.IsVisible || window.ShowActivated || window.WindowStyle != WindowStyle.None ||
                    hasHandle || PresentationSource.FromVisual(content) is not null)
                    failures.Add("actual-window/unexpected-host-or-chrome-" + editMode);
                checks.Add(new { editMode, alphaPixels, hasHandle, visible = window.IsVisible });
            }
        }
        finally
        {
            try
            {
                try { window?.Close(); }
                finally { controller?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            finally { application.Shutdown(); }
        }
        return checks;
    }

    private sealed class ResourceOnlyApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    private static void ReviewBlackBacking(string output, List<object> captures, List<string> failures)
    {
        var profile = new DriftZoneScoringProfile(10, 110, (double)59.4f, 30, 40);
        var reading = new DriftAngleReading(40, DriftGuidanceState.AngleBonusIncreasing) { IsZoneGuidance = true, MagnitudeDegrees = 40 };
        foreach (var dpi in new[] { 96, 144 })
            foreach (var opacity in new[] { 0d, .25, .5, 1 })
            {
                var scale = dpi / 96d;
                var size = new Size(620, 108);
                var width = (int)(size.Width * scale);
                var height = (int)(size.Height * scale);
                var phase = $"black-backing-{opacity:P0}-{dpi}";
                var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
                var presentation = new DriftGaugePresentation(width, height, (float)scale, (float)scale, 1, true, 40, 10,
                    GuidanceMode: DriftGaugeGuidanceMode.DriftZoneAngleBonus, ZoneProfile: profile, BackgroundEnabled: true, BackgroundOpacity: opacity);
                var count = DriftGaugeVisuals.Build(commands, presentation, reading);
                var backing = commands.Take(count).Where(command => command.TextureId == 12).ToArray();
                if (backing.Length != (opacity > 0 ? 1 : 0)) failures.Add(phase + "/backing-visibility");
                if (backing.Length == 1 && (backing[0].TintR != 0 || backing[0].TintG != 0 || backing[0].TintB != 0 || backing[0].TintA != (float)opacity))
                    failures.Add(phase + "/backing-color-or-opacity");
                var angle = commands.Take(count).Where(command => command.TextureId == 3 && Math.Abs(command.OriginY / scale - 50) < .01).ToArray();
                var percent = commands.Take(count).Where(command => command.TextureId == 3 && Math.Abs(command.OriginY / scale - 58) < .01).ToArray();
                if (angle.Length == 0 || angle.Any(command => Math.Abs(command.AxisYY / scale - 56) > .01)) failures.Add(phase + "/small-angle-readout");
                if (percent.Length != 3 || percent.Any(command => Math.Abs(command.AxisYY / scale - 39.2) > .01)) failures.Add(phase + "/small-percent-readout");
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    DriftGaugeVisuals.Draw(drawing, size, reading, 40, 10, guidanceMode: DriftGaugeGuidanceMode.DriftZoneAngleBonus,
                        zoneProfile: profile, backgroundEnabled: true, backgroundOpacity: opacity);
                VisualTreeHelper.SetRootDpi(visual, new DpiScale(scale, scale));
                var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                var offset = ((int)Math.Round(60 * scale) * width + (int)Math.Round(450 * scale)) * 4;
                if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0 || Math.Abs(pixels[offset + 3] - opacity * 255) > 1)
                    failures.Add(phase + "/actual-black-alpha");
                foreach (var point in new[] { new Point(0, 0), new Point(width / 2, 0), new Point(width - 1, height / 2) })
                    if (pixels[((int)point.Y * width + (int)point.X) * 4 + 3] != 0) failures.Add(phase + "/enclosing-edge");
                var filename = "drift-" + phase + ".png";
                Save(bitmap, filename);
                foreach (var sky in new[] { false, true })
                {
                    var composite = new DrawingVisual();
                    using (var drawing = composite.RenderOpen())
                    {
                        drawing.DrawRectangle(sky ? Brushes.White : new SolidColorBrush(Color.FromRgb(24, 29, 35)), null, new Rect(size));
                        drawing.DrawImage(bitmap, new Rect(size));
                    }
                    VisualTreeHelper.SetRootDpi(composite, new DpiScale(scale, scale));
                    var preview = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                    preview.Render(composite);
                    Save(preview, (sky ? "sky-preview-" : "preview-") + filename);
                }
                captures.Add(new { phase, filename, opacity, dpi, commandCount = count });

                void Save(BitmapSource image, string file)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(output, file)); encoder.Save(stream);
                }
            }
    }

    private static void ReviewZoneArtwork(string output, IReadOnlyList<AnalogHudTexture> assets,
        List<object> captures, List<string> failures)
    {
        var profile = new DriftZoneScoringProfile(10, 110, (double)59.4f, 30, 40);
        var samples = new (string Name, double? Signed, double? Magnitude, DriftGuidanceState State, bool Verified, int? Percent)[]
        {
            ("below-minimum", -5, 5, DriftGuidanceState.BelowScoringAngle, true, null),
            ("scoring-onset", 10, 10, DriftGuidanceState.AngleBonusIncreasing, true, 75),
            ("scoring-20", 20, 20, DriftGuidanceState.AngleBonusIncreasing, true, 80),
            ("scoring-30", 30, 30, DriftGuidanceState.AngleBonusIncreasing, true, 85),
            ("left-scoring-40", -40, 40, DriftGuidanceState.AngleBonusIncreasing, true, 90),
            ("before-saturation", 59.3, 59.3, DriftGuidanceState.AngleBonusIncreasing, true, 99),
            ("saturation", profile.SaturationAngleDegrees, profile.SaturationAngleDegrees, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("ceiling-tick", 60, 60, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("no-extra-bonus", 70, 70, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("left-overflow-100", -100, 100, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("limit-110", 110, 110, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("above-limit", -135, 135, DriftGuidanceState.AboveScoringAngle, true, null),
            ("unverified-build", 70, 70, DriftGuidanceState.ProfileUnavailable, false, null),
            ("unsigned-vertical", null, 62, DriftGuidanceState.MaximumAngleBonus, true, 100),
            ("stale-or-missing", null, null, DriftGuidanceState.Unavailable, true, null)
        };
        var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
        var size = new Size(DriftGaugeVisuals.DesignWidth, DriftGaugeVisuals.DesignHeight);
        foreach (var variant in new[] { (Dark: false, Dpi: 96), (Dark: true, Dpi: 96), (Dark: false, Dpi: 144), (Dark: true, Dpi: 144) })
            foreach (var sample in samples)
            {
                var phase = "zone-" + sample.Name + (variant.Dark ? "-dark" : "") + "-" + variant.Dpi;
                var scale = variant.Dpi / 96d;
                var reading = new DriftAngleReading(sample.Signed, sample.State)
                { IsZoneGuidance = true, MagnitudeDegrees = sample.Magnitude };
                var presentation = new DriftGaugePresentation((int)(size.Width * scale), (int)(size.Height * scale),
                    (float)scale, (float)scale, 1, true, 40, 10, variant.Dark,
                    DriftGaugeGuidanceMode.DriftZoneAngleBonus, sample.Verified ? profile : null);
                var count = DriftGaugeVisuals.Build(commands, presentation, reading);
                var scene = commands.Take(count).ToArray();
                Check(count is > 3 and < DriftGaugeVisuals.MaximumCommands, "command-count");
                Check(scene.Count(command => command.TextureId == (sample.Verified ? 9u : 7u)) == 1, "cached-zone-scale-missing");
                Check(scene.All(command => command.TextureId != (sample.Verified ? 7u : 9u)), "wrong-profile-scale");
                Check(!scene.Any(command => command.TextureId is 2 or 4), "custom-scale-in-zone-mode");
                Check(variant.Dark ? scene[0].TextureId == 8 && scene.Any(command => command.TextureId == 5)
                    : scene.All(command => command.TextureId is 1 or 3 or 7 or 9 or 10), "localized-halo-mode");
                foreach (var command in scene)
                {
                    Check(assets.Any(texture => texture.Id == command.TextureId), "unknown-texture");
                    Check(float.IsFinite(command.OriginX) && float.IsFinite(command.OriginY) &&
                          command.OriginX >= -.1 && command.OriginY >= -.1 &&
                          command.OriginX + command.AxisXX <= presentation.Width + .1 &&
                          command.OriginY + command.AxisYY <= presentation.Height + .1, "command-clipped");
                }
                if (variant.Dark)
                {
                    var light = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
                    var lightCount = DriftGaugeVisuals.Build(light, presentation with { DarkMode = false }, reading);
                    Check(scene.Where(command => command.TextureId is 1 or 3 or 7 or 9 or 10).SequenceEqual(light.Take(lightCount)),
                        "dark-mode-changes-foreground-artwork");
                }
                Check(scene.Where(command => command.TextureId == 1).All(command =>
                    Math.Abs(command.OriginY / scale - 7) < .01 && Math.Abs(command.AxisXX / scale - 3) < .01 && Math.Abs(command.AxisYY / scale - 34) < .01 ||
                    Math.Abs(command.OriginY / scale - 5) < .01 && Math.Abs(command.AxisXX / scale - 8) < .01 && Math.Abs(command.AxisYY / scale - 3) < .01),
                    "filled-zone-band-or-container-remains");
                var markers = scene.Where(command => command.TextureId == 1 &&
                    Math.Abs(command.AxisXX / scale - 3) < .01 && Math.Abs(command.AxisYY / scale - 34) < .01).ToArray();
                Check(markers.Length == (sample.Signed.HasValue ? 1 : 0), "invented-or-missing-direction-marker");
                if (markers.Length == 1)
                {
                    var marker = markers[0];
                    Check(Math.Abs((marker.OriginX + marker.AxisXX / 2) / scale - X(Math.Clamp(sample.Signed!.Value, -90, 90))) < .01,
                        "marker-position");
                    var expected = ExpectedColor(sample.Magnitude, sample.Verified);
                    Check(Math.Abs(marker.TintR - expected.R / 255f) < .001 && Math.Abs(marker.TintG - expected.G / 255f) < .001 &&
                        Math.Abs(marker.TintB - expected.B / 255f) < .001, "guidance-color");
                }
                else Check(scene.All(command => command.TextureId != 6), "unsigned-angle-invents-marker-halo");
                var number = sample.Magnitude is { } magnitude
                    ? Math.Round(magnitude, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture) + "°" : "−−°";
                Check(TextAt(scene, 50, scale) == number, "numeric-angle-does-not-match-magnitude");
                Check(TextAt(scene, 58, scale) == (sample.Percent is { } percent ? percent.ToString(CultureInfo.InvariantCulture) + "%" : ""), "incorrect-angle-factor-percentage");
                Check(scene.Where(command => command.TextureId == 3 && Math.Abs(command.OriginY / scale - 50) < .01)
                    .All(command => Math.Abs(command.AxisYY / scale - 56) < .01), "small-angle-readout");
                Check(scene.Where(command => command.TextureId == 3 && Math.Abs(command.OriginY / scale - 58) < .01)
                    .All(command => Math.Abs(command.AxisYY / scale - 39.2) < .01), "small-angle-percentage");
                Check(scene.Count(command => command.TextureId == 10) == (sample.Percent.HasValue ? 1 : 0), "angle-factor-caption-missing-or-unavailable");
                Check(scene.Count(command => command.TextureId == 11) == (sample.Percent.HasValue && variant.Dark ? 1 : 0), "angle-factor-caption-halo-state");
                if (sample.State == DriftGuidanceState.AngleBonusIncreasing)
                    Check(TextAt(scene, 87, scale) == "SCORINGANGLE", "ordinary-drift-angle-shown-as-not-scoring");
                if (sample.State == DriftGuidanceState.MaximumAngleBonus)
                    Check(TextAt(scene, 87, scale) == (sample.Magnitude > 60 ? "NOEXTRABONUS" : "FULLANGLEBONUS"), "ceiling-guidance-confuses-score-factor");

                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    DriftGaugeVisuals.Draw(drawing, size, reading, 40, 10, variant.Dark,
                        DriftGaugeGuidanceMode.DriftZoneAngleBonus, presentation.ZoneProfile);
                VisualTreeHelper.SetRootDpi(visual, new DpiScale(scale, scale));
                var bitmap = new RenderTargetBitmap(presentation.Width, presentation.Height, variant.Dpi, variant.Dpi, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[presentation.Width * presentation.Height * 4];
                bitmap.CopyPixels(pixels, presentation.Width * 4, 0);
                var nontransparent = 0;
                var touchedEdge = false;
                for (var y = 0; y < presentation.Height; y++)
                    for (var x = 0; x < presentation.Width; x++)
                    {
                        if (pixels[(y * presentation.Width + x) * 4 + 3] == 0) continue;
                        nontransparent++;
                        if (x == 0 || y == 0 || x == presentation.Width - 1 || y == presentation.Height - 1) touchedEdge = true;
                    }
                Check(nontransparent > 1500 * scale * scale, "empty-gauge-artwork");
                Check(!touchedEdge, "artwork-touches-image-edge");
                foreach (var clear in new[] { new Point(5, 60), new Point(615, 60), new Point(310, 2), new Point(450, 105), new Point(X(-82.5), 33), new Point(X(62.5), 33) })
                {
                    var index = ((int)Math.Round(clear.Y * scale) * presentation.Width + (int)Math.Round(clear.X * scale)) * 4;
                    Check(pixels[index + 3] == 0, "enclosing-container-remains");
                }
                foreach (var degrees in variant.Dark ? Array.Empty<double>() : new[] { -60d, -30, 0, 30, 60 })
                {
                    // The direction marker can cover one tick; inspect the remaining static artwork.
                    if (sample.Signed is { } signed && Math.Abs(Math.Clamp(signed, -90, 90) - degrees) < 3) continue;
                    var index = ((int)Math.Round(20 * scale) * presentation.Width + (int)Math.Round(X(degrees) * scale)) * 4;
                    var alpha = pixels[index + 3];
                    var expected = ExpectedColor(Math.Abs(degrees), sample.Verified);
                    Check(alpha > 20 && Math.Abs(pixels[index] * 255d / alpha - expected.B) <= 3 &&
                          Math.Abs(pixels[index + 1] * 255d / alpha - expected.G) <= 3 &&
                          Math.Abs(pixels[index + 2] * 255d / alpha - expected.R) <= 3, "cached-tick-color-lost-in-preview");
                }
                var captionInk = 0;
                for (var y = (int)Math.Ceiling(91 * scale); y < (int)Math.Floor(106 * scale); y++)
                    for (var x = (int)Math.Ceiling(480 * scale); x < (int)Math.Floor(596 * scale); x++)
                        if (pixels[(y * presentation.Width + x) * 4 + 3] > 20) captionInk++;
                Check(sample.Percent.HasValue ? captionInk > 80 * scale * scale : captionInk == 0, "cached-caption-not-visible-or-shown-without-data");
                var filename = "drift-" + phase + ".png";
                Save(bitmap, filename);
                foreach (var sky in new[] { false, true })
                {
                    var background = new DrawingVisual();
                    using (var drawing = background.RenderOpen())
                    {
                        drawing.DrawRectangle(sky ? Brushes.White : new SolidColorBrush(Color.FromRgb(24, 29, 35)), null, new Rect(size));
                        drawing.DrawImage(bitmap, new Rect(size));
                    }
                    VisualTreeHelper.SetRootDpi(background, new DpiScale(scale, scale));
                    var preview = new RenderTargetBitmap(presentation.Width, presentation.Height, variant.Dpi, variant.Dpi, PixelFormats.Pbgra32);
                    preview.Render(background);
                    Save(preview, (sky ? "sky-preview-" : "preview-") + filename);
                }
                captures.Add(new
                {
                    phase,
                    filename,
                    darkMode = variant.Dark,
                    presentation.Width,
                    presentation.Height,
                    commandCount = count,
                    nontransparent,
                    touchedEdge,
                    sample.Verified,
                    sample.Magnitude
                });

                void Check(bool condition, string code)
                {
                    if (!condition) failures.Add(phase + "/" + code);
                }
                void Save(BitmapSource image, string file)
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(output, file)); encoder.Save(stream);
                }
            }

        static double X(double degrees) => 24 + (degrees + 90) / 180 * 572;
        static string TextAt(DirectCompositionDrawCommand[] scene, double y, double scale)
        {
            const string glyphs = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-−°.%";
            return new string(scene.Where(command => command.TextureId == 3 && Math.Abs(command.OriginY / scale - y) < .01)
                .Select(command => glyphs[(int)Math.Round(command.UvTop * 3) * 16 + (int)Math.Round(command.UvLeft * 16)]).ToArray());
        }
        Color ExpectedColor(double? magnitude, bool verified)
        {
            if (!verified || magnitude is not { } angle) return Colors.White;
            if (angle < 10 || angle > 110) return Color.FromRgb(255, 90, 95);
            if (angle > 60) return Color.FromRgb(255, 205, 76);
            var fraction = Math.Clamp((angle - 10) / (profile.SaturationAngleDegrees - 10), 0, 1);
            return Color.FromRgb((byte)Math.Round(255 - 131 * fraction), (byte)Math.Round(205 + 37 * fraction), (byte)Math.Round(76 + 125 * fraction));
        }
    }
}
