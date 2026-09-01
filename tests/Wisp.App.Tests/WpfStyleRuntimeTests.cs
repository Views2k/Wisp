using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class WpfStyleRuntimeTests
{
    private readonly ITestOutputHelper _output;

    public WpfStyleRuntimeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ScrollViewerCornerBrushResolvesToTheWindowBackground()
    {
        Color? resolvedColor = null;
        var nativeDigitalPixels = 0;
        var nativeAnalogPixels = 0;
        var nativeElectricDigitalPixels = 0;
        var nativeElectricAnalogPixels = 0;
        var nativeAnalogRightEdgePixels = 0;
        var nativeAnalogNeedleChangedPixels = 0;
        Visibility nativeGForceVisibility = Visibility.Collapsed;
        Visibility digitalAssistVisibility = Visibility.Collapsed;
        Visibility unavailableDigitalAssists = Visibility.Visible;
        Visibility digitalStmVisibilityWhenAvailable = Visibility.Collapsed;
        Visibility analogStmVisibilityWhenAvailable = Visibility.Collapsed;
        string digitalAbsAsset = string.Empty;
        string digitalTcrAsset = string.Empty;
        string digitalTcrGlowAsset = string.Empty;
        string digitalStmAssetWhenAvailable = string.Empty;
        string analogLcAsset = string.Empty;
        string analogLcGlowAsset = string.Empty;
        string analogStmAssetWhenAvailable = string.Empty;
        string analogAutomaticDriveAsset = string.Empty;
        double analogAutomaticDriveWidth = 0;
        double analogAutomaticDriveHeight = 0;
        double analogManualGearWidthAfterSwitch = 0;
        double analogManualGearHeightAfterSwitch = 0;
        double digitalOffOpacity = 0;
        double digitalGlowOpacity = 0;
        double analogOffOpacity = 0;
        double analogGlowOpacity = 0;
        double analogAbsAngle = 0;
        double analogTcrAngle = 0;
        double analogLcAngle = 0;
        double digitalUnitRightEdge = double.PositiveInfinity;
        Color decodedGearBackplate = default;
        Color decodedDigitEdge = default;
        Color decodedRedlineGlow = default;
        Color decodedAnalogAssistSector = default;
        Color decodedAnalogActiveAssistText = default;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var application = new ResourceOnlyApplication();
                application.Resources = LoadApplicationResources();
                NativeGaugeLifecycleTests.AssertConsumersOnCurrentDispatcher();
                NativeRenderLifetimeTests.AssertConsumersOnCurrentDispatcher(_output.WriteLine);
                decodedGearBackplate = ColorAt(
                    NativeAssetCache.Get(NativeGaugeMode.Digital, "HUD_Dial_Digital_Gear_1.png"),
                    75,
                    82);
                decodedDigitEdge = ColorAt(
                    NativeAssetCache.Get(NativeGaugeMode.Digital, "HUD_Dial_Speed_Digital_0.png"),
                    100,
                    56);
                decodedRedlineGlow = ColorAt(
                    NativeAssetCache.Get(
                        NativeGaugeMode.Digital,
                        "HUD_Dial_Digital_Gear_Redline_glow_1.png"),
                    57,
                    36);
                decodedAnalogAssistSector = ColorAt(
                    NativeAssetCache.Get(
                        NativeGaugeMode.Analogue,
                        "HUD_Dial_Assist_Analogue_ABS_Off.png"),
                    42,
                    1);
                decodedAnalogActiveAssistText = ColorAt(
                    NativeAssetCache.Get(
                        NativeGaugeMode.Analogue,
                        "HUD_Dial_Assist_Analogue_ABS_On.png"),
                    47,
                    20);
                var viewer = new ScrollViewer
                {
                    Style = (Style)application.FindResource(typeof(ScrollViewer))
                };
                var brush = Assert.IsType<SolidColorBrush>(
                    viewer.FindResource(SystemColors.ControlBrushKey));
                resolvedColor = brush.Color;
                foreach (var (style, expectedForeground) in new[]
                {
                    ("PrimaryButtonStyle", Color.FromRgb(0x07, 0x12, 0x0F)),
                    ("DangerButtonStyle", Color.FromRgb(0xFF, 0xC5, 0xC9))
                })
                {
                    var button = new Button { Content = "Apply", Style = (Style)application.FindResource(style) };
                    button.Measure(new Size(120, 48));
                    button.Arrange(new Rect(0, 0, 120, 48));
                    button.UpdateLayout();
                    var border = Assert.IsType<Border>(button.Template.FindName("ButtonBorder", button));
                    var presenter = Assert.IsType<ContentPresenter>(border.Child);
                    var text = Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(presenter, 0));
                    Assert.Equal(expectedForeground, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                }
                nativeDigitalPixels = RenderedPixelCount(
                    new NativeDigitalSpeedometer(),
                    NativeDigitalSpeedometer.FrameProperty,
                    new NativeGaugeFrame(
                        true,
                        123,
                        4_500,
                        9_000,
                        TransmissionGear.Fourth,
                        SpeedUnit.MilesPerHour,
                        ExactRedlineResult.Exact(785.397766113281)),
                    320,
                    160);
                var analogControl = new NativeAnalogSpeedometer();
                var analogFrame = new NativeGaugeFrame(
                    true,
                    123,
                    4_500,
                    9_000,
                    TransmissionGear.Fourth,
                    SpeedUnit.MilesPerHour,
                    ExactRedlineResult.Exact(785.397766113281),
                    CarOrdinal: 1,
                    GameTimestampMilliseconds: 1,
                    NativeNeedleAngleDegrees: NativeGaugeGeometry.AnalogNeedleAngle(4_500, 9_000),
                    NativeNeedleBlurAmount: 0,
                    NativeGaugeObservedTimestamp: System.Diagnostics.Stopwatch.GetTimestamp());
                var electricFrame = analogFrame with
                {
                    IsElectric = true,
                    PowerWatts = 84_500,
                    TorqueNm = 310.25
                };
                nativeElectricDigitalPixels = RenderedElectricControlPixels(
                    "Wisp.App.NativeElectricDigitalSpeedometer",
                    electricFrame,
                    320,
                    160);
                nativeElectricAnalogPixels = RenderedElectricControlPixels(
                    "Wisp.App.NativeElectricAnalogSpeedometer",
                    electricFrame,
                    345,
                    345);
                BindingOperations.ClearBinding(analogControl, NativeAnalogSpeedometer.FrameProperty);
                analogControl.Frame = analogFrame;
                var automaticAnalogControl = new NativeAnalogSpeedometer();
                BindingOperations.ClearBinding(
                    automaticAnalogControl,
                    NativeAnalogSpeedometer.FrameProperty);
                automaticAnalogControl.Frame = analogFrame with
                {
                    GearDisplayMode = GearDisplayMode.Automatic
                };
                var automaticAnalogGear = Assert.IsType<Image>(
                    automaticAnalogControl.FindName("GearImage"));
                analogAutomaticDriveAsset = AssetName(
                    automaticAnalogGear.Source,
                    NativeGaugeMode.Digital,
                    "HUD_Dial_Digital_Gear_Drive.png");
                analogAutomaticDriveWidth = automaticAnalogGear.Width;
                analogAutomaticDriveHeight = automaticAnalogGear.Height;
                automaticAnalogControl.Frame = analogFrame;
                analogManualGearWidthAfterSwitch = automaticAnalogGear.Width;
                analogManualGearHeightAfterSwitch = automaticAnalogGear.Height;
                var analogPixels = RenderedPixels(analogControl, 293, 294);
                nativeAnalogPixels = CountVisiblePixels(analogPixels);
                nativeAnalogRightEdgePixels = CountVisiblePixels(analogPixels, 293, 282, 0, 6, 294);
                var analogueNeedle = Assert.IsType<Canvas>(analogControl.FindName("Needle"));
                analogueNeedle.Visibility = Visibility.Collapsed;
                var analogPixelsWithoutNeedle = RenderedPixels(analogControl, 293, 294);
                nativeAnalogNeedleChangedPixels = CountDifferentPixels(
                    analogPixels,
                    analogPixelsWithoutNeedle);
                var exactRedline = ExactRedlineResult.Exact(7_500 * 2 * Math.PI / 60);
                var assists = NativeAssistStateCalculator.Calculate(
                    new NativeAssistRawState(
                        true,
                        true,
                        false,
                        true,
                        0,
                        0.2f,
                        0,
                        0,
                        [0, 0, 0, 0],
                        0,
                        0,
                        2,
                        0),
                    0.1f,
                    1,
                    314);
                var assistFrame = new NativeGaugeFrame(
                    true,
                    64,
                    4_000,
                    8_000,
                    TransmissionGear.Third,
                    SpeedUnit.MilesPerHour,
                    exactRedline,
                    assists);
                var digitalAssists = new NativeDigitalSpeedometer();
                BindingOperations.ClearBinding(digitalAssists, NativeDigitalSpeedometer.FrameProperty);
                digitalAssists.Frame = assistFrame;
                digitalAssists.Measure(new Size(320, 160));
                digitalAssists.Arrange(new Rect(0, 0, 320, 160));
                digitalAssists.UpdateLayout();
                var digitalUnit = Assert.IsType<Image>(digitalAssists.FindName("UnitImage"));
                digitalUnitRightEdge = digitalUnit.TranslatePoint(
                    new Point(digitalUnit.ActualWidth, 0),
                    digitalAssists).X;
                digitalAssistVisibility = Assert.IsType<StackPanel>(
                    digitalAssists.FindName("AssistStack")).Visibility;
                var digitalAbs = Assert.IsType<Image>(digitalAssists.FindName("AbsImage"));
                var digitalTcr = Assert.IsType<Image>(digitalAssists.FindName("TcrImage"));
                digitalAbsAsset = AssetName(
                    digitalAbs.Source,
                    NativeGaugeMode.Digital,
                    "HUD_Dial_Assist_Digital_ABS_Off.png");
                digitalTcrAsset = AssetName(
                    digitalTcr.Source,
                    NativeGaugeMode.Digital,
                    "HUD_Dial_Assist_Digital_TCR_On.png");
                digitalOffOpacity = digitalAbs.Opacity;
                var headlightAssists = assists with
                {
                    HeadlightStateAvailable = true,
                    AreHeadlightsOn = true
                };
                digitalAssists.Frame = assistFrame with { Assists = headlightAssists };
                digitalTcrGlowAsset = AssetName(
                    digitalTcr.Source,
                    NativeGaugeMode.Digital,
                    "HUD_Dial_Assist_Digital_TCR_On_glow.png");
                digitalGlowOpacity = digitalTcr.Opacity;

                var stmAvailableAssists = NativeAssistStateCalculator.Calculate(
                    new NativeAssistRawState(
                        true,
                        true,
                        true,
                        true,
                        0,
                        0,
                        0,
                        0,
                        [0, 0, 0, 0],
                        0,
                        1,
                        0,
                        0),
                    0.1f,
                    2,
                    314);
                digitalAssists.Frame = assistFrame with { Assists = stmAvailableAssists };
                var digitalStm = Assert.IsType<Image>(digitalAssists.FindName("StmImage"));
                digitalStmVisibilityWhenAvailable = digitalStm.Visibility;
                digitalStmAssetWhenAvailable = AssetName(
                    digitalStm.Source,
                    NativeGaugeMode.Digital,
                    "HUD_Dial_Assist_Digital_STM_Off.png");

                digitalAssists.Frame = assistFrame with { Assists = NativeAssistSnapshot.Unavailable() };
                unavailableDigitalAssists = Assert.IsType<StackPanel>(
                    digitalAssists.FindName("AssistStack")).Visibility;

                var analogAssists = new NativeAnalogSpeedometer();
                BindingOperations.ClearBinding(analogAssists, NativeAnalogSpeedometer.FrameProperty);
                analogAssists.Frame = assistFrame;
                var analogAbs = Assert.IsType<Image>(analogAssists.FindName("AbsImage"));
                var analogLc = Assert.IsType<Image>(analogAssists.FindName("LcImage"));
                analogLcAsset = AssetName(
                    analogLc.Source,
                    NativeGaugeMode.Analogue,
                    "HUD_Dial_Assist_Analogue_LC_On.png");
                analogOffOpacity = analogAbs.Opacity;
                analogAssists.Frame = assistFrame with { Assists = headlightAssists };
                analogLcGlowAsset = AssetName(
                    analogLc.Source,
                    NativeGaugeMode.Analogue,
                    "HUD_Dial_Assist_Analogue_LC_On_glow.png");
                analogGlowOpacity = analogLc.Opacity;
                analogAssists.Frame = assistFrame with { Assists = stmAvailableAssists };
                var analogStm = Assert.IsType<Image>(analogAssists.FindName("StmImage"));
                analogStmVisibilityWhenAvailable = analogStm.Visibility;
                analogStmAssetWhenAvailable = AssetName(
                    analogStm.Source,
                    NativeGaugeMode.Analogue,
                    "HUD_Dial_Assist_Analogue_STM_Off.png");
                analogAssists.Frame = assistFrame;
                analogAbsAngle = Assert.IsType<RotateTransform>(analogAssists.FindName("AbsRotation")).Angle;
                analogTcrAngle = Assert.IsType<RotateTransform>(analogAssists.FindName("TcrRotation")).Angle;
                analogLcAngle = Assert.IsType<RotateTransform>(analogAssists.FindName("LcRotation")).Angle;

                var nativeSettings = new AppSettings
                {
                    LayoutMode = HudLayoutMode.Native,
                    GForceEnabled = true,
                    StartWithWindows = false
                };
                var controller = new AppController(
                    nativeSettings,
                    new SettingsService(Path.Combine(Path.GetTempPath(), $"wisp-style-{Guid.NewGuid():N}.json")));
                var gForceWindow = new GForceWindow(controller);
                gForceWindow.Measure(new Size(144, 100));
                gForceWindow.Arrange(new Rect(0, 0, 144, 100));
                gForceWindow.UpdateLayout();
                var nativeMeter = Assert.IsType<NativeGForceMeterView>(
                    gForceWindow.FindName("NativeGForceMeter"));
                nativeMeter.Measure(new Size(144, 100));
                nativeMeter.Arrange(new Rect(0, 0, 144, 100));
                nativeMeter.UpdateLayout();
                nativeGForceVisibility = nativeMeter.Visibility;
                var mainWindow = new MainWindow(controller);
                var tabs = Assert.IsType<TabControl>(mainWindow.FindName("RootTabs"));
                var surface = Assert.IsAssignableFrom<FrameworkElement>(mainWindow.Content);
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    tabs.SelectedIndex = tab;
                    surface.Measure(new Size(720, 440));
                    surface.Arrange(new Rect(0, 0, 720, 440));
                    surface.UpdateLayout();
                }

                ScrollLayoutAssertions.Verify(mainWindow, surface, tabs);
                CalmSidebarTests.AssertOnCurrentDispatcher(mainWindow, surface);

                tabs.SelectedIndex = 2;
                surface.UpdateLayout();
                mainWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                var logo = Assert.IsType<Image>(mainWindow.FindName("HeaderLogo"));
                var logoBitmap = Assert.IsAssignableFrom<BitmapSource>(logo.Source);
                Assert.True(logoBitmap.PixelWidth > 0 && logoBitmap.PixelHeight > 0);
                Assert.Equal(20, logo.Width);
                Assert.Equal(20, logo.Height);
                AssertCenteredHeader(mainWindow, surface);
                foreach (var name in new[] { "CloseWindowButton", "MinimizeWindowButton", "MaximizeWindowButton" })
                {
                    var button = Assert.IsType<Button>(mainWindow.FindName(name));
                    Assert.Equal(24, button.ActualWidth);
                    Assert.Equal(32, button.ActualHeight);
                    var dot = Assert.IsType<System.Windows.Shapes.Ellipse>(button.Template.FindName("WindowDot", button));
                    Assert.Equal(12, dot.ActualWidth);
                    Assert.Same(button.Background, dot.Fill);
                    Assert.NotNull(button.ToolTip);
                    Assert.True(button.Focusable && button.IsTabStop && button.IsHitTestVisible);
                    Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(button));
                }
                Assert.Equal(controller.ViewModel.NativeCompatibilityUpdates,
                    Assert.IsType<TextBlock>(mainWindow.FindName("CompatibilityStatusText")).Text);
                Assert.False(Assert.IsType<Button>(mainWindow.FindName("CompatibilityCheckButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(mainWindow.FindName("CompatibilityImportButton")).IsEnabled);
                mainWindow.Close();
                gForceWindow.Close();
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "WPF style/runtime STA verification did not finish within 30 seconds.");
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(Color.FromRgb(0x09, 0x0C, 0x11), resolvedColor);
        Assert.True(nativeDigitalPixels > 500);
        Assert.True(nativeAnalogPixels > 500);
        Assert.True(nativeElectricDigitalPixels > 500);
        Assert.True(nativeElectricAnalogPixels > 500);
        Assert.True(nativeAnalogRightEdgePixels > 0);
        Assert.True(
            nativeAnalogNeedleChangedPixels > 50,
            $"Native analogue needle changed only {nativeAnalogNeedleChangedPixels} pixels.");
        Assert.True(digitalUnitRightEdge <= 327.5);
        Assert.True(digitalUnitRightEdge >= 317.5);
        Assert.Equal(Visibility.Visible, nativeGForceVisibility);
        Assert.Equal(Visibility.Visible, digitalAssistVisibility);
        Assert.Equal(Visibility.Collapsed, unavailableDigitalAssists);
        Assert.Equal(Visibility.Visible, digitalStmVisibilityWhenAvailable);
        Assert.Equal(Visibility.Visible, analogStmVisibilityWhenAvailable);
        Assert.EndsWith("HUD_Dial_Assist_Digital_ABS_Off.png", digitalAbsAsset, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Digital_TCR_On.png", digitalTcrAsset, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Digital_TCR_On_glow.png", digitalTcrGlowAsset, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Digital_STM_Off.png", digitalStmAssetWhenAvailable, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Analogue_LC_On.png", analogLcAsset, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Analogue_LC_On_glow.png", analogLcGlowAsset, StringComparison.Ordinal);
        Assert.EndsWith("HUD_Dial_Assist_Analogue_STM_Off.png", analogStmAssetWhenAvailable, StringComparison.Ordinal);
        Assert.Equal("HUD_Dial_Digital_Gear_Drive.png", analogAutomaticDriveAsset);
        Assert.Equal(68, analogAutomaticDriveWidth);
        Assert.Equal(68, analogAutomaticDriveHeight);
        Assert.Equal(100, analogManualGearWidthAfterSwitch);
        Assert.Equal(100, analogManualGearHeightAfterSwitch);
        // FH6's Digital assist style applies A_Color_Faded_HUDInfo_Digital
        // (#4DFFFFFF) to Off. On and On_glow override it with #FFFFFFFF.
        Assert.Equal(77d / 255d, digitalOffOpacity, 6);
        Assert.Equal(1, digitalGlowOpacity);
        Assert.Equal(1, analogOffOpacity);
        Assert.Equal(1, analogGlowOpacity);
        Assert.Equal(40, analogAbsAngle);
        Assert.Equal(0, analogTcrAngle);
        Assert.Equal(-40, analogLcAngle);
        Assert.Equal(Color.FromArgb(128, 179, 179, 179), decodedGearBackplate);
        Assert.Equal(Color.FromArgb(128, 255, 255, 255), decodedDigitEdge);
        Assert.Equal(Color.FromArgb(128, 253, 0, 133), decodedRedlineGlow);
        // The exported FH6 PNG stores premultiplied RGB.  NativeAssetCache
        // restores straight BGRA so WPF's Pbgra32 compositor does not multiply
        // the assist sector by alpha a second time.
        Assert.Equal(Color.FromArgb(26, 255, 255, 255), decodedAnalogAssistSector);
        Assert.Equal(Color.FromArgb(255, 255, 255, 255), decodedAnalogActiveAssistText);
    }

    private static void AssertCenteredHeader(MainWindow window, FrameworkElement surface)
    {
        var titleBar = Assert.IsType<Grid>(window.FindName("TitleBar"));
        var brand = Assert.IsType<StackPanel>(window.FindName("HeaderBrand"));
        var controls = Assert.IsType<StackPanel>(window.FindName("WindowControls"));
        var status = Assert.IsType<Border>(window.FindName("HeaderStatus"));
        var dots = controls.Children.OfType<Button>()
            .Select(button => Assert.IsType<System.Windows.Shapes.Ellipse>(button.Template.FindName("WindowDot", button)))
            .ToArray();
        Assert.Equal(3, dots.Length);
        var originalStatusWidth = status.Width;
        foreach (var (width, height) in new[] { (720d, 440d), (1040d, 760d), (1280d, 900d) })
        {
            foreach (var statusWidth in new[] { 140d, 205d })
            {
                status.Width = statusWidth;
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                surface.UpdateLayout();
                var titleBarTop = titleBar.TranslatePoint(new Point(), surface).Y;
                var brandTop = brand.TranslatePoint(new Point(), surface).Y;
                var brandLeft = brand.TranslatePoint(new Point(), surface).X;
                var controlsLeft = controls.TranslatePoint(new Point(), surface).X;
                var statusTop = status.TranslatePoint(new Point(), surface).Y;
                var statusLeft = status.TranslatePoint(new Point(), surface).X;
                Assert.Equal(56, titleBar.ActualHeight);
                Assert.Equal(32, controls.ActualHeight);
                Assert.InRange(Math.Abs(brandLeft + brand.ActualWidth / 2 - width / 2), 0, 0.5);
                Assert.InRange(Math.Abs(brandTop + brand.ActualHeight / 2 - titleBarTop - 28), 0, 0.5);
                Assert.Equal(15, controlsLeft, precision: 2);
                for (var index = 0; index < dots.Length; index++)
                {
                    var dotPosition = dots[index].TranslatePoint(new Point(), surface);
                    Assert.Equal(21d + index * 24d, dotPosition.X, precision: 2);
                    Assert.Equal(22, dotPosition.Y - titleBarTop, precision: 2);
                    Assert.Equal(12, dots[index].ActualWidth);
                    Assert.Equal(12, dots[index].ActualHeight);
                }
                Assert.True(controlsLeft + controls.ActualWidth < brandLeft);
                Assert.True(statusLeft > brandLeft + brand.ActualWidth);
                Assert.True(statusLeft + status.ActualWidth <= width - 18);
                Assert.True(statusTop >= titleBarTop && statusTop + status.ActualHeight <= titleBarTop + 56);
            }
        }
        status.Width = originalStatusWidth;
    }

    private sealed class ResourceOnlyApplication : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // A dispatcher pump must never start UDP or attach to a running game during UI tests.
        }
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(Path.Combine(directory!.FullName, "src", "Wisp.App", "App.xaml"));
        var resources = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            document.Root!.Element(presentation + "Application.Resources")!.Elements());
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }

    private static int RenderedPixelCount(
        FrameworkElement control,
        DependencyProperty frameProperty,
        NativeGaugeFrame frame,
        int width,
        int height)
    {
        BindingOperations.ClearBinding(control, frameProperty);
        control.SetValue(frameProperty, frame);
        var pixels = RenderedPixels(control, width, height);
        return CountVisiblePixels(pixels);
    }

    private static int RenderedElectricControlPixels(
        string typeName,
        NativeGaugeFrame frame,
        int width,
        int height)
    {
        var type = typeof(DiagnosticsViewModel).Assembly.GetType(typeName);
        Assert.NotNull(type);
        var control = Assert.IsAssignableFrom<FrameworkElement>(Activator.CreateInstance(type!));
        var frameField = type!.GetField(
            "FrameProperty",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.FlattenHierarchy);
        Assert.NotNull(frameField);
        var frameProperty = Assert.IsType<DependencyProperty>(frameField!.GetValue(null));
        BindingOperations.ClearBinding(control, frameProperty);
        control.SetValue(frameProperty, frame);

        return CountVisiblePixels(RenderedPixels(control, width, height));
    }

    private static int CountVisiblePixels(
        byte[] pixels,
        int width = 0,
        int left = 0,
        int top = 0,
        int regionWidth = 0,
        int regionHeight = 0)
    {
        if (width == 0)
        {
            var count = 0;
            for (var index = 3; index < pixels.Length; index += 4)
            {
                if (pixels[index] > 0)
                {
                    count++;
                }
            }

            return count;
        }

        var visible = 0;
        for (var y = top; y < top + regionHeight; y++)
        {
            for (var x = left; x < left + regionWidth; x++)
            {
                if (AlphaAt(pixels, width, x, y) > 0)
                {
                    visible++;
                }
            }
        }

        return visible;
    }

    private static int CountDifferentPixels(byte[] left, byte[] right)
    {
        Assert.Equal(left.Length, right.Length);
        var changed = 0;
        for (var index = 0; index < left.Length; index += 4)
        {
            if (left[index] != right[index] ||
                left[index + 1] != right[index + 1] ||
                left[index + 2] != right[index + 2] ||
                left[index + 3] != right[index + 3])
            {
                changed++;
            }
        }

        return changed;
    }

    private static byte[] RenderedPixels(
        FrameworkElement control,
        int width,
        int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte AlphaAt(byte[] pixels, int width, int x, int y) =>
        pixels[((y * width + x) * 4) + 3];

    private static Color ColorAt(BitmapSource source, int x, int y)
    {
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static string AssetName(ImageSource source, NativeGaugeMode mode, string expectedName) =>
        ReferenceEquals(source, NativeAssetCache.Get(mode, expectedName)) ? expectedName : string.Empty;
}
