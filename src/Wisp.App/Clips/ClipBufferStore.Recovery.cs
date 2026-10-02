using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App.Clips;

internal sealed partial class ClipBufferStore
{
    // Only call after an acknowledged native refusal, before publication began.
    internal async Task<bool> IsEmptyAsync(Guid clip, CancellationToken token)
    {
        if (clip == Guid.Empty) throw new ArgumentException("A clip reservation is required.", nameof(clip));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Missing(_directory, $"{clip:N}.mp4") && Missing(_directory, RecordName(clip)) && Missing(_directory, CommitName(clip));
        }
        finally { _gate.Release(); }
    }

    private static bool Missing(SafeFileHandle directory, string name)
    {
        try { using var file = NativeFile.Open(directory, name, NativeFile.ReadAccess, 1, 1, false); return false; }
        catch (IOException error) when (ClipLibraryFiles.IsMissing(error)) { return true; }
    }

    internal static Task<ClipPendingRecoveryResult> RecoverPendingAsync(ClipLibrary library,
        IReadOnlyList<ClipSaveTarget> pending, string root, CancellationToken token) => Task.Run(async () =>
    {
        const int maximumChanges = ClipLibrary.PageSize;
        if (pending.Count == 0) return new ClipPendingRecoveryResult(0, 0, 0, false);
        if (!ClipsSettings.TryNormalizeStorageDirectory(root, out var normalized))
            throw new RecorderClientException("buffer_storage_unavailable");
        var targets = pending.ToDictionary(item => item.Id);
        var retained = new HashSet<Guid>();
        var recovered = new HashSet<Guid>();
        var budget = new ClipRecoveryBudget();
        var incomplete = false;
        try
        {
            using var parent = NativeFile.OpenDirectoryTree(normalized, create: false);
            var paths = Directory.EnumerateFileSystemEntries(normalized).Take(MaximumWorkspaces + 1).ToArray();
            incomplete = paths.Length > MaximumWorkspaces;
            foreach (var path in paths.Take(MaximumWorkspaces))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!Guid.TryParseExact(name, "N", out var session) || session == Guid.Empty) continue;
                try
                {
                    using var directory = NativeFile.Open(parent, name, NativeFile.DirectoryAccess, 3, 1, true);
                    using var lease = NativeFile.Open(directory, LeaseName, NativeFile.ReadWriteDelete, 0, 1, false);
                    if (NativeFile.Identity(lease).Bytes != 0 || ReadRecord<OwnerRecord>(directory, OwnerName) != new OwnerRecord(Format, 1, session))
                    { incomplete = true; continue; }
                    var names = Directory.EnumerateFileSystemEntries(path).Take(MaximumEntries + 1).Select(Path.GetFileName).ToArray();
                    incomplete |= names.Length > MaximumEntries;
                    var entries = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var target in pending)
                    {
                        if (!entries.Contains($"{target.Id:N}.mp4") && !entries.Contains(RecordName(target.Id)) && !entries.Contains(CommitName(target.Id))) continue;
                        retained.Add(target.Id);
                        if (recovered.Contains(target.Id) || !entries.Contains(RecordName(target.Id))) continue;
                        try
                        {
                            var record = ReadRecord<ClipRecord>(directory, RecordName(target.Id));
                            if (!ValidRecord(record, session, target.Id) || !SameTarget(record.Target, target)) continue;
                            ClipLibrary.ValidateMedia(record.Media, target.Recording);
                            using var sourceHandle = NativeFile.Open(directory, $"{target.Id:N}.mp4", NativeFile.ReadAccess, 1, 1, false);
                            if (NativeFile.Identity(sourceHandle) != record.Identity) continue;
                            if (!budget.TryStart(record.Media.FileBytes)) { incomplete = true; continue; }
                            await using var source = NativeFile.Stream(sourceHandle, FileAccess.Read);
                            var destination = Path.GetDirectoryName(target.MediaPath)!;
                            await ClipLibraryFiles.CopyOrVerifyAsync(source, destination, $"{target.Id:N}.mp4", reuseIdentical: true, token).ConfigureAwait(false);
                            using var outputDirectory = NativeFile.OpenDirectoryTree(destination, create: false);
                            using var outputHandle = NativeFile.Open(outputDirectory, $"{target.Id:N}.mp4", NativeFile.ReadAccess, 1, 1, false);
                            var identity = NativeFile.Identity(outputHandle);
                            await using var output = NativeFile.Stream(outputHandle, FileAccess.Read);
                            source.Position = 0;
                            var sourceHash = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
                            var outputHash = await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
                            if (identity.Bytes != (ulong)record.Media.FileBytes || !CryptographicOperations.FixedTimeEquals(sourceHash, outputHash)) continue;
                            var media = record.Media with
                            {
                                PublicationReceipt = new ClipBufferCommitReceipt(file =>
                                {
                                    if (NativeFile.Identity(file) != identity) throw new RecorderClientException("clip_publish_failed");
                                }, () => { })
                            };
                            // Recovery keeps the original finalized file and its record.
                            await library.CommitFinalizedAsync(target.Id, media, token).ConfigureAwait(false);
                            recovered.Add(target.Id);
                        }
                        catch (Exception error) when (StorageError(error) || error is InvalidDataException) { }
                    }
                }
                catch (Exception error) when (StorageError(error))
                { incomplete = true; }
            }
        }
        catch (IOException error) when (ClipLibraryFiles.IsMissing(error)) { }
        catch (Exception error) when (StorageError(error)) { incomplete = true; }

        var removed = 0;
        if (!incomplete)
        {
            foreach (var target in targets.Values)
            {
                if (removed + recovered.Count >= maximumChanges) break;
                if (!retained.Contains(target.Id) && await library.DropEmptyReservationAsync(target.Id, token).ConfigureAwait(false)) removed++;
            }
        }
        var remaining = (await library.ListPendingAsync(token).ConfigureAwait(false)).Count;
        return new ClipPendingRecoveryResult(recovered.Count, removed, remaining, incomplete);
    }, token);

    private static bool SameTarget(ClipSaveTarget recorded, ClipSaveTarget expected) =>
        recorded.Id == expected.Id && recorded.RequestedAtUtc == expected.RequestedAtUtc && recorded.Recording == expected.Recording &&
        string.Equals(recorded.MediaPath, expected.MediaPath, StringComparison.OrdinalIgnoreCase);
}

internal sealed class ClipRecoveryBudget
{
    private long _attemptedBytes;
    private int _attempts;

    internal bool TryStart(long bytes)
    {
        if (bytes <= 0 || _attempts >= ClipLibrary.PageSize || bytes > ClipLibrary.MaximumMediaBytes - _attemptedBytes) return false;
        // Charge before any copy/hash work. Failed attempts do not refund the budget.
        _attemptedBytes += bytes;
        _attempts++;
        return true;
    }
}
