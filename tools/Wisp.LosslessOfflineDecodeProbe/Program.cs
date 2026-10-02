using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LibVLCSharp.Shared;
using static Wisp.LosslessOfflineDecodeProbe.ProbeFiles;

namespace Wisp.LosslessOfflineDecodeProbe;

internal static class Program
{
    private const string SourceSha256 = "48A33FB214FE9F4D4802FAF88E71E61280C2C8CC680F2DF4A0135A1416806ABD";
    private const long SourceBytes = 965569830;
    private const int Width = 3840, Height = 2160;

    private static int Main(string[] args)
    {
        if (args.Length != 8 || args[0] != "--source" || args[2] != "--payload" || args[4] != "--output" ||
            args[6] != "--policy" || args[7] is not ("default" or "no-hurry" or "one-thread" or "no-direct-rendering" or "short-prefetch"))
        {
            Console.WriteLine("--source <reported MP4> --payload <retained payload> --output <new checkout work directory> --policy default|no-hurry|one-thread|no-direct-rendering|short-prefetch");
            return 1;
        }
        // Process termination never waits on output pipes or native decoder teardown.
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        using var files = new ProbeFiles();
        var report = new Report { Policy = args[7], SourceSha256 = SourceSha256 };
        var started = Stopwatch.GetTimestamp();
        var checkedApps = started;
        LibVLC? engine = null;
        DecoderLogCounters? decoderLogs = null;
        MediaPlayer? player = null;
        Media? media = null;
        Pixels? pixels = null;
        StreamWriter? progress = null;
        var ended = 0; var failed = 0; var pausedEvents = 0; var playingEvents = 0;
        var created = false;
        try
        {
            Need(AppsClosed(), "apps-must-be-closed");
            files.Prepare(args[1], SourceSha256, SourceBytes, args[3], args[5]);
            created = true;
            report.ManagedSha256 = files.ManagedHash; report.NativeManifestSha256 = files.ManifestHash;
            progress = new StreamWriter(new FileStream(Path.Combine(files.Output, "progress.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Mark("initialize-packaged-decoder");
            Core.Initialize(files.Native);
            var options = new List<string> { "--ignore-config", "--quiet", "--no-audio", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=none" };
            if (args[7] == "no-hurry") options.Add("--no-avcodec-hurry-up");
            if (args[7] == "one-thread") options.Add("--avcodec-threads=1");
            if (args[7] == "no-direct-rendering") options.Add("--no-avcodec-dr");
            Mark("create-engine");
            engine = new LibVLC(options.ToArray());
            decoderLogs = new DecoderLogCounters(files.Native, engine.NativeReference, started);
            Mark("create-player");
            player = new MediaPlayer(engine) { Mute = true, Volume = 0, EnableMouseInput = false, EnableKeyInput = false };
            pixels = new Pixels(Width, Height, started);
            player.SetVideoFormat("RV32", Width, Height, Width * 4);
            player.SetVideoCallbacks(pixels.Lock, pixels.Unlock, null);
            player.EndReached += (_, _) => Interlocked.Exchange(ref ended, 1);
            player.EncounteredError += (_, _) => Interlocked.Exchange(ref failed, 1);
            player.Paused += (_, _) => Interlocked.Increment(ref pausedEvents);
            player.Playing += (_, _) => Interlocked.Increment(ref playingEvents);
            media = new Media(engine, files.Source, FromType.FromPath, ":start-paused", ":no-audio");
            if (args[7] == "short-prefetch") media.AddOption(":file-caching=100");
            player.Media = media;
            Mark("play-start-paused");
            Need(player.Play(), "play-rejected");
            while (player.State != VLCState.Paused) Tick();
            pixels.SetPhase("paused-seek");
            Mark("paused-seek-zero");
            player.Time = 0;
            while (pixels.Count == 0) Tick();
            Need(player.State == VLCState.Paused && player.Time is >= 0 and <= 34, "initial-paused-state-invalid");
            report.PreparedPaused = true;
            report.ReportedDurationMs = player.Length;
            Need(Math.Abs(report.ReportedDurationMs - 2467) <= 100, "source-duration-mismatch");
            pixels.SetPhase("explicit-play");
            Mark("resume");
            player.SetPause(false);
            while (Volatile.Read(ref ended) == 0) Tick();
            report.EndReached = true;
            Mark("end-reached");
        }
        catch (ProbeFailure error) { report.Failure = error.Reason; }
        catch (Exception error) { report.Failure = error.GetType().Name; report.HResult = error.HResult; }
        finally
        {
            // Hold callbacks, memory, media and source until native Stop/Dispose joins.
            // If a native call blocks, the independent process deadline terminates this probe.
            try
            {
                Mark("stop-player"); player?.Stop();
                Mark("dispose-player"); player?.Dispose(); player = null;
                Mark("dispose-media"); media?.Dispose(); media = null;
                decoderLogs?.Dispose(); report.DecoderLogs = decoderLogs?.Read(); decoderLogs = null;
                if (report.DecoderLogs?.CallbackFailed == true) report.Failure ??= "log-callback-failed";
                Mark("dispose-engine"); engine?.Dispose(); engine = null;
                report.Disposed = true;
                report.DecoderStoppedElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (pixels is not null)
                {
                    var pixelWorkStarted = Stopwatch.GetTimestamp();
                    pixels.HashCapturedFrames();
                    report.PostDecodePixelProcessingMilliseconds = Stopwatch.GetElapsedTime(pixelWorkStarted).TotalMilliseconds;
                    report.Frames = pixels.Records.ToArray(); report.CallbackFailure = pixels.Failure;
                    report.CallbackElapsedMilliseconds = pixels.ElapsedMilliseconds;
                    if (created) pixels.WriteImages(files.Output);
                    pixels.Dispose(); pixels = null;
                }
            }
            catch (Exception error)
            {
                report.Failure ??= "cleanup-failed"; report.HResult = error.HResult;
                // Keep every remaining owner alive until process exit after the report.
            }
            report.PausedEvents = Volatile.Read(ref pausedEvents); report.PlayingEvents = Volatile.Read(ref playingEvents);
            report.ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            report.Completed = report.Failure is null && report.CallbackFailure is null && report.PreparedPaused && report.EndReached && report.Disposed;
            if (created)
            {
                try
                {
                    Mark("write-report");
                    using var output = new FileStream(Path.Combine(files.Output, "decode.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
                }
                catch { report.Completed = false; report.Failure ??= "report-write-failed"; }
            }
            try { progress?.Dispose(); } catch { report.Completed = false; }
            GC.KeepAlive(player); GC.KeepAlive(media); GC.KeepAlive(engine); GC.KeepAlive(pixels); GC.KeepAlive(decoderLogs);
            if (!report.Disposed) Environment.Exit(2);
        }
        Console.WriteLine(report.Completed ? "Offline decode completed; compare frame hashes and images." : "Offline decode did not complete; inspect the sanitized report.");
        return report.Completed ? 0 : 2;

        void Tick()
        {
            Need(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30), "soft-deadline");
            Need(Volatile.Read(ref failed) == 0, "decoder-error-event");
            Need(pixels?.Failure is null, "pixel-callback-failed");
            if (Stopwatch.GetElapsedTime(checkedApps) >= TimeSpan.FromMilliseconds(250))
            { checkedApps = Stopwatch.GetTimestamp(); Need(AppsClosed(), "apps-started-during-check"); }
            Thread.Sleep(10);
        }
        void Mark(string stage)
        {
            report.Stage = stage;
            try
            {
                progress?.WriteLine(JsonSerializer.Serialize(new { stage, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }));
                progress?.Flush();
            }
            catch { report.Failure ??= "progress-write-failed"; }
        }
    }

    private static bool AppsClosed()
    {
        foreach (var name in new[] { "Wisp", "Wisp.Recorder", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return true;
    }

    private sealed class Pixels : IDisposable
    {
        private readonly object _gate = new();
        private readonly int _width, _height, _bytes;
        private readonly long _started;
        private readonly byte[] _rgb;
        private IntPtr _allocation, _pixels;
        private readonly ImageSample[] _images;
        private string _phase = "opening";
        private string? _failure;
        private int _count;
        internal readonly List<Frame> Records = new(512);
        internal int Count => Volatile.Read(ref _count);
        internal string? Failure => Volatile.Read(ref _failure);
        internal double ElapsedMilliseconds { get; private set; }

        internal Pixels(int width, int height, long started)
        {
            _width = width; _height = height; _bytes = checked(width * height * 4); _started = started;
            _rgb = new byte[checked(width * height * 3)];
            _images = [new(0, _bytes), new(42, _bytes), new(75, _bytes)];
            _allocation = Marshal.AllocHGlobal(_bytes + 31);
            _pixels = new IntPtr((_allocation.ToInt64() + 31) & ~31L);
        }
        internal void SetPhase(string phase) => Volatile.Write(ref _phase, phase);
        internal IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            Monitor.Enter(_gate);
            Marshal.WriteIntPtr(planes, _pixels);
            return IntPtr.Zero;
        }
        internal unsafe void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            var began = Stopwatch.GetTimestamp();
            try
            {
                if (_failure is not null) return;
                Need(_count < 512, "callback-count-bound");
                // Three preallocated copies only. Conversion and hashing run after native teardown.
                var sample = _count switch { 0 => _images[0], 42 => _images[1], 75 => _images[2], _ => null };
                if (sample is not null)
                {
                    new ReadOnlySpan<byte>((void*)_pixels, _bytes).CopyTo(sample.Bytes);
                    sample.Captured = true;
                }
                Records.Add(new(_count, Volatile.Read(ref _phase), Stopwatch.GetElapsedTime(_started).TotalMilliseconds, null));
                Volatile.Write(ref _count, _count + 1);
            }
            catch (ProbeFailure error) { Volatile.Write(ref _failure, error.Reason); }
            catch (Exception error) { Volatile.Write(ref _failure, error.GetType().Name); }
            finally { ElapsedMilliseconds += Stopwatch.GetElapsedTime(began).TotalMilliseconds; Monitor.Exit(_gate); }
        }
        internal void HashCapturedFrames()
        {
            foreach (var image in _images)
            {
                if (!image.Captured) continue;
                // VLC RV32 on Windows is B,G,R,padding. Ignore padding in the RGB oracle.
                for (int input = 0, output = 0; input < image.Bytes.Length; input += 4)
                { _rgb[output++] = image.Bytes[input + 2]; _rgb[output++] = image.Bytes[input + 1]; _rgb[output++] = image.Bytes[input]; }
                Records[image.Ordinal] = Records[image.Ordinal] with { RgbSha256 = Convert.ToHexString(SHA256.HashData(_rgb)) };
            }
        }
        internal void WriteImages(string directory)
        {
            foreach (var image in _images)
            {
                if (!image.Captured) continue;
                using var file = new FileStream(Path.Combine(directory, $"callback-{image.Ordinal:D3}.bmp"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new BinaryWriter(file);
                writer.Write((ushort)0x4D42); writer.Write(checked(54 + _bytes)); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(_width); writer.Write(-_height); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(_bytes); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(image.Bytes);
            }
        }
        public void Dispose() { if (_allocation != IntPtr.Zero) Marshal.FreeHGlobal(_allocation); _allocation = _pixels = IntPtr.Zero; }
        private sealed class ImageSample(int ordinal, int bytes)
        {
            internal readonly int Ordinal = ordinal;
            internal readonly byte[] Bytes = new byte[bytes];
            internal bool Captured;
        }
    }

    private sealed record Frame(int CallbackOrdinal, string Phase, double ElapsedMs, string? RgbSha256);
    private sealed class Report
    {
        public string Policy { get; set; } = "";
        public string SourceSha256 { get; set; } = "";
        public string ManagedSha256 { get; set; } = "";
        public string NativeManifestSha256 { get; set; } = "";
        public string Stage { get; set; } = "not-started";
        public string? Failure { get; set; }
        public int HResult { get; set; }
        public bool PreparedPaused { get; set; }
        public bool EndReached { get; set; }
        public bool Disposed { get; set; }
        public bool Completed { get; set; }
        public long ReportedDurationMs { get; set; }
        public int PausedEvents { get; set; }
        public int PlayingEvents { get; set; }
        public double ElapsedMs { get; set; }
        public double DecoderStoppedElapsedMs { get; set; }
        public double CallbackElapsedMilliseconds { get; set; }
        public double PostDecodePixelProcessingMilliseconds { get; set; }
        public string? CallbackFailure { get; set; }
        public DecoderLogCounters.Snapshot? DecoderLogs { get; set; }
        public Frame[] Frames { get; set; } = [];
        public string PixelPolicy => "Only callback ordinals 0, 42 and 75 are copied into preallocated buffers; their RGB conversion and hashes run after decoder teardown. All other RgbSha256 values are null and unverified.";
        public string Scope => "Offline software decode of one hash-pinned saved clip. No audio, windows, GPU decode or capture. Callback ordinals are not media-frame indices; align sampled RGB hashes to FFmpeg. Three samples do not prove every displayed frame or real player performance/A-V sync.";
    }
}
