using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Wisp.App.DebugLogging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class CompositorNeedleWorkerTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        const int controlId = 987654;
        var wasEnabled = TachDiagnostics.IsEnabled;
        var window = new Window
        {
            Width = 340,
            Height = 340,
            Left = 30,
            Top = 30,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStyle = WindowStyle.None
        };
        AnalogHudRenderWorker? worker = null;
        using var renderingBlocked = new ManualResetEvent(false);
        using var releaseRendering = new ManualResetEvent(false);
        var blockRendering = 0;
        TachDiagnostics.SetEnabled(true);
        try
        {
            window.Show();
            var presentation = new AnalogHudPresentation(340, 340, 0, 0, 1, 0, 0, 1,
                1, true, false, default, AnalogHudLayout.Authored);
            worker = new(new WindowInteropHelper(window).Handle, controlId, AnalogHudAssets.LoadOnUiThread(),
                presentation, (_, _) => { }, beforeDrawForTest: stop =>
                {
                    if (Volatile.Read(ref blockRendering) != 1) return;
                    renderingBlocked.Set();
                    WaitHandle.WaitAny([stop, releaseRendering]);
                }, afterPrepareForTest: stop =>
                {
                    if (Volatile.Read(ref blockRendering) != 2) return;
                    renderingBlocked.Set();
                    WaitHandle.WaitAny([stop, releaseRendering]);
                });
            var watch = Stopwatch.StartNew();
            foreach (var native in new[] { true, false, true })
            {
                var phaseStart = Stopwatch.GetTimestamp();
                var expectedSource = native ? "native" : "fallback";
                watch.Restart();
                while (watch.ElapsedMilliseconds < 4000 && !HasCompositorSample())
                {
                    var now = Stopwatch.GetTimestamp();
                    worker.UpdateFrame(new(true, 123, 4500, 10000, TransmissionGear.Fourth, SpeedUnit.MilesPerHour,
                        ExactRedlineResult.Exact(8500 * Math.PI / 30), CarOrdinal: 314,
                        GameTimestampMilliseconds: unchecked((uint)Environment.TickCount), ReceivedTimestamp: now,
                        NativeNeedleAngleDegrees: native ? 108 : double.NaN,
                        NativeNeedleBlurAmount: native ? -.04 : double.NaN,
                        NativeGaugeObservedTimestamp: now), now);
                    Pump();
                    Thread.Sleep(10);
                }
                Assert.True(HasCompositorSample(), $"No compositor submission for {expectedSource} source.");
                Assert.Contains(Rows(), r => r.Stage == "compositor_update" && r.Result == "ready" && r.StartedTimestamp >= phaseStart);
                Assert.Contains(Rows(), r => r.Stage == "compositor_motion" && r.Result == "ready" && r.StartedTimestamp >= phaseStart);

                bool HasCompositorSample() => TachDiagnostics.Snapshot()?.NeedleRecent.Any(r =>
                    r.ControlId == controlId && r.AppliedTimestamp >= phaseStart &&
                    r.Route == "compositor_snapshot" && r.Source == expectedSource) == true &&
                    Rows().Any(r => r.Stage == "compositor_motion" && r.Result == "ready" &&
                        r.StartedTimestamp >= phaseStart && r.ReceivedTimestamp >= phaseStart &&
                        r.MotionGeometryAccepted == true);
            }
            Assert.Contains(Rows(), r => r.Stage == "compositor_update" && r.Result == "ready");
            Assert.DoesNotContain(Rows(), r => r.Result == "error");
            AssertMotionContinuesWhileRendererIsBlocked(afterPreparation: false);
            AssertMotionContinuesWhileRendererIsBlocked(afterPreparation: true);
            // The old 1 ms rounding bug produced tens of thousands of immediate
            // timeouts per second. Keep a generous bound that catches that spin.
            Assert.True(Rows().Count(r => r.Stage == "frame_wait" && r.Result == "timeout") < 5000);
            // Stop publishing: independent motion must retire while ordinary
            // fallback drawing continues. This exercises the managed/native ABI.
            watch.Restart();
            while (watch.ElapsedMilliseconds < 2000 && !Rows().Any(r => r.Stage == "compositor_motion" && r.Result == "discarded"))
            { Pump(); Thread.Sleep(10); }
            Assert.Contains(Rows(), r => r.Stage == "compositor_motion" && r.Result == "discarded");
            var clearedAt = Rows().Where(r => r.Stage == "compositor_motion" && r.Result == "discarded")
                .Max(r => r.CompletedTimestamp);
            watch.Restart();
            while (watch.ElapsedMilliseconds < 2000 && !Rows().Any(r => r.Stage == "present" && r.Result == "submitted" && r.StartedTimestamp > clearedAt))
            { Pump(); Thread.Sleep(10); }
            Assert.Contains(Rows(), r => r.Stage == "present" && r.Result == "submitted" && r.StartedTimestamp > clearedAt);
            Assert.DoesNotContain(Rows(), r => r.Result == "error");
        }
        finally
        {
            releaseRendering.Set();
            worker?.Dispose();
            var watch = Stopwatch.StartNew();
            while (worker is not null && !worker.Completion.IsCompleted && watch.ElapsedMilliseconds < 4000)
            { Pump(); Thread.Sleep(10); }
            window.Close();
            TachDiagnostics.SetEnabled(wasEnabled);
            if (!wasEnabled) TachDiagnostics.Clear();
            Assert.True(worker is null || worker.Completion.IsCompleted);
        }

        IEnumerable<TachRendererDiagnostic> Rows() => TachDiagnostics.Snapshot()?.RendererRecent
            .Where(r => r.ControlId == controlId) ?? [];

        void AssertMotionContinuesWhileRendererIsBlocked(bool afterPreparation)
        {
            var rendererThread = Rows().Last(r => r.Stage == "draw" && r.Result == "ready").NativeThreadId;
            renderingBlocked.Reset();
            releaseRendering.Reset();
            Volatile.Write(ref blockRendering, afterPreparation ? 2 : 1);
            var timer = Stopwatch.StartNew();
            while (!renderingBlocked.WaitOne(0) && timer.ElapsedMilliseconds < 2000)
            { Publish(); Pump(); Thread.Sleep(10); }
            Assert.True(renderingBlocked.WaitOne(0), "The regression must actually withhold the HUD renderer.");
            var blockedAt = Stopwatch.GetTimestamp();
            timer.Restart();
            // Longer than freshness: the originally queued curve cannot cover
            // this interval. New publications must reach the motion worker itself.
            while (timer.ElapsedMilliseconds < 160)
            { Publish(); Pump(); Thread.Sleep(10); }
            var motion = Rows().Where(r => r.Stage == "compositor_motion" && r.Result == "ready" &&
                r.StartedTimestamp >= blockedAt && r.ReceivedTimestamp > blockedAt).ToArray();
            Assert.True(motion.Select(r => r.ReceivedTimestamp).Distinct().Count() >= 4,
                "Motion must consume several new publications while bitmap rendering is blocked.");
            Assert.All(motion, row =>
            {
                Assert.NotEqual(rendererThread, row.NativeThreadId);
                Assert.True(row.CompositorCommitTimestamp >= row.StartedTimestamp);
                Assert.True(row.CurveEndTimestamp >= row.SampleTimestamp);
            });
            Assert.Contains(motion, row => row.MotionGeometryAccepted == true &&
                row.CurveEndTimestamp > blockedAt + Stopwatch.Frequency / 10);
            Assert.DoesNotContain(Rows(), row => row.StartedTimestamp >= blockedAt && row.Stage is "draw" or "present");
            Volatile.Write(ref blockRendering, 0);
            releaseRendering.Set();
            timer.Restart();
            while (timer.ElapsedMilliseconds < 2000 && !Rows().Any(r => r.Stage == "present" &&
                r.Result == "submitted" && r.StartedTimestamp > blockedAt))
            { Publish(); Pump(); Thread.Sleep(10); }
            Assert.Contains(Rows(), r => r.Stage == "present" && r.Result == "submitted" && r.StartedTimestamp > blockedAt);
            if (afterPreparation)
            {
                var firstPresent = Rows().Where(r => r.Stage == "present" && r.Result == "submitted" &&
                    r.StartedTimestamp > blockedAt).Min(r => r.StartedTimestamp);
                Assert.DoesNotContain(Rows(), r => r.Stage == "compositor_update" && r.Result == "discarded" &&
                    r.StartedTimestamp >= blockedAt && r.StartedTimestamp <= firstPresent);
            }

            void Publish()
            {
                var now = Stopwatch.GetTimestamp();
                worker!.UpdateFrame(new(true, 123, 4500, 10000, TransmissionGear.Fourth, SpeedUnit.MilesPerHour,
                    ExactRedlineResult.Exact(8500 * Math.PI / 30), CarOrdinal: 314,
                    GameTimestampMilliseconds: unchecked((uint)Environment.TickCount), ReceivedTimestamp: now,
                    NativeNeedleAngleDegrees: 108 + timer.ElapsedMilliseconds / 10d,
                    NativeNeedleBlurAmount: -.04, NativeGaugeObservedTimestamp: now), now);
            }
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
