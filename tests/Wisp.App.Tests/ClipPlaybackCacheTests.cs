using System.IO;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipPlaybackCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WispPlaybackCacheTests", Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // These fixtures exercise storage ownership, never decoder/color correctness.
    private static readonly byte[] SourceBytes = [1, 2, 3, 4, 5, 6];
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 100, LosslessVideo: true);

    [Fact]
    public async Task CompletedCopyIsReusedAcrossInstancesWithoutEncodingOrChangingOriginalState()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        string preview;
        using (var first = await cache.PrepareAsync(original, SourcePath(original), source, null, Token))
        {
            Assert.False(first.CacheHit);
            Assert.Equal(original.Id, first.Entry.Id);
            Assert.False(first.Entry.Media.LosslessVideo);
            Assert.False(first.Entry.Recording.LosslessVideo);
            Assert.True(first.Entry.Media.HdrVideo);
            Assert.Null(first.Entry.ExportedAtUtc);
            Assert.Null(first.Entry.Media.PublicationReceipt);
            Assert.Equal(original.DurationSeconds, first.Entry.DurationSeconds);
            preview = first.Path;
        }
        var viewed = original with { ViewedAtUtc = DateTimeOffset.UtcNow, ExportedAtUtc = DateTimeOffset.UtcNow };
        using var reused = await new ClipPlaybackCache(_root, exporter).PrepareAsync(viewed, SourcePath(original), source, null, Token);
        Assert.True(reused.CacheHit);
        Assert.Equal(preview, reused.Path);
        Assert.Equal(1, exporter.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
        Assert.True(original.Media.LosslessVideo);
        Assert.Null(original.ExportedAtUtc);
        Assert.False(File.Exists(Path.Combine(_root, ClipLibrary.IndexFileName)));
    }

    [Fact]
    public async Task ReplacedOriginalCannotReuseTheOldCopyEvenWithIdenticalLengthAndTimestamp()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        string firstPath;
        using (var source = Hold(original))
        using (var first = await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) firstPath = first.Path;
        var time = File.GetLastWriteTimeUtc(SourcePath(original));
        File.Move(SourcePath(original), SourcePath(original) + ".old");
        await File.WriteAllBytesAsync(SourcePath(original), SourceBytes, Token);
        File.SetLastWriteTimeUtc(SourcePath(original), time);
        using var replacement = Hold(original);
        using var second = await cache.PrepareAsync(original, SourcePath(original), replacement, null, Token);
        Assert.False(second.CacheHit);
        Assert.NotEqual(firstPath, second.Path);
        Assert.Equal(2, exporter.Calls);
    }

    [Fact]
    public async Task ColdPlaybackStartsWhenRequestedWithoutAnotherObservation()
    {
        var original = await CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = new FakeExporter(async (path, _, token) =>
        {
            entered.SetResult();
            await finish.Task.WaitAsync(token);
            await File.WriteAllBytesAsync(path, new byte[128], token);
        });
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        Assert.Equal(0, exporter.Calls);
        Assert.False(Directory.Exists(Path.Combine(_root, ClipPlaybackCache.DirectoryName)));
        var pending = cache.PrepareAsync(original, SourcePath(original), source, null, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, exporter.Calls);
            Assert.False(pending.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(completed.CacheHit);
        Assert.Equal(original.Id, completed.Entry.Id);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Fact]
    public async Task CancelledColdPlaybackCleansPartialOutputAndRetriesOnlyWhenRequested()
    {
        var original = await CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        string? firstPath = null;
        var exporter = new FakeExporter(async (path, _, token) =>
        {
            await File.WriteAllBytesAsync(path, new byte[128], token);
            if (++attempts == 1)
            {
                firstPath = path;
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        });
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        using var selection = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = cache.PrepareAsync(original, SourcePath(original), source, null, selection.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        selection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, exporter.Calls);
        Assert.False(File.Exists(firstPath));
        using var completed = await cache.PrepareAsync(original, SourcePath(original), source, null, Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.False(completed.CacheHit);
        Assert.Equal(original.Id, completed.Entry.Id);
        Assert.Equal(2, exporter.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
        Assert.False(File.Exists(Path.Combine(_root, ClipLibrary.IndexFileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtCompletionReleasesTheReturnedLeaseAndKeepsTheValidCopy(bool warm)
    {
        var original = await CreateAsync();
        string? playbackPath = null;
        var exporter = new FakeExporter(async (path, _, token) =>
        {
            playbackPath = path;
            await File.WriteAllBytesAsync(path, new byte[128], token);
        });
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        if (warm)
            using (await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) { }
        using var selection = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new InlineProgress(value => { if (value == 100) selection.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.PrepareAsync(original, SourcePath(original), source, progress, selection.Token));
        Assert.NotNull(playbackPath);
        using (var exclusive = new FileStream(playbackPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(128, exclusive.Length);
        using var reused = await cache.PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.True(reused.CacheHit);
        Assert.Equal(playbackPath, reused.Path);
        Assert.Equal(1, exporter.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Fact]
    public async Task MissingOriginalDoesNotServeAStalePlaybackCopy()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        using (var source = Hold(original))
        using (await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) { }
        using var movedSource = new FileStream(SourcePath(original), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        File.Move(SourcePath(original), SourcePath(original) + ".moved");
        await Assert.ThrowsAnyAsync<IOException>(() => cache.PrepareAsync(original, SourcePath(original), movedSource, null, Token));
        Assert.Equal(1, exporter.Calls);
    }

    [Fact]
    public async Task CancelledPreparationWaitsForEncoderCleanupAndNeverPublishes()
    {
        var original = await CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? stagedPath = null;
        var exporter = new FakeExporter(async (path, progress, token) =>
        {
            stagedPath = path;
            await File.WriteAllBytesAsync(path, new byte[128], token);
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cleanupEntered.SetResult(); await releaseCleanup.Task; }
        });
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = cache.PrepareAsync(original, SourcePath(original), source, null, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Token);
            cancellation.Cancel();
            await cleanupEntered.Task.WaitAsync(Token);
            Assert.False(pending.IsCompleted);
            Assert.True(File.Exists(stagedPath));
        }
        finally { releaseCleanup.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(stagedPath));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, ClipPlaybackCache.DirectoryName)));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Fact]
    public async Task AnActivePlaybackLeasePreventsEvictionAndClosedCopiesCanBeEvicted()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter, maximumEntries: 1);
        using var firstSource = Hold(first);
        using var secondSource = Hold(second);
        var lease = await cache.PrepareAsync(first, SourcePath(first), firstSource, null, Token);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => cache.PrepareAsync(second, SourcePath(second), secondSource, null, Token));
            Assert.True(File.Exists(lease.Path));
            Assert.Equal(1, exporter.Calls);
        }
        finally { lease.Dispose(); }
        using var next = await cache.PrepareAsync(second, SourcePath(second), secondSource, null, Token);
        Assert.False(File.Exists(lease.Path));
        Assert.True(File.Exists(next.Path));
        Assert.Equal(2, exporter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvictionPreservesReplacedOrModifiedMedia(bool replace)
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter, maximumEntries: 1);
        using var firstSource = Hold(first);
        string preview;
        using (var lease = await cache.PrepareAsync(first, SourcePath(first), firstSource, null, Token)) preview = lease.Path;
        if (replace) File.Move(preview, Path.Combine(_root, "old-preview.bin"));
        await File.WriteAllBytesAsync(preview, [42], Token);
        using var secondSource = Hold(second);
        await Assert.ThrowsAnyAsync<IOException>(() => cache.PrepareAsync(second, SourcePath(second), secondSource, null, Token));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(preview, Token));
        Assert.Equal(1, exporter.Calls);
    }

    [Fact]
    public async Task StagingBytesCountAgainstTheBudgetAndOversizeIsNotCommitted()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter(async (path, progress, token) =>
        {
            await File.WriteAllBytesAsync(path, new byte[70000], token);
            progress?.Report(50);
            token.ThrowIfCancellationRequested();
        });
        var cache = new ClipPlaybackCache(_root, exporter, maximumBytes: 65536);
        using var source = Hold(original);
        await Assert.ThrowsAnyAsync<IOException>(() => cache.PrepareAsync(original, SourcePath(original), source, null, Token));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, ClipPlaybackCache.DirectoryName)));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Fact]
    public async Task ReplacementDuringFailedPreparationIsPreserved()
    {
        var original = await CreateAsync();
        string? output = null;
        var exporter = new FakeExporter(async (path, progress, token) =>
        {
            output = path;
            File.Move(path, Path.Combine(_root, "old-staging.bin"));
            await File.WriteAllBytesAsync(path, [42], token);
            throw new IOException("Synthetic converter failure.");
        });
        using var source = Hold(original);
        await Assert.ThrowsAnyAsync<IOException>(() => new ClipPlaybackCache(_root, exporter)
            .PrepareAsync(original, SourcePath(original), source, null, Token));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(output!, Token));
    }

    [Fact]
    public async Task UnknownFilesAreKeptAndCannotBeCountedAsFreeSpace()
    {
        var original = await CreateAsync();
        var directory = Path.Combine(_root, ClipPlaybackCache.DirectoryName);
        Directory.CreateDirectory(directory);
        var unrelated = Path.Combine(directory, "unrelated.bin");
        await File.WriteAllBytesAsync(unrelated, [42], Token);
        var exporter = new FakeExporter();
        using var source = Hold(original);
        await Assert.ThrowsAnyAsync<IOException>(() => new ClipPlaybackCache(_root, exporter)
            .PrepareAsync(original, SourcePath(original), source, null, Token));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(unrelated, Token));
        Assert.Equal(0, exporter.Calls);
    }

    [Fact]
    public async Task InterruptedOwnedEntryWithoutCompletionIsRegeneratedAtTheSameKey()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        string path;
        using (var lease = await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) path = lease.Path;
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "complete.json"));
        using var regenerated = await cache.PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.False(regenerated.CacheHit);
        Assert.Equal(path, regenerated.Path);
        Assert.Equal(2, exporter.Calls);
    }

    [Fact]
    public async Task DiagnosticCacheOverrideLeavesTheSourceLibraryUnchanged()
    {
        var original = await CreateAsync();
        var diagnosticRoot = Path.Combine(_root, "diagnostic-output", "cache");
        var exporter = new FakeExporter();
        using var source = Hold(original);
        using var lease = await new ClipPlaybackCache(_root, exporter, cacheDirectory: diagnosticRoot)
            .PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.StartsWith(diagnosticRoot, lease.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, ClipPlaybackCache.DirectoryName)));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedInitializationKeepsUnprovenFilesWithoutBlockingANewCopy(bool partialOwner)
    {
        var original = await CreateAsync();
        var stage = Path.Combine(_root, ClipPlaybackCache.DirectoryName, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var unproven = Path.Combine(stage, $"{original.Id:N}.mp4");
        await File.WriteAllBytesAsync(unproven, [42], Token);
        if (partialOwner) await File.WriteAllTextAsync(Path.Combine(stage, "owner.json"), "{\"revision\":", Token);
        var exporter = new FakeExporter();
        using var source = Hold(original);
        using var prepared = await new ClipPlaybackCache(_root, exporter).PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.False(prepared.CacheHit);
        Assert.Equal(1, exporter.Calls);
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(unproven, Token));
    }

    [Fact]
    public async Task InterruptedInitializationBytesStillConsumeTheBudget()
    {
        var original = await CreateAsync();
        var stage = Path.Combine(_root, ClipPlaybackCache.DirectoryName, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var unproven = Path.Combine(stage, $"{original.Id:N}.mp4");
        await File.WriteAllBytesAsync(unproven, new byte[60000], Token);
        var exporter = new FakeExporter();
        using var source = Hold(original);
        await Assert.ThrowsAnyAsync<IOException>(() => new ClipPlaybackCache(_root, exporter, maximumBytes: 65536)
            .PrepareAsync(original, SourcePath(original), source, null, Token));
        Assert.Equal(60000, new FileInfo(unproven).Length);
        Assert.Equal(0, exporter.Calls);
    }

    [Fact]
    public async Task MissingCompletedPlaybackFileIsRegeneratedWithoutTouchingOriginal()
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        string path;
        using (var first = await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) path = first.Path;
        File.Delete(path);
        using var next = await cache.PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.False(next.CacheHit);
        Assert.Equal(path, next.Path);
        Assert.Equal(2, exporter.Calls);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InterruptedRetirementCannotLeaveTheSourceKeyBlocked(int deletedStage)
    {
        var original = await CreateAsync();
        var exporter = new FakeExporter();
        var cache = new ClipPlaybackCache(_root, exporter);
        using var source = Hold(original);
        string path;
        using (var first = await cache.PrepareAsync(original, SourcePath(original), source, null, Token)) path = first.Path;
        var retired = Path.Combine(_root, ClipPlaybackCache.DirectoryName, "retired-" + Guid.NewGuid().ToString("N"));
        // Simulate process death after retirement, after deleting ownership, or
        // after deleting children but before removing the retired directory.
        Directory.Move(Path.GetDirectoryName(path)!, retired);
        if (deletedStage >= 1) File.Delete(Path.Combine(retired, "owner.json"));
        if (deletedStage >= 2)
            foreach (var child in Directory.EnumerateFiles(retired)) File.Delete(child);
        using var regenerated = await cache.PrepareAsync(original, SourcePath(original), source, null, Token);
        Assert.False(regenerated.CacheHit);
        Assert.Equal(path, regenerated.Path);
        Assert.Equal(2, exporter.Calls);
        Assert.True(Directory.Exists(retired));
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(SourcePath(original), Token));
    }

    private async Task<ClipEntry> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        var entry = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording,
            new(SourceBytes.Length, 1920, 1080, 60, 0, 10_000_000, true, LosslessVideo: true, HdrVideo: true));
        await File.WriteAllBytesAsync(SourcePath(entry), SourceBytes, Token);
        return entry;
    }
    private string SourcePath(ClipEntry entry) => Path.Combine(_root, $"{entry.Id:N}.mp4");
    private FileStream Hold(ClipEntry entry) => new(SourcePath(entry), FileMode.Open, FileAccess.Read, FileShare.Read);
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
    private sealed class FakeExporter(Func<string, IProgress<double>?, CancellationToken, Task>? action = null) : ICompatibleClipExporter
    {
        internal int Calls { get; private set; }
        public async Task ExportAsync(ClipEntry clip, string sourcePath, string stagingPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            if (action is not null) { await action(stagingPath, progress, cancellationToken); return; }
            await File.WriteAllBytesAsync(stagingPath, new byte[128], cancellationToken);
            progress?.Report(98);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
