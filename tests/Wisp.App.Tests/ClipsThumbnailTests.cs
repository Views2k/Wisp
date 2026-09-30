using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsThumbnailTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RequestUsesBoundedUtf16AndExactFileVersion()
    {
        var entry = Entry();
        var path = $@"C:\Clips\{entry.Id:N}.mp4";
        var bytes = ClipThumbnailWire.Request(path, entry, 1234567);
        Assert.Equal("WTR1", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((uint)(path.Length * 2), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ulong)entry.Media.FileBytes, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(1234567ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(path, Encoding.Unicode.GetString(bytes, ClipThumbnailWire.RequestHeaderBytes, path.Length * 2));
        Assert.Throws<InvalidDataException>(() => ClipThumbnailWire.Request($@"\\server\clips\{entry.Id:N}.mp4", entry, 1));
        Assert.Throws<InvalidDataException>(() => ClipThumbnailWire.Request($@"C:\Clips\{Guid.NewGuid():N}.mp4", entry, 1));
        Assert.Throws<InvalidDataException>(() => ClipThumbnailWire.Request(path, entry, 0));
    }

    [Fact]
    public async Task ResponseAcceptsOnlyOneOpaqueFixedSizePoster()
    {
        using var valid = new MemoryStream(Response());
        var image = await ClipThumbnailWire.ReadPosterAsync(valid, Token);
        Assert.NotNull(image);
        Assert.Equal(ClipThumbnailWire.PixelBytes, image.Length);
        var bad = Response(); bad[ClipThumbnailWire.ResponseHeaderBytes + 3] = 0;
        using var transparent = new MemoryStream(bad);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClipThumbnailWire.ReadPosterAsync(transparent, Token));
        bad = Response(); BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(20), uint.MaxValue);
        using var excessive = new MemoryStream(bad);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClipThumbnailWire.ReadPosterAsync(excessive, Token));
        using var trailing = new MemoryStream([.. Response(), 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClipThumbnailWire.ReadPosterAsync(trailing, Token));
        using var incomplete = new MemoryStream(Response()[..25]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ClipThumbnailWire.ReadPosterAsync(incomplete, Token));
    }

    [Fact]
    public async Task NativeRefusalIsFallbackWithoutPixels()
    {
        var bytes = new byte[ClipThumbnailWire.ResponseHeaderBytes];
        "WTP1"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        using var input = new MemoryStream(bytes);
        Assert.Null(await ClipThumbnailWire.ReadPosterAsync(input, Token));
    }

    [Fact]
    public async Task CacheUsesFileVersionAndReturnsFrozenImage()
    {
        using var files = new Files();
        var (entry, path) = await files.AddAsync();
        var decoder = new Decoder();
        using var provider = new RecorderThumbnailProvider(decoder);
        var first = await provider.LoadAsync(entry, path, Token);
        Assert.NotNull(first); Assert.True(first.IsFrozen);
        Assert.Same(first, await provider.LoadAsync(entry, path, Token));
        Assert.Equal(1, decoder.Calls);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
        Assert.NotSame(first, await provider.LoadAsync(entry, path, Token));
        Assert.Equal(2, decoder.Calls);
    }

    [Fact]
    public async Task TransientPreviewFailureCanRetryWithoutChangingTheFile()
    {
        using var files = new Files();
        var (entry, path) = await files.AddAsync();
        var decoder = new Decoder { RefuseFirst = true };
        using var provider = new RecorderThumbnailProvider(decoder);
        Assert.Null(await provider.LoadAsync(entry, path, Token));
        var image = await provider.LoadAsync(entry, path, Token);
        Assert.NotNull(image); Assert.True(image.IsFrozen);
        Assert.Same(image, await provider.LoadAsync(entry, path, Token));
        Assert.Equal(2, decoder.Calls);
    }

    [Fact]
    public async Task CacheEvictsOldestAtFiftyEntries()
    {
        using var files = new Files();
        var decoder = new Decoder();
        using var provider = new RecorderThumbnailProvider(decoder);
        var first = await files.AddAsync();
        await provider.LoadAsync(first.Entry, first.Path, Token);
        for (var index = 0; index < RecorderThumbnailProvider.CacheCapacity; index++)
        {
            var next = await files.AddAsync();
            await provider.LoadAsync(next.Entry, next.Path, Token);
        }
        await provider.LoadAsync(first.Entry, first.Path, Token);
        Assert.Equal(RecorderThumbnailProvider.CacheCapacity + 2, decoder.Calls);
    }

    [Fact]
    public async Task DifferentProvidersShareOneDecoderSlotAndWaitingCanCancel()
    {
        using var files = new Files();
        var one = await files.AddAsync(); var two = await files.AddAsync();
        var firstDecoder = new Decoder { Hold = true }; var secondDecoder = new Decoder();
        using var first = new RecorderThumbnailProvider(firstDecoder);
        using var second = new RecorderThumbnailProvider(secondDecoder);
        var current = first.LoadAsync(one.Entry, one.Path, Token);
        await firstDecoder.Started.Task.WaitAsync(Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = second.LoadAsync(two.Entry, two.Path, cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, secondDecoder.Calls);
        firstDecoder.Release.TrySetResult();
        await current;
    }

    [Fact]
    public async Task HelperStartsWithoutShellOrPathArgumentsAndClosesAfterReply()
    {
        using var child = new Child();
        ProcessStartInfo? start = null;
        var decoder = new RecorderThumbnailDecoder(@"C:\App\Wisp.Recorder.exe", value => { start = value; return child; });
        Assert.NotNull(await decoder.DecodeAsync([1, 2, 3], Token));
        Assert.NotNull(start); Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        Assert.Equal("--thumbnail-stdio", Assert.Single(start.ArgumentList));
        Assert.True(child.Disposed); Assert.False(child.Killed);
    }

    [Fact]
    public async Task FailedWaitStillKillsAndConfirmsExactOwnedChild()
    {
        using var child = new Child { FailFirstWait = true };
        var decoder = new RecorderThumbnailDecoder(@"C:\App\Wisp.Recorder.exe", _ => child);
        Assert.Null(await decoder.DecodeAsync([1], Token));
        Assert.True(child.Killed); Assert.True(child.Disposed); Assert.Equal(2, child.WaitCalls);
    }

    [Fact]
    public async Task UncontainedChildReceivesNoRequestAndIsStopped()
    {
        using var child = new Child { IsReady = false };
        var decoder = new RecorderThumbnailDecoder(@"C:\App\Wisp.Recorder.exe", _ => child);
        Assert.Null(await decoder.DecodeAsync([1], Token));
        Assert.Empty(child.Written); Assert.True(child.Killed); Assert.True(child.Disposed);
    }

    [Fact]
    public void HiddenGalleryDoesNotDecodeAndSlowPreviewDoesNotBlockBrowsing() => OnDispatcher(async () =>
    {
        using var files = new Files(); await files.AddIndexedAsync();
        var thumbnails = new DelayedThumbnails();
        using var model = new ClipsViewModel(new() { StorageDirectory = files.Directory }, new IdleRecorder(), Dispatcher.CurrentDispatcher, thumbnails);
        await model.InitializeAsync();
        Assert.False(thumbnails.Started.Task.IsCompleted);
        model.SetGalleryActive(true);
        await thumbnails.Started.Task.WaitAsync(Token);
        Assert.False(model.IsBusy); Assert.True(model.CanBrowse); Assert.True(model.CanEditSettings);
        Assert.Equal("Loading preview…", Assert.Single(model.Clips).PreviewStatus);
        model.SetGalleryActive(false);
        Assert.True(thumbnails.CapturedToken.IsCancellationRequested);
        thumbnails.Result.TrySetResult(FrozenImage());
        await model.ThumbnailCompletion;
        var card = Assert.Single(model.Clips);
        Assert.Null(card.Thumbnail); Assert.Equal("Preview unavailable", card.PreviewStatus); Assert.Equal("New", card.ReviewState);
        Assert.Equal(1, model.NewClipCount);
    });

    [Fact]
    public void OldFolderPreviewCannotPopulateNewFolder() => OnDispatcher(async () =>
    {
        using var oldFiles = new Files(); using var newFiles = new Files();
        await oldFiles.AddIndexedAsync();
        var thumbnails = new DelayedThumbnails();
        using var model = new ClipsViewModel(new() { StorageDirectory = oldFiles.Directory }, new IdleRecorder(), Dispatcher.CurrentDispatcher, thumbnails);
        model.SetGalleryActive(true); await model.InitializeAsync();
        await thumbnails.Started.Task.WaitAsync(Token);
        var oldWork = model.ThumbnailCompletion;
        await model.SetStorageDirectoryAsync(newFiles.Directory);
        Assert.True(thumbnails.CapturedToken.IsCancellationRequested);
        thumbnails.Result.TrySetResult(FrozenImage()); await oldWork;
        Assert.Empty(model.Clips); Assert.Equal(0, model.NewClipCount); Assert.False(model.IsBusy);
    });

    [Fact]
    public void DisposingModelCancelsOwnedThumbnailProvider() => OnDispatcher(async () =>
    {
        using var files = new Files(); await files.AddIndexedAsync();
        var thumbnails = new DelayedThumbnails();
        var model = new ClipsViewModel(new() { StorageDirectory = files.Directory }, new IdleRecorder(), Dispatcher.CurrentDispatcher, thumbnails);
        model.SetGalleryActive(true); await model.InitializeAsync(); await thumbnails.Started.Task.WaitAsync(Token);
        model.Dispose();
        Assert.True(thumbnails.Disposed); Assert.True(thumbnails.CapturedToken.IsCancellationRequested);
        thumbnails.Result.TrySetResult(FrozenImage()); await model.ThumbnailCompletion;
        Assert.Null(Assert.Single(model.Clips).Thumbnail);
    });

    private static ClipEntry Entry() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, 1080, 60, 75), new(4, 1920, 1080, 60, 0, 10_000_000, false));
    private static byte[] Pixels()
    {
        var pixels = new byte[ClipThumbnailWire.PixelBytes];
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return pixels;
    }
    private static byte[] Response()
    {
        var bytes = new byte[ClipThumbnailWire.ResponseHeaderBytes + ClipThumbnailWire.PixelBytes];
        "WTP1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), ClipThumbnailWire.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), ClipThumbnailWire.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), ClipThumbnailWire.Stride);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), ClipThumbnailWire.PixelBytes);
        Pixels().CopyTo(bytes, ClipThumbnailWire.ResponseHeaderBytes); return bytes;
    }
    private static BitmapSource FrozenImage()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        image.Freeze(); return image;
    }
    private sealed class Decoder : IClipThumbnailDecoder
    {
        public int Calls { get; private set; }
        public bool Hold { get; init; }
        public bool RefuseFirst { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]?> DecodeAsync(byte[] request, CancellationToken cancellationToken)
        { Calls++; Started.TrySetResult(); if (Hold) await Release.Task.WaitAsync(cancellationToken); return RefuseFirst && Calls == 1 ? null : Pixels(); }
    }
    private sealed class Child : IThumbnailChild
    {
        private readonly MemoryStream _input = new();
        public bool IsReady { get; init; } = true;
        public bool FailFirstWait { get; init; }
        public Stream Input => _input;
        public Stream Output { get; } = new MemoryStream(Response());
        public Stream Error { get; } = new MemoryStream();
        public int ExitCode => 0;
        public byte[] Written => _input.ToArray();
        public int WaitCalls { get; private set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); WaitCalls++; return FailFirstWait && WaitCalls == 1 ? Task.FromException(new IOException()) : Task.CompletedTask; }
        public void Kill() => Killed = true;
        public void Dispose() { Disposed = true; _input.Dispose(); Output.Dispose(); Error.Dispose(); }
    }
    private sealed class DelayedThumbnails : IClipThumbnailProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BitmapSource?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CapturedToken { get; private set; }
        public bool Disposed { get; private set; }
        public Task<BitmapSource?> LoadAsync(ClipEntry clip, string validatedMediaPath, CancellationToken cancellationToken)
        { CapturedToken = cancellationToken; Started.TrySetResult(); return Result.Task; }
        public void Dispose() => Disposed = true;
    }
    private sealed class IdleRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot => new(ClipRecorderState.Disabled, false, true, false, "Clipping is off");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispThumbnailContracts", Guid.NewGuid().ToString("N"));
        public async Task<(ClipEntry Entry, string Path)> AddAsync()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var entry = Entry(); var path = Path.Combine(Directory, $"{entry.Id:N}.mp4");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4], Token); return (entry, path);
        }
        public async Task AddIndexedAsync()
        {
            var library = new ClipLibrary(Directory);
            var entry = Entry(); var reserved = await library.ReserveSaveAsync(entry.Recording, Token);
            await File.WriteAllBytesAsync(reserved.MediaPath, [1, 2, 3, 4], Token);
            await library.CommitFinalizedAsync(reserved.Id, entry.Media, Token);
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }
    private static void OnDispatcher(Func<Task> action)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); finished.Set(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), Token), "Thumbnail model test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
