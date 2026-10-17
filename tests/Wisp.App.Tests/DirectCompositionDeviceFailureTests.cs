using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.Drift;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

// Native E_OUTOFMEMORY (Direct3D 11 / DirectComposition allocation pressure while the game
// starts) must reach the render workers as a recorded, reported renderer failure. Surfaced as
// the runtime's OutOfMemoryException it bypassed their catch filters and ended the process.
public sealed class DirectCompositionDeviceFailureTests
{
    private const int OutOfMemory = unchecked((int)0x8007000E);

    [Fact]
    public void FailedNativeCreateRaisesANativeRendererExceptionWithTheHResult()
    {
        RunWithHiddenWindow(handle =>
        {
            var error = Assert.Throws<NativeRendererException>(() => DirectCompositionDevice.Create(
                handle, 64, 64, cpuRendering: true, nativeFailureForTest: operation => operation == "create" ? OutOfMemory : 0));
            Assert.Equal(OutOfMemory, error.HResult);
            Assert.Equal("create", error.Operation);
        });
    }

    [Theory]
    [InlineData("create")]
    [InlineData("upload_texture")]
    public void AnalogWorkerRecordsAndReportsNativeOutOfMemory(string operation)
    {
        RunWithHiddenWindow(handle =>
        {
            var before = DateTimeOffset.UtcNow.AddMilliseconds(-1);
            var status = new TaskCompletionSource<(bool Ready, int HResult)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentation = new AnalogHudPresentation(64, 64, 0, 0, 1, 0, 0, 1, 1, true, false, default, AnalogHudLayout.Authored);
            var worker = new AnalogHudRenderWorker(handle, 4242, [Texture()], presentation,
                (ready, hresult) => status.TrySetResult((ready, hresult)), cpuRendering: true,
                nativeFailureForTest: name => name == operation ? OutOfMemory : 0);
            try
            {
                worker.UpdateFrame(Frame(), Stopwatch.GetTimestamp());
                PumpUntil(() => status.Task.IsCompleted, 10000, "The render worker did not report its status.");
                Assert.Equal((false, OutOfMemory), status.Task.Result);
                PumpUntil(() => worker.Completion.IsCompleted, 10000, "The render worker did not finish.");
                var failure = HealthContextRecorder.Current.CaptureForReport().Breadcrumbs.LastOrDefault(crumb =>
                    crumb.Code == HealthEventCode.RendererFailed && crumb.ErrorCode == OutOfMemory && crumb.TimestampUtc >= before);
                Assert.NotNull(failure);
                Assert.Equal(HealthFailureStage.RendererCreate, failure.FailureStage);
            }
            finally { worker.Dispose(); }
        });
    }

    [Theory]
    [InlineData("create")]
    [InlineData("upload_texture")]
    public void DriftWorkerRecordsAndReportsNativeOutOfMemory(string operation)
    {
        RunWithHiddenWindow(handle =>
        {
            var before = DateTimeOffset.UtcNow.AddMilliseconds(-1);
            var status = new TaskCompletionSource<(bool Ready, int HResult)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentation = new DriftGaugePresentation(620, 108, 1, 1, 1, true, 40, 10);
            var worker = new DriftGaugeRenderWorker(handle, () => null, presentation,
                (ready, hresult) => status.TrySetResult((ready, hresult)), cpuRendering: true,
                nativeFailureForTest: name => name == operation ? OutOfMemory : 0);
            try
            {
                PumpUntil(() => status.Task.IsCompleted, 10000, "The drift gauge worker did not report its status.");
                Assert.Equal((false, OutOfMemory), status.Task.Result);
                PumpUntil(() => worker.Completion.IsCompleted, 10000, "The drift gauge worker did not finish.");
                Assert.Contains(HealthContextRecorder.Current.CaptureForReport().Breadcrumbs, crumb =>
                    crumb.Code == HealthEventCode.RendererFailed && crumb.ErrorCode == OutOfMemory && crumb.TimestampUtc >= before);
            }
            finally { worker.Dispose(); }
        });
    }

    private static AnalogHudTexture Texture() => new(1, 2, 2, 8, new byte[16]);

    private static NativeGaugeFrame Frame()
    {
        var now = Stopwatch.GetTimestamp();
        return new NativeGaugeFrame(true, 137, 5000, 9000, TransmissionGear.Third, SpeedUnit.MilesPerHour,
            ExactRedlineResult.Exact(8000 * Math.PI / 30), CarOrdinal: 1, GameTimestampMilliseconds: 1,
            ReceivedTimestamp: now, NativeNeedleAngleDegrees: 210, NativeNeedleBlurAmount: -.2, NativeGaugeObservedTimestamp: now);
    }

    // The device binds a DirectComposition target to a real HWND owned by this STA thread, which
    // keeps pumping so that the worker's release can message it. The window is never shown.
    private static void RunWithHiddenWindow(Action<IntPtr> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                window = new Window { Width = 64, Height = 64, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
                test(new WindowInteropHelper(window).EnsureHandle());
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "The STA test thread did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpUntil(Func<bool> predicate, int milliseconds, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(10);
        }
        Assert.True(predicate(), message);
    }
}
