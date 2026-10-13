using System.IO;
using System.Reflection;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunLibraryCancellationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.LibraryCancellation", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("load")]
    [InlineData("list")]
    [InlineData("export")]
    [InlineData("export-all")]
    [InlineData("import")]
    [InlineData("import-many")]
    [InlineData("delete")]
    [InlineData("delete-all")]
    public async Task RevokedQueuedWorkCompletesWithoutWaitingForOrChangingTheStore(string action)
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        var original = Path.Combine(_directory, $"{run.Id:N}.wisprun");
        var bytes = await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken);
        var gate = StoreGate(store);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        using var lifetime = new CancellationTokenSource();
        try
        {
            Task pending = action switch
            {
                "load" => store.LoadAsync(run.Id, lifetime.Token),
                "list" => store.ListAsync(lifetime.Token),
                "export" => store.ExportAsync(run.Id, Path.Combine(_directory, "export.wisprun"), lifetime.Token),
                "export-all" => store.ExportAllAsync(Path.Combine(_directory, "export.zip"), lifetime.Token),
                "import" => store.ImportAsync(original, lifetime.Token),
                "import-many" => store.ImportManyAsync([original], lifetime.Token),
                "delete" => store.DeleteAsync(run.Id, lifetime.Token),
                _ => store.DeleteAllAsync(lifetime.Token)
            };
            lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(_directory, "*.wisprun"));
            Assert.Empty(Directory.GetFiles(_directory, "*.zip"));
            Assert.Empty(Directory.GetDirectories(_directory));
        }
        finally { gate.Release(); }
        Assert.Equal(run.Id, Assert.Single(await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Id);
    }

    [Fact]
    public async Task ImportRevokedAfterInputEnumerationRollsBackItsPreparationAndKeepsExistingRuns()
    {
        var store = new RunStore(_directory);
        var run = RunTestData.CreateRun();
        await store.SaveAsync(run);
        using var lifetime = new CancellationTokenSource();
        IEnumerable<string> Sources()
        {
            yield return Path.Combine(_directory, $"{run.Id:N}.wisprun");
            lifetime.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportManyAsync(Sources(), lifetime.Token));
        Assert.Equal(run.Id, Assert.Single(await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken)).Id);
        Assert.Empty(Directory.GetDirectories(_directory, ".wisp-bulk-*"));
    }

    [Fact]
    public async Task ArchiveCopyObservesCancellationDuringTheStreamAndReleasesItsStagedFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "staged.fixture");
        using var lifetime = new CancellationTokenSource();
        await using var input = new CancellingStream(lifetime);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunArchive.CopyBoundedAsync(
            input, path, RunStore.MaximumFileBytes, cancellationToken: lifetime.Token));
        Assert.Equal(2, input.Reads);
        // The transaction owner can clean up immediately after the cancelled copy.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(65536, exclusive.Length);
    }

    internal static SemaphoreSlim StoreGate(RunStore store) => (SemaphoreSlim)typeof(RunStore)
        .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private sealed class CancellingStream(CancellationTokenSource lifetime) : MemoryStream
    {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++Reads == 2) lifetime.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
