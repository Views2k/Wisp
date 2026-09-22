using System.IO;
using Wisp.Core;

namespace Wisp.App;

public sealed partial class AppController
{
    private ShiftCaptureSession? _shiftCapture;
    private string _shiftCaptureStatus = "No session started. Stay parked for the recorder check.";
    private Task<string?>? _shiftCaptureSaving;
    private bool _shiftCaptureStarting;
    internal bool ShiftCaptureActive => _shiftCapture is not null;
    internal bool ShiftCaptureStarting => _shiftCaptureStarting;
    internal bool ShiftCaptureSaving => _shiftCaptureSaving is not null;
    internal string ShiftCaptureStatus => ShiftCaptureSaving ? "Saving and verifying the session ZIP…" :
        _shiftCapture?.Status ?? _shiftCaptureStatus;

    internal async Task StartShiftCaptureAsync()
    {
        if (ShiftCaptureActive || ShiftCaptureSaving || ShiftCaptureStarting || !CanStartShiftCapture()) return;
        _shiftCaptureStarting = true;
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "ShiftCaptures");
        try
        {
            _shiftCaptureStatus = "Checking the recorder before arming…";
            var selfCheck = ShiftCaptureSelfCheck.Run();
            if (!selfCheck.Passed) { _shiftCaptureStatus = selfCheck.Status; return; }
            _shiftCaptureStatus = "Checking recording, ZIP packaging and file verification…";
            var storageCheck = await ShiftCaptureStorageCheck.RunAsync(parent);
            if (!storageCheck.Passed)
            {
                _shiftCaptureStatus = "Recording storage check failed (" + storageCheck.ErrorType + "). No session was armed. Check free disk space and Wisp folder access; keep the retained storage-check evidence.";
                return;
            }
            if (!CanStartShiftCapture()) return; // The app or settings may change during the disk check.
            var directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            _shiftCapture = new ShiftCaptureSession(_receiver, Settings, directory);
            _shiftCapture.Record("recorder_self_check", selfCheck);
            _shiftCapture.Record("storage_self_check", storageCheck);
            ShiftCaptureHub.Attach(_shiftCapture);
            _nativeHudProcessService.ShiftCueEnabled = true;
            _shiftCapture.RequestCanary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_shiftCapture is { } failed) _ = failed.StopAsync("start-failed");
            _shiftCapture = null;
            _shiftCaptureStatus = "Could not start. Stop any run recording and check that Wisp can save files, then retry.";
        }
        finally { _shiftCaptureStarting = false; }
    }

    private bool CanStartShiftCapture()
    {
        if (_disposed || _runtimeSuspended) return false;
        if (!_receiver.IsRunning || Settings.RequiresSetup)
        { _shiftCaptureStatus = "Complete the telemetry connection first."; return false; }
        if (!ViewModel.AccelerationShiftCueEnabled)
        { _shiftCaptureStatus = "Enable the performance shift cue before starting this check."; return false; }
        if (Settings.NativeGaugeMode != NativeGaugeMode.Analogue || Settings.LayoutMode != HudLayoutMode.Native)
        { _shiftCaptureStatus = "Choose the Native analogue HUD before starting this check."; return false; }
        if (ShiftCaptureHub.Current is not null)
        { _shiftCaptureStatus = "A diagnostic session is already active."; return false; }
        return true;
    }

    internal void CheckShiftCapture() => _shiftCapture?.RequestCanary();

    internal Task<string?> StopShiftCaptureAsync()
    {
        if (_shiftCaptureSaving is not null) return _shiftCaptureSaving;
        if (_shiftCapture is not { } session) return Task.FromResult<string?>(null);
        return _shiftCaptureSaving = SaveAsync(session);
    }

    private async Task<string?> SaveAsync(ShiftCaptureSession session)
    {
        // Ensure the task field is set before a synchronously completed save can clear it.
        await Task.Yield();
        try
        {
            var path = await session.StopAsync();
            _shiftCaptureStatus = session.SavedSummary;
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _shiftCaptureStatus = "ZIP could not be completed. Original evidence remains in Wisp's ShiftCaptures folder.";
            return null;
        }
        finally
        {
            _shiftCapture = null;
            _shiftCaptureSaving = null;
            _nativeHudProcessService.ShiftCueEnabled = Settings.AccelerationShiftCueEnabled;
        }
    }
}
