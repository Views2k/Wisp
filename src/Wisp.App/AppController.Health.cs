using System.ComponentModel;
using Wisp.App.Clips;
using Wisp.App.DebugLogging;
using Wisp.App.Tunes;

namespace Wisp.App;

public sealed partial class AppController
{
    private bool _healthContextAttached, _healthClipSelected, _healthClipExporting;
    private int _healthRecorderState = -1;

    private void InitializeHealthContext()
    {
        if (_healthContextAttached) return;
        _healthContextAttached = true;
        _clipRecorder.StateChanged += RecordHealthRecorderState;
        Clips.PropertyChanged += RecordHealthClipState;
        Tunes.PropertyChanged += RecordHealthTuneState;
        HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.ApplicationStarted);
        RecordHealthRecorderState(null, EventArgs.Empty);
    }

    private void DisposeHealthContext()
    {
        if (!_healthContextAttached) return;
        _healthContextAttached = false;
        _clipRecorder.StateChanged -= RecordHealthRecorderState;
        Clips.PropertyChanged -= RecordHealthClipState;
        Tunes.PropertyChanged -= RecordHealthTuneState;
        HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.ApplicationStopping);
    }

    private void RecordHealthRecorderState(object? sender, EventArgs args)
    {
        if (_disposed || !_healthContextAttached) return;
        var state = _clipRecorder.Snapshot.State;
        if (Interlocked.Exchange(ref _healthRecorderState, (int)state) != (int)state)
            HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.RecorderChanged, recorderState: state);
    }

    private void RecordHealthClipState(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || !_healthContextAttached) return;
        if (args.PropertyName == nameof(ClipsViewModel.HasSelection) && _healthClipSelected != Clips.HasSelection)
        {
            _healthClipSelected = Clips.HasSelection;
            HealthContextRecorder.Current.RecordBreadcrumb(_healthClipSelected ? HealthEventCode.ClipSelected : HealthEventCode.ClipClosed);
        }
        else if (args.PropertyName == nameof(ClipsViewModel.IsExporting) && _healthClipExporting != Clips.IsExporting)
        {
            _healthClipExporting = Clips.IsExporting;
            HealthContextRecorder.Current.RecordBreadcrumb(_healthClipExporting ? HealthEventCode.ClipExportStarted : HealthEventCode.ClipExportFinished);
        }
        else if (args.PropertyName == nameof(ClipsViewModel.HasError) && Clips.HasError ||
                 args.PropertyName == nameof(ClipsViewModel.HasExportFailureDetails) && Clips.HasExportFailureDetails)
            HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.ClipActionFailed);
    }

    private void RecordHealthTuneState(object? sender, PropertyChangedEventArgs args)
    {
        if (!_disposed && _healthContextAttached && args.PropertyName == nameof(TuneViewModel.HasFailureDetails) && Tunes.HasFailureDetails)
            HealthContextRecorder.Current.RecordBreadcrumb(HealthEventCode.TuneReadFailed);
    }
}
