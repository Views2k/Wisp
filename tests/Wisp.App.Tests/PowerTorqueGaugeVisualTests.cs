using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

internal static class PowerTorqueGaugeVisualTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var gauge = new PowerTorqueGaugeView { Width = 140, Height = 140, Maximum = 1_000 };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        foreach (var (value, angle) in new[] { (-100d, 135d), (0d, 135d), (500d, 270d), (1_000d, 405d), (1_500d, 405d) })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, value, 1_500, 1_500);
            Assert.True(gauge.HasVisibleNeedle);
            Assert.Equal(value, gauge.DisplayedValue);
            Assert.Equal(angle, gauge.CurrentNeedleAngle, 8);
        }
        gauge.Display = PowerTorqueDisplay.Unavailable;
        Assert.False(gauge.HasVisibleNeedle);
        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        gauge.IsTorque = true;
        gauge.Maximum = 1_200;
        Assert.Equal(507, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 507d / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.TorqueUnit = TorqueUnit.PoundFeet;
        Assert.Equal(373.94400968357366, gauge.DisplayedValue, 8);
        Assert.Equal(135 + 270 * gauge.DisplayedValue / 1_200, gauge.CurrentNeedleAngle, 8);
        gauge.IsTorque = false;
        gauge.Maximum = 1_000;
        Assert.Equal(427, gauge.DisplayedValue);
        Assert.Equal(135 + 270 * 427d / 1_000, gauge.CurrentNeedleAngle, 8);

        foreach (var value in new[] { -999d, 0d, 427d, 1_500d, 12_000d })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, 0, 0, 0);
            var drawing = RenderMethod(gauge, "DrawValue");
            var leaves = Flatten(drawing).ToArray();
            var digitCount = Math.Abs(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture).Length;
            Assert.Equal(digitCount, leaves.OfType<ImageDrawing>().Count());
            Assert.Equal(value < 0 ? 1 : 0, leaves.OfType<GeometryDrawing>().Count(item => item.Geometry is LineGeometry));
            Assert.True(drawing.Bounds.Width <= 48.01);
        }

        gauge.Display = new PowerTorqueDisplay(true, 427, 507, 612, 690);
        RenderMethod(gauge, "OnRender");
        var field = typeof(PowerTorqueGaugeView).GetField("_dial", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalDial = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(originalDial.IsFrozen);
        gauge.Display = gauge.Display with { PowerBhp = 500, PeakPowerBhp = 650 };
        RenderMethod(gauge, "OnRender");
        Assert.Same(originalDial, field.GetValue(gauge));
        gauge.Maximum = 2_000;
        RenderMethod(gauge, "OnRender");
        Assert.NotSame(originalDial, field.GetValue(gauge));
        Assert.Equal(135 + 270 * 500d / 2_000, gauge.CurrentNeedleAngle, 8);

        foreach (var maximum in new[] { 0, -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => gauge.Maximum = maximum);
        gauge.Display = new PowerTorqueDisplay(true, double.NaN, 0, 0, 0);
        Assert.False(gauge.HasVisibleNeedle);
        Assert.Equal(135, gauge.CurrentNeedleAngle);

        ReadoutAndNeedleUseTheirSeparateSamples();
        ReadoutDrawingIsCachedBetweenNumericPublications();
        DriftFlashChangesOnlyNumberColorInNativeAndWpfRendering();
        NativeFlashFrequencyChangesPreservePhaseAndArtwork();
        CustomPaletteHonorsAllStopsAndTheirOpacity();
        ColoredArcCacheIsFrozenReusedAndReplacedWhenItsPaletteChanges();
        ActiveArcClampsNegativeAndOverrangeOutput();
        ChangingNumberColorsDoesNotGrowTheGlobalTintedTextureCache();
        SupplementaryRimsHaveMatchingRenderedDiameterAndCenter();
        SharedPreviewUsesActualGaugeBindingsAndIndependentSizes();
        DetachedPairPreviewShowsEveryEnabledGauge();
    }

    private static void DetachedPairPreviewShowsEveryEnabledGauge()
    {
        var model = new DiagnosticsViewModel(new AppSettings { PowerGaugeAttached = false, TorqueGaugeAttached = false });
        var preview = new PowerTorqueGaugePairPreview { DataContext = model };
        var panel = Assert.IsType<System.Windows.Controls.StackPanel>(preview.Content);
        var power = Assert.Single(panel.Children.OfType<PowerTorqueGaugeView>(), gauge => !gauge.IsTorque);
        var torque = Assert.Single(panel.Children.OfType<PowerTorqueGaugeView>(), gauge => gauge.IsTorque);
        foreach (var mask in new[] { 3, 2, 1, 0 })
        {
            model.PowerGaugeEnabled = (mask & 1) != 0;
            model.TorqueGaugeEnabled = (mask & 2) != 0;
            preview.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal(mask == 0 ? Visibility.Collapsed : Visibility.Visible, preview.Visibility);
            Assert.Equal((mask & 1) == 0 ? Visibility.Collapsed : Visibility.Visible, power.Visibility);
            Assert.Equal((mask & 2) == 0 ? Visibility.Collapsed : Visibility.Visible, torque.Visibility);
            Assert.Equal((mask & 1) == 0 ? 0 : 2, torque.Margin.Top);
            Assert.False(model.PowerGaugeAttached);
            Assert.False(model.TorqueGaugeAttached);
        }
        preview.DataContext = null;
    }

    private static void SharedPreviewUsesActualGaugeBindingsAndIndependentSizes()
    {
        var settings = new AppSettings
        {
            BoostGaugeEnabled = true,
            BoostGaugeAttached = false,
            TireTemperatureGaugeEnabled = true,
            TireTemperatureGaugeAttached = false,
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true,
            PowerGaugeAttached = false,
            TorqueGaugeAttached = false,
            BoostGaugeScale = .5,
            TireTemperatureGaugeScale = 1.25,
            PowerGaugeScale = 2,
            TorqueGaugeScale = 1
        };
        var preview = new SupplementaryAnalogGaugePreview { DataContext = new DiagnosticsViewModel(settings) };
        var boost = Assert.Single(preview.Children.OfType<AnalogBoostGaugeView>());
        var tire = Assert.Single(preview.Children.OfType<AnalogTireTemperatureGaugeView>());
        var power = Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => !gauge.IsTorque);
        var torque = Assert.Single(preview.Children.OfType<PowerTorqueGaugeView>(), gauge => gauge.IsTorque);
        Assert.All(new FrameworkElement[] { boost, tire, power, torque },
            gauge => Assert.Equal(Visibility.Visible, gauge.Visibility));
        Assert.False(settings.BoostGaugeAttached);
        Assert.False(settings.TireTemperatureGaugeAttached);
        Assert.False(settings.PowerGaugeAttached);
        Assert.False(settings.TorqueGaugeAttached);
        Assert.Equal("PreviewBoostDisplay", BindingOperations.GetBinding(boost, BoostVisualBase.DisplayProperty)!.Path.Path);
        Assert.Equal("SelectedBoostPressureUnit", BindingOperations.GetBinding(boost, BoostVisualBase.PressureUnitProperty)!.Path.Path);
        Assert.Equal("PreviewTireTemperatureDisplay", BindingOperations.GetBinding(tire, TireTemperatureVisualBase.DisplayProperty)!.Path.Path);
        Assert.Equal("SelectedTireTemperatureUnit", BindingOperations.GetBinding(tire, TireTemperatureVisualBase.TemperatureUnitProperty)!.Path.Path);
        Assert.Equal("PreviewPowerTorqueDisplay", BindingOperations.GetBinding(power, PowerTorqueGaugeView.DisplayProperty)!.Path.Path);
        Assert.Equal("PreviewPowerTorqueDisplay", BindingOperations.GetBinding(torque, PowerTorqueGaugeView.DisplayProperty)!.Path.Path);
        Assert.Equal(.5, Assert.IsType<ScaleTransform>(boost.LayoutTransform).ScaleX);
        Assert.Equal(1.25, Assert.IsType<ScaleTransform>(tire.LayoutTransform).ScaleX);
        Assert.Equal(2, Assert.IsType<ScaleTransform>(power.LayoutTransform).ScaleX);
        Assert.Equal(1, Assert.IsType<ScaleTransform>(torque.LayoutTransform).ScaleX);
        Assert.Equal(boost.Margin.Top + 34, power.Margin.Top + 136);
        Assert.Equal(tire.Margin.Top + 85, torque.Margin.Top + 68);
        preview.DataContext = null;
    }

    private static void SupplementaryRimsHaveMatchingRenderedDiameterAndCenter()
    {
        var boost = new AnalogBoostGaugeView { Display = new BoostDisplay(true, 0, 0, 0, 70) };
        var tire = new AnalogTireTemperatureGaugeView
        {
            Display = new TireTemperatureDisplay(true, 176, 177, .42, .423)
        };
        FrameworkElement[] gauges = [boost, tire, new PowerTorqueGaugeView(), new PowerTorqueGaugeView { IsTorque = true }];
        foreach (var gauge in gauges)
        {
            gauge.Width = gauge.Height = 136;
            gauge.Measure(new Size(136, 136));
            gauge.Arrange(new Rect(0, 0, 136, 136));
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
                gauge.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gauge, [dc]);
            var rim = Assert.Single(RimBounds(drawing, Matrix.Identity));
            // These arcs include both horizontal extrema and their top. Compare
            // actual authored drawing geometry, not just equal control widths.
            // WPF approximates these arcs with Beziers; their computed bounds
            // differ from the circle by about .02 DIP. Keep a subpixel tolerance.
            Assert.InRange(rim.Width, 116.91, 117.01);
            Assert.InRange(rim.Left + rim.Width / 2, 67.95, 68.05);
            Assert.InRange(rim.Top + rim.Width / 2, 67.95, 68.05);
        }
    }

    private static IEnumerable<Rect> RimBounds(Drawing drawing, Matrix transform)
    {
        if (drawing is DrawingGroup group)
        {
            var local = group.Transform?.Value ?? Matrix.Identity;
            local.Append(transform);
            foreach (var child in group.Children)
                foreach (var bounds in RimBounds(child, local)) yield return bounds;
        }
        else if (drawing is GeometryDrawing { Geometry: StreamGeometry, Pen.Brush: SolidColorBrush brush } geometry &&
                 brush.Color == Color.FromArgb(145, 142, 147, 156))
            yield return new MatrixTransform(transform).TransformBounds(geometry.Geometry.Bounds);
    }

    private static void ReadoutAndNeedleUseTheirSeparateSamples()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Maximum = 1_000,
            Display = new PowerTorqueDisplay(true, 800, 1_000, 900, 1_100)
            {
                ReadoutPowerBhp = 123,
                ReadoutTorqueNm = 200
            }
        };
        Assert.Equal(800, gauge.DisplayedValue);
        Assert.Equal(123, gauge.DisplayedReadout);
        Assert.Equal(351, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "123");
        gauge.Display = gauge.Display with { PowerBhp = 900 };
        Assert.Equal(378, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "123");

        gauge.IsTorque = true;
        gauge.TorqueUnit = TorqueUnit.PoundFeet;
        Assert.Equal(737.5621492772656, gauge.DisplayedValue, 8);
        Assert.Equal(147.51242985545312, gauge.DisplayedReadout, 8);
        Assert.Equal(135 + 270 * 737.5621492772656 / 1_000, gauge.CurrentNeedleAngle, 8);
        AssertDrawnDigits(gauge, "148");
        gauge.Display = new PowerTorqueDisplay(true, 345, 678, 900, 1_100);
        gauge.IsTorque = false;
        Assert.Equal(345, gauge.DisplayedReadout);
        AssertDrawnDigits(gauge, "345");
    }

    private static void CustomPaletteHonorsAllStopsAndTheirOpacity()
    {
        var low = Color.FromArgb(64, 20, 40, 60);
        var mid = Color.FromArgb(128, 100, 120, 140);
        var high = Color.FromArgb(192, 180, 200, 220);
        var gauge = new PowerTorqueGaugeView
        {
            LowBrush = new SolidColorBrush(low),
            MidBrush = new SolidColorBrush(mid),
            HighBrush = new SolidColorBrush(high)
        };
        Assert.Equal(low, gauge.PaletteColor(0));
        Assert.Equal(mid, gauge.PaletteColor(.56));
        Assert.Equal(high, gauge.PaletteColor(1));
        Assert.Equal(low, gauge.PaletteColor(-.5));
        Assert.Equal(high, gauge.PaletteColor(1.5));
        Assert.Equal(Color.FromArgb(96, 60, 80, 100), gauge.PaletteColor(.28));
        Assert.Equal(Color.FromArgb(160, 140, 160, 180), gauge.PaletteColor(.78));

        gauge.ColorNumber = true;
        gauge.Display = new PowerTorqueDisplay(true, 900, 0, 900, 0) { ReadoutPowerBhp = 560 };
        var drawing = RenderMethod(gauge, "DrawValue");
        var rectangles = Flatten(drawing).OfType<GeometryDrawing>().ToArray();
        Assert.Equal(3, rectangles.Length);
        Assert.All(rectangles, rectangle => Assert.Equal(mid, Assert.IsType<SolidColorBrush>(rectangle.Brush).Color));
        Assert.All(Groups(drawing).Where(group => group.OpacityMask is not null),
            group => Assert.True(Assert.IsType<ImageBrush>(group.OpacityMask).IsFrozen));
        Assert.Equal(3, Groups(drawing).Count(group => group.OpacityMask is not null));
    }

    private static void ReadoutDrawingIsCachedBetweenNumericPublications()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Width = 140,
            Height = 140,
            Display = new PowerTorqueDisplay(true, 500, 0, 600, 0) { ReadoutPowerBhp = 123 }
        };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        RenderMethod(gauge, "OnRender");
        var field = typeof(PowerTorqueGaugeView).GetField("_readoutDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(first.IsFrozen);
        gauge.Display = gauge.Display with { PowerBhp = 750 };
        RenderMethod(gauge, "OnRender");
        Assert.Same(first, field.GetValue(gauge));
        gauge.Display = gauge.Display with { ReadoutPowerBhp = 124 };
        RenderMethod(gauge, "OnRender");
        var next = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(next.IsFrozen);
        Assert.NotSame(first, next);
        gauge.ColorNumber = true;
        RenderMethod(gauge, "OnRender");
        var tinted = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(tinted.IsFrozen);
        Assert.NotSame(next, tinted);
        gauge.LowBrush = Brushes.Orange;
        RenderMethod(gauge, "OnRender");
        Assert.NotSame(tinted, field.GetValue(gauge));
    }

    private static void DriftFlashChangesOnlyNumberColorInNativeAndWpfRendering()
    {
        foreach (var torque in new[] { false, true })
            foreach (var colored in new[] { false, true })
            {
                var flash = Color.FromArgb(32, 223, 159, 79);
                var gauge = new PowerTorqueGaugeView
                {
                    Width = 140,
                    Height = 140,
                    Maximum = 2_000,
                    IsTorque = torque,
                    ColorNumber = colored,
                    DriftFlashBrush = new SolidColorBrush(flash),
                    LowBrush = new SolidColorBrush(Color.FromArgb(128, 32, 64, 96)),
                    MidBrush = new SolidColorBrush(Color.FromArgb(160, 96, 128, 160)),
                    HighBrush = new SolidColorBrush(Color.FromArgb(192, 160, 192, 224)),
                    Display = new PowerTorqueDisplay(true, 1_500, 1_600, 1_500, 1_600)
                    {
                        ReadoutPowerBhp = 1_400,
                        ReadoutTorqueNm = 1_450,
                        IsDriftPowerCut = true,
                        DriftPulseAllowed = true
                    }
                };
                gauge.Measure(new Size(140, 140));
                gauge.Arrange(new Rect(0, 0, 140, 140));
                RenderMethod(gauge, "OnRender");
                var readoutField = typeof(PowerTorqueGaugeView).GetField("_readoutDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var flashDrawingField = typeof(PowerTorqueGaugeView).GetField("_flashReadoutDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var flashBrushField = typeof(PowerTorqueGaugeView).GetField("_flashReadoutBrush", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var readout = Assert.IsType<DrawingGroup>(readoutField.GetValue(gauge));
                var digitCacheField = typeof(PowerTorqueGaugeView).GetField("_flashDigitImages", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var activeDigitsField = typeof(PowerTorqueGaugeView).GetField("_activeFlashDigitImages", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var digitCache = Assert.IsType<Dictionary<char, NativeTintedBitmap>>(digitCacheField.GetValue(gauge));
                var activeDigits = Assert.IsType<HashSet<NativeTintedBitmap>>(activeDigitsField.GetValue(gauge));
                Assert.Empty(digitCache);
                Assert.Empty(activeDigits);
                var tintField = typeof(NativeAssetCache).GetField("TintedImages", BindingFlags.Static | BindingFlags.NonPublic)!;
                var tintedImages = Assert.IsAssignableFrom<System.Collections.IDictionary>(tintField.GetValue(null));
                var tintCount = tintedImages.Count;
                var angle = gauge.CurrentNeedleAngle;
                var nativeDisplay = gauge.Display;
                var snapshot = PowerTorqueHudLayer.Capture(gauge, null);
                var start = Stopwatch.Frequency;
                var now = start;
                var playback = PowerTorquePlayback(() => now);
                playback.Update(snapshot, start);
                var baselineCommands = playback.Build(start);
                var firstDigit = (torque ? 25_000u : 20_000u) + (colored ? 30u : 10u);
                bool IsNumber(DirectCompositionDrawCommand command) => command.TextureId >= firstDigit && command.TextureId < firstDigit + 10;
                var baselineNumbers = baselineCommands.Where(IsNumber).ToArray();
                Assert.Equal(4, baselineNumbers.Length);
                var baseline = colored ? gauge.PaletteColor(gauge.DisplayedReadout / gauge.Maximum) : Colors.White;
                DrawingGroup? cachedFlashDrawing = null;
                SolidColorBrush? cachedFlashBrush = null;
                ImageSource[]? cachedDigitImages = null;
                Dictionary<char, NativeTintedBitmap>? cachedDigits = null;
                foreach (var (milliseconds, pulse) in new[] { (0, 0d), (200, .5), (400, 1d), (800, 0d) })
                {
                    now = start + Stopwatch.Frequency * milliseconds / 1_000;
                    var commands = playback.Build(now);
                    var numbers = commands.Where(IsNumber).ToArray();
                    Assert.Equal(baselineNumbers.Select(command => command.TextureId), numbers.Select(command => command.TextureId));
                    Assert.Equal(baselineCommands.Where(command => !IsNumber(command)), commands.Where(command => !IsNumber(command)));
                    foreach (var command in numbers)
                    {
                        Assert.Equal(baseline.A / 255f, command.TintA);
                        Assert.InRange(command.TintR * 255, baseline.R + (flash.R - baseline.R) * pulse - .501, baseline.R + (flash.R - baseline.R) * pulse + .501);
                        Assert.InRange(command.TintG * 255, baseline.G + (flash.G - baseline.G) * pulse - .501, baseline.G + (flash.G - baseline.G) * pulse + .501);
                        Assert.InRange(command.TintB * 255, baseline.B + (flash.B - baseline.B) * pulse - .501, baseline.B + (flash.B - baseline.B) * pulse + .501);
                    }
                    gauge.Display = gauge.Display with { DriftCutPulse = pulse };
                    var drawing = RenderMethod(gauge, "OnRender");
                    Assert.Same(readout, readoutField.GetValue(gauge));
                    if (pulse > 0)
                    {
                        var flashDrawing = Assert.IsType<DrawingGroup>(flashDrawingField.GetValue(gauge));
                        var flashBrush = Assert.IsType<SolidColorBrush>(flashBrushField.GetValue(gauge));
                        if (cachedFlashDrawing is not null) Assert.Same(cachedFlashDrawing, flashDrawing);
                        if (cachedFlashBrush is not null) Assert.Same(cachedFlashBrush, flashBrush);
                        cachedFlashDrawing = flashDrawing;
                        cachedFlashBrush = flashBrush;
                        Assert.Equal(baseline.A, flashBrush.Color.A);
                        Assert.Equal(flashBrush.Color.R / 255f, numbers[0].TintR);
                        Assert.Equal(flashBrush.Color.G / 255f, numbers[0].TintG);
                        Assert.Equal(flashBrush.Color.B / 255f, numbers[0].TintB);
                        Assert.True(AssertNumberOpacity(drawing, flashDrawing));
                        if (!colored)
                        {
                            var images = Flatten(flashDrawing).OfType<ImageDrawing>().Select(image => image.ImageSource).ToArray();
                            Assert.Equal(4, images.Length);
                            if (cachedDigitImages is not null)
                                for (var index = 0; index < images.Length; index++) Assert.Same(cachedDigitImages[index], images[index]);
                            cachedDigitImages ??= images;
                            cachedDigits ??= new Dictionary<char, NativeTintedBitmap>(digitCache);
                            Assert.Equal(images.Distinct().Count(), digitCache.Count);
                            Assert.True(activeDigits.SetEquals(digitCache.Values));
                            Assert.Equal(activeDigits.Count, images.Distinct().Count());
                        }
                        if (pulse == 1) AssertReadoutPixelCoverage(readout, flashDrawing);
                    }
                    else Assert.True(AssertNumberOpacity(drawing, readout));
                    Assert.Same(digitCache, digitCacheField.GetValue(gauge));
                    Assert.Same(activeDigits, activeDigitsField.GetValue(gauge));
                    Assert.InRange(digitCache.Count, 0, 10);
                    if (cachedDigits is not null)
                    {
                        Assert.Equal(cachedDigits.Count, digitCache.Count);
                        foreach (var (digit, image) in cachedDigits) Assert.Same(image, digitCache[digit]);
                    }
                    if (colored) Assert.Empty(digitCache);
                    Assert.Equal(angle, gauge.CurrentNeedleAngle);
                    var next = PowerTorqueHudLayer.Capture(gauge, null);
                    Assert.Same(snapshot.Textures, next.Textures);
                    Assert.Equal(snapshot.CompatibilityKey, next.CompatibilityKey);
                    if (milliseconds == 200)
                    {
                        flash = Color.FromArgb(16, 47, 175, 231);
                        gauge.DriftFlashBrush = new SolidColorBrush(flash);
                        gauge.Display = nativeDisplay;
                        var recolored = PowerTorqueHudLayer.Capture(gauge, null);
                        Assert.Same(snapshot.Textures, recolored.Textures);
                        Assert.Equal(snapshot.CompatibilityKey, recolored.CompatibilityKey);
                        var timestamp = start + Stopwatch.Frequency / 5;
                        playback.Update(recolored, timestamp);
                        var recoloredNumbers = playback.Build(timestamp).Where(IsNumber).ToArray();
                        Assert.Equal(numbers.Length, recoloredNumbers.Length);
                        Assert.All(recoloredNumbers, command =>
                        {
                            Assert.Equal(baseline.A / 255f, command.TintA);
                            Assert.InRange(command.TintR * 255, (baseline.R + flash.R) / 2d - .501, (baseline.R + flash.R) / 2d + .501);
                            Assert.InRange(command.TintG * 255, (baseline.G + flash.G) / 2d - .501, (baseline.G + flash.G) / 2d + .501);
                            Assert.InRange(command.TintB * 255, (baseline.B + flash.B) / 2d - .501, (baseline.B + flash.B) / 2d + .501);
                        });
                    }
                }
                Assert.Equal(tintCount, tintedImages.Count);
            }
    }

    private static void NativeFlashFrequencyChangesPreservePhaseAndArtwork()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        var gauge = new PowerTorqueGaugeView
        {
            Width = 140,
            Height = 140,
            Maximum = 2_000,
            DriftFlashBrush = new SolidColorBrush(Color.FromRgb(31, 63, 95))
        };
        gauge.Measure(new Size(140, 140));
        gauge.Arrange(new Rect(0, 0, 140, 140));
        var display = new PowerTorqueDisplay(true, 500, 600, 900, 1_100)
        {
            ReadoutPowerBhp = 500,
            ReadoutTorqueNm = 600,
            IsDriftPowerCut = true,
            DriftPulseAllowed = true
        };
        var start = Stopwatch.Frequency;
        var input = new NativePowerTorqueInput(display, 1, 1_000, start, start, 0)
        { DriftFlashFrequencyHz = .5 };
        var inputProperty = typeof(DiagnosticsViewModel).GetProperty(nameof(DiagnosticsViewModel.NativePowerTorqueInput),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        inputProperty.SetValue(model, input);
        var snapshot = PowerTorqueHudLayer.Capture(gauge, model);
        var now = start;
        var playback = PowerTorquePlayback(() => now);
        playback.Update(snapshot, start);
        var changedAt = start + Stopwatch.Frequency / 2;
        now = changedAt;
        var before = playback.Build(changedAt);
        bool IsNumber(DirectCompositionDrawCommand command) => command.TextureId is >= 20_010 and <= 20_019;
        Assert.All(before.Where(IsNumber), command => Assert.Equal(143 / 255f, command.TintR));

        inputProperty.SetValue(model, input with { DriftFlashFrequencyHz = 3 });
        var faster = PowerTorqueHudLayer.Capture(gauge, model);
        Assert.NotSame(snapshot, faster);
        Assert.Same(snapshot.Textures, faster.Textures);
        Assert.Equal(snapshot.CompatibilityKey, faster.CompatibilityKey);
        playback.Update(faster, changedAt);
        Assert.Equal(before, playback.Build(changedAt));
        var period = (long)Math.Round(Stopwatch.Frequency / 3d);
        var peak = playback.Build(changedAt + (long)Math.Round(period / 4d));
        var numbers = peak.Where(IsNumber).ToArray();
        Assert.Equal(3, numbers.Length);
        Assert.All(numbers, command =>
        {
            Assert.Equal(31 / 255f, command.TintR);
            Assert.Equal(63 / 255f, command.TintG);
            Assert.Equal(95 / 255f, command.TintB);
            Assert.Equal(1, command.TintA);
        });
        Assert.Equal(before.Where(command => !IsNumber(command)), peak.Where(command => !IsNumber(command)));
    }

    private static HudLayerPlayback PowerTorquePlayback(Func<long> clock) =>
        (HudLayerPlayback)Activator.CreateInstance(
            typeof(PowerTorqueHudLayer).GetNestedType("Playback", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { clock }, culture: null)!;

    private static bool AssertNumberOpacity(Drawing drawing, DrawingGroup numbers)
    {
        if (ReferenceEquals(drawing, numbers))
        {
            Assert.All(Groups(numbers), group => Assert.Equal(1, group.Opacity));
            return true;
        }
        if (drawing is not DrawingGroup parent) return false;
        foreach (var child in parent.Children)
        {
            if (!AssertNumberOpacity(child, numbers)) continue;
            Assert.Equal(1, parent.Opacity);
            return true;
        }
        return false;
    }

    private static void AssertReadoutPixelCoverage(Drawing baseline, Drawing flashed)
    {
        static byte[] Pixels(Drawing drawing)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawDrawing(drawing);
            var bitmap = new RenderTargetBitmap(140, 140, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[140 * 140 * 4];
            bitmap.CopyPixels(pixels, 140 * 4, 0);
            return pixels;
        }
        var normal = Pixels(baseline);
        var pulse = Pixels(flashed);
        var normalBounds = Rect.Empty;
        var pulseBounds = Rect.Empty;
        var changedRgb = 0;
        for (var offset = 0; offset < normal.Length; offset += 4)
        {
            Assert.InRange(Math.Abs(normal[offset + 3] - pulse[offset + 3]), 0, 1);
            var pixel = offset / 4;
            var point = new Point(pixel % 140, pixel / 140);
            if (normal[offset + 3] > 4) normalBounds.Union(point);
            if (pulse[offset + 3] > 4) pulseBounds.Union(point);
            if (normal[offset + 3] > 32 &&
                (Math.Abs(normal[offset] - pulse[offset]) > 4 ||
                 Math.Abs(normal[offset + 1] - pulse[offset + 1]) > 4 ||
                 Math.Abs(normal[offset + 2] - pulse[offset + 2]) > 4)) changedRgb++;
        }
        Assert.False(normalBounds.IsEmpty);
        Assert.Equal(normalBounds, pulseBounds);
        Assert.True(changedRgb > 50, "The number flash must visibly change RGB without reducing glyph coverage.");
    }

    private static void ColoredArcCacheIsFrozenReusedAndReplacedWhenItsPaletteChanges()
    {
        var gauge = new PowerTorqueGaugeView
        {
            Display = new PowerTorqueDisplay(true, 500, 0, 600, 0)
        };
        RenderMethod(gauge, "DrawActiveArc");
        var field = typeof(PowerTorqueGaugeView).GetField("_coloredArc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(first.IsFrozen);
        Assert.Equal(180, Flatten(first).Count());
        gauge.Display = gauge.Display with { PowerBhp = 800 };
        RenderMethod(gauge, "DrawActiveArc");
        Assert.Same(first, field.GetValue(gauge));
        gauge.Maximum = 2_000;
        RenderMethod(gauge, "DrawActiveArc");
        Assert.Same(first, field.GetValue(gauge));

        var low = new SolidColorBrush(Color.FromArgb(128, 0, 255, 0));
        gauge.LowBrush = low;
        RenderMethod(gauge, "DrawActiveArc");
        var second = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(second.IsFrozen);
        Assert.NotSame(first, second);
        low.Color = Color.FromArgb(96, 255, 0, 0);
        RenderMethod(gauge, "DrawActiveArc");
        var third = Assert.IsType<DrawingGroup>(field.GetValue(gauge));
        Assert.True(third.IsFrozen);
        Assert.NotSame(second, third);
        Assert.Equal(180, Flatten(third).Count());
        var segments = Flatten(third).OfType<GeometryDrawing>().ToArray();
        var core = Assert.IsType<SolidColorBrush>(Assert.IsType<Pen>(segments[1].Pen).Brush).Color;
        var soft = Assert.IsType<SolidColorBrush>(Assert.IsType<Pen>(segments[0].Pen).Brush).Color;
        Assert.Equal(gauge.PaletteColor(1d / 90), core);
        Assert.Equal((byte)Math.Round(core.A * 58d / 255), soft.A);
    }

    private static void ActiveArcClampsNegativeAndOverrangeOutput()
    {
        var gauge = new PowerTorqueGaugeView { Maximum = 1_000 };
        foreach (var value in new[] { -500d, 0 })
        {
            gauge.Display = new PowerTorqueDisplay(true, value, 0, 0, 0);
            Assert.Empty(RenderMethod(gauge, "DrawActiveArc").Children);
        }
        gauge.Display = new PowerTorqueDisplay(false, 500, 0, 0, 0);
        Assert.Empty(RenderMethod(gauge, "DrawActiveArc").Children);
        gauge.Display = new PowerTorqueDisplay(true, 500, 0, 0, 0);
        var half = RenderMethod(gauge, "DrawActiveArc");
        var clipped = Assert.Single(Groups(half), group => group.ClipGeometry is not null);
        var clip = Assert.IsAssignableFrom<Geometry>(clipped.ClipGeometry);
        Assert.True(clip.FillContains(PointOnArc(.25)));
        Assert.False(clip.FillContains(PointOnArc(.75)));

        gauge.Display = gauge.Display with { PowerBhp = 1_000 };
        var full = RenderMethod(gauge, "DrawActiveArc");
        Assert.DoesNotContain(Groups(full), group => group.ClipGeometry is not null);
        gauge.Display = gauge.Display with { PowerBhp = 1_500 };
        var over = RenderMethod(gauge, "DrawActiveArc");
        Assert.DoesNotContain(Groups(over), group => group.ClipGeometry is not null);
        Assert.Equal(full.Bounds, over.Bounds);
        Assert.Equal(1_500, gauge.DisplayedReadout);
        Assert.Equal(405, gauge.CurrentNeedleAngle, 8);
    }

    private static void ChangingNumberColorsDoesNotGrowTheGlobalTintedTextureCache()
    {
        var field = typeof(NativeAssetCache).GetField("TintedImages", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(null));
        var count = cache.Count;
        var gauge = new PowerTorqueGaugeView { ColorNumber = true };
        for (var index = 1; index <= 64; index++)
        {
            gauge.Display = new PowerTorqueDisplay(true, index * 15, 0, 1_000, 0);
            RenderMethod(gauge, "DrawValue");
        }
        Assert.Equal(count, cache.Count);
    }

    private static void AssertDrawnDigits(PowerTorqueGaugeView gauge, string digits)
    {
        var images = Flatten(RenderMethod(gauge, "DrawValue")).OfType<ImageDrawing>().ToArray();
        Assert.Equal(digits.Length, images.Length);
        for (var index = 0; index < digits.Length; index++)
            Assert.Same(NativeAssetCache.Get(NativeGaugeMode.Analogue,
                $"HUD_Dial_Speed_Analogue_{digits[index]}.png"), images[index].ImageSource);
    }

    private static Point PointOnArc(double fraction)
    {
        var radians = (135 + 270 * fraction) * Math.PI / 180;
        return new Point(70 + 60.2 * Math.Cos(radians), 70 + 60.2 * Math.Sin(radians));
    }

    private static IEnumerable<DrawingGroup> Groups(Drawing drawing) => drawing is DrawingGroup group
        ? new[] { group }.Concat(group.Children.SelectMany(Groups)) : [];

    private static DrawingGroup RenderMethod(PowerTorqueGaugeView gauge, string name)
    {
        var drawing = new DrawingGroup();
        using var dc = drawing.Open();
        typeof(PowerTorqueGaugeView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gauge, [dc]);
        return drawing;
    }

    private static IEnumerable<Drawing> Flatten(Drawing drawing) => drawing is DrawingGroup group
        ? group.Children.SelectMany(Flatten) : [drawing];
}
