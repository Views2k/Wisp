using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

// A host-window Win32 call that failed four times at a transition (alt-tab return, game attach)
// retired the HUD window's native renderer until Wisp restarted, and its report carried E_FAIL
// instead of the Win32 error or the call. The host is released and built again on the next attach
// instead, and only a bounded number of consecutive failed episodes retires it.
public sealed class HudNativeHostRecoveryTests
{
    private const int InvalidWindowHandle = 1400; // ERROR_INVALID_WINDOW_HANDLE
    private const int InvalidWindowHandleResult = unchecked((int)0x80070578); // HRESULT_FROM_WIN32(1400)
    private const int UnspecifiedFailure = unchecked((int)0x80004005); // E_FAIL

    [Fact]
    public void FourFailedSynchronizationsReleaseTheHostUntilTheNextAttach()
    {
        var recovery = new HudNativeHostRecovery();
        for (var attempt = 1; attempt <= HudNativeHostRecovery.HostRetries; attempt++)
        {
            Assert.Equal(HudNativeHostRecoveryAction.Retry, recovery.Fail());
            Assert.Equal(attempt, recovery.Retries);
            Assert.False(recovery.RebuildPending);
        }
        Assert.Equal(HudNativeHostRecoveryAction.Rebuild, recovery.Fail());
        Assert.True(recovery.RebuildPending);
        Assert.Equal(0, recovery.Retries);
        Assert.Equal(1, recovery.FailedEpisodes);
        // The next attach or foreground return starts one rebuild; the same event cannot start another.
        Assert.True(recovery.TryBeginRebuild());
        Assert.False(recovery.RebuildPending);
        Assert.False(recovery.TryBeginRebuild());
        recovery.Presented();
        Assert.Equal(0, recovery.FailedEpisodes);
        Assert.Equal(0, recovery.Retries);
    }

    [Fact]
    public void ConsecutiveFailedEpisodesRetireTheHud()
    {
        var recovery = new HudNativeHostRecovery();
        for (var episode = 1; episode < HudNativeHostRecovery.MaximumFailedEpisodes; episode++)
        {
            Assert.Equal(HudNativeHostRecoveryAction.Rebuild, FailEpisode(recovery));
            Assert.Equal(episode, recovery.FailedEpisodes);
            Assert.True(recovery.TryBeginRebuild());
        }
        Assert.Equal(HudNativeHostRecoveryAction.Retire, FailEpisode(recovery));
        Assert.Equal(HudNativeHostRecovery.MaximumFailedEpisodes, recovery.FailedEpisodes);
        Assert.False(recovery.RebuildPending);
    }

    [Fact]
    public void APresentedFrameEndsTheEpisodeSequence()
    {
        var recovery = new HudNativeHostRecovery();
        for (var episode = 1; episode < HudNativeHostRecovery.MaximumFailedEpisodes; episode++)
        {
            Assert.Equal(HudNativeHostRecoveryAction.Rebuild, FailEpisode(recovery));
            Assert.True(recovery.TryBeginRebuild());
        }
        recovery.Presented();
        Assert.Equal(0, recovery.FailedEpisodes);
        Assert.Equal(HudNativeHostRecoveryAction.Rebuild, FailEpisode(recovery));
        Assert.Equal(1, recovery.FailedEpisodes);
    }

    [Fact]
    public void ALateFrameFromTheReleasedRendererDoesNotCountWhileARebuildIsPending()
    {
        var recovery = new HudNativeHostRecovery();
        Assert.Equal(HudNativeHostRecoveryAction.Rebuild, FailEpisode(recovery));
        recovery.Presented();
        Assert.True(recovery.RebuildPending);
        Assert.Equal(1, recovery.FailedEpisodes);
    }

