using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.Runs;
using Wisp.App.Tunes;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunTuneAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispRunTuneTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LegacyRunRemainsV1WithItsFreeTextTuneAndNoNewHeaderMember()
    {
        var run = RunTestData.CreateRun() with { Tune = "Existing free-text tune", Notes = "Existing notes" };
        Assert.Equal(1, run.SchemaVersion);
        var store = new RunStore(Folder("legacy"));
        await store.SaveAsync(run);
        var header = await ReadHeader(Path.Combine(Folder("legacy"), $"{run.Id:N}.wisprun"));
        Assert.DoesNotContain("tuneAttachment", header, StringComparison.Ordinal);
        Assert.Equal(1, JsonDocument.Parse(header).RootElement.GetProperty("schemaVersion").GetInt32());
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(run.Tune, loaded.Tune);
        Assert.Null(loaded.TuneAttachment);
        Assert.Null(Assert.Single(await store.ListAsync()).AttachedTuneName);
        Assert.Equal(run.Samples, loaded.Samples);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(4, true)]
    public void SchemaAndStructuredAttachmentMustAgree(int version, bool attach)
    {
        var run = AttachedRun();
        Assert.Throws<InvalidDataException>(() => RunStore.Validate(run with
        {
            SchemaVersion = version,
            TuneAttachment = attach ? run.TuneAttachment : null
        }));
    }

    [Fact]
    public void MismatchedCarAndDrivetrainAttachmentsAreRejected()
    {
        var run = AttachedRun();
        var attachment = run.TuneAttachment!;
        foreach (var identity in new[]
        {
            attachment.Snapshot.Identity with { CarOrdinal = attachment.Snapshot.Identity.CarOrdinal + 1 },
            attachment.Snapshot.Identity with { Drivetrain = Wisp.Core.Tunes.TuneDrivetrain.RearWheelDrive }
        })
        {
            var mismatch = attachment with { Snapshot = attachment.Snapshot with { Identity = identity } };
            Assert.Throws<InvalidDataException>(() => RunStore.Validate(run with { TuneAttachment = mismatch }));
        }
    }

    [Fact]
    public void SavedAndCurrentAttachmentKindsKeepTheirIdentityRequirements()
    {
        var attachment = AttachedRun().TuneAttachment!;
        Assert.True(attachment.IsValid);
        Assert.False((attachment with { SavedTuneId = Guid.NewGuid() }).IsValid);
        Assert.False((attachment with { Kind = RunTuneAttachmentKind.SavedMatchedAtStart }).IsValid);
        Assert.True((attachment with { Kind = RunTuneAttachmentKind.SavedMatchedAtStart, SavedTuneId = Guid.NewGuid() }).IsValid);
        Assert.False((attachment with { Kind = (RunTuneAttachmentKind)99 }).IsValid);
        Assert.False((attachment with { Name = "Bad\nname" }).IsValid);
    }

    [Fact]
    public async Task MetadataDraftAndCommittedEditsPreserveStructuredSnapshotAndLegacyLabel()
    {
        var store = new RunStore(Folder("metadata"));
        var run = AttachedRun() with { Tune = "Legacy label still editable" };
        await store.SaveAsync(run);
        var original = JsonSerializer.Serialize(run.TuneAttachment, RunStore.JsonOptions);
        var pending = await store.SaveMetadataDraftAsync(new(run.Id, "", run.Tune, "Pending notes"));
        Assert.True(pending.DraftPreserved);
        Assert.True(pending.NeedsName);
        Assert.Equal(original, JsonSerializer.Serialize((await store.LoadAsync(run.Id)).TuneAttachment, RunStore.JsonOptions));
        var committed = await store.SaveMetadataDraftAsync(new(run.Id, "Renamed run", "Updated legacy label", "Two\nlines"));
        Assert.NotNull(committed.Summary);
        Assert.Equal(run.TuneAttachment!.Name, committed.Summary.AttachedTuneName);
        Assert.Equal(run.TuneAttachment.Name, Assert.Single(await store.ListAsync()).AttachedTuneName);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal("Updated legacy label", loaded.Tune);
        Assert.Equal("Two\nlines", loaded.Notes);
        Assert.Equal(original, JsonSerializer.Serialize(loaded.TuneAttachment, RunStore.JsonOptions));
        await store.UpdateMetadataAsync(run.Id, "Again", "New label", "New notes");
        Assert.Equal(original, JsonSerializer.Serialize((await store.LoadAsync(run.Id)).TuneAttachment, RunStore.JsonOptions));
    }

    [Fact]
    public async Task RenamingOrRemovingLibraryTuneCannotChangeHistoricalAttachment()
    {
        var tunes = new TuneStore(Folder("tunes"));
        var original = await tunes.SaveAsync(TuneUiTestData.ValidSnapshot(), "Original tune", "Original description", TestContext.Current.CancellationToken);
        var attachment = new RunTuneAttachment(original.Snapshot, original.Name, original.Description,
            DateTimeOffset.UtcNow, RunTuneAttachmentKind.SavedMatchedAtStart, original.Id);
        var run = AttachedRun(attachment);
        var runs = new RunStore(Folder("runs"));
        await runs.SaveAsync(run);
        await tunes.UpdateMetadataAsync(original.Id, "Renamed later", "Changed later", TestContext.Current.CancellationToken);
        Assert.True(await tunes.DeleteAsync(original.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await tunes.ListAsync(TestContext.Current.CancellationToken));
        var loaded = (await runs.LoadAsync(run.Id)).TuneAttachment!;
        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.Description, loaded.Description);
        Assert.Equal(original.Snapshot.Id, loaded.Snapshot.Id);
        Assert.Equal(original.Id, loaded.SavedTuneId);
    }

    [Fact]
    public async Task SingleAndBulkArchiveRoundTripsRetainEveryAttachmentField()
    {
        var run = AttachedRun() with { Tune = "Keep legacy text" };
        var source = new RunStore(Folder("source"));
        await source.SaveAsync(run);
        var single = Path.Combine(_root, "one.wisprun");
        await source.ExportAsync(run.Id, single);
        var singleStore = new RunStore(Folder("single"));
        var imported = await singleStore.ImportAsync(single);
        Assert.NotEqual(run.Id, imported.Id);
        var loadedSingle = await singleStore.LoadAsync(imported.Id);
        Assert.Equal(JsonSerializer.Serialize(run.TuneAttachment, RunStore.JsonOptions),
            JsonSerializer.Serialize(loadedSingle.TuneAttachment, RunStore.JsonOptions));
        Assert.Equal(run.Tune, loadedSingle.Tune);
        var archive = Path.Combine(_root, "library.zip");
        await source.ExportAllAsync(archive);
        var bulkStore = new RunStore(Folder("bulk"));
        Assert.Single((await bulkStore.ImportManyAsync([archive])).Imported);
        var loadedBulk = await bulkStore.LoadAsync(run.Id);
        Assert.Equal(JsonSerializer.Serialize(run, RunStore.JsonOptions), JsonSerializer.Serialize(loadedBulk, RunStore.JsonOptions));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task InterruptedJournalRecoversItsSnapshotAndMarksContinuityUnverified(bool newJournal, int expectedVersion)
    {
        var run = AttachedRun() with { Tune = "Legacy label" };
        var store = new RunStore(Folder("recovery"));
        var journalPath = Path.Combine(Folder("recovery"), $"{run.Id:N}.partial");
        if (newJournal)
        {
            await using var journal = store.CreateJournal(run with { Samples = [] });
            foreach (var sample in run.Samples) await journal.AppendAsync(sample);
            await journal.FlushAsync();
            Assert.Empty(await store.ListAsync());
        }
        else
        {
            Directory.CreateDirectory(Folder("recovery"));
            await File.WriteAllLinesAsync(journalPath,
                new[] { JsonSerializer.Serialize(run with { Samples = [] }, RunStore.JsonOptions) }
                    .Concat(run.Samples.Select(sample => JsonSerializer.Serialize(sample, RunStore.JsonOptions))),
                TestContext.Current.CancellationToken);
        }
        await File.AppendAllTextAsync(journalPath, "{\"elapsedSeconds\":", TestContext.Current.CancellationToken);
        var recovered = new RunStore(Folder("recovery"));
        Assert.Single(await recovered.ListAsync());
        var loaded = await recovered.LoadAsync(run.Id);
        Assert.Equal(expectedVersion, loaded.SchemaVersion);
        Assert.True(loaded.IsIncomplete);
        Assert.True(loaded.TuneAttachment!.DrivingContinuityInterrupted);
        Assert.Equal(run.Tune, loaded.Tune);
        Assert.Equal(run.Samples, loaded.Samples);
        Assert.Equal(JsonSerializer.Serialize(run.TuneAttachment!.Snapshot, RunStore.JsonOptions),
            JsonSerializer.Serialize(loaded.TuneAttachment.Snapshot, RunStore.JsonOptions));
        Assert.False(File.Exists(journalPath));
    }

    private static RecordedRun AttachedRun(RunTuneAttachment? attachment = null)
    {
        attachment ??= new(TuneUiTestData.ValidSnapshot(), "Current setup", "Captured setup",
            DateTimeOffset.UtcNow, RunTuneAttachmentKind.CurrentAtStart);
        var run = RunTestData.CreateRun();
        return run with
        {
            SchemaVersion = RecordedRun.TuneAttachmentSchemaVersion,
            TuneAttachment = attachment,
            Samples = run.Samples.Select(sample => sample with
            {
                State = sample.State with
                {
                    CarOrdinal = attachment.Snapshot.Identity.CarOrdinal,
                    Drivetrain = (DrivetrainType)attachment.Snapshot.Identity.Drivetrain
                }
            }).ToArray()
        };
    }

    private static async Task<string> ReadHeader(string path)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return (await reader.ReadLineAsync(TestContext.Current.CancellationToken))!;
    }
    private string Folder(string name) => Path.Combine(_root, name);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
