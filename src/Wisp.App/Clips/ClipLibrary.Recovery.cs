using System.IO;

namespace Wisp.App.Clips;

public sealed record ClipPendingRecoveryResult(int Recovered, int RemovedEmpty, int Remaining, bool ScanIncomplete);

public sealed partial class ClipLibrary
{
    public Task DismissPendingNoticesAsync(CancellationToken cancellationToken = default) =>
        InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            if (index.Pending.All(item => item.NoticeDismissed)) return false;
            index.Pending = index.Pending.Select(item => item with { NoticeDismissed = true }).ToList();
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    // The caller must first establish that this save has finished without private
    // media. Cancelled/in-flight saves and incomplete recovery scans are not proof.
    internal Task<bool> DropEmptyReservationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return InBackground(async token =>
        {
            var index = await ReadIndexAsync(token).ConfigureAwait(false);
            var pending = index.Pending.SingleOrDefault(item => item.Id == id);
            if (pending is null) return false;
            using var directory = ClipLibraryFiles.OpenDirectory(_directory, create: false);
            try
            {
                using var media = ClipLibraryFiles.OpenRead(directory, $"{id:N}.mp4");
                return false;
            }
            catch (IOException error) when (ClipLibraryFiles.IsMissing(error)) { }
            index.Pending.Remove(pending);
            await WriteIndexAsync(index, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<ClipPendingRecoveryResult> ReconcilePendingAsync(CancellationToken cancellationToken = default) =>
        ReconcilePendingAsync(ClipBufferStore.DefaultRoot, cancellationToken);

    internal async Task<ClipPendingRecoveryResult> ReconcilePendingAsync(string bufferRoot, CancellationToken cancellationToken)
    {
        var pending = await ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return await ClipBufferStore.RecoverPendingAsync(this, pending, bufferRoot, cancellationToken).ConfigureAwait(false);
    }
}
