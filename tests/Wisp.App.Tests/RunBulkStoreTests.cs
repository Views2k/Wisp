using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.App.Runs;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunBulkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispBulkRunTests", Guid.NewGuid().ToString("N"));
    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private string Library(string name) => Path.Combine(_root, name);
    private static string RunPath(string directory, Guid id) => Path.Combine(directory, $"{id:N}.wisprun");

    [Fact]
    public async Task StandardArchiveRoundTripsAllMetadataAndRawRunBytesDeterministically()
    {
        var source = new RunStore(Library("source"));
        var first = RunTestData.CreateRun() with
        {
            Id = FirstId,
            Name = "雨 · Wet drift",
            Tune = "Road + wet tires",
            Notes = "Entry: slower\nExit: full throttle",
            Markers = [new(.05, "Transition")],
            IsIncomplete = true,
            RejectedDatagrams = 3,
            DroppedDatagrams = 4
        };
        first = first with { Samples = first.Samples.Select(sample => sample with { State = sample.State with { LocalVelocityXMetersPerSecond = 2, LocalVelocityZMetersPerSecond = 9 } }).ToArray() };
        var second = RunTestData.CreateRun() with { Id = SecondId, Name = "Second tune" };
        await source.SaveAsync(second);
        await source.SaveAsync(first);
        var archivePath = Path.Combine(_root, "backup.zip");
        var again = Path.Combine(_root, "same-library.zip");
        Assert.Equal(2, await source.ExportAllAsync(archivePath));
        Assert.Equal(2, await source.ExportAllAsync(again));
        Assert.Equal(await File.ReadAllBytesAsync(archivePath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(again, TestContext.Current.CancellationToken));
        using (var zip = ZipFile.OpenRead(archivePath))
        {
            Assert.Equal(["manifest.json", RunArchive.EntryPath(FirstId), RunArchive.EntryPath(SecondId)], zip.Entries.Select(entry => entry.FullName).ToArray());
            await using var input = zip.GetEntry(RunArchive.EntryPath(FirstId))!.Open();
            using var bytes = new MemoryStream();
            await input.CopyToAsync(bytes, TestContext.Current.CancellationToken);
            Assert.Equal(await File.ReadAllBytesAsync(RunPath(Library("source"), FirstId), TestContext.Current.CancellationToken), bytes.ToArray());
        }
        var destination = new RunStore(Library("destination"));
        var imported = await destination.ImportManyAsync([archivePath]);
        Assert.Equal(2, imported.Imported.Count);
        Assert.Equal(0, imported.SkippedDuplicates);
        var restored = await destination.LoadAsync(FirstId);
        Assert.Equal(JsonSerializer.Serialize(first, RunStore.JsonOptions), JsonSerializer.Serialize(restored, RunStore.JsonOptions));
        var duplicate = await destination.ImportManyAsync([archivePath]);
        Assert.Empty(duplicate.Imported);
        Assert.Equal(2, duplicate.SkippedDuplicates);
        Assert.Equal(2, (await destination.ListAsync()).Count);
    }

    [Fact]
    public async Task BulkExportNeverOverwritesAndLeavesCorruptSourceUntouched()
    {
        var source = new RunStore(Library("source"));
        await source.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        var destination = Path.Combine(_root, "backup.zip");
        await File.WriteAllTextAsync(destination, "keep existing backup", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => source.ExportAllAsync(destination));
        Assert.Equal("keep existing backup", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
        var corrupt = RunPath(Library("source"), SecondId);
        await File.WriteAllTextAsync(corrupt, "keep corrupt run", TestContext.Current.CancellationToken);
        var failedDestination = Path.Combine(_root, "failed.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ExportAllAsync(failedDestination));
        Assert.False(File.Exists(failedDestination));
        Assert.Equal("keep corrupt run", await File.ReadAllTextAsync(corrupt, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateDirectories(Library("source"), ".wisp-bulk-*"));
    }

    [Fact]
    public async Task MultipleSingleRunFilesKeepIdentitiesAndSkipEquivalentRecompression()
    {
        var source = new RunStore(Library("source"));
        var run = RunTestData.CreateRun() with { Id = FirstId };
        await source.SaveAsync(run);
        await source.SaveAsync(RunTestData.CreateRun() with { Id = SecondId });
        var recompressed = Path.Combine(_root, "same-run.wisprun");
        await WriteRunAsync(recompressed, run, CompressionLevel.SmallestSize);
        var store = new RunStore(Library("destination"));
        var result = await store.ImportManyAsync([RunPath(Library("source"), FirstId), RunPath(Library("source"), SecondId), recompressed]);
        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(run.Samples, (await store.LoadAsync(FirstId)).Samples);
    }

    [Fact]
    public async Task ConflictingIdentityRejectsWholeBatchWithoutReplacingNamesOrNotes()
    {
        var source = new RunStore(Library("source"));
        var existing = RunTestData.CreateRun() with { Id = SecondId, Name = "Keep me", Notes = "Original notes" };
        await source.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        await source.SaveAsync(existing with { Notes = "Different imported notes" });
        var store = new RunStore(Library("destination"));
        await store.SaveAsync(existing);
        var before = await File.ReadAllBytesAsync(RunPath(Library("destination"), SecondId), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportManyAsync([RunPath(Library("source"), FirstId), RunPath(Library("source"), SecondId)]));
        Assert.Single(await store.ListAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(RunPath(Library("destination"), SecondId), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(RunPath(Library("destination"), FirstId)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("corrupt")]
    [InlineData("schema")]
    public async Task InvalidArchiveRejectsEveryRunAndKeepsExistingLibrary(string fault)
    {
        var run = RunTestData.CreateRun() with { Id = FirstId };
        var payloadPath = Path.Combine(_root, "payload.wisprun");
        Directory.CreateDirectory(_root);
        await WriteRunAsync(payloadPath, fault == "schema" ? run with { SchemaVersion = 999 } : run);
        var bytes = fault == "corrupt" ? "not gzip"u8.ToArray() : await File.ReadAllBytesAsync(payloadPath, TestContext.Current.CancellationToken);
        var id = fault == "identity" ? SecondId : FirstId;
        var entry = new RunArchiveEntry(id, fault == "traversal" ? "../escape.wisprun" : RunArchive.EntryPath(id), bytes.Length,
            fault == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)));
        var manifest = new RunArchiveManifest(RunArchive.Format, fault == "version" ? 999 : 1, [entry]);
        var archivePath = Path.Combine(_root, "invalid.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, RunStore.JsonOptions));
            await WriteEntryAsync(archive, entry.Path, bytes);
            if (fault == "duplicate") await WriteEntryAsync(archive, entry.Path, bytes);
            if (fault == "extra") await WriteEntryAsync(archive, "unexpected.txt", "extra"u8.ToArray());
        }
        var store = new RunStore(Library("destination"));
        var existing = RunTestData.CreateRun();
        await store.SaveAsync(existing);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportManyAsync([archivePath]));
        Assert.Equal(existing.Id, Assert.Single(await store.ListAsync()).Id);
        Assert.False(File.Exists(Path.Combine(_root, "escape.wisprun")));
        Assert.Empty(Directory.EnumerateDirectories(Library("destination"), ".wisp-bulk-*"));
    }

    [Fact]
    public async Task SaveCollisionDuringCommitRollsBackEarlierImportedFiles()
    {
        var source = new RunStore(Library("source"));
        await source.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        await source.SaveAsync(RunTestData.CreateRun() with { Id = SecondId });
        var destination = Library("destination");
        Directory.CreateDirectory(RunPath(destination, SecondId));
        var store = new RunStore(destination);
        await Assert.ThrowsAnyAsync<IOException>(() => store.ImportManyAsync([RunPath(Library("source"), FirstId), RunPath(Library("source"), SecondId)]));
        Assert.Empty(await store.ListAsync());
        Assert.False(File.Exists(RunPath(destination, FirstId)));
        Assert.True(Directory.Exists(RunPath(destination, SecondId)));
        Assert.Empty(Directory.EnumerateDirectories(destination, ".wisp-bulk-*"));
        Assert.Equal(2, (await source.ListAsync()).Count);
    }

    [Fact]
    public async Task DeleteAllRetainsUnreadableAndUnrelatedFilesAndUndoRestoresRawBytes()
    {
        var directory = Library("library");
        var store = new RunStore(directory);
        var run = RunTestData.CreateRun() with { Id = FirstId, Notes = "Keep all data" };
        await store.SaveAsync(run);
        var original = await File.ReadAllBytesAsync(RunPath(directory, FirstId), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(RunPath(directory, SecondId), "damaged but retained", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "readme.txt"), "unrelated", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "manual.wisprun"), "not a library entry", TestContext.Current.CancellationToken);
        var batch = await store.DeleteAllAsync();
        Assert.Equal(1, batch.Count);
        Assert.Equal(1, batch.SkippedUnreadable);
        Assert.NotEqual(Guid.Empty, batch.Id);
        Assert.Empty(await store.ListAsync());
        Assert.Equal("damaged but retained", await File.ReadAllTextAsync(RunPath(directory, SecondId), TestContext.Current.CancellationToken));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(Path.Combine(directory, "readme.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(directory, "manual.wisprun")));
        var reopened = new RunStore(directory);
        Assert.Equal(1, await reopened.RestoreDeletedBatchAsync(batch.Id));
        Assert.Equal(original, await File.ReadAllBytesAsync(RunPath(directory, FirstId), TestContext.Current.CancellationToken));
        Assert.Equal(0, await reopened.RestoreDeletedBatchAsync(batch.Id));
        Assert.True(File.Exists(Path.Combine(directory, "Deleted", batch.Id.ToString("N"), "runs", $"{FirstId:N}.wisprun")));
    }

    [Fact]
    public async Task DeleteAllRefusesAnActiveRecordingAndDoesNotDisturbItsJournal()
    {
        var store = new RunStore(Library("library"));
        var saved = RunTestData.CreateRun() with { Id = FirstId };
        await store.SaveAsync(saved);
        var active = RunTestData.CreateRun() with { Id = SecondId, Samples = [] };
        await using var journal = store.CreateJournal(active);
        await journal.AppendAsync(saved.Samples[0]);
        await journal.FlushAsync();
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAllAsync());
        Assert.Equal(FirstId, Assert.Single(await store.ListAsync()).Id);
        Assert.True(File.Exists(Path.Combine(Library("library"), $"{SecondId:N}.partial")));
    }

    [Fact]
    public async Task FailedDeleteRestoresEarlierMovesAndPreservesBothRuns()
    {
        var directory = Library("library");
        var store = new RunStore(directory);
        await store.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        await store.SaveAsync(RunTestData.CreateRun() with { Id = SecondId });
        using var locked = new FileStream(RunPath(directory, SecondId), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => store.DeleteAllAsync());
        Assert.Equal(2, (await store.ListAsync()).Count);
        Assert.True(File.Exists(RunPath(directory, FirstId)));
        Assert.True(File.Exists(RunPath(directory, SecondId)));
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Delete")]
    public async Task RestartRollsBackAnInterruptedBatchBeforeListing(string kind)
    {
        var directory = Library("library");
        var original = new RunStore(directory);
        await original.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        await original.SaveAsync(RunTestData.CreateRun() with { Id = SecondId });
        var entries = new[] { await RunArchive.DescribeAsync(FirstId, RunPath(directory, FirstId)), await RunArchive.DescribeAsync(SecondId, RunPath(directory, SecondId)) };
        var batchId = Guid.NewGuid();
        var batch = kind == "Delete" ? Path.Combine(directory, "Deleted", batchId.ToString("N")) : Path.Combine(directory, $".wisp-bulk-{batchId:N}");
        Directory.CreateDirectory(Path.Combine(batch, "runs"));
        var stagedId = kind == "Delete" ? FirstId : SecondId;
        File.Move(RunPath(directory, stagedId), RunPath(Path.Combine(batch, "runs"), stagedId));
        await File.WriteAllTextAsync(Path.Combine(batch, "transaction.json"), JsonSerializer.Serialize(new { id = batchId, kind, phase = "Applying", runs = entries }, RunStore.JsonOptions), TestContext.Current.CancellationToken);
        var reopened = new RunStore(directory);
        var runs = await reopened.ListAsync();
        if (kind == "Import") Assert.Empty(runs);
        else Assert.Equal(2, runs.Count);
        Assert.False(Directory.Exists(batch));
    }

    [Fact]
    public async Task LibraryCapacityFailureDoesNotPartiallyImport()
    {
        var source = new RunStore(Library("source"));
        await source.SaveAsync(RunTestData.CreateRun() with { Id = FirstId });
        await source.SaveAsync(RunTestData.CreateRun() with { Id = SecondId });
        var directory = Library("full");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < RunStore.MaximumLibraryEntries - 1; index++)
            await File.WriteAllTextAsync(RunPath(directory, Guid.NewGuid()), "retained", TestContext.Current.CancellationToken);
        var store = new RunStore(directory);
        await Assert.ThrowsAsync<RunLibraryFullException>(() => store.ImportManyAsync([RunPath(Library("source"), FirstId), RunPath(Library("source"), SecondId)]));
        Assert.False(File.Exists(RunPath(directory, FirstId)));
        Assert.False(File.Exists(RunPath(directory, SecondId)));
        Assert.Equal(RunStore.MaximumLibraryEntries - 1, Directory.EnumerateFiles(directory, "*.wisprun").Count());
    }

    private static async Task WriteRunAsync(string path, RecordedRun run, CompressionLevel level = CompressionLevel.Fastest)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, level);
        await using var writer = new StreamWriter(gzip);
        await writer.WriteLineAsync(JsonSerializer.Serialize(run with { Samples = [] }, RunStore.JsonOptions));
        foreach (var sample in run.Samples) await writer.WriteLineAsync(JsonSerializer.Serialize(sample, RunStore.JsonOptions));
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes)
    {
        await using var output = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await output.WriteAsync(bytes, TestContext.Current.CancellationToken);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
