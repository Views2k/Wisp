using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispTuneStoreTests", Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SaveReopenAndMetadataEditPreserveImmutableSnapshotAndSeparateTimes()
    {
        var snapshot = TuneUiTestData.ValidSnapshot();
        var now = DateTimeOffset.UtcNow;
        var store = new TuneStore(_directory, () => now);
        var saved = await store.SaveAsync(snapshot, "  Road setup  ", "First line\nSecond line", TestContext.Current.CancellationToken);
        Assert.Equal("Road setup", saved.Name);
        Assert.Equal(now, saved.SavedAtUtc);
        Assert.Equal(now, saved.ModifiedAtUtc);
        var originalSnapshot = JsonSerializer.Serialize(snapshot, JsonOptions);
        var originalFile = await File.ReadAllBytesAsync(PathFor(saved.Id), TestContext.Current.CancellationToken);

        now = now.AddDays(1);
        var reopened = new TuneStore(_directory, () => now);
        var loaded = await reopened.LoadAsync(saved.Id, TestContext.Current.CancellationToken);
        Assert.Equal(originalSnapshot, JsonSerializer.Serialize(loaded.Snapshot, JsonOptions));
        var edited = await reopened.UpdateMetadataAsync(saved.Id, "New name", "Changed\nmultiline notes", TestContext.Current.CancellationToken);
        Assert.Equal(saved.Id, edited.Id);
        Assert.Equal(saved.SavedAtUtc, edited.SavedAtUtc);
        Assert.Equal(now, edited.ModifiedAtUtc);
        Assert.Equal(originalSnapshot, JsonSerializer.Serialize(edited.Snapshot, JsonOptions));
        Assert.Equal(originalFile, await File.ReadAllBytesAsync(Path.Combine(_directory, $"{saved.Id:N}.bak"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        Assert.Equal("Changed\nmultiline notes", (await new TuneStore(_directory).LoadAsync(saved.Id, TestContext.Current.CancellationToken)).Description);
    }

    [Fact]
    public async Task DuplicateNamesUseDistinctIdsAndDateOrderingHasStableTies()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new TuneStore(_directory, () => now);
        var snapshot = TuneUiTestData.ValidSnapshot();
        var first = await store.SaveAsync(snapshot, "Same name", "One", TestContext.Current.CancellationToken);
        var second = await store.SaveAsync(snapshot, "Same name", "Two", TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), (await store.ListAsync(TestContext.Current.CancellationToken)).Select(item => item.Id));
        now = now.AddDays(1);
        var newest = await store.SaveAsync(snapshot, "Later day", "Three", TestContext.Current.CancellationToken);
        Assert.Equal(newest.Id, (await store.ListAsync(TestContext.Current.CancellationToken))[0].Id);
    }

    [Fact]
    public async Task BackwardClockChangeCannotMoveMetadataTimeBeforeItsPriorValue()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new TuneStore(_directory, () => now);
        var saved = await store.SaveAsync(TuneUiTestData.ValidSnapshot(), "Road", "", TestContext.Current.CancellationToken);
        now = now.AddDays(-1);
        var edited = await store.UpdateMetadataAsync(saved.Id, "Renamed", "Notes", TestContext.Current.CancellationToken);
        Assert.Equal(saved.ModifiedAtUtc, edited.ModifiedAtUtc);
        Assert.Equal(saved.SavedAtUtc, edited.SavedAtUtc);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("identity")]
    [InlineData("unknownMember")]
    [InlineData("snapshot")]
    [InlineData("oversized")]
    public async Task UnreadableNeighborIsRetainedWithoutHidingAValidTune(string failure)
    {
        var store = new TuneStore(_directory);
        var snapshot = TuneUiTestData.ValidSnapshot();
        var valid = await store.SaveAsync(snapshot, "Keep visible", "Notes", TestContext.Current.CancellationToken);
        var broken = await store.SaveAsync(snapshot, "Broken neighbor", "Kept on disk", TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(PathFor(broken.Id), TestContext.Current.CancellationToken);
        var node = JsonNode.Parse(json)!.AsObject();
        string corrupted;
        switch (failure)
        {
            case "version": node["schemaVersion"] = 99; corrupted = node.ToJsonString(); break;
            case "duplicate": corrupted = "{\"schemaVersion\":1," + json[1..]; break;
            case "identity": node["id"] = Guid.NewGuid().ToString(); corrupted = node.ToJsonString(); break;
            case "unknownMember": node["unknown"] = true; corrupted = node.ToJsonString(); break;
            case "snapshot": node["snapshot"]!["fields"]![0]!["displayText"] = "999"; corrupted = node.ToJsonString(); break;
            default: corrupted = new string('x', TuneStore.MaximumFileBytes + 1); break;
        }
        await File.WriteAllTextAsync(PathFor(broken.Id), corrupted, TestContext.Current.CancellationToken);
        var listed = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(valid.Id, Assert.Single(listed).Id);
        Assert.NotNull(store.Warning);
        Assert.Equal(corrupted, await File.ReadAllTextAsync(PathFor(broken.Id), TestContext.Current.CancellationToken));
        var error = await Record.ExceptionAsync(() => store.LoadAsync(broken.Id, TestContext.Current.CancellationToken));
        Assert.True(error is InvalidDataException or JsonException);
    }

    [Theory]
    [InlineData("", "Notes")]
    [InlineData("Bad\nname", "Notes")]
    [InlineData("Name", "Bad\0description")]
    public async Task InvalidMetadataNeverCommitsAFile(string name, string description)
    {
        var store = new TuneStore(_directory);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(TuneUiTestData.ValidSnapshot(), name, description, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task UnknownBuildAndIncompleteSnapshotCannotBecomeSavedTunes()
    {
        var store = new TuneStore(_directory);
        var snapshot = TuneUiTestData.ValidSnapshot();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(snapshot with
        {
            Identity = snapshot.Identity with { GameVersion = "unknown" }
        }, "Unsupported", "", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(snapshot with { Fields = [] }, "Incomplete", "", TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedReplacementKeepsThePreviousMetadataAndLeavesNoTemporaryFile()
    {
        var store = new TuneStore(_directory);
        var saved = await store.SaveAsync(TuneUiTestData.ValidSnapshot(), "Keep this name", "Keep notes", TestContext.Current.CancellationToken);
        await using (var locked = new FileStream(PathFor(saved.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.UpdateMetadataAsync(saved.Id, "Failed change", "New notes", TestContext.Current.CancellationToken));
        }
        var loaded = await store.LoadAsync(saved.Id, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Name, loaded.Name);
        Assert.Equal(saved.Description, loaded.Description);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    private string PathFor(Guid id) => Path.Combine(_directory, $"{id:N}.wisptune");
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
