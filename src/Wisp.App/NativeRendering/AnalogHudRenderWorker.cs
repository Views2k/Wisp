using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App.DebugLogging;

namespace Wisp.App.NativeRendering;

internal sealed record AnalogHudPresentation(
    int Width, int Height, float OriginX, float OriginY,
    float AxisXX, float AxisXY, float AxisYX, float AxisYY, float Opacity,
    bool Active, bool TractionActive, AnalogHudColor TractionColor, AnalogHudLayout? Layout, HudWindowSnapshot? Hud = null);

internal sealed class AnalogHudRenderWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<(NativeGaugeFrame Frame, long Timestamp)> _frames = new(64);
    private readonly Queue<(HudWindowSnapshot Snapshot, long Timestamp)> _hudFrames = new(64);
    internal event Action<HudWindowSnapshot>? HudPresented;
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _changed = new(false);
    private readonly IntPtr _window;
    private readonly int _controlId;
    private readonly bool _cpuRendering;
    private readonly INativeNeedleHistorySource? _nativeSource;
    private readonly IReadOnlyList<AnalogHudTexture> _textures;
    private readonly Action<bool, int> _status;
    private readonly Thread _thread;
    private readonly CompositorNeedleMotionWorker? _motion;
    private readonly Action<WaitHandle>? _beforeDrawForTest;
    private readonly Action<WaitHandle>? _afterPrepareForTest;
    private long _motionGeneration = 1;
    private AnalogHudPresentation _presentation;
    private bool _reset;
    private bool _disposed;
    private bool _surfaceVisible;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _queueDropped;

    internal bool SurfaceVisible => Volatile.Read(ref _surfaceVisible);
    internal Task Completion => _completion.Task;

    internal AnalogHudRenderWorker(IntPtr window, int controlId, IReadOnlyList<AnalogHudTexture> textures,
        AnalogHudPresentation presentation, Action<bool, int> status, bool cpuRendering = false,
        INativeNeedleHistorySource? nativeSource = null, Action<WaitHandle>? beforeDrawForTest = null,
        Action<WaitHandle>? afterPrepareForTest = null)
    {
        _window = window;
        _controlId = controlId;
        _cpuRendering = cpuRendering;
        _nativeSource = nativeSource;
        _textures = textures;
        _presentation = presentation;
        _status = status;
        _beforeDrawForTest = beforeDrawForTest;
        _afterPrepareForTest = afterPrepareForTest;
        if (!cpuRendering)
            _motion = new(window, controlId, presentation, nativeSource, OnMotionFailure);
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
            if (_motion is not null) _motionGeneration = _motion.Publish(_presentation, frame, timestamp);
            _changed.Set();
        }
    }

    internal void UpdateHud(HudWindowSnapshot snapshot, long timestamp)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_hudFrames.Count == 64)
            {
                _queueDropped += _hudFrames.Count;
                _hudFrames.Clear();
                _reset = true;
            }
            if (!snapshot.Active || snapshot.Active != _presentation.Active) _reset = true;
            _presentation = new(snapshot.Width, snapshot.Height, 0, 0, 1, 0, 0, 1, snapshot.Opacity,
                snapshot.Active, false, default, null, snapshot);
            _hudFrames.Enqueue((snapshot, timestamp));
            if (_motion is not null) _motionGeneration = _motion.Publish(_presentation, null, timestamp);
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
            if (_motion is not null) _motionGeneration = _motion.Publish(presentation, null, Stopwatch.GetTimestamp());
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
            _motion?.Dispose();
        }
        // No UI-thread Join: disposing a DComp target can involve its HWND's
        // thread. The worker releases its own device and handles in finally.
    }

    private void OnMotionFailure(int error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stop.Set();
        }
        _status(false, error);
    }

    private void Run()
    {
        var nativeThreadId = GetCurrentThreadId();
        DirectCompositionDevice? device = null;
        var playback = new AnalogHudPlayback();
        playback.SetNativeSource(_nativeSource);
        var hudPlayback = new HudScenePlayback(_nativeSource);
        var hudBatch = new (HudWindowSnapshot Snapshot, long Timestamp)[64];
        var uploaded = new Dictionary<uint, AnalogHudTexture>();
        HudWindowSnapshot? announcedHud = null;
        var batch = new (NativeGaugeFrame Frame, long Timestamp)[64];
        var compositorPoints = new CompositorNeedlePoint[1];
        long compositorSampleTimestamp = 0, compositorSourceTimestamp = 0;
        long motionGeneration = 1;
        bool compositorActive = false, resetCompositor = false;
        var waitHandles = new WaitHandle[] { _stop, _changed };
        bool announced = false, hasFrame = false, frameReady = false;
        int width = 0, height = 0;
        float appliedOpacity = float.NaN;
        float appliedHostX = float.NaN, appliedHostY = float.NaN;
        AnalogHudPendingFrame? pending = null;
        NativeGaugeFrame latestFrame = default;
        long queuedTimestamp = 0, sequence = 0, operationStarted = 0;
        string operation = "state";
        DirectCompositionWaitMetrics waitMetrics = default;
        bool measuredWaitCompleted = false;
        try
        {
            while (!_stop.WaitOne(0))
            {
                AnalogHudPresentation presentation;
                int count = 0, hudCount = 0, queueDropped;
                bool measure = TachDiagnostics.IsEnabled;
                lock (_gate)
                {
                    presentation = _presentation;
                    motionGeneration = _motionGeneration;
                    if (_reset)
                    {
                        announcedHud = null;
                        playback.Reset();
                        hudPlayback.Reset();
                        hasFrame = false;
                        pending = null;
                        resetCompositor = true;
                        _reset = false;
                    }
                    queueDropped = _queueDropped;
                    _queueDropped = 0;
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                    while (_hudFrames.Count > 0) hudBatch[hudCount++] = _hudFrames.Dequeue();
                }
                if (queueDropped > 0)
                    RecordStage("state", "discarded", Stopwatch.GetTimestamp(), dropped: queueDropped);
                if (resetCompositor)
                {
                    if (compositorActive) ClearNeedle();
                    compositorActive = false;
                    resetCompositor = false;
                }
                if (!presentation.Active)
                {
                    if (device is not null && _surfaceVisible)
                    {
                        // Clear the independent surface before the parent can
                        // be shown again in a different layout.
                        BeginOperation("state");
                        device.SetVisible(false);
                        RecordStage(operation, "ready", operationStarted);
                    }
                    pending = null;
                    playback.Reset();
                    hudPlayback.Reset();
                    hasFrame = false;
                    compositorActive = false;
                    // Acknowledge the applied hide only after native visibility
                    // and pending playback have both been cleared.
                    if (_surfaceVisible) Volatile.Write(ref _surfaceVisible, false);
                    WaitHandle.WaitAny(waitHandles);
                    continue;
                }
                for (int i = 0; i < count; i++)
                {
                    Observe(batch[i]);
                    hasFrame = true;
                }
                for (int i = 0; i < hudCount; i++)
                {
                    hudPlayback.Update(hudBatch[i].Snapshot, hudBatch[i].Timestamp);
                    latestFrame = hudBatch[i].Snapshot.Frame;
                    queuedTimestamp = hudBatch[i].Timestamp;
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
                    device = DirectCompositionDevice.Create(_window, presentation.Width, presentation.Height, _cpuRendering,
                        compositorNeedle: !_cpuRendering);
                    if (_motion is not null) _motion.Attach(device.CreateMotionChannel());
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
                    appliedHostX = appliedHostY = float.NaN;
                    compositorActive = false;
                    width = presentation.Width;
                    height = presentation.Height;
                    RecordStage(operation, "resized", operationStarted);
                }
                // Host geometry is independent of HUD texture dimensions and
                // local needle geometry. Moving the root must retain motion
                // acceptance and any already drawn frame awaiting Present.
                var hostX = presentation.Hud?.HostOffsetX ?? 0;
                var hostY = presentation.Hud?.HostOffsetY ?? 0;
                if (appliedHostX != hostX || appliedHostY != hostY)
                {
                    BeginOperation("state");
                    device.SetOffset(hostX, hostY);
                    appliedHostX = hostX;
                    appliedHostY = hostY;
                    RecordStage(operation, "positioned", operationStarted);
                }
                // A previously hidden surface can still contain the old car.
                // Resume with a transparent swapchain before making it visible.
                if (!_surfaceVisible)
                {
                    BeginOperation("state");
                    if (device.PrepareForResume()) frameReady = false;
                    compositorActive = false;
                    pending = null;
                    RecordStage(operation, "resumed", operationStarted);
                }
                if (appliedOpacity != presentation.Opacity || !_surfaceVisible)
                {
                    BeginOperation("state");
                    device.SetOpacity(presentation.Opacity);
                    device.SetVisible(true);
                    appliedOpacity = presentation.Opacity;
                    RecordStage(operation, "ready", operationStarted);
                    Volatile.Write(ref _surfaceVisible, true);
                }

                if (!frameReady)
                {
                    BeginOperation("frame_wait");
                    var ready = device.WaitForNextFrame(100, _stop.SafeWaitHandle, measure, out waitMetrics);
                    measuredWaitCompleted = measure;
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
                // after the frame wait. Preserve readiness across that restart;
                // waiting again without presenting can stall the swapchain.

                // Consume input received during the wait before drawing a new
                // frame or checking whether pending pixels remain valid.
                lock (_gate)
                {
                    if (_reset || !_presentation.Active) continue;
                    presentation = _presentation;
                    motionGeneration = _motionGeneration;
                    count = 0;
                    hudCount = 0;
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                    while (_hudFrames.Count > 0) hudBatch[hudCount++] = _hudFrames.Dequeue();
                }
                for (int i = 0; i < count; i++) Observe(batch[i]);
                for (int i = 0; i < hudCount; i++)
                {
                    hudPlayback.Update(hudBatch[i].Snapshot, hudBatch[i].Timestamp);
                    latestFrame = hudBatch[i].Snapshot.Frame;
                    queuedTimestamp = hudBatch[i].Timestamp;
                }
                if (width != presentation.Width || height != presentation.Height) continue;
                var nativeSourceInvalidated = presentation.Hud is null
                    ? playback.RefreshNativeHistory(Stopwatch.GetTimestamp())
                    : hudPlayback.RefreshNativeHistory(Stopwatch.GetTimestamp());
                if (nativeSourceInvalidated)
                {
                    pending = null;
                    if (compositorActive && !HasCurrentMotion()) ClearNeedle();
                }
                if (pending is { } old && (!old.CanReuse(presentation,
                    presentation.Hud is null ? playback.CurrentFrame : latestFrame,
                    playback.HasNativeNeedle(Stopwatch.GetTimestamp())) ||
                    (presentation.Hud is not null && !hudPlayback.CanReuse(Stopwatch.GetTimestamp()))))
                {
                    RecordStage("state", "discarded", Stopwatch.GetTimestamp());
                    pending = null;
                }
                if (pending is null)
                {
                    _beforeDrawForTest?.Invoke(_stop);
                    if (_stop.WaitOne(0)) break;
                    lock (_gate)
                    {
                        if (_reset || !_presentation.Active || _motionGeneration != motionGeneration) continue;
                    }
                    var material = _motion?.Material;
                    var wasCompositorActive = compositorActive;
                    if (material is not null && material.Generation == motionGeneration &&
                        Stopwatch.GetTimestamp() < material.Curve.FreshUntilTimestamp)
                    {
                        BeginOperation("compositor_update");
                        compositorPoints[0] = material.Point;
                        compositorSampleTimestamp = material.Curve.StartTimestamp;
                        compositorSourceTimestamp = material.Curve.LatestObservationTimestamp;
                        var geometry = material.Geometry;
                        compositorActive = device.PrepareCompositorNeedleMotion(in geometry,
                            material.Curve with { Count = 1 }, compositorPoints, motionGeneration);
                        RecordStage(operation, compositorActive ? "ready" : "discarded", operationStarted);
                    }
                    else compositorActive = false;
                    if (!compositorActive && wasCompositorActive) ClearNeedle();
                    _afterPrepareForTest?.Invoke(_stop);
                    if (_stop.WaitOne(0)) break;
                    BeginOperation("draw");
                    var now = Stopwatch.GetTimestamp();
                    var sample = presentation.Hud is null ? playback.Sample(now) :
                        default(AnalogHudSample) with { Frame = presentation.Hud.Frame, Timestamp = now };
                    DirectCompositionDrawCommand[] commands;
                    int commandCount;
                    if (presentation.Hud is not null)
                    {
                        if (hudPlayback.ConsumeTextureChanges())
                        {
                            var retained = new HashSet<uint>();
                            foreach (var texture in hudPlayback.Textures)
                            {
                                retained.Add(texture.Id);
                                if (uploaded.TryGetValue(texture.Id, out var previous) && previous == texture) continue;
                                device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                                uploaded[texture.Id] = texture;
                            }
                            foreach (var id in uploaded.Keys.Where(id => !retained.Contains(id)).ToArray())
                            {
                                device.RemoveTexture(id);
                                uploaded.Remove(id);
                            }
                        }
                        (commands, commandCount) = hudPlayback.BuildReusable(now);
                    }
                    else
                    {
                        commands = AnalogHudScene.Build(sample.Frame, sample.Angle, sample.Blur,
                            sample.NeedleVisible, presentation.TractionActive, presentation.TractionColor,
                            presentation.Layout, sample.AppliedRpm);
                        Transform(commands, presentation);
                        commandCount = commands.Length;
                    }
                    var sceneTicks = measure ? Stopwatch.GetTimestamp() - operationStarted : 0;
                    pending = new(sample, presentation, queuedTimestamp, ++sequence);
                    if (ShiftCaptureHub.Current is not null)
                        pending = pending.Value with
                        {
                            CaptureFrame = presentation.Hud is null ? sample.Frame :
                            hudPlayback.NeedleDiagnostics.Where(item => item.Kind == "analogue")
                                .Select(item => (NativeGaugeFrame?)item.Sample.Frame).FirstOrDefault()
                        };
                    device.DrawForPresentation(commands, commandCount, measure, out var drawMetrics);
                    RecordStage(operation, "ready", operationStarted, sceneTicks: sceneTicks,
                        drawCommands: commandCount, drawMetrics: drawMetrics);
                }
                // A busy Present has not consumed these pixels. Retry only the
                // submission; rebuilding here repeats GPU work and samples blur
                // for frames that were never submitted. New input still enters
                // playback above, ready for the next frame after success.
                // Invalidation can also happen during Draw. Do not submit old
                // native pixels after a session/source reset observed here.
                lock (_gate)
                {
                    if (_reset || !_presentation.Active || _motionGeneration != motionGeneration)
                    { pending = null; continue; }
                }
                var beforePresent = Stopwatch.GetTimestamp();
                var invalidatedBeforePresent = presentation.Hud is null
                    ? playback.RefreshNativeHistory(beforePresent)
                    : hudPlayback.RefreshNativeHistory(beforePresent);
                beforePresent = Stopwatch.GetTimestamp();
                if (invalidatedBeforePresent || !pending.Value.CanReuse(presentation,
                    presentation.Hud is null ? playback.CurrentFrame : latestFrame,
                    playback.HasNativeNeedle(beforePresent)) ||
                    (presentation.Hud is not null && !hudPlayback.CanReuse(beforePresent)))
                {
                    if (invalidatedBeforePresent && compositorActive && !HasCurrentMotion())
                    {
                        ClearNeedle();
                    }
                    pending = null;
                    continue;
                }
                BeginOperation("present");
                var shiftCapture = ShiftCaptureHub.Current;
                var captureStarted = shiftCapture is null ? 0 : Stopwatch.GetTimestamp();
                var submitted = device.TryPresent(measure, out var presentMetrics);
                if (pending.Value.CaptureFrame is { } drawnFrame)
                    shiftCapture?.RecordSubmission(_window, drawnFrame,
                        pending.Value.Sequence, pending.Value.QueuedTimestamp, captureStarted, submitted);
                RecordStage(operation, submitted ? "submitted" : device.LastRenderWasOccluded ? "occluded" : "busy",
                    operationStarted, presentMetrics: presentMetrics);
                if (!submitted)
                {
                    if (device.LastRenderWasOccluded)
                    {
                        frameReady = false;
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
                if (pending.Value.Presentation.Hud is { } submittedHud)
                {
                    if (TachDiagnostics.IsEnabled)
                        foreach (var diagnostic in hudPlayback.NeedleDiagnostics)
                            Record(diagnostic.Sample, diagnostic.Kind, compositorActive && diagnostic.Kind == "analogue"
                                ? "compositor_snapshot" : "directcomposition");
                    if (announcedHud is null || !submittedHud.CompatibleWith(announcedHud))
                    {
                        announcedHud = submittedHud;
                        HudPresented?.Invoke(submittedHud);
                    }
                }
                else Record(pending.Value.Sample, route: compositorActive ? "compositor_snapshot" : "directcomposition");
                pending = null;

                void ClearNeedle()
                {
                    BeginOperation("compositor_update");
                    device?.ClearCompositorNeedle();
                    RecordStage(operation, "discarded", operationStarted);
                    compositorActive = false;
                }

                bool HasCurrentMotion() => _motion?.Material is { } latest &&
                    latest.Generation == motionGeneration && Stopwatch.GetTimestamp() < latest.Curve.FreshUntilTimestamp;

                void BeginOperation(string stage)
                {
                    operation = stage;
                    operationStarted = measure ? Stopwatch.GetTimestamp() : 0;
                    waitMetrics = default;
                    measuredWaitCompleted = false;
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
            try
            {
                // Only this background render thread waits for channel teardown.
                // The UI remains free to service HWND/COM messages during release.
                _motion?.Dispose();
                _motion?.Completion.GetAwaiter().GetResult();
                device?.Dispose();
                Volatile.Write(ref _surfaceVisible, false);
                lock (_gate)
                {
                    _disposed = true;
                    _frames.Clear();
                    _hudFrames.Clear();
                    _changed.Dispose();
                    _stop.Dispose();
                }
            }
            finally { _completion.TrySetResult(); }
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
            var hasWaitDetails = stage == "frame_wait" && (measuredWaitCompleted || waitMetrics.TotalTicks > 0);
            var diagnostic = new TachRendererDiagnostic
            {
                ControlId = _controlId,
                NativeThreadId = nativeThreadId,
                CpuRendering = _cpuRendering,
                GpuPriority = device?.GpuPriority,
                HostWindowHandle = _window.ToInt64(),
                Sequence = pending?.Sequence ?? sequence + 1,
                Stage = stage,
                Result = result,
                StartedTimestamp = started,
                CompletedTimestamp = Stopwatch.GetTimestamp(),
                SampleTimestamp = stage == "compositor_update" ? compositorSampleTimestamp : pending?.Sample.Timestamp,
                ReceivedTimestamp = stage == "compositor_update" ? compositorSourceTimestamp : pending?.Sample.Frame.ReceivedTimestamp ?? latestFrame.ReceivedTimestamp,
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
                PacingWaitTicks = hasWaitDetails ? waitMetrics.PacingTicks : null,
                PresentationSyncInterval = hasWaitDetails ? waitMetrics.SyncInterval : null,
                PresentationRefreshRate = hasWaitDetails ? waitMetrics.RefreshRate : null,
                PacingHResult = hasWaitDetails ? waitMetrics.PacingHResult : null,
                PacingWaitReturnCode = hasWaitDetails ? waitMetrics.PacingWaitResult : null,
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

    private void Record(AnalogHudSample sample, string kind = "analogue", string route = "directcomposition")
    {
        if (!TachDiagnostics.IsEnabled) return;
        var diagnostic = new TachNeedleDiagnostic
        {
            ControlId = _controlId,
            ControlKind = kind,
            HostKind = nameof(OverlayWindow),
            HostWindowHandle = _window.ToInt64(),
            Route = route,
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
    internal NativeGaugeFrame? CaptureFrame { get; init; }
    internal bool CanReuse(AnalogHudPresentation presentation, NativeGaugeFrame frame, bool nativeNeedle) =>
        presentation.Hud is { } hud
            ? Presentation.Hud is { } previousHud && hud.CompatibleWith(previousHud)
            : Presentation.Hud is null && presentation.Active &&
        presentation.Width == Presentation.Width && presentation.Height == Presentation.Height &&
        presentation.OriginX == Presentation.OriginX && presentation.OriginY == Presentation.OriginY &&
        presentation.AxisXX == Presentation.AxisXX && presentation.AxisXY == Presentation.AxisXY &&
        presentation.AxisYX == Presentation.AxisYX && presentation.AxisYY == Presentation.AxisYY &&
        presentation.TractionActive == Presentation.TractionActive &&
        presentation.TractionColor == Presentation.TractionColor && presentation.Layout == Presentation.Layout &&
        frame.CarOrdinal == Sample.Frame.CarOrdinal && frame.IsElectric == Sample.Frame.IsElectric &&
        frame.NativeSourceIdentity == Sample.Frame.NativeSourceIdentity &&
        frame.Unit == Sample.Frame.Unit && frame.GearDisplayMode == Sample.Frame.GearDisplayMode &&
        nativeNeedle == Sample.Native &&
        frame.ExactRedline == Sample.Frame.ExactRedline &&
        frame.TachometerMaximumRpm.Equals(Sample.Frame.TachometerMaximumRpm) &&
        frame.ShiftCue.Appearance == Sample.Frame.ShiftCue.Appearance &&
        frame.NativeGaugeSourceInvalidated == Sample.Frame.NativeGaugeSourceInvalidated;
}