    [Fact]
    public void AFailureNamesTheCallAndCarriesTheWin32CodeAsAnHResult()
    {
        var error = new NativeHostInteropException("SetWindowPos", InvalidWindowHandle);
        Assert.Equal("SetWindowPos", error.Call);
        Assert.Equal(InvalidWindowHandle, error.NativeErrorCode);
        Assert.Equal(UnspecifiedFailure, error.HResult); // Win32Exception itself reports E_FAIL whichever call failed.
        Assert.Equal(InvalidWindowHandleResult, error.FailureCode);
        Assert.Equal("SetWindowPos", NativeHostInteropException.CallOf(error));
        Assert.Equal(InvalidWindowHandleResult, NativeHostInteropException.FailureCodeOf(error));
    }

    [Theory]
    [InlineData(5, 0x80070005u)] // ERROR_ACCESS_DENIED
    [InlineData(0, 0x80004005u)] // no error code was left: E_FAIL stays
    public void AnUnnamedWin32FailureStillCarriesItsCode(int code, uint expected)
    {
        var error = new Win32Exception(code);
        Assert.Equal("Win32", NativeHostInteropException.CallOf(error));
        Assert.Equal(unchecked((int)expected), NativeHostInteropException.FailureCodeOf(error));
    }

    private static HudNativeHostRecoveryAction FailEpisode(HudNativeHostRecovery recovery)
    {
        for (var attempt = 0; attempt < HudNativeHostRecovery.HostRetries; attempt++)
            Assert.Equal(HudNativeHostRecoveryAction.Retry, recovery.Fail());
        return recovery.Fail();
    }

    // Driven from HudNativeHostTests on the shared WPF dispatcher against a live, presenting host.
    // One failed window call is retried with the renderer kept; four end the episode, release the
    // host and leave it waiting until the window is attached above a game again.
    internal static void AssertHostRebuildOnAttach(OverlayWindow window, HudNativeHost host, AppController controller,
        UserControl analog, Action seed)
    {
        var retired = HudNativeHost.PresentationHandle(window);
        Assert.NotEqual(IntPtr.Zero, retired);
        var worker = Worker(host);
        var recovery = Field<HudNativeHostRecovery>(host, "_hostRecovery");
        Assert.Equal(0, recovery.Retries);
        Assert.Equal(0, recovery.FailedEpisodes);

        // A first failure is reported once, with the call and HRESULT_FROM_WIN32 of its error code,
        // and the same window is synchronized again after the retry delay.
        var before = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        Callback(host, "OnHostInterop", new NativeHostInteropException("ClientToScreen", InvalidWindowHandle));
        Assert.Equal(1, recovery.Retries);
        Assert.True(Field<bool>(host, "_retryPending"));
        Assert.Equal("HUD renderer: host window retry after ClientToScreen (0x80070578)", controller.ViewModel.NativeRendererStatus);
        seed(); // captured by the retried synchronization, so that a changed frame is presented
        var failure = Assert.Single(RendererFailures(before));
        Assert.Equal(InvalidWindowHandleResult, failure.ErrorCode);
        Assert.Equal(HealthFailureStage.RendererHost, failure.FailureStage);
        PumpUntil(() => !Field<bool>(host, "_retryPending") && recovery.Retries == 0 &&
            controller.ViewModel.NativeRendererStatus.Contains("DirectComposition", StringComparison.Ordinal), 4000,
            () => $"step=retry; status={controller.ViewModel.NativeRendererStatus}; retries={recovery.Retries}");
        Assert.Same(worker, Worker(host));
        Assert.Equal(retired, HudNativeHost.PresentationHandle(window));
        Assert.False(Field<bool>(host, "_failed"));

        // Three failed synchronizations were retried already; the fourth ends the episode. The
        // renderer and its HWND are released, the WPF artwork stays suppressed and nothing short of
        // an attach above the game builds the host again.
        for (var attempt = 0; attempt < HudNativeHostRecovery.HostRetries; attempt++)
            Assert.Equal(HudNativeHostRecoveryAction.Retry, recovery.Fail());
        Callback(host, "OnHostInterop", new NativeHostInteropException("SetWindowPos", InvalidWindowHandle));
        Assert.True(recovery.RebuildPending);
        Assert.Equal(1, recovery.FailedEpisodes);
        Assert.Equal(0, recovery.Retries);
        Assert.False(Field<bool>(host, "_failed"));
        Assert.False(Field<bool>(host, "_retryPending"));
        Assert.Single(RendererFailures(before)); // the episode's later failures add no breadcrumb
        Assert.Equal("HUD renderer: host window rebuild pending after SetWindowPos (0x80070578)", controller.ViewModel.NativeRendererStatus);
        Assert.Equal(IntPtr.Zero, HudNativeHost.PresentationHandle(window));
        Assert.Null(typeof(HudNativeHost).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));
        PumpUntil(() => !IsWindow(retired), 2000, () => "step=release; the released host window was not destroyed");
        Assert.False(HudNativeHost.IsPresented(analog));
        Assert.True(HudNativeHost.IsWpfContentSuppressed(analog));
        Assert.Equal(Visibility.Hidden, ((UIElement)analog.Content).Visibility);
        seed();
        Pump();
        Thread.Sleep(100);
        Pump();
        Assert.Equal(IntPtr.Zero, HudNativeHost.PresentationHandle(window));
        Assert.True(recovery.RebuildPending);
        Assert.True(window.IsVisible);

