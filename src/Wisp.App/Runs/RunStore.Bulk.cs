using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.Core.Runs;

namespace Wisp.App.Runs;

public sealed partial class RunStore
{
    private bool _bulkRecovered;
    private sealed record BulkTransaction(Guid Id, string Kind, string Phase, RunArchiveEntry[] Runs);
    private sealed record PreparedImport(RunSummary Summary, string ContentHash);

    public Task<int> ExportAllAsync(string destination) => InBackground(async () =>
    {
        var (directory, _) = BeginTransaction("Export");
        try
        {
            var entries = new List<RunArchiveEntry>();
            foreach (var (id, path) in LibraryFiles())
            {
                var staged = TransactionRunPath(directory, id);
                await using (var input = File.OpenRead(path))
                    await RunArchive.CopyBoundedAsync(input, staged, MaximumFileBytes).ConfigureAwait(false);
                _ = await ReadAsync(staged, id).ConfigureAwait(false);
                entries.Add(await RunArchive.DescribeAsync(id, staged).ConfigureAwait(false));
            }
            await RunArchive.WriteAsync(Path.Combine(directory, "runs"), entries.ToArray(), destination).ConfigureAwait(false);
            return entries.Count;
        }
        finally { CleanupTransaction(directory); }
    });

    public Task<RunImportResult> ImportManyAsync(IEnumerable<string> sources) => InBackground(() => ImportManyCoreAsync(sources));

    public Task<RunDeletionBatch> DeleteAllAsync() => InBackground(async () =>
    {
        if (!_activeJournals.IsEmpty) throw new IOException("Finish recording before removing saved runs.");
        var entries = new List<RunArchiveEntry>();
        var skipped = 0;
        foreach (var (id, path) in LibraryFiles())
        {
            try
            {
                _ = await ReadAsync(path, id).ConfigureAwait(false);
                entries.Add(await RunArchive.DescribeAsync(id, path).ConfigureAwait(false));
            }
            catch (Exception error) when (IsDataError(error)) { skipped++; }
        }
        if (entries.Count == 0) return new RunDeletionBatch(Guid.Empty, 0, skipped);
        var (directory, initial) = BeginTransaction("Delete");
        var transaction = initial with { Phase = "Applying", Runs = entries.ToArray() };
        try
        {
            WriteTransaction(directory, transaction);
            foreach (var entry in entries)
                File.Move(RunPath(entry.Id), TransactionRunPath(directory, entry.Id), overwrite: false);
            var committed = transaction with { Phase = "Committed" };
            WriteTransaction(directory, committed);
            transaction = committed;
            Volatile.Write(ref _isFull, false);
            return new RunDeletionBatch(transaction.Id, entries.Count, skipped);
        }
        catch
        {
            RollbackAfterFailure(directory, transaction);
            throw;
        }
    });

    public Task<int> RestoreDeletedBatchAsync(Guid batchId) => InBackground(async () =>
    {
        if (batchId == Guid.Empty) throw new ArgumentException("Choose a removed run batch.", nameof(batchId));
        var directory = Path.Combine(_directory, "Deleted", batchId.ToString("N"));
        var transaction = ReadTransaction(directory);
        if (transaction.Id != batchId || transaction.Kind != "Delete" || transaction.Phase != "Committed")
            throw new InvalidDataException("The removed run batch is invalid or incomplete.");
        var sources = new List<string>();
        foreach (var entry in transaction.Runs)
        {
            var path = TransactionRunPath(directory, entry.Id);
            var actual = await RunArchive.DescribeAsync(entry.Id, path).ConfigureAwait(false);
            if (actual.Length != entry.Length || !SameHash(actual.Sha256, entry.Sha256))
                throw new InvalidDataException("A removed run has changed. The batch has been kept.");
            sources.Add(path);
        }
        var result = await ImportManyCoreAsync(sources).ConfigureAwait(false);
        return result.Imported.Count;
    });

