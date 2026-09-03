using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeRenderLifetimeTests
{
    [Fact]
    public void MinimizedHostStopsRenderingEvenWhenTheControlRemainsVisible()
    {
        Assert.Equal(NativeRenderActivity.Live, Activity(hostIsMinimized: false));
        Assert.Equal(NativeRenderActivity.Inactive, Activity(hostIsMinimized: true));
        Assert.Equal(NativeRenderActivity.Inactive, Activity(hostIsVisible: false));
        Assert.Equal(NativeRenderActivity.Inactive, Activity(isVisible: false));
    }

    [Fact]
    public void DetachedPreloadCanRenderButKnownHiddenOrUnloadedControlsCannot()
    {
        Assert.Equal(NativeRenderActivity.Static,
            Activity(isLoaded: false, isVisible: false, hasHost: false));
        Assert.Equal(NativeRenderActivity.Inactive,
            Activity(isLoaded: false, isVisible: false, hasHost: false, hasHiddenAncestor: true));
        Assert.Equal(NativeRenderActivity.Inactive,
            Activity(isLoaded: false, isVisible: false, hostIsVisible: false));
        Assert.Equal(NativeRenderActivity.Inactive,
            Activity(isLoaded: false, isVisible: false, hasHost: false, wasUnloaded: true));
    }

    // Use the existing resource-only STA fixture: shader resources must not
    // cross dispatchers, and none of these independent hosts is ever shown.
    internal static void AssertConsumersOnCurrentDispatcher(Action<string> report)
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            foreach (var (control, frameProperty) in Consumers())
            {
                BindingOperations.ClearBinding(control, frameProperty);
                AssertHiddenWork(control, frameProperty, report);
                AssertKnownHiddenHost(control, frameProperty);
                AssertUnloadedWork(control, frameProperty);
            }

            AssertElectricHistory(new NativeElectricDigitalSpeedometer(),
                NativeElectricDigitalSpeedometer.FrameProperty, NativeAssetFamily.Digital);
            AssertElectricHistory(new NativeElectricAnalogSpeedometer(),
                NativeElectricAnalogSpeedometer.FrameProperty, NativeAssetFamily.Electric);
            AssertElectricDialUnitCache();
        }
        finally
        {
            application.ShutdownMode = shutdownMode;
        }
    }

    private static void AssertHiddenWork(FrameworkElement control, DependencyProperty frameProperty,
        Action<string> report)
    {
        var ones = Assert.IsType<Image>(control.FindName("OnesImage"));
        var descriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        var changes = 0;
        EventHandler changed = (_, _) => changes++;
        descriptor.AddValueChanged(ones, changed);
        try
        {
            EmitFrames(control, frameProperty, 0, 20);
            Assert.NotNull(ones.Source);
            changes = 0;
            var start = GC.GetAllocatedBytesForCurrentThread();
            EmitFrames(control, frameProperty, 20, 120);
            var visibleBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(120, changes);
            var displayed = ones.Source;

            control.Visibility = Visibility.Collapsed;
            changes = 0;
            start = GC.GetAllocatedBytesForCurrentThread();
            EmitFrames(control, frameProperty, 143, 120);
            var hiddenBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(0, changes);
            Assert.Same(displayed, ones.Source);
            Assert.Equal(Frame(263), ReadField<NativeGaugeFrame>(control, "_latestFrame"));
            Assert.True(hiddenBytes < visibleBytes,
                $"{control.GetType().Name}: hidden {hiddenBytes} bytes, visible {visibleBytes} bytes.");

            control.Visibility = Visibility.Visible;
            Layout(control);
            Assert.Equal(1, changes);
            Assert.NotSame(displayed, ones.Source);
            Layout(control);
            Assert.Equal(1, changes);
            Assert.False(ReadField<bool>(control, "_framePending"));
            report($"{control.GetType().Name}: 120 static updates={visibleBytes} B; " +
                   $"120 collapsed frames={hiddenBytes} B, 0 digit changes; resume=1 digit change.");
        }
        finally
        {
            descriptor.RemoveValueChanged(ones, changed);
        }
    }

    private static void AssertKnownHiddenHost(FrameworkElement control, DependencyProperty frameProperty)
    {
        var host = new Window { Content = control, ShowActivated = false, ShowInTaskbar = false };
        var ones = Assert.IsType<Image>(control.FindName("OnesImage"));
        var displayed = ones.Source;
        try
        {
            Assert.False(host.IsVisible);
            Assert.False(host.IsActive);
            foreach (var state in new[] { WindowState.Normal, WindowState.Minimized })
            {
                host.WindowState = state;
                EmitFrames(control, frameProperty, 300, 120);
                Assert.Same(displayed, ones.Source);
                Assert.False(ReadField<NativeRenderLifetime>(control, "_renderLifetime").CanUpdateVisuals);
            }

            host.Content = null;
            Layout(control);
            Assert.NotSame(displayed, ones.Source);
            Assert.False(ReadField<bool>(control, "_framePending"));
        }
        finally
        {
            host.Content = null;
            host.Close();
        }
    }

    private static void AssertUnloadedWork(FrameworkElement control, DependencyProperty frameProperty)
    {
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var lifetime = ReadField<NativeRenderLifetime>(control, "_renderLifetime");
        Assert.True(lifetime.IsLoaded);
        Assert.False(lifetime.IsLive);
        if (control is NativeAnalogSpeedometer or NativeDigitalSpeedometer or NativeElectricAnalogSpeedometer)
            Assert.False(ReadField<bool>(control, "_renderingAttached"));

        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var ones = Assert.IsType<Image>(control.FindName("OnesImage"));
        var displayed = ones.Source;
        EmitFrames(control, frameProperty, 443, 120);
        Assert.Same(displayed, ones.Source);
        Assert.False(lifetime.IsLoaded);
        Assert.False(lifetime.CanUpdateVisuals);
        Assert.Equal(Frame(563), ReadField<NativeGaugeFrame>(control, "_latestFrame"));
    }

    private static void AssertElectricHistory(FrameworkElement control, DependencyProperty frameProperty,
        NativeAssetFamily family)
    {
        BindingOperations.ClearBinding(control, frameProperty);
        var first = Frame(0) with
        {
            IsElectric = true,
            Gear = TransmissionGear.First,
            ElectricGearState = new NativeElectricGearState(
                true,
                1,
                2,
                0,
                1,
                false)
        };
        control.SetValue(frameProperty, first);
        control.Visibility = Visibility.Collapsed;
        control.SetValue(frameProperty, first with
        {
            Gear = TransmissionGear.Second,
            ElectricGearState = first.ElectricGearState with
            {
                Gear = 2,
                GearNext = 3,
                GearPrevious = 1,
                GearGaugeState = 2
            }
        });
        control.SetValue(frameProperty, first with { Unit = SpeedUnit.KilometersPerHour });
        control.Visibility = Visibility.Visible;
        Layout(control);

        var gear = Assert.IsType<Image>(control.FindName("GearImage"));
        Assert.Same(NativeAssetCache.Get(family, family == NativeAssetFamily.Electric
            ? "HUD_EV_Gear_1.png" : "HUD_Dial_Digital_Gear_1.png"), gear.Source);
        var unit = Assert.IsType<Image>(control.FindName("UnitImage"));
        Assert.Same(NativeAssetCache.Get(family == NativeAssetFamily.Electric
                ? NativeAssetFamily.Analogue : NativeAssetFamily.Digital,
            family == NativeAssetFamily.Electric ? "HUD_Dial_Unit_KPH.png" : "HUD_Dial_Unit_Digital_KPH.png"),
            unit.Source);

        control.Visibility = Visibility.Collapsed;
        control.SetValue(frameProperty, first with
        {
            CarOrdinal = 3766,
            ElectricGearState = first.ElectricGearState with { UseDriveFor1 = true }
        });
        control.Visibility = Visibility.Visible;
        Layout(control);
        Assert.Same(NativeAssetCache.Get(family, family == NativeAssetFamily.Electric
            ? "HUD_EV_Gear_Drive.png" : "HUD_Dial_Digital_Gear_Drive.png"), gear.Source);

        control.Visibility = Visibility.Collapsed;
        control.SetValue(frameProperty, first with
        {
            Gear = TransmissionGear.Reverse,
            ElectricGearState = NativeElectricGearState.Unavailable
        });
        control.Visibility = Visibility.Visible;
        Layout(control);
        Assert.Equal(Visibility.Visible, gear.Visibility);
        Assert.Same(NativeAssetCache.Get(family, family == NativeAssetFamily.Electric
            ? "HUD_EV_Gear_Reverse.png" : "HUD_Dial_Digital_Gear_R.png"), gear.Source);
    }

    private static void AssertElectricDialUnitCache()
    {
        var control = new NativeElectricAnalogSpeedometer();
        BindingOperations.ClearBinding(control, NativeElectricAnalogSpeedometer.FrameProperty);
        control.Frame = Frame(0) with { IsElectric = true };
        var number = Assert.IsType<Image>(control.FindName("DialNumber1"));
        var unit = Assert.IsType<Image>(control.FindName("UnitImage"));
        var marker = new DrawingImage();
        marker.Freeze();
        number.Source = marker;
        unit.Source = marker;
        EmitFrames(control, NativeElectricAnalogSpeedometer.FrameProperty, 0, 120);
        Assert.Same(marker, number.Source);
        Assert.Same(marker, unit.Source);

        control.Frame = Frame(121) with { IsElectric = true, Unit = SpeedUnit.KilometersPerHour };
        Assert.Same(NativeAssetCache.GetTinted(NativeAssetFamily.Electric,
            "HUD_EV_Dial_Speed50.png", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), number.Source);
        control.Frame = Frame(122) with { IsElectric = true };
        Assert.Same(NativeAssetCache.GetTinted(NativeAssetFamily.Electric,
            "HUD_EV_Dial_Speed30.png", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), number.Source);
    }

    private static NativeRenderActivity Activity(bool isLoaded = true, bool wasUnloaded = false,
        bool isVisible = true, bool hasHiddenAncestor = false, bool hasHost = true,
        bool hostIsVisible = true, bool hostIsMinimized = false) => NativeRenderLifetime.ActivityFor(
            isLoaded, wasUnloaded, isVisible, hasHiddenAncestor, hasHost, hostIsVisible, hostIsMinimized);

    private static IEnumerable<(FrameworkElement Control, DependencyProperty FrameProperty)> Consumers()
    {
        yield return (new NativeAnalogSpeedometer(), NativeAnalogSpeedometer.FrameProperty);
        yield return (new NativeDigitalSpeedometer(), NativeDigitalSpeedometer.FrameProperty);
        yield return (new NativeElectricAnalogSpeedometer(), NativeElectricAnalogSpeedometer.FrameProperty);
        yield return (new NativeElectricDigitalSpeedometer(), NativeElectricDigitalSpeedometer.FrameProperty);
    }

    private static void EmitFrames(FrameworkElement control, DependencyProperty property, int start, int count)
    {
        for (var index = start + 1; index <= start + count; index++)
            control.SetValue(property, Frame(index));
    }

    private static NativeGaugeFrame Frame(int index) =>
        HudPreviewSample.Create(SpeedUnit.MilesPerHour, GearDisplayMode.Manual) with
        {
            Speed = 120 + index % 10,
            EngineRpm = 4_000 + index,
            Gear = TransmissionGear.First,
            CarOrdinal = 314,
            GameTimestampMilliseconds = (uint)index,
            ReceivedTimestamp = index + 1L
        };

    private static void Layout(FrameworkElement control)
    {
        control.InvalidateMeasure();
        control.Measure(new Size(400, 400));
        control.Arrange(new Rect(0, 0, 400, 400));
        control.UpdateLayout();
    }

    private static T ReadField<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
