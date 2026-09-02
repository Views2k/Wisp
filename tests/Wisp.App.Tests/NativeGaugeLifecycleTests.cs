using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGaugeLifecycleTests
{
    [Fact]
    public void TransientNativeReadFailureKeepsTheVerifiedTachometerMaterialForTheSameCar()
    {
        var verified = Frame(314, 7_200, 0);
        var transient = verified with
        {
            EngineRpm = 1_200,
            ExactRedline = ExactRedlineResult.Unavailable(ExactRedlineStatus.TelemetryMismatch),
            TachometerMaximumRpm = 0,
            NativeGaugeSourceInvalidated = false
        };

        var stable = transient.PreserveStableTachometerState(verified);

        Assert.Equal(verified.ExactRedline, stable.ExactRedline);
        Assert.Equal(verified.TachometerMaximumRpm, stable.TachometerMaximumRpm);
        Assert.Equal(1_200, stable.EngineRpm);
    }

    [Fact]
    public void DefinitiveInvalidationOrCarChangeClearsThePreviousTachometerMaterial()
    {
        var verified = Frame(314, 7_200, 0);
        var unavailable = verified with
        {
            ExactRedline = ExactRedlineResult.Unavailable(),
            TachometerMaximumRpm = 0,
            NativeGaugeSourceInvalidated = true
        };
        var nextCar = unavailable with
        {
            CarOrdinal = 3766,
            NativeGaugeSourceInvalidated = false
        };

        Assert.False(NativeGaugeGeometry.HasExactTachometerState(
            unavailable.PreserveStableTachometerState(verified).ExactRedline,
            unavailable.PreserveStableTachometerState(verified).TachometerMaximumRpm));
        Assert.False(NativeGaugeGeometry.HasExactTachometerState(
            nextCar.PreserveStableTachometerState(verified).ExactRedline,
            nextCar.PreserveStableTachometerState(verified).TachometerMaximumRpm));
    }

    [Theory]
    [InlineData(10, 900d)]
    [InlineData(20, 900d)]
    [InlineData(10, double.NaN)]
    [InlineData(20, -1d)]
    public void DroppedReceiveCannotChangeTheAcceptedCar(int receivedMilliseconds, double rpm)
    {
        var interpolator = new NativeTachometerInterpolator();
        interpolator.Observe(314, 0, 1_000, Timestamp(0), Timestamp(0));
        interpolator.Observe(314, 20, 5_000, Timestamp(20), Timestamp(20));

        interpolator.Observe(3766, 40, rpm, Timestamp(40), Timestamp(receivedMilliseconds));

        Assert.Equal(314, interpolator.AcceptedCarOrdinal);
        Assert.Equal(3_000, interpolator.Sample(Timestamp(50)), 6);
    }

    [Fact]
    public void AcceptedCarChangesOnlyAfterTheNewReceiveIsAccepted()
    {
        var interpolator = new NativeTachometerInterpolator();
        Assert.Null(interpolator.AcceptedCarOrdinal);
        interpolator.Observe(314, 0, 1_000, Timestamp(0), Timestamp(0));
        Assert.Equal(314, interpolator.AcceptedCarOrdinal);

        Assert.Equal(900, interpolator.Observe(3766, 20, 900, Timestamp(20), Timestamp(20)), 6);
        Assert.Equal(3766, interpolator.AcceptedCarOrdinal);
        interpolator.Reset();
        Assert.Null(interpolator.AcceptedCarOrdinal);
    }

    // Run within the existing resource-only STA fixture: no window is shown,
    // and WPF shader resources never cross test dispatchers.
    internal static void AssertConsumersOnCurrentDispatcher()
    {
        AssertPlaybackBoundaries(new NativeDigitalSpeedometer(), NativeDigitalSpeedometer.FrameProperty);
        AssertExactNativeNeedle(new NativeAnalogSpeedometer(), NativeAnalogSpeedometer.FrameProperty);
        AssertExactNativeNeedle(
            new NativeElectricAnalogSpeedometer(),
            NativeElectricAnalogSpeedometer.FrameProperty);
        AssertElectricDigitalPowerBarGeometry();
        AssertElectricGearDoesNotSynthesizeShiftState();
        AssertElectricSpeedUsesNativeStateOnlyForNativeSource();
        AssertElectricAssistAssets();
    }

    private static void AssertExactNativeNeedle(
        FrameworkElement control,
        DependencyProperty frameProperty)
    {
        BindingOperations.ClearBinding(control, frameProperty);
        control.SetValue(
            frameProperty,
            Frame(314, 1_000, 0) with
            {
                NativeNeedleAngleDegrees = 222.25,
                NativeNeedleBlurAmount = -0.30
            });

        var needle = Assert.IsType<System.Windows.Controls.Canvas>(control.FindName("Needle"));
        var rotation = Assert.IsType<System.Windows.Media.RotateTransform>(
            control.FindName("NeedleRotation"));
        var material = Assert.IsType<NativeAnalogNeedleVisual>(control.FindName("NeedleMaterial"));
        Assert.Equal(Visibility.Visible, needle.Visibility);
        Assert.Equal(222.25, rotation.Angle, 12);
        Assert.Equal(-0.30, material.BlurAmount, 12);

        control.SetValue(
            frameProperty,
            Frame(314, 1_000, 20) with
            {
                NativeNeedleAngleDegrees = 250,
                NativeNeedleBlurAmount = double.NaN
            });
        Assert.Equal(-0.30, material.BlurAmount, 12);
        Assert.Equal(Visibility.Visible, needle.Visibility);

        control.SetValue(
            frameProperty,
            Frame(314, 1_000, 40) with
            {
                NativeNeedleAngleDegrees = double.NaN,
                NativeNeedleBlurAmount = double.NaN,
                TachometerMaximumRpm = 9_000,
                NativeElectricMaximumSpeed = 240,
                NativeGaugeSourceInvalidated = true
            });
        Assert.Equal(0, material.BlurAmount);
        Assert.Equal(Visibility.Visible, needle.Visibility);
    }

    private static void AssertElectricDigitalPowerBarGeometry()
    {
        var control = new NativeElectricDigitalSpeedometer();
        BindingOperations.ClearBinding(control, NativeElectricDigitalSpeedometer.FrameProperty);
        var exactBars = Frame(314, 1_000, 0) with
        {
            IsElectric = true,
            Gear = TransmissionGear.Second,
            NativeRegenFillAmount = 0.25,
            NativePowerFillAmount = 0.60,
            NativeRegenPowerRatio = 0.40,
            ElectricGearState = new NativeElectricGearState(
                true,
                2,
                3,
                1,
                2,
                false)
        };
        control.Frame = exactBars;

        var barGrid = Assert.IsType<System.Windows.Controls.Grid>(control.FindName("PowerBarGrid"));
        var panel = Assert.IsType<System.Windows.Controls.StackPanel>(control.FindName("PowerBarPanel"));
        var regen = Assert.IsType<System.Windows.Controls.ColumnDefinition>(
            control.FindName("RegenColumn"));
        var power = Assert.IsType<System.Windows.Controls.ColumnDefinition>(
            control.FindName("PowerColumn"));
        Assert.Equal(234, barGrid.Width);
        Assert.Equal(-13, panel.Margin.Right);
        Assert.Equal(93.6, regen.Width.Value, 12);
        Assert.Equal(140.4, power.Width.Value, 12);

        control.Frame = exactBars with
        {
            CarOrdinal = 3766,
            Gear = TransmissionGear.First,
            ElectricGearState = new NativeElectricGearState(
                true,
                1,
                1,
                1,
                -1,
                true)
        };
        Assert.Equal(215, barGrid.Width);
        Assert.Equal(6, panel.Margin.Right);
        Assert.Equal(86, regen.Width.Value, 12);
        Assert.Equal(129, power.Width.Value, 12);
    }

    private static void AssertElectricGearDoesNotSynthesizeShiftState()
    {
        var state = new NativeElectricGearState(
            true,
            1,
            2,
            0,
            -1,
            true);
        var redlineFrame = Frame(314, 9_000, 0) with
        {
            IsElectric = true,
            Gear = TransmissionGear.First,
            ElectricGearState = state
        };

        var analogue = new NativeElectricAnalogSpeedometer();
        BindingOperations.ClearBinding(analogue, NativeElectricAnalogSpeedometer.FrameProperty);
        analogue.Frame = redlineFrame;
        var analogueGear = Assert.IsType<System.Windows.Controls.Image>(analogue.FindName("GearImage"));
        var analogueArc = Assert.IsType<System.Windows.Controls.Image>(analogue.FindName("GearArcImage"));
        var reverse = Assert.IsType<System.Windows.Controls.Image>(analogue.FindName("PreviousGearImage"));
        Assert.Same(
            NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_Gear_Drive.png"),
            analogueGear.Source);
        Assert.Same(
            NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_Gear_Arc.png"),
            analogueArc.Source);
        Assert.Equal(Visibility.Visible, reverse.Visibility);
        Assert.Same(
            NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_Gear_Small_Reverse.png"),
            reverse.Source);

        var digital = new NativeElectricDigitalSpeedometer();
        BindingOperations.ClearBinding(digital, NativeElectricDigitalSpeedometer.FrameProperty);
        digital.Frame = redlineFrame;
        var digitalGear = Assert.IsType<System.Windows.Controls.Image>(digital.FindName("GearImage"));
        var next = Assert.IsType<System.Windows.Controls.Image>(digital.FindName("NextGearImage"));
        Assert.Same(
            NativeAssetCache.Get(NativeAssetFamily.Digital, "HUD_Dial_Digital_Gear_Drive.png"),
            digitalGear.Source);
        Assert.Equal(Visibility.Visible, next.Visibility);
        Assert.Same(
            NativeAssetCache.Get(NativeAssetFamily.Electric, "HUD_EV_Gear_2.png"),
            next.Source);
    }

    private static void AssertElectricSpeedUsesNativeStateOnlyForNativeSource()
    {
        var nativeSpeed = new NativeDisplayedSpeedState(
            true,
            2,
            4,
            6,
            false,
            false,
            false,
            SpeedUnit.MilesPerHour);
        var frame = Frame(314, 1_000, 0) with
        {
            IsElectric = true,
            DisplayedSpeedState = nativeSpeed,
            NativeGaugeObservedTimestamp = Stopwatch.GetTimestamp()
        };

        var analogue = new NativeElectricAnalogSpeedometer();
        BindingOperations.ClearBinding(analogue, NativeElectricAnalogSpeedometer.FrameProperty);
        analogue.Frame = frame;
        AssertElectricDigits(analogue, NativeAssetFamily.Electric, "HUD_EV_Speed", 1, 2, 3);
        analogue.Frame = frame with
        {
            SpeedSource = SpeedSourceMode.Fh6VehicleSpeed,
            Unit = SpeedUnit.KilometersPerHour
        };
        AssertElectricDigits(analogue, NativeAssetFamily.Electric, "HUD_EV_Speed", 1, 2, 3);
        analogue.Frame = frame with
        {
            SpeedSource = SpeedSourceMode.Fh6VehicleSpeed,
            NativeGaugeObservedTimestamp = Stopwatch.GetTimestamp()
        };
        AssertElectricDigits(analogue, NativeAssetFamily.Electric, "HUD_EV_Speed", 2, 4, 6);

        var digital = new NativeElectricDigitalSpeedometer();
        BindingOperations.ClearBinding(digital, NativeElectricDigitalSpeedometer.FrameProperty);
        frame = frame with { NativeGaugeObservedTimestamp = Stopwatch.GetTimestamp() };
        digital.Frame = frame;
        AssertElectricDigits(digital, NativeAssetFamily.Digital, "HUD_Dial_Speed_Digital_", 1, 2, 3);
        digital.Frame = frame with { SpeedSource = SpeedSourceMode.Fh6VehicleSpeed };
        AssertElectricDigits(digital, NativeAssetFamily.Digital, "HUD_Dial_Speed_Digital_", 2, 4, 6);
    }

    private static void AssertElectricDigits(
        FrameworkElement control,
        NativeAssetFamily family,
        string assetPrefix,
        int hundreds,
        int tens,
        int ones)
    {
        Assert.Same(
            NativeAssetCache.Get(family, $"{assetPrefix}{hundreds}.png"),
            Assert.IsType<System.Windows.Controls.Image>(control.FindName("HundredsImage")).Source);
        Assert.Same(
            NativeAssetCache.Get(family, $"{assetPrefix}{tens}.png"),
            Assert.IsType<System.Windows.Controls.Image>(control.FindName("TensImage")).Source);
        Assert.Same(
            NativeAssetCache.Get(family, $"{assetPrefix}{ones}.png"),
            Assert.IsType<System.Windows.Controls.Image>(control.FindName("OnesImage")).Source);
    }

    private static void AssertElectricAssistAssets()
    {
        var assists = new NativeAssistSnapshot(
            true,
            1,
            314,
            NativeAssistProviderStatus.Ready,
            true,
            false,
            true,
            true,
            true,
            false,
            true,
            true,
            60,
            20,
            -20,
            -60,
            true,
            true);

        AssertElectricAssistAssets(
            new NativeElectricDigitalSpeedometer(),
            NativeElectricDigitalSpeedometer.FrameProperty,
            NativeGaugeMode.Digital,
            NativeAssetFamily.Digital,
            assists);
        AssertElectricAssistAssets(
            new NativeElectricAnalogSpeedometer(),
            NativeElectricAnalogSpeedometer.FrameProperty,
            NativeGaugeMode.Analogue,
            NativeAssetFamily.Analogue,
            assists);
    }

    private static void AssertElectricAssistAssets(
        FrameworkElement control,
        DependencyProperty frameProperty,
        NativeGaugeMode mode,
        NativeAssetFamily family,
        NativeAssistSnapshot assists)
    {
        BindingOperations.ClearBinding(control, frameProperty);
        control.SetValue(
            frameProperty,
            Frame(314, 1_000, 0) with
            {
                IsElectric = true,
                Assists = assists
            });

        AssertAssistAsset(control, "AbsImage", family, mode, "ABS", false, assists);
        AssertAssistAsset(control, "TcrImage", family, mode, "TCR", true, assists);
        AssertAssistAsset(control, "StmImage", family, mode, "STM", false, assists);
        AssertAssistAsset(control, "LcImage", family, mode, "LC", true, assists);
    }

    private static void AssertAssistAsset(
        FrameworkElement control,
        string imageName,
        NativeAssetFamily family,
        NativeGaugeMode mode,
        string assistName,
        bool active,
        NativeAssistSnapshot assists)
    {
        var image = Assert.IsType<System.Windows.Controls.Image>(control.FindName(imageName));
        Assert.Equal(Visibility.Visible, image.Visibility);
        Assert.Same(
            NativeAssetCache.Get(
                family,
                NativeAssistAssetSelector.FileName(mode, assistName, active, assists)),
            image.Source);
    }

    private static void AssertPlaybackBoundaries(FrameworkElement control, DependencyProperty frameProperty)
    {
        BindingOperations.ClearBinding(control, frameProperty);
        var interpolator = ReadField<NativeTachometerInterpolator>(control, "_tachometerInterpolator");
        try
        {
            Assert.False(control.IsVisible);
            foreach (var unload in new[] { false, true })
            {
                Invoke(control, "ObserveTachometer", Frame(314, 1_000, 0), Timestamp(0));
                Invoke(control, "ObserveTachometer", Frame(314, 5_000, 20), Timestamp(20));
                Assert.Equal(3_000, (double)Invoke(control, "SampleTachometer", Timestamp(50))!, 6);
                if (unload)
                {
                    control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                }
                else
                {
                    ChangeVisibility(control, false);
                }

                Assert.Null(interpolator.AcceptedCarOrdinal);
                Assert.True(ReadField<bool>(control, "_needsTachometerSeed"));
                Assert.Equal(TimeSpan.MinValue, ReadField<TimeSpan>(control, "_lastRenderingTime"));

                // Hidden timer-fed frames must not teach the live playback a
                // slower cadence or restore the discarded interpolation history.
                foreach (var milliseconds in new[] { 100, 150, 200, 250 })
                {
                    control.SetValue(frameProperty, Frame(314, milliseconds * 10, milliseconds));
                    Assert.Null(interpolator.AcceptedCarOrdinal);
                }

                ChangeVisibility(control, true);
                Assert.Equal(2_500, (double)Invoke(control, "SampleTachometer", Timestamp(260))!, 6);
                Assert.Equal(314, interpolator.AcceptedCarOrdinal);
                Assert.Equal(2_500, (double)Invoke(control, "ObserveTachometer",
                    Frame(314, 3_500, 270), Timestamp(270))!, 6);
                Assert.Equal(3_000, (double)Invoke(control, "SampleTachometer", Timestamp(300))!, 6);
            }

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Null(interpolator.AcceptedCarOrdinal);
            Invoke(control, "ObserveTachometer", Frame(314, 1_000, 400), Timestamp(400));
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            control.Opacity = 0;
            control.Opacity = 1;
            Assert.Equal(314, interpolator.AcceptedCarOrdinal);

            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Null(interpolator.AcceptedCarOrdinal);
            Assert.False(ReadField<bool>(control, "_renderingAttached"));
            control.SetValue(frameProperty, Frame(3766, 900, 500) with { ReceivedTimestamp = null });
            ChangeVisibility(control, true);
            Assert.Equal(900, (double)Invoke(control, "SampleTachometer", Timestamp(510))!, 6);
            Assert.Equal(3766, interpolator.AcceptedCarOrdinal);
        }
        finally
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    }

    private static void ChangeVisibility(FrameworkElement control, bool visible) =>
        Invoke(control, "OnIsVisibleChanged", control,
            new DependencyPropertyChangedEventArgs(UIElement.IsVisibleProperty, !visible, visible));

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static T ReadField<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static NativeGaugeFrame Frame(int carOrdinal, double rpm, int receivedMilliseconds) =>
        new(true, 123, rpm, 9_000, TransmissionGear.Fourth, SpeedUnit.MilesPerHour,
            ExactRedlineResult.Exact(7_500 * 2 * Math.PI / 60), CarOrdinal: carOrdinal,
            GameTimestampMilliseconds: (uint)receivedMilliseconds, ReceivedTimestamp: Timestamp(receivedMilliseconds));

    private static long Timestamp(int milliseconds) =>
        (long)Math.Round(Stopwatch.Frequency * milliseconds / 1_000d);
}
