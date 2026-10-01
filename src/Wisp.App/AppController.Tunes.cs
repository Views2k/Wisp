using System.IO;
using Wisp.App.Runs;
using Wisp.App.Tunes;
using Wisp.Core.Runs;
using Wisp.Core.Tunes;

namespace Wisp.App;

public sealed partial class AppController
{
    private TuneCaptureService? _tuneCapture;
    private TuneStore _tuneStore = null!;
    private int _observedTuneCar;
    private bool? _observedTuneGameRunning;
    public TuneViewModel Tunes { get; private set; } = null!;

    private void InitializeTunes(string? directory, bool enableCapture)
    {
        // Preview and test hosts have an isolated library and never open the game.
        if (enableCapture) _tuneCapture = new TuneCaptureService();
        directory ??= Path.Combine(Path.GetTempPath(), "Wisp", "TuneReview", Guid.NewGuid().ToString("N"));
        _tuneStore = new TuneStore(directory);
        Tunes = new TuneViewModel(_tuneStore, CaptureTuneAsync,
            snapshot => _tuneCapture?.IsCurrent(snapshot) == true, _dispatcher);
        if (_tuneCapture is not null) _tuneCapture.Invalidated += TuneCaptureInvalidated;
        Runs.LoadTuneChoices = async token => (await _tuneStore.ListAsync(token)).Select(saved =>
            new RunTuneChoice($"{saved.Name} · Car {saved.Snapshot.Identity.CarOrdinal} · {saved.SavedAtUtc.ToLocalTime():g}", SavedTuneId: saved.Id)).ToArray();
        Runs.PrepareTune = PrepareRunTuneAsync;
        Runs.ValidatePreparedTune = attachment => _tuneCapture?.IsCurrent(attachment.Snapshot) == true;
        Runs.AttachedTuneRequested += OpenAttachedTune;
        Runs.AttachedTuneComparisonRequested += CompareAttachedTunes;
    }

    private Task<TuneCaptureResult> CaptureTuneAsync(CancellationToken token) => _tuneCapture is { } capture
        ? capture.RequestSnapshotAsync(token)
        : Task.FromResult(new TuneCaptureResult(null, TuneCaptureStatus.Unavailable,
            "Current-car reading is unavailable in this preview. Saved tunes can still be opened."));

    private async Task<RunTunePreparation> PrepareRunTuneAsync(RunTuneChoice choice, CancellationToken token)
    {
        SavedTune? saved = choice.SavedTuneId is { } id ? await _tuneStore.LoadAsync(id, token) : null;
        var result = await CaptureTuneAsync(token);
        token.ThrowIfCancellationRequested();
        if (result.Snapshot is not { IsComplete: true } snapshot || _tuneCapture?.IsCurrent(snapshot) != true)
            return new(null, result.Message.Length > 0 ? result.Message + " Record without a tune or try again."
                : "The current tune could not be verified. Record without a tune or try again.");
        if (saved is not null && !TuneComparison.HaveSameSetupIdentity(saved.Snapshot, snapshot))
            return new(null, "The selected saved tune does not match this car's current setup. Choose another tune, retry, or record without a tune.");
        var attachment = new RunTuneAttachment(snapshot, saved?.Name ?? "Current car", saved?.Description ?? "",
            DateTimeOffset.UtcNow, saved is null ? RunTuneAttachmentKind.CurrentAtStart : RunTuneAttachmentKind.SavedMatchedAtStart,
            saved?.Id);
        return new(attachment);
    }

    private void OpenAttachedTune(RunTuneAttachment attachment) =>
        ControlPanel?.OpenTuneSnapshot(attachment.Snapshot, attachment.Name, attachment.Description);

    private void CompareAttachedTunes(RunTuneAttachment a, RunTuneAttachment? b) =>
        ControlPanel?.OpenTuneComparison(a.Snapshot, a.Name, a.Description, b?.Snapshot, b?.Name ?? "", b?.Description ?? "");

    private void TuneCaptureInvalidated(object? sender, EventArgs e) => Tunes.InvalidateCurrent(invalidateRefresh: false);

    private void ObserveTuneCar(int carOrdinal)
    {
        if (carOrdinal <= 0 || carOrdinal == _observedTuneCar) return;
        var hadCar = _observedTuneCar > 0;
        _observedTuneCar = carOrdinal;
        if (hadCar) _tuneCapture?.Invalidate();
    }

    private void ObserveTuneGame(bool running)
    {
        if (_observedTuneGameRunning == running) return;
        var wasRunning = _observedTuneGameRunning;
        _observedTuneGameRunning = running;
        if (!running && wasRunning is not false) _tuneCapture?.Invalidate();
    }

    private async ValueTask DisposeTunesAsync()
    {
        Runs.AttachedTuneRequested -= OpenAttachedTune;
        Runs.AttachedTuneComparisonRequested -= CompareAttachedTunes;
        Tunes.Dispose();
        if (_tuneCapture is not { } capture) return;
        capture.Invalidated -= TuneCaptureInvalidated;
        await capture.DisposeAsync();
    }
}