    private async Task<RunImportResult> ImportManyCoreAsync(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var supplied = sources.Take(MaximumLibraryEntries + 1).ToArray();
        if (supplied.Length is 0 or > MaximumLibraryEntries)
            throw new InvalidDataException("Choose between one and 2,000 run files or archives.");
        var paths = supplied.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var (directory, initial) = BeginTransaction("Import");
        var transaction = initial;
        var prepared = new Dictionary<Guid, PreparedImport>();
        var seen = new Dictionary<Guid, string>();
        var entries = new List<RunArchiveEntry>();
        var duplicates = 0;
        var inputCount = 0;
        try
        {
            foreach (var source in paths)
            {
                if (!Path.IsPathFullyQualified(source)) throw new InvalidDataException("Choose run files using full paths.");
                CheckPath(source);
                var folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                IReadOnlyList<StagedRunFile> files;
                if (string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
                    files = await RunArchive.ExtractAsync(source, folder, MaximumLibraryEntries - inputCount).ConfigureAwait(false);
                else
                {
                    ValidateExternalPath(source);
                    var staged = Path.Combine(folder, "input.wisprun");
                    await using (var input = File.OpenRead(source))
                        await RunArchive.CopyBoundedAsync(input, staged, MaximumFileBytes).ConfigureAwait(false);
                    files = [new(staged, null)];
                }
                inputCount += files.Count;
                if (inputCount > MaximumLibraryEntries) throw new InvalidDataException("Import no more than 2,000 runs at a time.");
                foreach (var item in files)
                {
                    var run = await ReadAsync(item.File, item.ExpectedId).ConfigureAwait(false);
                    if (_activeJournals.ContainsKey(run.Id)) throw new IOException("A run being recorded has the same identity. Finish recording before importing it.");
                    var contentHash = ContentHash(run);
                    if (!seen.TryGetValue(run.Id, out var previousHash) && File.Exists(RunPath(run.Id)))
                        previousHash = ContentHash(await ReadAsync(RunPath(run.Id), run.Id).ConfigureAwait(false));
                    if (previousHash is not null)
                    {
                        if (!SameHash(previousHash, contentHash))
                            throw new InvalidDataException("A run with the same identity has different content or notes. No runs were imported; existing runs were kept.");
                        duplicates++;
                    }
                    else
                    {
                        var target = TransactionRunPath(directory, run.Id);
                        File.Move(item.File, target, overwrite: false);
                        entries.Add(await RunArchive.DescribeAsync(run.Id, target).ConfigureAwait(false));
                        prepared.Add(run.Id, new(Summarize(run), contentHash));
                    }
                    seen[run.Id] = contentHash;
                }
            }
            if (Directory.EnumerateFiles(_directory, "*.wisprun").Count() + _activeJournals.Count + prepared.Count > MaximumLibraryEntries)
                throw new RunLibraryFullException();
            transaction = initial with { Phase = "Applying", Runs = entries.OrderBy(value => value.Id).ToArray() };
            WriteTransaction(directory, transaction);
            foreach (var entry in transaction.Runs)
                File.Move(TransactionRunPath(directory, entry.Id), RunPath(entry.Id), overwrite: false);
            var committed = transaction with { Phase = "Committed" };
            WriteTransaction(directory, committed);
            transaction = committed;
            foreach (var item in prepared.Values)
            {
                try { await WriteSummaryAsync(item.Summary).ConfigureAwait(false); }
                catch (Exception error) when (IsDataError(error)) { Warning = "Runs were imported. Their library summaries will be rebuilt when needed."; }
            }
            try { CleanupTransaction(directory); }
            catch (Exception error) when (IsDataError(error))
            {
                _bulkRecovered = false;
                Warning = "Runs were imported. Temporary files will be cleared when the library is reopened.";
            }
            return new(prepared.Values.Select(value => value.Summary).OrderByDescending(value => value.StartedAtUtc).ThenBy(value => value.Id).ToArray(), duplicates);
        }
        catch
        {
            RollbackAfterFailure(directory, transaction);
            throw;
        }
    }

    private (Guid Id, string Path)[] LibraryFiles()
    {
        var files = Directory.EnumerateFiles(_directory, "*.wisprun")
            .Select(path => (Id: Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : Guid.Empty, Path: path))
            .Where(item => item.Id != Guid.Empty).OrderBy(item => item.Id).Take(MaximumLibraryEntries + 1).ToArray();
        if (files.Length > MaximumLibraryEntries) throw new InvalidDataException("The saved run library exceeds its supported size.");
        foreach (var (_, path) in files) CheckPath(path);
        return files;
    }

    private static string ContentHash(RecordedRun run)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(JsonSerializer.Serialize(run with { Samples = [] }, JsonOptions));
        foreach (var sample in run.Samples) Add(JsonSerializer.Serialize(sample, JsonOptions));
        return Convert.ToHexString(hash.GetHashAndReset());
        void Add(string value) { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData("\n"u8); }
    }

