using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App.Clips;

internal interface IClipThumbnailDecoder
{
    Task<byte[]?> DecodeAsync(byte[] request, CancellationToken cancellationToken);
}

internal static class ClipThumbnailWire
{
    internal const int Width = 320, Height = 180, Stride = Width * 4, PixelBytes = Stride * Height;
    internal const int RequestHeaderBytes = 32, ResponseHeaderBytes = 24, MaximumPathBytes = 8192;
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    internal static byte[] Request(string path, ClipEntry clip, long lastWriteFileTime)
    {
        if (clip.Id == Guid.Empty || clip.Media.FileBytes is <= 0 or > ClipLibrary.MaximumMediaBytes || lastWriteFileTime <= 0 ||
            !ClipsSettings.ResolutionChoices.Contains(clip.Media.Height) ||
            clip.Media.Width != (clip.Media.Height == 480 ? 854 : clip.Media.Height * 16 / 9) ||
            !Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !string.Equals(Path.GetFileName(path), $"{clip.Id:N}.mp4", StringComparison.Ordinal) ||
            !ClipsSettings.TryNormalizeStorageDirectory(Path.GetDirectoryName(path), out var parent) ||
            !string.Equals(path, Path.Combine(parent, Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The clip preview source is invalid.");
        var name = Utf16.GetBytes(path);
        if (name.Length is 0 or > MaximumPathBytes) throw new InvalidDataException("The clip preview path is too long.");
        var result = new byte[RequestHeaderBytes + name.Length];
        "WTR1"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)name.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), (ulong)clip.Media.FileBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)clip.Media.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (uint)clip.Media.Height);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), (ulong)lastWriteFileTime);
        name.CopyTo(result, RequestHeaderBytes);
        return result;
    }

    internal static async Task<byte[]?> ReadPosterAsync(Stream source, CancellationToken cancellationToken)
    {
        var header = new byte[ResponseHeaderBytes];
        await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("WTP1"u8)) throw new InvalidDataException("The clip preview response is invalid.");
        var status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var stride = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        byte[]? image = null;
        if (status == 0 && width == Width && height == Height && stride == Stride && length == PixelBytes)
        {
            image = new byte[PixelBytes];
            await source.ReadExactlyAsync(image, cancellationToken).ConfigureAwait(false);
            for (var index = 3; index < image.Length; index += 4)
                if (image[index] != 255) throw new InvalidDataException("The clip preview has an invalid alpha channel.");
        }
        else if (status != 1 || width != 0 || height != 0 || stride != 0 || length != 0)
            throw new InvalidDataException("The clip preview dimensions are invalid.");
        if (await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("The clip preview response has trailing data.");
        return image;
    }
}

internal sealed class RecorderThumbnailProvider : IClipThumbnailProvider, IDisposable
{
    // Shared across provider instances: one thumbnail decoder can run at a time.
    private static readonly SemaphoreSlim DecoderGate = new(1, 1);
    private readonly IClipThumbnailDecoder _decoder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly object _sync = new();
    private readonly LinkedList<(string Key, BitmapSource? Image)> _cache = [];
    private bool _disposed;
    internal const int CacheCapacity = 50;

    internal RecorderThumbnailProvider(string helperPath) : this(new RecorderThumbnailDecoder(helperPath)) { }
    internal RecorderThumbnailProvider(IClipThumbnailDecoder decoder) { _decoder = decoder; _token = _lifetime.Token; }

    public async Task<BitmapSource?> LoadAsync(ClipEntry clip, string mediaPath, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        var token = linked.Token;
        var metadata = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // Reject nonlocal or non-owned syntax before touching the file.
            _ = ClipThumbnailWire.Request(mediaPath, clip, 1);
            ClipLibrary.CheckPath(mediaPath);
            var file = new FileInfo(mediaPath);
            if (!file.Exists || file.Length != clip.Media.FileBytes) throw new InvalidDataException("The saved clip has changed.");
            var modified = file.LastWriteTimeUtc.ToFileTimeUtc();
            var request = ClipThumbnailWire.Request(mediaPath, clip, modified);
            var key = $"{mediaPath.ToUpperInvariant()}|{clip.Media.FileBytes}|{modified}|{clip.Media.LosslessVideo}";
            return (Request: request, Key: key);
        }, token).ConfigureAwait(false);
        await DecoderGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var node = Find(metadata.Key);
                if (node is not null) { _cache.Remove(node); _cache.AddFirst(node); return node.Value.Image; }
            }
            var pixels = clip.Media.LosslessVideo
                ? await LosslessThumbnailDecoder.DecodeAsync(clip, mediaPath, token).ConfigureAwait(false)
                : await _decoder.DecodeAsync(metadata.Request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            BitmapSource? image = null;
            if (pixels is not null)
            {
                if (pixels.Length != ClipThumbnailWire.PixelBytes) throw new InvalidDataException("The preview data has the wrong size.");
                image = BitmapSource.Create(ClipThumbnailWire.Width, ClipThumbnailWire.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, ClipThumbnailWire.Stride);
                image.Freeze();
            }
            if (image is not null) lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _cache.AddFirst((metadata.Key, image));
                    if (_cache.Count > CacheCapacity) _cache.RemoveLast();
                }
            return image;
        }
        finally { DecoderGate.Release(); }
    }

    private LinkedListNode<(string Key, BitmapSource? Image)>? Find(string key)
    {
        for (var item = _cache.First; item is not null; item = item.Next)
            if (string.Equals(item.Value.Key, key, StringComparison.Ordinal)) return item;
        return null;
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _cache.Clear(); }
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}

