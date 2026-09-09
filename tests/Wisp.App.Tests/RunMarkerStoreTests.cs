using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App.Runs;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunMarkerStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.RunMarkerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FullUnicodeMarkerSetSurvivesSaveExportAndImport()
    {
        var run = RunTestData.CreateRun() with
        {
            Markers = Enumerable.Range(0, RunStore.MaximumMarkers).Select(_ => new RunMarker(.1, new string('\u4E00', 80))).ToArray()
        };
        var store = new RunStore(Path.Combine(_directory, "library"));
        await store.SaveAsync(run);
        Assert.Equal(run.Markers, (await store.LoadAsync(run.Id)).Markers);
        var export = Path.Combine(_directory, "shared.wisprun");
        await store.ExportAsync(run.Id, export);
        var imported = await store.ImportAsync(export);
        Assert.NotEqual(run.Id, imported.Id);
        Assert.Equal(run.Markers, (await store.LoadAsync(imported.Id)).Markers);
    }

    [Fact]
    public async Task SchemaOneFileWithoutMarkersStillImports()
    {
        var run = RunTestData.CreateRun();
        var path = await WriteInput(run, omitMarkers: true);
        var store = new RunStore(Path.Combine(_directory, "library"));
        var imported = await store.ImportAsync(path);
        Assert.Empty((await store.LoadAsync(imported.Id)).Markers);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("past_end")]
    [InlineData("nonfinite")]
    [InlineData("long_label")]
    [InlineData("control")]
    [InlineData("too_many")]
    [InlineData("unordered")]
    [InlineData("missing")]
    public async Task InvalidMarkerInputCannotEnterTheLibrary(string invalid)
    {
        var run = RunTestData.CreateRun();
        var markers = invalid switch
        {
            "negative" => new[] { new RunMarker(-1, "Moment") },
            "past_end" => [new RunMarker(.2, "Moment")],
            "nonfinite" => [new RunMarker(double.NaN, "Moment")],
            "long_label" => [new RunMarker(0, new string('x', 81))],
            "control" => [new RunMarker(0, "hidden\0text")],
            "too_many" => Enumerable.Range(0, 129).Select(_ => new RunMarker(0, "Moment")).ToArray(),
            "unordered" => [new RunMarker(.1, "Later"), new RunMarker(0, "Earlier")],
            _ => [null!]
        };
        var path = await WriteInput(run with { Markers = markers });
        var store = new RunStore(Path.Combine(_directory, "library"));
        var error = await Record.ExceptionAsync(() => store.ImportAsync(path));
        Assert.True(error is InvalidDataException or JsonException);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RecoveryKeepsDurableMarkersAndDiscardsAnIncompleteTrailingMarker()
    {
        var run = RunTestData.CreateRun();
        var store = new RunStore(_directory);
        await using (var journal = store.CreateJournal(run with { Samples = [] }))
        {
            await journal.AppendAsync(run.Samples[0]);
            await journal.AppendMarkerAsync(new(0, "Launch"));
            await journal.AppendAsync(run.Samples[1]);
            await journal.AppendMarkerAsync(new(.1, "Shift"));
            await journal.FlushAsync();
        }
        await File.AppendAllTextAsync(Path.Combine(_directory, $"{run.Id:N}.partial"), "{\"marker\":{", TestContext.Current.CancellationToken);
        var recovery = new RunStore(_directory);
        Assert.Single(await recovery.ListAsync());
        var restored = await recovery.LoadAsync(run.Id);
        Assert.True(restored.IsIncomplete);
        Assert.Equal(new[] { new RunMarker(0, "Launch"), new RunMarker(.1, "Shift") }, restored.Markers);
        Assert.Equal(run.Samples, restored.Samples);
    }

    private async Task<string> WriteInput(RecordedRun run, bool omitMarkers = false)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "input.wisprun");
        var options = new JsonSerializerOptions(RunStore.JsonOptions)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var header = JsonSerializer.SerializeToNode(run with { Samples = [] }, options)!.AsObject();
        if (omitMarkers) header.Remove("markers");
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new StreamWriter(gzip);
        await writer.WriteLineAsync(header.ToJsonString(options));
        foreach (var sample in run.Samples) await writer.WriteLineAsync(JsonSerializer.Serialize(sample, options));
        return path;
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
