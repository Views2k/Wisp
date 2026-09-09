using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispRunTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavedLibraryMetadataDeleteAndRestorePreserveSamples()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        var summary = Assert.Single(await store.ListAsync());
        Assert.Equal(run.Id, summary.Id);
        Assert.Equal(2, summary.SampleCount);
        var loaded = await store.LoadAsync(run.Id);
        Assert.Equal(run.Samples, loaded.Samples);
        await store.UpdateMetadataAsync(run.Id, "  Wet drift  ", "Road tune", "Two laps\nSame tires");
        Assert.Equal("Wet drift", (await store.LoadAsync(run.Id)).Name);
        await store.DeleteAsync(run.Id);
        Assert.Empty(await store.ListAsync());
        Assert.True(File.Exists(Path.Combine(_directory, "Deleted", $"{run.Id:N}.wisprun")));
        await store.RestoreAsync(run.Id);
        Assert.Equal(run.Samples, (await store.LoadAsync(run.Id)).Samples);
    }

    [Fact]
    public async Task ExportAndImportUseNewIdentityAndNeverOverwriteDestination()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        var export = Path.Combine(_directory, $"{Guid.NewGuid():N}.wisprun");
        await store.ExportAsync(run.Id, export);
        var original = await File.ReadAllBytesAsync(export, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => store.ExportAsync(run.Id, export));
        Assert.Equal(original, await File.ReadAllBytesAsync(export, TestContext.Current.CancellationToken));
        var imported = await store.ImportAsync(export);
        Assert.NotEqual(run.Id, imported.Id);
        Assert.Equal(run.Samples, (await store.LoadAsync(imported.Id)).Samples);
    }

    [Fact]
    public async Task BadMetadataNeverReplacesExistingRun()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateMetadataAsync(run.Id, "bad\0name", "", ""));
        Assert.Equal(run.Name, (await store.LoadAsync(run.Id)).Name);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CorruptSummaryIsRebuiltFromTheSavedRun()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        await File.WriteAllTextAsync(Path.Combine(_directory, $"{run.Id:N}.summary.json"), "{broken", TestContext.Current.CancellationToken);
        var summary = Assert.Single(await store.ListAsync());
        Assert.Equal(run.Id, summary.Id);
        Assert.Equal(run.Samples, (await store.LoadAsync(run.Id)).Samples);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("nonfinite")]
    [InlineData("time")]
    [InlineData("calibration")]
    [InlineData("car")]
    [InlineData("missing")]
    public async Task InvalidImportIsRejectedBeforeAnyLibraryWrite(string kind)
    {
        Directory.CreateDirectory(_directory);
        var run = RunTestData.CreateRun();
        run = kind switch
        {
            "version" => run with { SchemaVersion = 100 },
            "nonfinite" => run with { Samples = [run.Samples[0] with { ElapsedSeconds = double.NaN }] },
            "time" => run with { Samples = [run.Samples[1], run.Samples[0]] },
            "calibration" => run with { Samples = [run.Samples[0] with { WheelSpeedMetersPerSecond = 50, FrontRadiusMeters = .35, RearRadiusMeters = .35 }] },
            "car" => run with { Samples = [run.Samples[0], run.Samples[1] with { State = run.Samples[1].State with { CarOrdinal = 2 } }] },
            _ => run with { Samples = [null!] }
        };
        var source = Path.Combine(_directory, "invalid.wisprun");
        var options = new JsonSerializerOptions(RunStore.JsonOptions) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        await using (var file = File.Create(source))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new StreamWriter(gzip))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(run with { Samples = [] }, options));
            foreach (var sample in run.Samples) await writer.WriteLineAsync(JsonSerializer.Serialize(sample, options));
        }
        var store = new RunStore(Path.Combine(_directory, "library"));
        var error = await Record.ExceptionAsync(() => store.ImportAsync(source));
        Assert.True(error is InvalidDataException or JsonException);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task OversizeInputIsRejectedWithoutReadingItsPayload()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "oversize.wisprun");
        using (var file = File.Create(source)) file.SetLength(RunStore.MaximumFileBytes + 1);
        var store = new RunStore(Path.Combine(_directory, "library"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(source));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task PartialJournalRecoversCompletePrefixAndMarksItIncomplete()
    {
        var run = RunTestData.CreateRun();
        var store = new RunStore(_directory);
        await using (var journal = store.CreateJournal(run with { Samples = [] }))
        {
            await journal.AppendAsync(run.Samples[0]);
            await journal.AppendAsync(run.Samples[1]);
            await journal.FlushAsync();
            Assert.Empty(await store.ListAsync());
        }
        await File.AppendAllTextAsync(Path.Combine(_directory, $"{run.Id:N}.partial"), "{\"elapsedSeconds\":", TestContext.Current.CancellationToken);
        var recoveredStore = new RunStore(_directory);
        var summary = Assert.Single(await recoveredStore.ListAsync());
        Assert.True(summary.IsIncomplete);
        Assert.Contains("Recovered", summary.FinishReason);
        Assert.Equal(run.Samples, (await recoveredStore.LoadAsync(run.Id)).Samples);
        Assert.False(File.Exists(Path.Combine(_directory, $"{run.Id:N}.partial")));
    }

    [Fact]
    public async Task ActiveJournalCannotBeRecoveredOrDuplicateItsCompletedRun()
    {
        var run = RunTestData.CreateRun();
        var store = new RunStore(_directory);
        await using var journal = store.CreateJournal(run with { Samples = [] });
        await journal.AppendAsync(run.Samples[0]);
        await journal.FlushAsync();
        Assert.Empty(await store.ListAsync());
        await store.SaveAsync(run);
        Assert.Single(await store.ListAsync());
        Assert.False((await store.LoadAsync(run.Id)).IsIncomplete);
    }

    [Fact]
    public async Task FullLibraryRejectsAdditionalRecordingAndKeepsExistingFiles()
    {
        Directory.CreateDirectory(_directory);
        for (var index = 0; index < RunStore.MaximumLibraryEntries; index++)
            await File.WriteAllTextAsync(Path.Combine(_directory, $"{Guid.NewGuid():N}.wisprun"), "retained", TestContext.Current.CancellationToken);
        var store = new RunStore(_directory);
        Assert.Throws<RunLibraryFullException>(() => store.CreateJournal(RunTestData.CreateRun() with { Samples = [] }));
        Assert.True(store.IsFull);
        Assert.Equal(RunStore.MaximumLibraryEntries, Directory.EnumerateFiles(_directory, "*.wisprun").Count());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.partial"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

internal static class RunTestData
{
    internal static VehicleState State(uint time = 0) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = time,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        CarOrdinal = 2468,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        GroundSpeedMetersPerSecond = 10,
        WheelRotationRadiansPerSecond = new(10, 10, 10, 10),
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 3000,
        EngineMaximumRpm = 8000,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 128,
        Brake = 0
    };
    internal static RecordedRun CreateRun() => new()
    {
        Name = "Test run",
        StartedAtUtc = DateTimeOffset.UtcNow,
        FinishReason = "Stopped by you",
        Samples = [new() { State = State(), ElapsedSeconds = 0, IsDriving = true }, new() { State = State(100), ElapsedSeconds = .1, IsDriving = true }]
    };
}
