using System.Windows.Input;
using Wisp.App.Tunes;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed record RunTuneChoice(string Label, bool CurrentCar = false, Guid? SavedTuneId = null)
{
    public static RunTuneChoice None { get; } = new("None");
    public static RunTuneChoice Current { get; } = new("Current car", CurrentCar: true);
}
public sealed record RunTunePreparation(RunTuneAttachment? Attachment, string? Error = null);

public sealed partial class RunsViewModel
{
    private RunTuneChoice _attachTuneChoice = RunTuneChoice.None;
    private CancellationTokenSource? _tunePreparation;
    private long _tunePreparationRevision, _tuneChoicesRevision;
    private bool _tunePreparationFailed;
    private bool _recordWithoutTuneOnce;
    private ICommand? _recordWithoutTuneCommand, _viewAttachedTuneCommand, _compareAttachedTuneCommand;
    public Func<CancellationToken, Task<IReadOnlyList<RunTuneChoice>>>? LoadTuneChoices { get; set; }
    public Func<RunTuneChoice, CancellationToken, Task<RunTunePreparation>>? PrepareTune { get; set; }
    public Func<RunTuneAttachment, bool>? ValidatePreparedTune { get; set; }
    public event Action<RunTuneAttachment>? AttachedTuneRequested;
    public event Action<RunTuneAttachment, RunTuneAttachment?>? AttachedTuneComparisonRequested;
    public System.Collections.ObjectModel.ObservableCollection<RunTuneChoice> TuneChoices { get; } = [RunTuneChoice.None, RunTuneChoice.Current];
    public RunTuneChoice AttachTuneChoice
    {
        get => _attachTuneChoice;
        set
        {
            if (!CanEditRecordingOptions || value is null || !TuneChoices.Contains(value)) return;
            if (Set(ref _attachTuneChoice, value)) { _tunePreparationFailed = false; OnChanged(nameof(CanRecordWithoutTune)); RaiseCommands(); }
        }
    }
    public bool IsPreparingTune => _tunePreparation is not null;
    public bool CanRecordWithoutTune => _tunePreparationFailed && !RecordingActive && !_metadataClosing && _storageOperations == 0 && _service.CanStart;
    public ICommand RecordWithoutTuneCommand => _recordWithoutTuneCommand ??= Command(async () =>
    {
        _recordWithoutTuneOnce = true;
        await ToggleRecordingAsync();
        if (!IsCountingDown) _recordWithoutTuneOnce = false;
    }, () => CanRecordWithoutTune);
    public string AttachedTuneName => _runA?.TuneAttachment?.Name ?? "";
    public string AttachedTuneDetail => _runA?.TuneAttachment is { } attachment
        ? $"{TunePresentation.Car(attachment.Snapshot)} · " +
            (attachment.Kind == RunTuneAttachmentKind.SavedMatchedAtStart ? "Saved tuning values matched at start · Other upgrades not checked" : "Tune captured at start") +
            (attachment.DrivingContinuityInterrupted ? " · Driving was interrupted" : "")
        : "";
    public bool HasAttachedTune => _runA?.TuneAttachment is not null;
    public ICommand ViewAttachedTuneCommand => _viewAttachedTuneCommand ??= Command(() =>
    {
        if (_runA?.TuneAttachment is { } attachment) AttachedTuneRequested?.Invoke(attachment);
        return Task.CompletedTask;
    }, () => HasAttachedTune && !RecordingActive);
    public ICommand CompareAttachedTuneCommand => _compareAttachedTuneCommand ??= Command(() =>
    {
        if (_runA?.TuneAttachment is { } attachment) AttachedTuneComparisonRequested?.Invoke(attachment, _runB?.TuneAttachment);
        return Task.CompletedTask;
    }, () => HasAttachedTune && !RecordingActive);

    private async Task RefreshTuneChoicesAsync()
    {
        var loader = LoadTuneChoices;
        if (loader is null || _disposed || RecordingActive) return;
        var revision = ++_tuneChoicesRevision;
        try
        {
            var choices = await loader(CancellationToken.None);
            if (_disposed || revision != _tuneChoicesRevision || RecordingActive) return;
            var selected = _attachTuneChoice;
            TuneChoices.Clear(); TuneChoices.Add(RunTuneChoice.None); TuneChoices.Add(RunTuneChoice.Current);
            foreach (var choice in choices.Where(item => item.SavedTuneId is not null)) TuneChoices.Add(choice);
            _attachTuneChoice = TuneChoices.FirstOrDefault(item => item.SavedTuneId == selected.SavedTuneId && item.CurrentCar == selected.CurrentCar)
                ?? RunTuneChoice.None;
            OnChanged(nameof(AttachTuneChoice));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { if (!_disposed) Error = "Saved tunes could not be read. Recording without a tune is still available."; }
    }

    private void CancelTunePreparation(string reason)
    {
        var pending = _tunePreparation;
        if (pending is null) return;
        _tunePreparation = null; ++_tunePreparationRevision; pending.Cancel();
        _recordingNotice = reason; RefreshStatus();
    }

    private async Task PrepareTuneAndStartAsync(RunTuneChoice choice, CancellationTokenSource cancellation, long revision)
    {
        try
        {
            RunTuneAttachment? attachment = null;
            var reason = "The tune could not be verified.";
            try
            {
                var result = PrepareTune is { } prepare ? await prepare(choice, cancellation.Token)
                    : new RunTunePreparation(null, "Tune reading is unavailable.");
                if (_disposed || cancellation.IsCancellationRequested || revision != _tunePreparationRevision || _metadataClosing) return;
                if (result.Error is null && result.Attachment is { } prepared && ValidatePreparedTune?.Invoke(prepared) == true)
                    attachment = prepared;
                else reason = result.Error ?? "The car or tune changed during the check.";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { reason = "The tune could not be read."; }
            if (_disposed || cancellation.IsCancellationRequested || revision != _tunePreparationRevision || _metadataClosing) return;

            BeforeStart?.Invoke();
            var started = _service.Start(new RunRecordingOptions(_activeStopAfter, attachment));
            // Start rechecks the current car and attachment age under its lock.
            // A rejected attachment must not block an otherwise available run.
            // A failed start has no active session; the ordinary start retains
            // every telemetry, scene, storage and duration check.
            if (!started && attachment is not null && _service.Error is null && _service.CanStart)
            {
                attachment = null;
                reason = "The car or tune changed before recording started.";
                started = _service.Start(new RunRecordingOptions(_activeStopAfter));
            }
            _tunePreparationFailed = !started && attachment is null;
            if (!started) Error = _service.Error ?? _service.Status;
            else if (attachment is null)
            {
                _recordingNotice = "No tune attached";
                Error = "Recording started without a tune. " + reason;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed && revision == _tunePreparationRevision)
            { _tunePreparationFailed = true; Error = "The recording could not start. Check live telemetry and local storage, then try again."; }
        }
        finally
        {
            if (ReferenceEquals(_tunePreparation, cancellation)) _tunePreparation = null;
            cancellation.Dispose();
            if (!_disposed) RefreshStatus();
        }
    }

    private void NotifyTuneRecording()
    {
        foreach (var name in new[] { nameof(IsPreparingTune), nameof(CanRecordWithoutTune), nameof(HasAttachedTune),
                     nameof(AttachedTuneName), nameof(AttachedTuneDetail) }) OnChanged(name);
    }
}
