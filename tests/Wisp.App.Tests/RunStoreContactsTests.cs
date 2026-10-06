using System.IO;
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
    public async Task AddAndRemoveContactPreserveLatestMetadataAndEverySample()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun() with { Markers = [new(0, "Apex")] };
        await store.SaveAsync(run);
        await store.UpdateMetadataAsync(run.Id, "Edited after opening lap", "Current tune", "New notes");
        var time = run.Samples[^1].ElapsedSeconds;
        var markers = await store.SetContactMarkerAsync(run.Id, time, true);
        Assert.Equal(2, markers.Length);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal("Edited after opening lap", loaded.Name);
        Assert.Equal("Current tune", loaded.Tune);
        Assert.Equal("New notes", loaded.Notes);
        Assert.Equal(run.Samples, loaded.Samples);
        Assert.Equal(markers, loaded.Markers);
        Assert.Equal(run.LapTimingMode, loaded.LapTimingMode);
        Assert.Equal(run.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(run.StartedAtUtc, loaded.StartedAtUtc);
        Assert.Equal(2, (await store.SetContactMarkerAsync(run.Id, time, true)).Length);
        Assert.Equal([new RunMarker(0, "Apex")], await store.SetContactMarkerAsync(run.Id, time, false));
        Assert.Equal(run.Samples, (await store.LoadAsync(run.Id)).Samples);
    }

    [Fact]
    public async Task ContactRemovalKeepsOtherMarkersAtTheSameTime()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun() with { Markers = [new(0, "Contact"), new(0, "Apex"), new(.1, "Contact")] };
        await store.SaveAsync(run);
        Assert.Equal([new RunMarker(0, "Apex"), new(.1, "Contact")], await store.SetContactMarkerAsync(run.Id, 0, false));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.1)]
    [InlineData(100000)]
    public async Task InvalidTimeCannotModifySavedRun(double time)
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetContactMarkerAsync(run.Id, time, true));
        Assert.Equal(run.Markers, (await store.LoadAsync(run.Id)).Markers);
    }

    [Fact]
    public async Task MarkerLimitDoesNotReplaceOrLoseExistingMarkers()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun() with
        {
            Markers = Enumerable.Range(0, RunStore.MaximumMarkers).Select(index => new RunMarker(0, $"Note {index}")).ToArray()
        };
        await store.SaveAsync(run);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetContactMarkerAsync(run.Id, 0, true));
        Assert.Equal(run.Markers, (await store.LoadAsync(run.Id)).Markers);
        Assert.Equal(run.Markers, await store.SetContactMarkerAsync(run.Id, 0, false));
    }

    [Fact]
    public async Task ConcurrentEditsReadLatestRunBeforeChangingOnlyTheirOwnedFields()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        await Task.WhenAll(store.UpdateMetadataAsync(run.Id, "Renamed", "Retuned", "Updated"),
            store.SetContactMarkerAsync(run.Id, 0, true), store.SetContactMarkerAsync(run.Id, run.Samples[^1].ElapsedSeconds, true));
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal("Renamed", loaded.Name);
        Assert.Equal("Retuned", loaded.Tune);
        Assert.Equal("Updated", loaded.Notes);
        Assert.Equal(2, loaded.Markers.Count(marker => marker.Label == "Contact"));
        Assert.Equal(run.Samples, loaded.Samples);
    }

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
            Samples = original.Samples.Select(sample => sample with
            {
                State = sample.State with
                {
                    CarOrdinal = snapshot.Identity.CarOrdinal,
                    Drivetrain = (DrivetrainType)snapshot.Identity.Drivetrain,
                    SmashableVelocityLossMetersPerSecond = 0,
                    SmashableMassKilograms = 0
                }
            }).ToArray()
        };
        var store = new RunStore(_directory);
        await store.SaveAsync(run);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(run.TuneAttachment, RunStore.JsonOptions),
            JsonSerializer.Serialize(loaded.TuneAttachment, RunStore.JsonOptions));
        await store.SetContactMarkerAsync(run.Id, 0, true);
        Assert.Equal(RecordedRun.CurrentSchemaVersion, (await store.LoadAsync(run.Id)).SchemaVersion);
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
    public void InvalidOrPartialObjectImpactMetadataCannotEnterTheRunLibrary(float? loss, float? mass)
    {
        var sample = RunTestData.CreateRun().Samples[0];
        sample = sample with { State = sample.State with { SmashableVelocityLossMetersPerSecond = loss, SmashableMassKilograms = mass } };
        Assert.Throws<InvalidDataException>(() => RunStore.ValidateSample(sample, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
