using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App.DebugLogging;

namespace Wisp.App.NativeRendering;

internal sealed record CompositorNeedleMaterial(long Generation, CompositorNeedleGeometry Geometry,
    CompositorNeedleCurve Curve, CompositorNeedlePoint Point);

// Owns playback and the GPU-free DComp motion channel. Publications reach this
// mailbox directly, even when the separate bitmap worker is blocked in a driver.
internal sealed class CompositorNeedleMotionWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Input> _inputs = new(64);
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _changed = new(false);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly INativeNeedleHistorySource? _nativeSource;
    private readonly Action<int> _failed;
    private readonly IntPtr _window;
    private readonly int _controlId;
    private AnalogHudPresentation _publishedPresentation;
    private NativeGaugeFrame? _publishedFrame;
    private DirectCompositionDevice.MotionChannel? _channel;
    private CompositorNeedleMaterial? _material;
    private long _generation = 1;
    private bool _disposed;

    private readonly record struct Input(AnalogHudPresentation Presentation, NativeGaugeFrame? Frame,
        long Timestamp, long Generation, bool Reset);

    internal Task Completion => _completion.Task;
    internal CompositorNeedleMaterial? Material => Volatile.Read(ref _material);

    internal CompositorNeedleMotionWorker(IntPtr window, int controlId, AnalogHudPresentation presentation,
        INativeNeedleHistorySource? nativeSource, Action<int> failed)
    {
        _window = window;
        _controlId = controlId;
        _publishedPresentation = presentation;
        _nativeSource = nativeSource;
        _failed = failed;
        new Thread(Run) { IsBackground = true, Name = "Wisp needle motion" }.Start();
    }

    internal long Publish(AnalogHudPresentation presentation, NativeGaugeFrame? frame, long timestamp)
    {
        lock (_gate)
        {
            if (_disposed) return _generation;
            var reset = !presentation.Active || presentation.Active != _publishedPresentation.Active;
            var changed = !CompatiblePresentation(presentation, _publishedPresentation);
            var inputFrame = frame ?? presentation.Hud?.Frame;
            if (inputFrame is { } incoming && _publishedFrame is { } previous && !CompatibleFrame(incoming, previous))
            { changed = true; reset = true; }
            if (_inputs.Count == 64)
            { _inputs.Clear(); reset = true; changed = true; }
            if (changed) _generation++;
            _publishedPresentation = presentation;
            if (inputFrame is { } latest) _publishedFrame = latest;
            _inputs.Enqueue(new(presentation, frame, timestamp, _generation, reset));
            _changed.Set();
            return _generation;
        }
    }

    internal void Attach(DirectCompositionDevice.MotionChannel channel)
    {
        lock (_gate)
        {
            if (_disposed) { channel.Dispose(); return; }
            if (_channel is not null) throw new InvalidOperationException("The motion channel is already attached.");
            _channel = channel;
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
    }

    private void Run()
    {
        var playback = new AnalogHudPlayback();
        playback.SetNativeSource(_nativeSource);
        var hudPlayback = new HudScenePlayback(_nativeSource);
        var batch = new Input[64];
        var points = new CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        var waits = new WaitHandle[] { _stop, _changed };
        var interval = Math.Max(1, Stopwatch.Frequency / 250);
        var nativeThread = GetCurrentThreadId();
        var presentation = _publishedPresentation;
        DirectCompositionDevice.MotionChannel? channel = null;
        long generation = 1, nextUpdate = 0, sequence = 0;
        bool hasFrame = false, committed = false;
        try
        {
            while (!_stop.WaitOne(0))
            {
                var count = 0;
                lock (_gate)
                {
                    channel = _channel;
                    while (_inputs.Count > 0) batch[count++] = _inputs.Dequeue();
                }
                for (var index = 0; index < count; index++)
                {
                    var input = batch[index];
                    if (input.Generation != generation || input.Reset)
                    {
                        Clear();
                        nextUpdate = 0;
                    }
                    generation = input.Generation;
                    presentation = input.Presentation;
                    if (input.Reset)
                    { playback.Reset(); hudPlayback.Reset(); hasFrame = false; }
                    if (presentation.Hud is { } hud)
                    {
                        hudPlayback.Update(hud, input.Timestamp);
                        hasFrame = true;
                    }
                    else if (input.Frame is { } frame)
                    {
                        playback.ObserveQueued(frame, input.Timestamp, Stopwatch.GetTimestamp());
                        hasFrame = true;
                    }
                }
                var eligible = presentation.Active && hasFrame && (presentation.Hud is null
                    ? !playback.CurrentFrame.IsElectric : hudPlayback.SupportsCompositorNeedle);
                if (!eligible || channel is null)
                {
                    Clear();
                    WaitHandle.WaitAny(waits);
                    continue;
                }
                var now = Stopwatch.GetTimestamp();
                if (now >= nextUpdate)
                {
                    var invalidated = presentation.Hud is null
                        ? playback.RefreshNativeHistory(now) : hudPlayback.RefreshNativeHistory(now);
                    if (invalidated) Clear();
                    now = Stopwatch.GetTimestamp();
                    CompositorNeedleCurve curve;
                    CompositorNeedleGeometry geometry;
                    bool copied;
                    if (presentation.Hud is not null)
                        copied = hudPlayback.TryCopyCompositorNeedle(now, points, out curve, out geometry);
                    else
                    {
                        copied = playback.TryCopyCompositorCurve(now, points, out curve);
                        geometry = CompositorNeedleGeometry.Create(presentation.Layout ?? AnalogHudLayout.Authored);
                        geometry.Place(presentation.OriginX, presentation.OriginY, presentation.AxisXX,
                            presentation.AxisXY, presentation.AxisYX, presentation.AxisYY, 1);
                    }
                    if (copied)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var accepted = channel.Update(in geometry, curve, points, generation, out var status);
                        if (!accepted) throw new InvalidOperationException("Independent needle motion became unavailable.");
                        committed = true;
                        Volatile.Write(ref _material, new(generation, geometry, curve, points[0]));
                        Record("ready", started, curve, status);
                    }
                    else Clear();
                    nextUpdate = Stopwatch.GetTimestamp() + interval;
                }
                // A publication wake never postpones the scheduled service. A
                // positive timeout avoids turning sub-millisecond gaps into a spin.
                var delay = Math.Clamp((int)Math.Ceiling((nextUpdate - Stopwatch.GetTimestamp()) * 1_000d / Stopwatch.Frequency), 1, 100);
                WaitHandle.WaitAny(waits, delay);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Record("error", Stopwatch.GetTimestamp(), default, default, error.HResult);
            if (!_stop.WaitOne(0)) _failed(error.HResult);
        }
        finally
        {
            try
            {
                Volatile.Write(ref _material, null);
                channel?.Dispose();
                lock (_gate)
                {
                    // Attach may have arrived after the last mailbox drain.
                    if (!ReferenceEquals(channel, _channel)) _channel?.Dispose();
                    _disposed = true;
                    _inputs.Clear();
                    _changed.Dispose();
                    _stop.Dispose();
                }
            }
            finally { _completion.TrySetResult(); }
        }

        void Clear()
        {
            Volatile.Write(ref _material, null);
            if (!committed || channel is null) return;
            var started = Stopwatch.GetTimestamp();
            channel.Clear();
            committed = false;
            Record("discarded", started, default, default);
        }

        void Record(string result, long started, CompositorNeedleCurve curve, CompositorMotionStatus status, int error = 0)
        {
            if (!TachDiagnostics.IsEnabled) return;
            var row = new TachRendererDiagnostic
            {
                ControlId = _controlId,
                CpuRendering = false,
                NativeThreadId = nativeThread,
                HostWindowHandle = _window.ToInt64(),
                Sequence = ++sequence,
                Stage = "compositor_motion",
                Result = result,
                StartedTimestamp = started,
                CompletedTimestamp = Stopwatch.GetTimestamp(),
                SampleTimestamp = curve.StartTimestamp > 0 ? curve.StartTimestamp : null,
                ReceivedTimestamp = curve.LatestObservationTimestamp > 0 ? curve.LatestObservationTimestamp : null,
                CurveEndTimestamp = curve.EndTimestamp > 0 ? curve.EndTimestamp : null,
                CompositorCommitTimestamp = status.CommitTimestamp > 0 ? status.CommitTimestamp : null,
                MotionGeneration = generation,
                MotionGeometryAccepted = status.GeometryAccepted != 0,
                HResult = error
            };
            TachDiagnostics.RecordRenderer(in row);
        }
    }

    private static bool CompatiblePresentation(AnalogHudPresentation current, AnalogHudPresentation previous) =>
        current.Hud is { } hud ? previous.Hud is { } old && hud.CompatibleWith(old) :
        previous.Hud is null && current == previous;

    private static bool CompatibleFrame(NativeGaugeFrame current, NativeGaugeFrame previous) =>
        current.CarOrdinal == previous.CarOrdinal && current.NativeSourceIdentity == previous.NativeSourceIdentity &&
        current.IsElectric == previous.IsElectric && current.Unit == previous.Unit &&
        current.GearDisplayMode == previous.GearDisplayMode && current.ExactRedline == previous.ExactRedline &&
        current.TachometerMaximumRpm.Equals(previous.TachometerMaximumRpm) &&
        current.NativeGaugeSourceInvalidated == previous.NativeGaugeSourceInvalidated;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
