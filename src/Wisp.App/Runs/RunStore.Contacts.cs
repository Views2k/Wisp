using System.IO;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class RunStore
{
    public Task<RunMarker[]> SetContactMarkerAsync(Guid id, double elapsedSeconds, bool marked) => InBackground(async () =>
    {
        var run = await ReadAsync(RunPath(id), id).ConfigureAwait(false);
        if (!double.IsFinite(elapsedSeconds) || run.Samples.Length == 0 ||
            elapsedSeconds < run.Samples[0].ElapsedSeconds || elapsedSeconds > run.Samples[^1].ElapsedSeconds)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Choose a recorded position in this run.");
        var existing = run.Markers.Where(marker => IsContactAt(marker, elapsedSeconds)).ToArray();
        if (marked && existing.Length > 0 || !marked && existing.Length == 0) return run.Markers;
        RunMarker[] markers;
        if (marked)
        {
            if (run.Markers.Length >= MaximumMarkers)
                throw new InvalidDataException("This run has reached its marker limit. Remove a marker before adding another.");
            markers = run.Markers.Append(new(elapsedSeconds, LapReviewContacts.ContactMarkerLabel))
                .OrderBy(marker => marker.ElapsedSeconds).ToArray();
        }
        else markers = run.Markers.Where(marker => !IsContactAt(marker, elapsedSeconds)).ToArray();
        // Read and change only the latest saved markers under the store gate. A
        // stale review window must not overwrite concurrent notes or tune changes.
        await WriteAsync(run with { Markers = markers }, overwrite: true).ConfigureAwait(false);
        return markers;
    });

    private static bool IsContactAt(RunMarker marker, double seconds) =>
        string.Equals(marker.Label.Trim(), LapReviewContacts.ContactMarkerLabel, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(marker.ElapsedSeconds - seconds) <= .0001;
}