internal interface IThumbnailChild : IDisposable
{
    bool IsReady { get; }
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class RecorderThumbnailDecoder : IClipThumbnailDecoder
{
    private readonly string _helperPath;
    private readonly Func<ProcessStartInfo, IThumbnailChild> _launch;
    // Retain exact ownership if the OS could not confirm termination. Further
    // previews fail closed; the owned job still kills this child on app exit.
    private static IThumbnailChild? _unconfirmed;
    internal RecorderThumbnailDecoder(string helperPath, Func<ProcessStartInfo, IThumbnailChild>? launch = null)
    {
        if (!Path.IsPathFullyQualified(helperPath) || !string.Equals(Path.GetFileName(helperPath), "Wisp.Recorder.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed recorder helper path is invalid.", nameof(helperPath));
        _helperPath = helperPath; _launch = launch ?? (start => new ThumbnailProcessChild(start));
    }

    public async Task<byte[]?> DecodeAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _unconfirmed) is not null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        IThumbnailChild? child = null;
        Task[] running = [];
        var exited = false;
        try
        {
            var start = new ProcessStartInfo(_helperPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_helperPath)!
            };
            start.ArgumentList.Add("--thumbnail-stdio");
            child = _launch(start);
            if (!child.IsReady) throw new IOException("The thumbnail helper could not be contained.");
            var poster = ClipThumbnailWire.ReadPosterAsync(child.Output, deadline.Token);
            var errors = DiscardErrorAsync(child.Error, deadline.Token);
            var exit = child.WaitForExitAsync(deadline.Token);
            running = [poster, errors, exit];
            await child.Input.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await child.Input.FlushAsync(deadline.Token).ConfigureAwait(false);
            child.Input.Close();
            await Task.WhenAll(poster, errors, exit).ConfigureAwait(false);
            exited = true;
            if (child.ExitCode != 0) return null;
            return await poster.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally
        {
            deadline.Cancel();
            // Input failure can precede the combined await; still observe every
            // fault while cancellation unwinds the bounded pipe operations.
            _ = Task.WhenAll(running).ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (child is not null)
            {
                if (!exited)
                {
                    try { child.Kill(); } catch (Exception error) when (error is not OutOfMemoryException) { }
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await child.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); exited = true; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                }
                if (exited) child.Dispose();
                else Interlocked.CompareExchange(ref _unconfirmed, child, null);
            }
        }
    }

    private static async Task DiscardErrorAsync(Stream source, CancellationToken token)
    {
        var buffer = new byte[1024];
        var total = 0;
        for (; ; )
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > 16 * 1024) throw new InvalidDataException("The thumbnail helper returned too much diagnostic data.");
        }
    }
}

internal sealed class ThumbnailProcessChild : IThumbnailChild
{
    private readonly Process _process = new();
    private readonly SafeFileHandle _job;
    public bool IsReady { get; private set; }
    internal ThumbnailProcessChild(ProcessStartInfo start)
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) { _job.Dispose(); throw new Win32Exception(); }
        var started = false;
        try
        {
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())) throw new Win32Exception();
            _process.StartInfo = start;
            if (!_process.Start()) throw new IOException("The thumbnail helper could not start.");
            started = true;
            // The helper cannot decode until stdin is supplied. Assign before
            // returning its pipes, with no breakaway or other process membership.
            IsReady = AssignProcessToJobObject(_job, _process.Handle);
        }
        catch
        {
            // After Start, return an unready owned child to the decoder, which
            // performs and verifies cleanup without losing process ownership.
            if (started) return;
            _job.Dispose(); _process.Dispose();
            throw;
        }
    }
    public Stream Input => _process.StandardInput.BaseStream;
    public Stream Output => _process.StandardOutput.BaseStream;
    public Stream Error => _process.StandardError.BaseStream;
    public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Kill()
    {
        _job.Dispose();
        try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { }
    }
    public void Dispose() { _job.Dispose(); _process.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
