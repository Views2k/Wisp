using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class TachNeedleDiagnosticsTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var application = Application.Current;
        var shutdownMode = application.ShutdownMode;
        var mainWindow = application.MainWindow;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        TachDiagnostics.SetEnabled(false);
        TachDiagnostics.Clear();
        try
        {
            AssertAnalogueUpdatesAndLifecycle();
            AssertDigitalAppliedFraction();
        }
        finally
        {
            TachDiagnostics.SetEnabled(false);
            TachDiagnostics.Clear();
            application.MainWindow = mainWindow;
            application.ShutdownMode = shutdownMode;
        }
    }

    private static void AssertAnalogueUpdatesAndLifecycle()
    {
        var control = new NativeAnalogSpeedometer();
        BindingOperations.ClearBinding(control, NativeAnalogSpeedometer.FrameProperty);
        control.Frame = Frame(1_000);
        TachDiagnostics.SetEnabled(true);
        TachDiagnostics.Clear();

        control.Frame = Frame(4_500);
        var first = Assert.Single(Interval().Needles);
        Assert.Equal("analogue", first.ControlKind);
        Assert.Equal("unhosted", first.HostKind);
        Assert.Equal(0, first.HostWindowHandle);
        Assert.Equal(1, first.Applied);
        Assert.Equal(1, first.Fallback);
        Assert.Equal(1, first.Changed);
        var rotation = Assert.IsType<RotateTransform>(control.FindName("NeedleRotation"));
        Assert.Equal(NativeGaugeGeometry.AnalogNeedleAngle(4_500, 9_000), rotation.Angle);

        control.Frame = control.Frame with { Speed = 124 };
        var unchanged = Assert.Single(Interval().Needles);
        Assert.Equal(first.ControlId, unchanged.ControlId);
        Assert.Equal(1, unchanged.Applied);
        Assert.Equal(0, unchanged.Changed);

        control.Frame = Frame(7_000) with
        {
            NativeNeedleAngleDegrees = 211,
            NativeNeedleBlurAmount = -0.13,
            NativeGaugeObservedTimestamp = Stopwatch.GetTimestamp()
        };
        var native = Assert.Single(Interval().Needles);
        Assert.Equal(first.ControlId, native.ControlId);
        Assert.Equal(1, native.Native);
        Assert.Equal(0, native.Fallback);
        Assert.Equal(1, native.SourceSwitches);
        Assert.Equal(211, rotation.Angle);
        var material = Assert.IsType<NativeAnalogNeedleVisual>(control.FindName("NeedleMaterial"));
        Assert.Equal(-0.13, material.BlurAmount);

        control.Frame = Frame(2_000) with
        {
            TachometerMaximumRpm = 0,
            ExactRedline = ExactRedlineResult.Unavailable(),
            NativeGaugeSourceInvalidated = true
        };
        var unavailable = Assert.Single(Interval().Needles);
        Assert.Equal(1, unavailable.Unavailable);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Canvas>(control.FindName("Needle")).Visibility);

        var preview = new NativeAnalogSpeedometer();
        BindingOperations.ClearBinding(preview, NativeAnalogSpeedometer.FrameProperty);
        preview.Frame = Frame(3_000);
        var separate = Assert.Single(Interval().Needles, item => item.ControlId != first.ControlId);
        Assert.Equal("analogue", separate.ControlKind);
        Assert.Equal(1, separate.Fallback);
        var snapshot = Assert.IsType<TachCaptureExport>(TachDiagnostics.Snapshot());
        Assert.Empty(snapshot.NeedleStartup);
        Assert.Empty(snapshot.NeedleRecent);

        var host = new DiagnosticsPreviewHost { Content = control, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var loaded = Assert.IsType<TachCaptureExport>(TachDiagnostics.Snapshot()).Lifecycle
                .Last(item => item.ControlId == first.ControlId);
            Assert.Equal(nameof(DiagnosticsPreviewHost), loaded.HostKind);
            Assert.Equal(0, loaded.HostWindowHandle);
            Assert.True(loaded.IsLoaded);
            Assert.False(loaded.IsVisible);
            Assert.False(loaded.IsLive);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(host).Handle);
            Assert.False(host.IsVisible);
            Assert.False(host.IsActive);

            control.Frame = Frame(6_000);
            Assert.Equal(0, Interval().Needles.Single(item => item.ControlId == first.ControlId).Applied);
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var unloaded = Assert.IsType<TachCaptureExport>(TachDiagnostics.Snapshot()).Lifecycle
                .Last(item => item.ControlId == first.ControlId);
            Assert.False(unloaded.IsLoaded);
            Assert.False(unloaded.IsLive);
        }
        finally
        {
            host.Content = null;
            host.Close();
        }
    }

    private static void AssertDigitalAppliedFraction()
    {
        TachDiagnostics.SetEnabled(false);
        var control = new NativeDigitalSpeedometer();
        BindingOperations.ClearBinding(control, NativeDigitalSpeedometer.FrameProperty);
        control.Frame = Frame(1_000);
        TachDiagnostics.SetEnabled(true);
        TachDiagnostics.Clear();

        control.Frame = Frame(4_500);
        var first = Assert.Single(Interval().Needles);
        Assert.Equal("digital", first.ControlKind);
        Assert.Equal(1, first.Fallback);
        Assert.Equal(1, first.Changed);
        var visual = Assert.IsType<NativeDigitalGaugeVisual>(control.FindName("GaugeVisual"));
        var effect = Assert.IsType<DigitalGaugeShaderEffect>(visual.Effect);
        Assert.Equal(0.5, effect.GaugeParameters.X);

        control.Frame = Frame(12_000);
        Assert.Equal(1, Assert.Single(Interval().Needles).Changed);
        Assert.Equal(1, effect.GaugeParameters.X);
        control.Frame = Frame(13_000);
        var clamped = Assert.Single(Interval().Needles);
        Assert.Equal(1, clamped.Applied);
        Assert.Equal(0, clamped.Changed);
        Assert.Equal(1, effect.GaugeParameters.X);

        TachDiagnostics.SetEnabled(false);
        control.Frame = Frame(2_000);
        Assert.Equal(NativeGaugeGeometry.NormalizedRpm(2_000, 9_000), effect.GaugeParameters.X);
        TachDiagnostics.SetEnabled(true);
        Assert.Empty(Interval().Needles);
    }

    private static TachIntervalDiagnostic Interval() =>
        Assert.IsType<TachIntervalDiagnostic>(TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow));

    private static NativeGaugeFrame Frame(double rpm) => new(
        true, 123, rpm, 9_000, TransmissionGear.Fourth, SpeedUnit.MilesPerHour,
        ExactRedlineResult.Exact(785.397766113281), CarOrdinal: 314,
        GameTimestampMilliseconds: 1_000, ReceivedTimestamp: Stopwatch.GetTimestamp());

    private sealed class DiagnosticsPreviewHost : Window
    {
    }
}
