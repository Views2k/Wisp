using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderHostIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackagedHelperConfiguresAndStopsWithoutStartingMedia(bool preserveHdrRecording)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Wisp.Recorder.exe");
        Assert.True(File.Exists(helper), "The recorder helper must be copied beside the test application.");
        using var directory = new IsolatedDirectory();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        ObservedRealChild? child = null;
        var launches = 0;
        await using var client = new RecorderProcessClient(helper, start =>
        {
            Interlocked.Increment(ref launches);
            return child = new ObservedRealChild(start);
        }, directory.BufferPath);
        var states = new ConcurrentQueue<RecorderStateUpdate>();
        var waiting = new TaskCompletionSource<RecorderStateUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, state) =>
        {
            states.Enqueue(state);
            if (state.State == "waiting") waiting.TrySetResult(state);
            else if (state.State == "error") waiting.TrySetException(new RecorderClientException(state.Reason));
        };

        // Config creates no MediaSession. This test deliberately never supplies
        // a target identity or sends Start, Save, capture, or audio commands.
        try { await client.OpenAsync(new(60, 1080, 60, 75, PreserveHdrRecording: preserveHdrRecording), directory.Path, deadline.Token); }
        catch (RecorderClientException error)
        {
            throw new Xunit.Sdk.XunitException($"Helper configuration failed: reason={error.Reason}; nativeReason={client.FailureDiagnostic?.Reason}; stage={client.FailureDiagnostic?.Stage}");
        }
        var nativeWaiting = await waiting.Task.WaitAsync(deadline.Token);
        Assert.Equal("waiting_for_game", nativeWaiting.Reason);
        Assert.False(Directory.EnumerateFileSystemEntries(directory.Path).Any(), "Config must not create storage files.");
        Assert.False(Directory.Exists(System.IO.Path.Combine(directory.Path, $".wisp-recorder-{client.Session:N}")));

        await client.StopAsync(deadline.Token);
        Assert.Equal(1, launches);
        Assert.NotNull(child);
        Assert.Equal(0, child.Kills);
        Assert.Equal(1, child.CompletedWaits);
        Assert.Equal(1, child.Disposals);
        Assert.All(states, state => Assert.True(state.State is "waiting" or "stopped",
            "Config and Stop must not produce a media-active or error state."));
        Assert.False(Directory.EnumerateFileSystemEntries(directory.Path).Any(), "Stopping an unstarted helper must leave storage empty.");
        var cleanup = await ClipBufferStore.CleanupAsync(directory.BufferPath, deadline.Token);
        Assert.Equal(0, cleanup.Retained);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.BufferPath));
    }

    // Delegates every pipe, launch and shutdown operation to the real child.
    // Counting only the lifecycle calls distinguishes normal exit from fallback
    // termination without introducing a fake protocol peer or process discovery.
    private sealed class ObservedRealChild(ProcessStartInfo start) : IRecorderChild
    {
        private readonly RecorderProcessChild _child = new(start);
        private int _kills, _completedWaits, _disposals;
        internal int Kills => Volatile.Read(ref _kills);
        internal int CompletedWaits => Volatile.Read(ref _completedWaits);
        internal int Disposals => Volatile.Read(ref _disposals);
        public Stream Input => _child.Input;
        public Stream Output => _child.Output;
        public Stream Error => _child.Error;
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _child.WaitForExitAsync(cancellationToken);
            Interlocked.Increment(ref _completedWaits);
        }
        public void Kill() { Interlocked.Increment(ref _kills); _child.Kill(); }
        public void Dispose() { Interlocked.Increment(ref _disposals); _child.Dispose(); }
    }

    private sealed class IsolatedDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "WispRecorderHostIntegrationTests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_root, "clips");
        internal string BufferPath => System.IO.Path.Combine(_root, "buffer");
        internal IsolatedDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // Preserve unexpected files for diagnosis; remove only this new,
            // empty test directory, never a recorder spool or saved media.
            if (Directory.Exists(Path) && !Directory.EnumerateFileSystemEntries(Path).Any())
                Directory.Delete(Path, recursive: false);
            if (Directory.Exists(BufferPath) && !Directory.EnumerateFileSystemEntries(BufferPath).Any())
                Directory.Delete(BufferPath, recursive: false);
            if (Directory.Exists(_root) && !Directory.EnumerateFileSystemEntries(_root).Any())
                Directory.Delete(_root, recursive: false);
        }
    }
}