    private (string Directory, BulkTransaction Transaction) BeginTransaction(string kind)
    {
        var id = Guid.NewGuid();
        var directory = kind == "Delete" ? Path.Combine(_directory, "Deleted", id.ToString("N")) : Path.Combine(_directory, $".wisp-bulk-{id:N}");
        CheckPath(directory);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "runs"));
        var transaction = new BulkTransaction(id, kind, "Preparing", []);
        WriteTransaction(directory, transaction);
        return (directory, transaction);
    }

    private static string TransactionRunPath(string directory, Guid id)
    {
        var path = Path.Combine(directory, "runs", $"{id:N}.wisprun");
        CheckPath(path);
        return path;
    }

    private static void WriteTransaction(string directory, BulkTransaction transaction)
    {
        var destination = Path.Combine(directory, "transaction.json");
        CheckPath(destination);
        var temporary = Path.Combine(directory, $".transaction-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, transaction, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static BulkTransaction ReadTransaction(string directory)
    {
        var path = Path.Combine(directory, "transaction.json");
        CheckPath(path);
        if (new FileInfo(path).Length is <= 0 or > RunArchive.MaximumManifestBytes)
            throw new InvalidDataException("The interrupted run operation has invalid metadata. Its files have been kept.");
        var transaction = JsonSerializer.Deserialize<BulkTransaction>(File.ReadAllText(path), JsonOptions);
        if (transaction is null || transaction.Id == Guid.Empty || transaction.Kind is not ("Import" or "Export" or "Delete") ||
            transaction.Phase is not ("Preparing" or "Applying" or "Committed"))
            throw new InvalidDataException("The interrupted run operation is invalid. Its files have been kept.");
        var expectedName = transaction.Kind == "Delete" ? transaction.Id.ToString("N") : $".wisp-bulk-{transaction.Id:N}";
        if (Path.GetFileName(directory) != expectedName) throw new InvalidDataException("The interrupted run operation identity is invalid.");
        RunArchive.ValidateEntries(transaction.Runs);
        return transaction;
    }

    private void RecoverBulkTransactions()
    {
        if (_bulkRecovered) return;
        var directories = Directory.EnumerateDirectories(_directory, ".wisp-bulk-*");
        var deleted = Path.Combine(_directory, "Deleted");
        CheckPath(deleted);
        if (Directory.Exists(deleted)) directories = directories.Concat(Directory.EnumerateDirectories(deleted).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)));
        foreach (var directory in directories.ToArray())
        {
            CheckPath(directory);
            if (!File.Exists(Path.Combine(directory, "transaction.json"))) continue;
            var transaction = ReadTransaction(directory);
            if (transaction.Kind == "Delete" && transaction.Phase == "Committed") continue;
            if (transaction.Phase == "Applying") RollbackTransaction(directory, transaction);
            CleanupTransaction(directory);
        }
        _bulkRecovered = true;
    }

    private void RollbackAfterFailure(string directory, BulkTransaction transaction)
    {
        if (transaction.Phase == "Committed") { _bulkRecovered = false; return; }
        try
        {
            if (transaction.Phase == "Applying") RollbackTransaction(directory, transaction);
            CleanupTransaction(directory);
        }
        catch (Exception error) when (IsDataError(error))
        {
            _bulkRecovered = false;
            Warning = "An interrupted run operation needs recovery. Its saved and staged files have been kept; reopen the run library to retry.";
        }
    }

    private void RollbackTransaction(string directory, BulkTransaction transaction)
    {
        foreach (var entry in transaction.Runs.Reverse())
        {
            var library = RunPath(entry.Id);
            var staged = TransactionRunPath(directory, entry.Id);
            if (transaction.Kind == "Delete")
            {
                if (File.Exists(staged)) File.Move(staged, library, overwrite: false);
            }
            else if (!File.Exists(staged) && File.Exists(library))
            {
                using var stream = File.OpenRead(library);
                if (stream.Length != entry.Length || !SameHash(Convert.ToHexString(SHA256.HashData(stream)), entry.Sha256))
                    throw new IOException("A run changed during recovery. Both copies have been kept.");
                stream.Dispose();
                File.Move(library, staged, overwrite: false);
            }
        }
    }

    private void CleanupTransaction(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var relative = Path.GetRelativePath(_directory, Path.GetFullPath(directory));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative) || relative == ".")
            throw new IOException("The temporary run directory is outside the library.");
        CheckPath(directory);
        ValidateTree(directory);
        Directory.Delete(directory, recursive: true);
        static void ValidateTree(string folder)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                CheckPath(path);
                if (Directory.Exists(path)) ValidateTree(path);
            }
        }
    }

    private static bool SameHash(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
