using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App.DebugLogging;

namespace Wisp.App.NativeRendering;

internal sealed record AnalogHudPresentation(
    int Width, int Height, float OriginX, float OriginY,
    float AxisXX, float AxisXY, float AxisYX, float AxisYY, float Opacity,
    bool Active, bool TractionActive, AnalogHudColor TractionColor, AnalogHudLayout? Layout);

internal sealed class AnalogHudRenderWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(NativeGaugeFrame Frame, long Timestamp)> _frames = new(64);
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _changed = new(false);
    private readonly IntPtr _window;
    private readonly int _controlId;
    private readonly bool _cpuRendering;
    private readonly IReadOnlyList<AnalogHudTexture> _textures;
    private readonly Action<bool, int> _status;
    private readonly Thread _thread;
    private AnalogHudPresentation _presentation;
    private bool _reset;
    private bool _disposed;
    private int _queueDropped;

    internal AnalogHudRenderWorker(IntPtr window, int controlId, IReadOnlyList<AnalogHudTexture> textures,
        AnalogHudPresentation presentation, Action<bool, int> status, bool cpuRendering = false)
    {
        _window = window;
        _controlId = controlId;
        _cpuRendering = cpuRendering;
        _textures = textures;
        _presentation = presentation;
        _status = status;
        _thread = new Thread(Run) { IsBackground = true, Name = "Wisp analogue rendering" };
        _thread.Start();
    }

    internal void UpdateFrame(NativeGaugeFrame frame, long timestamp)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_frames.Count == 64)
            {
                // An old backlog must never play after a render/device stall.
                _queueDropped += _frames.Count;
                _frames.Clear();
                _reset = true;
            }
            _frames.Enqueue((frame, timestamp));
            _changed.Set();
        }
    }

    internal void UpdatePresentation(AnalogHudPresentation presentation)
    {
        lock (_gate)
        {
            if (_disposed || presentation == _presentation) return;
            if (!presentation.Active || presentation.Active != _presentation.Active)
                _reset = true;
            _presentation = presentation;
            _changed.Set();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
        }
        // No UI-thread Join: disposing a DComp target can involve its HWND's
        // thread. The worker releases its own device and handles in finally.
    }

    private void Run()
    {
        var nativeThreadId = GetCurrentThreadId();
        DirectCompositionDevice? device = null;
        var playback = new AnalogHudPlayback();
        var batch = new (NativeGaugeFrame Frame, long Timestamp)[64];
        var waitHandles = new WaitHandle[] { _stop, _changed };
        bool announced = false, hasFrame = false, wasActive = false, frameReady = false;
        int width = 0, height = 0;
        float appliedOpacity = float.NaN;
        AnalogHudPendingFrame? pending = null;
        NativeGaugeFrame latestFrame = default;
        long queuedTimestamp = 0, sequence = 0, operationStarted = 0;
        string operation = "state";
        DirectCompositionWaitMetrics waitMetrics = default;
        try
        {
            while (!_stop.WaitOne(0))
            {
                AnalogHudPresentation presentation;
                int count = 0, queueDropped;
                bool measure = TachDiagnostics.IsEnabled;
                lock (_gate)
                {
                    presentation = _presentation;
                    if (_reset)
                    {
                        playback.Reset();
                        hasFrame = false;
                        pending = null;
                        _reset = false;
                    }
                    queueDropped = _queueDropped;
                    _queueDropped = 0;
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                }
                if (queueDropped > 0)
                    RecordStage("state", "discarded", Stopwatch.GetTimestamp(), dropped: queueDropped);
                if (!presentation.Active)
                {
                    if (device is not null && wasActive)
                    {
                        // Clear the independent surface before the parent can
                        // be shown again in a different layout.
                        BeginOperation("state");
                        device.SetVisible(false);
                        RecordStage(operation, "ready", operationStarted);
                    }
                    wasActive = false;
                    pending = null;
                    playback.Reset();
                    hasFrame = false;
                    WaitHandle.WaitAny(waitHandles);
                    continue;
                }
                for (int i = 0; i < count; i++)
                {
                    Observe(batch[i]);
                    hasFrame = true;
                }
                if (!hasFrame)
                {
                    WaitHandle.WaitAny(waitHandles);
                    continue;
                }
                if (device is null)
                {
                    BeginOperation("state");
                    device = DirectCompositionDevice.Create(_window, presentation.Width, presentation.Height, _cpuRendering);
                    foreach (var texture in _textures)
                        device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                    width = presentation.Width;
                    height = presentation.Height;
                    RecordStage(operation, "created", operationStarted);
                }
                if (width != presentation.Width || height != presentation.Height)
                {
                    pending = null;
                    BeginOperation("state");
                    device.Resize(presentation.Width, presentation.Height);
                    width = presentation.Width;
                    height = presentation.Height;
                    RecordStage(operation, "resized", operationStarted);
                }
                // A previously hidden surface can still contain the old car.
                // Resume with a transparent swapchain before making it visible.
                if (!wasActive)
                {
                    BeginOperation("state");
                    if (device.PrepareForResume()) frameReady = false;
                    pending = null;
                    RecordStage(operation, "resumed", operationStarted);
                }
                if (appliedOpacity != presentation.Opacity || !wasActive)
                {
                    BeginOperation("state");
                    device.SetOpacity(presentation.Opacity);
                    device.SetVisible(true);
                    appliedOpacity = presentation.Opacity;
                    RecordStage(operation, "ready", operationStarted);
                }
                wasActive = true;
                if (!frameReady)
                {
                    BeginOperation("frame_wait");
                    var ready = device.WaitForNextFrame(100, _stop.SafeWaitHandle, measure, out waitMetrics);
                    RecordStage(operation, ready switch
                    {
                        DirectCompositionWaitResult.Ready => "ready",
                        DirectCompositionWaitResult.Cancelled => "cancelled",
                        _ => "timeout"
                    }, operationStarted);
                    if (ready == DirectCompositionWaitResult.Cancelled) break;
                    if (ready == DirectCompositionWaitResult.Timeout) continue;
                    frameReady = true;
                }

                // A layout change or playback reset can restart this iteration
                // after the frame wait. Keep its readiness until Present succeeds;
                // waiting again without presenting can stall the swapchain.

                // Consume input received during the wait before drawing a new
                // frame or checking whether pending pixels remain valid.
                lock (_gate)
                {
                    if (_reset || !_presentation.Active) continue;
                    presentation = _presentation;
                    count = 0;
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                }
                for (int i = 0; i < count; i++) Observe(batch[i]);
                if (width != presentation.Width || height != presentation.Height) continue;
                if (pending is { } old && !old.CanReuse(presentation, latestFrame,
                    playback.HasNativeNeedle(Stopwatch.GetTimestamp())))
                {
                    RecordStage("state", "discarded", Stopwatch.GetTimestamp());
                    pending = null;
                }
                if (pending is null)
                {
                    BeginOperation("draw");
                    var sample = playback.Sample(Stopwatch.GetTimestamp());
                    var commands = AnalogHudScene.Build(sample.Frame, sample.Angle, sample.Blur,
                        sample.NeedleVisible, presentation.TractionActive, presentation.TractionColor,
                        presentation.Layout, sample.AppliedRpm);
                    Transform(commands, presentation);
                    var sceneTicks = measure ? Stopwatch.GetTimestamp() - operationStarted : 0;
                    pending = new(sample, presentation, queuedTimestamp, ++sequence);
                    device.DrawForPresentation(commands, commands.Length, measure, out var drawMetrics);
                    RecordStage(operation, "ready", operationStarted, sceneTicks: sceneTicks,
                        drawCommands: commands.Length, drawMetrics: drawMetrics);
                }
                // A busy Present has not consumed these pixels. Retry only the
                // submission; rebuilding here repeats GPU work and samples blur
                // for frames that were never submitted. New input still enters
                // playback above, ready for the next frame after success.
                BeginOperation("present");
                var submitted = device.TryPresent(measure, out var presentMetrics);
                RecordStage(operation, submitted ? "submitted" : device.LastRenderWasOccluded ? "occluded" : "busy",
                    operationStarted, presentMetrics: presentMetrics);
                if (!submitted)
                {
                    if (device.LastRenderWasOccluded)
                    {
                        // Initialization succeeded even when Windows cannot show
                        // this HWND. Do not count this as a submitted needle frame.
                        AnnounceReady();
                        RetryWait(100);
                        // Once the surface becomes visible, sample current
                        // input instead of showing a frame held while occluded.
                        pending = null;
                        continue;
                    }
                    // DO_NOT_WAIT can race another compositor consumer. Yield
                    // to cancellation instead of spinning or accumulating work.
                    RetryWait(1);
                    continue;
                }
                frameReady = false;
                AnnounceReady();
                Record(pending.Value.Sample);
                pending = null;

                void BeginOperation(string stage)
                {
                    operation = stage;
                    operationStarted = measure ? Stopwatch.GetTimestamp() : 0;
                    waitMetrics = default;
                }

                void RetryWait(int milliseconds)
                {
                    BeginOperation("retry_wait");
                    var cancelled = _stop.WaitOne(milliseconds);
                    RecordStage(operation, cancelled ? "cancelled" : "ready", operationStarted);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            RecordStage(operation, "error", operationStarted, hResult: error.HResult);
            if (!_stop.WaitOne(0)) _status(false, error.HResult);
        }
        finally
        {
            device?.Dispose();
            lock (_gate)
            {
                _disposed = true;
                _frames.Clear();
                _changed.Dispose();
                _stop.Dispose();
            }
        }

        void AnnounceReady()
        {
            if (announced) return;
            announced = true;
            _status(true, 0);
        }

        void Observe((NativeGaugeFrame Frame, long Timestamp) item)
        {
            playback.ObserveQueued(item.Frame, item.Timestamp, Stopwatch.GetTimestamp());
            latestFrame = item.Frame;
            queuedTimestamp = item.Timestamp;
        }

        void RecordStage(string stage, string result, long started, long sceneTicks = 0,
            int drawCommands = 0, DirectCompositionDrawMetrics drawMetrics = default,
            DirectCompositionPresentMetrics presentMetrics = default, int dropped = 0, int hResult = 0)
        {
            if (started == 0 || !TachDiagnostics.IsEnabled) return;
            var hasWaitDetails = stage == "frame_wait" && waitMetrics.TotalTicks > 0;
            var diagnostic = new TachRendererDiagnostic
            {
                ControlId = _controlId,
                NativeThreadId = nativeThreadId,
                CpuRendering = _cpuRendering,
                HostWindowHandle = _window.ToInt64(),
                Sequence = pending?.Sequence ?? sequence + 1,
                Stage = stage,
                Result = result,
                StartedTimestamp = started,
                CompletedTimestamp = Stopwatch.GetTimestamp(),
                SampleTimestamp = pending?.Sample.Timestamp,
                ReceivedTimestamp = pending?.Sample.Frame.ReceivedTimestamp ?? latestFrame.ReceivedTimestamp,
                QueuedTimestamp = pending?.QueuedTimestamp ?? (queuedTimestamp > 0 ? queuedTimestamp : null),
                SceneTicks = sceneTicks,
                DrawCommands = drawCommands,
                MapCount = (int)drawMetrics.MapCount,
                MapTicks = drawMetrics.MapTicks,
                MaxMapTicks = drawMetrics.MaximumMapTicks,
                NativeSetupTicks = drawMetrics.SetupTicks,
                NativeDrawTicks = drawMetrics.TotalTicks,
                NativePresentTicks = presentMetrics.DurationTicks,
                NativeWaitTicks = hasWaitDetails ? waitMetrics.TotalTicks : null,
                WaitPrecheckTicks = hasWaitDetails ? waitMetrics.PrecheckTicks : null,
                WaitCallTicks = hasWaitDetails ? waitMetrics.WaitCallTicks : null,
                WaitPostcheckTicks = hasWaitDetails ? waitMetrics.PostcheckTicks : null,
                SwapChainGeneration = hasWaitDetails && waitMetrics.SwapChainGeneration > 0 ? waitMetrics.SwapChainGeneration : null,
                WaitReturnCode = hasWaitDetails ? waitMetrics.WaitResult : null,
                CpuThreadTicks = hasWaitDetails ? waitMetrics.CpuThreadStopwatchTicks : null,
                HResult = hResult != 0 ? hResult : presentMetrics.HResult,
                QueueDropped = dropped
            };
            TachDiagnostics.RecordRenderer(in diagnostic);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    internal static void Transform(DirectCompositionDrawCommand[] commands, AnalogHudPresentation presentation)
    {
        for (int i = 0; i < commands.Length; i++)
        {
            ref var command = ref commands[i];
            (command.OriginX, command.OriginY) = (
                presentation.OriginX + command.OriginX * presentation.AxisXX + command.OriginY * presentation.AxisYX,
                presentation.OriginY + command.OriginX * presentation.AxisXY + command.OriginY * presentation.AxisYY);
            (command.AxisXX, command.AxisXY) = (
                command.AxisXX * presentation.AxisXX + command.AxisXY * presentation.AxisYX,
                command.AxisXX * presentation.AxisXY + command.AxisXY * presentation.AxisYY);
            (command.AxisYX, command.AxisYY) = (
                command.AxisYX * presentation.AxisXX + command.AxisYY * presentation.AxisYX,
                command.AxisYX * presentation.AxisXY + command.AxisYY * presentation.AxisYY);
        }
    }

    private void Record(AnalogHudSample sample)
    {
        if (!TachDiagnostics.IsEnabled) return;
        var diagnostic = new TachNeedleDiagnostic
        {
            ControlId = _controlId,
            ControlKind = "analogue",
            HostKind = nameof(OverlayWindow),
            HostWindowHandle = _window.ToInt64(),
            Route = "directcomposition",
            Source = sample.NeedleVisible ? sample.Native ? "native" : "fallback" : "unavailable",
            IsLoaded = true,
            IsVisible = true,
            IsLive = true,
            NeedleVisible = sample.NeedleVisible,
            CarOrdinal = sample.Frame.CarOrdinal,
            GameTimestampMilliseconds = sample.Frame.GameTimestampMilliseconds,
            ReceivedTimestamp = sample.Frame.ReceivedTimestamp,
            NativeObservedTimestamp = sample.Frame.NativeGaugeObservedTimestamp,
            AppliedTimestamp = sample.Timestamp,
            RawRpm = sample.Frame.EngineRpm,
            AppliedRpm = sample.AppliedRpm,
            Angle = sample.Angle,
            Blur = sample.Blur,
            PlaybackDelayMilliseconds = sample.PlaybackDelayMilliseconds,
            PlaybackTargetDelayMilliseconds = sample.PlaybackTargetDelayMilliseconds,
            BufferedSamples = sample.BufferedSamples,
            PlaybackAtNewest = sample.PlaybackAtNewest,
            ReseedCount = sample.ReseedCount,
            StarvationReseedCount = sample.StarvationReseedCount
        };
        TachDiagnostics.RecordNeedle(in diagnostic);
    }
}

internal readonly record struct AnalogHudPendingFrame(
    AnalogHudSample Sample, AnalogHudPresentation Presentation, long QueuedTimestamp, long Sequence)
{
    internal bool CanReuse(AnalogHudPresentation presentation, NativeGaugeFrame frame, bool nativeNeedle) =>
        presentation.Active &&
        presentation.Width == Presentation.Width && presentation.Height == Presentation.Height &&
        presentation.OriginX == Presentation.OriginX && presentation.OriginY == Presentation.OriginY &&
        presentation.AxisXX == Presentation.AxisXX && presentation.AxisXY == Presentation.AxisXY &&
        presentation.AxisYX == Presentation.AxisYX && presentation.AxisYY == Presentation.AxisYY &&
        presentation.TractionActive == Presentation.TractionActive &&
        presentation.TractionColor == Presentation.TractionColor && presentation.Layout == Presentation.Layout &&
        frame.CarOrdinal == Sample.Frame.CarOrdinal && frame.IsElectric == Sample.Frame.IsElectric &&
        frame.Unit == Sample.Frame.Unit && frame.GearDisplayMode == Sample.Frame.GearDisplayMode &&
        nativeNeedle == Sample.Native &&
        frame.ExactRedline == Sample.Frame.ExactRedline &&
        frame.TachometerMaximumRpm.Equals(Sample.Frame.TachometerMaximumRpm) &&
        frame.NativeGaugeSourceInvalidated == Sample.Frame.NativeGaugeSourceInvalidated;
}
