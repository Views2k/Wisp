using System.Diagnostics;
using Wisp.App.NativeRendering;
using Wisp.Core;

namespace Wisp.App.Drift;

internal sealed class DriftGaugeRenderWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEvent _interrupted = new(false);
    private readonly IntPtr _window;
    private readonly Func<VehicleState?> _latest;
    private readonly Func<DriftZoneScoringProfile?>? _zoneProfile;
    private readonly Action<bool, int> _status;
    private readonly bool _cpuRendering;
    private readonly IReadOnlyList<AnalogHudTexture> _textures;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DriftGaugePresentation _presentation;
    private bool _disposed;
    internal Task Completion => _completion.Task;

    internal DriftGaugeRenderWorker(IntPtr window, Func<VehicleState?> latest, DriftGaugePresentation presentation,
        Action<bool, int> status, bool cpuRendering = false, Func<DriftZoneScoringProfile?>? zoneProfile = null)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(status);
        _window = window; _latest = latest; _presentation = presentation; _status = status; _cpuRendering = cpuRendering;
        _zoneProfile = zoneProfile;
        _textures = DriftGaugeVisuals.LoadOnUiThread();
        _thread = new Thread(Run) { IsBackground = true, Name = "Wisp drift gauge rendering" };
        _thread.Start();
    }

    internal void UpdatePresentation(DriftGaugePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        lock (_gate)
        {
            if (_disposed || presentation == _presentation) return;
            _presentation = presentation;
            _interrupted.Set();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _interrupted.Set();
        }
        // Device release can involve the HWND thread. Never join this worker from that thread.
    }

    internal static DriftAngleReading ReadFresh(VehicleState? state, double targetDegrees, double toleranceDegrees,
        long nowTimestamp, DateTimeOffset nowUtc, DriftGaugeGuidanceMode guidanceMode = DriftGaugeGuidanceMode.CustomTarget,
        DriftZoneScoringProfile? zoneProfile = null)
    {
        var unavailable = new DriftAngleReading(null, DriftGuidanceState.Unavailable)
        { IsZoneGuidance = guidanceMode == DriftGaugeGuidanceMode.DriftZoneAngleBonus };
        if (state is null) return unavailable;
        var age = state.ReceivedTimestamp is { } received && received > 0
            ? Stopwatch.GetElapsedTime(received, nowTimestamp)
            : nowUtc - state.ReceivedAtUtc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromMilliseconds(250))
            return unavailable;
        if (guidanceMode != DriftGaugeGuidanceMode.DriftZoneAngleBonus)
            return DriftAngle.Measure(state, targetDegrees, toleranceDegrees);
        if (DriftAngle.GetMovementRestriction(state) is { } restriction)
            return unavailable with { State = restriction };
        return DriftZoneScoring.Measure(state, zoneProfile);
    }

    private void Run()
    {
        DirectCompositionDevice? device = null;
        var commands = new DirectCompositionDrawCommand[DriftGaugeVisuals.MaximumCommands];
        var width = 0; var height = 0;
        var active = false; var ready = false; var announced = false; var pending = false;
        var opacity = float.NaN;
        long pendingAt = 0;
        DriftAngleReading? displayedReading = null;
        DriftAngleReading pendingReading = default;
        DriftGaugePresentation? displayedPresentation = null, pendingPresentation = null;
        DriftGaugePresentation? previousInput = null, effectivePresentation = null;
        try
        {
            while (true)
            {
                DriftGaugePresentation presentation;
                lock (_gate)
                {
                    if (_disposed) break;
                    presentation = _presentation;
                    _interrupted.Reset();
                }
                if (!presentation.Active)
                {
                    if (device is not null && active) device.SetVisible(false);
                    active = false; pending = false; displayedReading = null; displayedPresentation = null;
                    _interrupted.WaitOne();
                    continue;
                }
                if (device is null)
                {
                    device = DirectCompositionDevice.Create(_window, presentation.Width, presentation.Height, _cpuRendering);
                    foreach (var texture in _textures)
                        device.UploadTexture(texture.Id, texture.Width, texture.Height, texture.Stride, texture.Pixels.ToArray());
                    width = presentation.Width; height = presentation.Height;
                }
                if (width != presentation.Width || height != presentation.Height)
                {
                    device.Resize(presentation.Width, presentation.Height);
                    width = presentation.Width; height = presentation.Height;
                    pending = false; displayedReading = null;
                }
                if (!active)
                {
                    if (device.PrepareForResume()) ready = false;
                    pending = false;
                }
                if (!active || opacity != presentation.Opacity)
                {
                    device.SetOpacity(Math.Clamp(presentation.Opacity, 0, 1));
                    device.SetVisible(true);
                    opacity = presentation.Opacity;
                }
                active = true;
                if (!ready)
                {
                    var wait = device.WaitForNextFrame(100, _interrupted.SafeWaitHandle);
                    if (wait != DirectCompositionWaitResult.Ready) continue;
                    ready = true;
                }
                lock (_gate)
                {
                    if (_disposed) break;
                    if (_presentation != presentation) continue;
                }
                var inputPresentation = presentation;
                var profile = presentation.GuidanceMode == DriftGaugeGuidanceMode.DriftZoneAngleBonus ? _zoneProfile?.Invoke() : null;
                if (!ReferenceEquals(previousInput, inputPresentation) || effectivePresentation?.ZoneProfile != profile)
                {
                    previousInput = inputPresentation;
                    effectivePresentation = inputPresentation.ZoneProfile == profile
                        ? inputPresentation : inputPresentation with { ZoneProfile = profile };
                }
                presentation = effectivePresentation!;
                var state = _latest();
                var now = Stopwatch.GetTimestamp();
                var reading = ReadFresh(state, presentation.TargetDegrees, presentation.ToleranceDegrees, now, DateTimeOffset.UtcNow,
                    presentation.GuidanceMode, presentation.ZoneProfile);
                if (!pending && reading == displayedReading && presentation == displayedPresentation)
                {
                    // Keep readiness until a fresh reading changes the image. No redraw or present while stationary.
                    _interrupted.WaitOne(8);
                    continue;
                }
                if (pending && (pendingPresentation != presentation ||
                    reading.SignedDegrees is null && reading.MagnitudeDegrees is null &&
                        (pendingReading.SignedDegrees is not null || pendingReading.MagnitudeDegrees is not null) ||
                    reading != pendingReading && Stopwatch.GetElapsedTime(pendingAt, now) > TimeSpan.FromMilliseconds(32)))
                    pending = false;
                if (!pending)
                {
                    var count = DriftGaugeVisuals.Build(commands, presentation, reading);
                    device.DrawForPresentation(commands, count, false, out _);
                    pendingReading = reading; pendingPresentation = presentation; pendingAt = now; pending = true;
                }
                if (!device.TryPresent(false, out _))
                {
                    if (device.LastRenderWasOccluded)
                    {
                        pending = false; displayedReading = null;
                        _interrupted.WaitOne(100);
                    }
                    else _interrupted.WaitOne(1);
                    continue;
                }
                displayedReading = pendingReading; displayedPresentation = pendingPresentation;
                pending = false; ready = false;
                if (!announced)
                {
                    announced = true;
                    _status(true, 0);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _status(false, exception.HResult);
        }
        finally
        {
            lock (_gate) _disposed = true;
            try { device?.Dispose(); }
            finally
            {
                _interrupted.Dispose();
                _completion.TrySetResult();
            }
        }
    }
}
