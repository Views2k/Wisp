using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunStoreContactsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispRunContactTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OptionalObjectImpactFieldsRoundTripAndLegacyRunsKeepUnknownRatherThanInventedZero()
    {
        var store = new RunStore(_directory);
        var original = RunTestData.CreateRun();
        await store.SaveAsync(original);
        var legacy = await store.LoadAsync(original.Id);
        Assert.Equal(RecordedRun.BaseSchemaVersion, legacy.SchemaVersion);
        Assert.All(legacy.Samples, sample =>
        {
            Assert.Null(sample.State.SmashableVelocityLossMetersPerSecond);
            Assert.Null(sample.State.SmashableMassKilograms);
            var json = System.Text.Json.JsonSerializer.Serialize(sample.State, RunStore.JsonOptions);
            Assert.DoesNotContain("smashable", json, StringComparison.OrdinalIgnoreCase);
        });
        var current = original with
        {
            Id = Guid.NewGuid(),
            Samples = original.Samples.Select(sample => sample with
            {
                State = sample.State with { SmashableVelocityLossMetersPerSecond = 2.5f, SmashableMassKilograms = 150 }
            }).ToArray()
        };
        await store.SaveAsync(current);
        var saved = await store.LoadAsync(current.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.Equal(current.Samples, saved.Samples);
        var exported = Path.Combine(_directory, "contact-export.wisprun");
        await store.ExportAsync(current.Id, exported);
        var importStore = new RunStore(Path.Combine(_directory, "imported"));
        var imported = await importStore.ImportAsync(exported);
        var importedRun = await importStore.LoadAsync(imported.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, importedRun.SchemaVersion);
        Assert.Equal(current.Samples, importedRun.Samples);
    }

    [Fact]
    public async Task ContactTelemetryUpgradesStructuredTuneFormatWithoutChangingSnapshot()
    {
        var snapshot = TuneUiTestData.ValidSnapshot();
        var original = RunTestData.CreateRun();
        var run = original with
        {
            SchemaVersion = RecordedRun.TuneAttachmentSchemaVersion,
            TuneAttachment = new(snapshot, "Saved setup", "Tune snapshot", DateTimeOffset.UtcNow, RunTuneAttachmentKind.CurrentAtStart),
            Samples = original.Samples.Select((sample, index) => sample with
            {
                State = sample.State with
                {
                    CarOrdinal = snapshot.Identity.CarOrdinal,
                    Drivetrain = (DrivetrainType)snapshot.Identity.Drivetrain,
                    SmashableVelocityLossMetersPerSecond = index == 1 ? 2 : 0,
                    SmashableMassKilograms = index == 1 ? 150 : 0
                }
            }).ToArray()
        };
        var store = new RunStore(_directory);
        await store.SaveAsync(run);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(run.Samples, loaded.Samples);
        Assert.Equal(JsonSerializer.Serialize(run.TuneAttachment, RunStore.JsonOptions),
            JsonSerializer.Serialize(loaded.TuneAttachment, RunStore.JsonOptions));
        await store.UpdateMetadataAsync(run.Id, run.Name, run.Tune, "Updated notes");
        Assert.Equal(RecordedRun.CurrentSchemaVersion, (await store.LoadAsync(run.Id)).SchemaVersion);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalizedRunWithoutPositiveObjectDataUsesLegacyFormat(bool withTune, bool zeroFields)
    {
        var run = ContactRun(withTune, zeroFields);
        var store = new RunStore(_directory);
        await store.SaveAsync(run);
        var loaded = await store.LoadAsync(run.Id);
        var expected = WithoutContactFields(run);
        Assert.Equal(JsonSerializer.Serialize(expected, RunStore.JsonOptions), JsonSerializer.Serialize(loaded, RunStore.JsonOptions));
        Assert.All(loaded.Samples, sample =>
            Assert.DoesNotContain("smashable", JsonSerializer.Serialize(sample.State, RunStore.JsonOptions), StringComparison.OrdinalIgnoreCase));
        if (zeroFields) Assert.All(run.Samples, sample => Assert.Equal(0f, sample.State.SmashableMassKilograms));
    }

    [Theory]
    [InlineData(2f, 0f)]
    [InlineData(0f, 150f)]
    [InlineData(2f, 150f)]
    public async Task AnyPositiveObjectFieldRetainsV3AndEveryZeroBaseline(float loss, float mass)
    {
        var run = ContactRun(withTune: false, zeroFields: true);
        run = run with
        {
            Samples = [run.Samples[0], run.Samples[1] with
            {
                State = run.Samples[1].State with { SmashableVelocityLossMetersPerSecond = loss, SmashableMassKilograms = mass }
            }, run.Samples[1] with { ElapsedSeconds = .2, State = run.Samples[1].State with { GameTimestampMilliseconds = 200 } }]
        };
        var store = new RunStore(_directory);
        await store.SaveAsync(run);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(run.Samples, loaded.Samples);
        var archive = Path.Combine(_directory, "contacts.zip");
        Assert.Equal(1, await store.ExportAllAsync(archive));
        var importedDirectory = Path.Combine(_directory, "imported");
        var imported = new RunStore(importedDirectory);
        await imported.ImportManyAsync([archive]);
        var importedRun = await imported.LoadAsync(run.Id);
        Assert.Equal(JsonSerializer.Serialize(loaded, RunStore.JsonOptions), JsonSerializer.Serialize(importedRun, RunStore.JsonOptions));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_directory, $"{run.Id:N}.wisprun"), TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(Path.Combine(importedDirectory, $"{run.Id:N}.wisprun"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingAndImportedZeroOnlyV3RunsRetainTheirFormatAndFields()
    {
        var run = ContactRun(withTune: false, zeroFields: true);
        var path = Path.Combine(_directory, $"{run.Id:N}.wisprun");
        await WriteRawRunAsync(path, run);
        var store = new RunStore(_directory);
        Assert.Single(await store.ListAsync());
        await store.UpdateMetadataAsync(run.Id, run.Name, run.Tune, "Edited notes");
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(run.Samples, loaded.Samples);
        Assert.Equal(run.Markers, loaded.Markers);
        var singleImport = new RunStore(Path.Combine(_directory, "single"));
        var summary = await singleImport.ImportAsync(path);
        var single = await singleImport.LoadAsync(summary.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, single.SchemaVersion);
        Assert.Equal(run.Samples, single.Samples);
        var bulkImport = new RunStore(Path.Combine(_directory, "bulk"));
        await bulkImport.ImportManyAsync([path]);
        var bulk = await bulkImport.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, bulk.SchemaVersion);
        Assert.Equal(run.Samples, bulk.Samples);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroOnlyJournalRemainsV3UntilRecoveryFinalizesItsLegacyFormat(bool withTune)
    {
        var run = ContactRun(withTune, zeroFields: true);
        var store = new RunStore(_directory);
        await using (var journal = store.CreateJournal(run with { Samples = [] }))
        {
            foreach (var sample in run.Samples) await journal.AppendAsync(sample);
            await journal.FlushAsync();
        }
        var path = Path.Combine(_directory, $"{run.Id:N}.partial");
        using (var reader = File.OpenText(path))
        using (var header = JsonDocument.Parse((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!))
            Assert.Equal(RecordedRun.CurrentSchemaVersion, header.RootElement.GetProperty("schemaVersion").GetInt32());
        var recovered = new RunStore(_directory);
        Assert.Single(await recovered.ListAsync());
        var loaded = await recovered.LoadAsync(run.Id);
        Assert.Equal(WithoutContactFields(run).SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(WithoutContactFields(run).Samples, loaded.Samples);
        Assert.True(loaded.IsIncomplete);
        if (withTune) Assert.True(loaded.TuneAttachment!.DrivingContinuityInterrupted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task NewJournalDeclaresContactCapableFormatBeforeTheFirstSample()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        var contact = run.Samples[1] with
        {
            State = run.Samples[1].State with
            {
                SmashableVelocityLossMetersPerSecond = 2,
                SmashableMassKilograms = 150
            }
        };
        await using (var journal = store.CreateJournal(run with { Samples = [] }))
        {
            await journal.AppendAsync(run.Samples[0]);
            await journal.AppendAsync(contact);
            await journal.FlushAsync();
        }
        var path = Path.Combine(_directory, $"{run.Id:N}.partial");
        using (var reader = File.OpenText(path))
        using (var header = JsonDocument.Parse((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!))
            Assert.Equal(RecordedRun.CurrentSchemaVersion, header.RootElement.GetProperty("schemaVersion").GetInt32());
        var recovered = new RunStore(_directory);
        Assert.Single(await recovered.ListAsync());
        var loaded = await recovered.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal([run.Samples[0], contact], loaded.Samples);
    }

    [Fact]
    public async Task UnsupportedFutureJournalWithReadablePrefixIsKeptIntact()
    {
        Directory.CreateDirectory(_directory);
        var run = RunTestData.CreateRun() with { SchemaVersion = RecordedRun.CurrentSchemaVersion + 1 };
        var path = Path.Combine(_directory, $"{run.Id:N}.partial");
        var lines = new[] { JsonSerializer.Serialize(run with { Samples = [] }, RunStore.JsonOptions),
            JsonSerializer.Serialize(run.Samples[0], RunStore.JsonOptions), "{\"futureSampleField\":1}" };
        await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var store = new RunStore(_directory);
        Assert.Empty(await store.ListAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_directory, $"{run.Id:N}.wisprun")));
    }

    [Fact]
    public void ContactTelemetryCannotValidateAsLegacyFormat()
    {
        var run = RunTestData.CreateRun();
        run = run with
        {
            Samples = run.Samples.Select(sample => sample with
            {
                State = sample.State with
                {
                    SmashableVelocityLossMetersPerSecond = 0,
                    SmashableMassKilograms = 0
                }
            }).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => RunStore.Validate(run));
        RunStore.Validate(run with { SchemaVersion = RecordedRun.CurrentSchemaVersion });
    }

    [Theory]
    [InlineData(float.NaN, 10f)]
    [InlineData(1f, float.PositiveInfinity)]
    [InlineData(-1f, 10f)]
    [InlineData(501f, 10f)]
    [InlineData(1f, -1f)]
    [InlineData(1f, 10_000_001f)]
    [InlineData(null, 10f)]
    [InlineData(1f, null)]
    [InlineData(null, 0f)]
    [InlineData(0f, null)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, -1f)]
    public async Task InvalidOrPartialObjectImpactMetadataCannotEnterTheRunLibrary(float? loss, float? mass)
    {
        var run = ContactRun(withTune: false, zeroFields: true);
        var sample = run.Samples[0];
        sample = sample with { State = sample.State with { SmashableVelocityLossMetersPerSecond = loss, SmashableMassKilograms = mass } };
        Assert.Throws<InvalidDataException>(() => RunStore.ValidateSample(sample, null));
        var store = new RunStore(_directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(run with { Samples = [sample, run.Samples[1]] }));
        Assert.False(File.Exists(Path.Combine(_directory, $"{run.Id:N}.wisprun")));
    }

    private static RecordedRun ContactRun(bool withTune, bool zeroFields)
    {
        var run = RunTestData.CreateRun() with
        {
            SchemaVersion = RecordedRun.CurrentSchemaVersion,
            Notes = "Keep recorded metadata",
            Markers = [new(.05, "Contact")]
        };
        var snapshot = withTune ? TuneUiTestData.ValidSnapshot() : null;
        return run with
        {
            TuneAttachment = snapshot is null ? null : new(snapshot, "Saved setup", "Tune snapshot", DateTimeOffset.UtcNow, RunTuneAttachmentKind.CurrentAtStart),
            Samples = run.Samples.Select(sample => sample with
            {
                State = sample.State with
                {
                    CarOrdinal = snapshot?.Identity.CarOrdinal ?? sample.State.CarOrdinal,
                    Drivetrain = snapshot is null ? sample.State.Drivetrain : (DrivetrainType)snapshot.Identity.Drivetrain,
                    SmashableVelocityLossMetersPerSecond = zeroFields ? 0 : null,
                    SmashableMassKilograms = zeroFields ? 0 : null
                }
            }).ToArray()
        };
    }

    private static RecordedRun WithoutContactFields(RecordedRun run) => run with
    {
        SchemaVersion = run.TuneAttachment is null ? RecordedRun.BaseSchemaVersion : RecordedRun.TuneAttachmentSchemaVersion,
        Samples = run.Samples.Select(sample => sample with
        {
            State = sample.State with { SmashableVelocityLossMetersPerSecond = null, SmashableMassKilograms = null }
        }).ToArray()
    };

    private static async Task WriteRawRunAsync(string path, RecordedRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(gzip);
        await writer.WriteLineAsync(JsonSerializer.Serialize(run with { Samples = [] }, RunStore.JsonOptions));
        foreach (var sample in run.Samples)
            await writer.WriteLineAsync(JsonSerializer.Serialize(sample, RunStore.JsonOptions));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
