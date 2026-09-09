using System.Diagnostics;
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
    private readonly IReadOnlyList<AnalogHudTexture> _textures;
    private readonly Action<bool, int> _status;
    private readonly Thread _thread;
    private AnalogHudPresentation _presentation;
    private bool _reset;
    private bool _disposed;

    internal AnalogHudRenderWorker(IntPtr window, int controlId, IReadOnlyList<AnalogHudTexture> textures,
        AnalogHudPresentation presentation, Action<bool, int> status)
    {
        _window = window;
        _controlId = controlId;
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
        DirectCompositionDevice? device = null;
        var playback = new AnalogHudPlayback();
        var batch = new (NativeGaugeFrame Frame, long Timestamp)[64];
        var waitHandles = new WaitHandle[] { _stop, _changed };
        bool announced = false, hasFrame = false, wasActive = false, frameReady = false;
        int width = 0, height = 0;
        try
        {
            while (!_stop.WaitOne(0))
            {
                AnalogHudPresentation presentation;
                int count = 0;
                lock (_gate)
                {
                    presentation = _presentation;
                    if (_reset)
                    {
                        playback.Reset();
                        hasFrame = false;
                        _reset = false;
                    }
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                }
                if (!presentation.Active)
                {
                    if (device is not null && wasActive)
                    {
                        // Clear the independent surface before the parent can
                        // be shown again in a different layout.
                        device.SetVisible(false);
                    }
                    wasActive = false;
                    playback.Reset();
                    hasFrame = false;
                    WaitHandle.WaitAny(waitHandles);
                    continue;
                }
                for (int i = 0; i < count; i++)
                {
                    playback.Observe(batch[i].Frame, batch[i].Timestamp);
                    hasFrame = true;
                }
                if (!hasFrame)
                {
                    WaitHandle.WaitAny(waitHandles);
                    continue;
                }
                if (device is null)
                {
                    device = DirectCompositionDevice.Create(_window, presentation.Width, presentation.Height);
                    foreach (var texture in _textures)
                        device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                    width = presentation.Width;
                    height = presentation.Height;
                }
                if (width != presentation.Width || height != presentation.Height)
                {
                    device.Resize(presentation.Width, presentation.Height);
                    width = presentation.Width;
                    height = presentation.Height;
                }
                // A previously hidden surface can still contain the old car.
                // Resume with a transparent swapchain before making it visible.
                if (!wasActive && device.PrepareForResume()) frameReady = false;
                device.SetOpacity(presentation.Opacity);
                device.SetVisible(true);
                wasActive = true;
                if (!frameReady)
                {
                    var ready = device.WaitForNextFrame(100, _stop.SafeWaitHandle);
                    if (ready == DirectCompositionWaitResult.Cancelled) break;
                    if (ready == DirectCompositionWaitResult.Timeout) continue;
                    frameReady = true;
                }

                // A layout change or playback reset can restart this iteration
                // after the frame wait. Keep its readiness until Present succeeds;
                // waiting again without presenting can stall the swapchain.

                // Fresh samples received while awaiting the swapchain belong
                // to this frame; never render an earlier queued snapshot.
                lock (_gate)
                {
                    if (_reset || !_presentation.Active) continue;
                    presentation = _presentation;
                    count = 0;
                    while (_frames.Count > 0) batch[count++] = _frames.Dequeue();
                }
                for (int i = 0; i < count; i++) playback.Observe(batch[i].Frame, batch[i].Timestamp);
                if (width != presentation.Width || height != presentation.Height) continue;
                var sample = playback.Sample(Stopwatch.GetTimestamp());
                var commands = AnalogHudScene.Build(sample.Frame, sample.Angle, sample.Blur,
                    sample.NeedleVisible, presentation.TractionActive, presentation.TractionColor,
                    presentation.Layout, sample.AppliedRpm);
                Transform(commands, presentation);
                if (!device.RenderPresent(commands, commands.Length))
                {
                    if (device.LastRenderWasOccluded)
                    {
                        // Initialization succeeded even when Windows cannot show
                        // this HWND. Do not count this as a submitted needle frame.
                        AnnounceReady();
                        _stop.WaitOne(100);
                        continue;
                    }
                    // DO_NOT_WAIT can race another compositor consumer. Yield
                    // to cancellation instead of spinning or accumulating work.
                    _stop.WaitOne(1);
                    continue;
                }
                frameReady = false;
                AnnounceReady();
                Record(sample);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
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
    }

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