        using var owner = ForeignWindowOwner.Start();
        try
        {
            Assert.True(WindowZOrder.AttachAboveGame(window, owner.Handle, raise: false));
            Assert.False(recovery.RebuildPending);
            seed();
            PumpUntil(() => HudNativeHost.PresentationHandle(window) is var rebuilt && rebuilt != IntPtr.Zero &&
                rebuilt != retired && HudNativeHost.IsPresented(analog), 8000,
                () => $"step=rebuild; status={controller.ViewModel.NativeRendererStatus}; handle={HudNativeHost.PresentationHandle(window)}; pending={recovery.RebuildPending}");
            var rebuiltHandle = HudNativeHost.PresentationHandle(window);
            Assert.Equal(owner.Handle, GetWindow(rebuiltHandle, 4));
            Assert.NotSame(worker, Worker(host));
            Assert.True(Worker(host).SurfaceVisible);
            Assert.Equal(Visibility.Hidden, ((UIElement)analog.Content).Visibility);
            PumpUntil(() => recovery.FailedEpisodes == 0, 2000, () => $"step=presented; episodes={recovery.FailedEpisodes}");
            Assert.False(Field<bool>(host, "_failed"));
            Assert.DoesNotContain("unavailable", controller.ViewModel.NativeRendererStatus, StringComparison.Ordinal);
            // An attach without a pending rebuild leaves the working host alone.
            var current = Worker(host);
            HudNativeHost.RebuildOnGameAttach(window);
            Pump();
            Assert.Equal(rebuiltHandle, HudNativeHost.PresentationHandle(window));
            Assert.Same(current, Worker(host));
        }
        finally { WindowZOrder.DetachFromGame(window); }
    }

    // The breadcrumbs of the injected Win32 failure, apart from anything the renderer itself reports.
    private static HealthBreadcrumb[] RendererFailures(DateTimeOffset since) =>
        HealthContextRecorder.Current.CaptureForReport().Breadcrumbs.Where(crumb =>
            crumb.Code == HealthEventCode.RendererFailed && crumb.ErrorCode == InvalidWindowHandleResult &&
            crumb.TimestampUtc >= since).ToArray();

    private static AnalogHudRenderWorker Worker(HudNativeHost host) =>
        Assert.IsType<AnalogHudRenderWorker>(typeof(HudNativeHost)
            .GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host));
    private static T Field<T>(HudNativeHost host, string name) =>
        (T)typeof(HudNativeHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static void Callback(HudNativeHost host, string name, params object[] arguments) =>
        typeof(HudNativeHost).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, arguments);
    private static void PumpUntil(Func<bool> predicate, int milliseconds, Func<string> failureDetails)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
        var reached = predicate();
        Assert.True(reached, reached ? null : $"The native HUD host did not reach the expected recovery state. {failureDetails()}");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}
