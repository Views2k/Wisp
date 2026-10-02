using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using static Wisp.MpvOfflineDecodeProbe.ProbeFiles;

namespace Wisp.MpvOfflineDecodeProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--source" || args[2] != "--dependency" || args[4] != "--output")
        {
            Console.WriteLine("--source <reported MP4> --dependency <verified libmpv directory> --output <new checkout work directory>");
            return 1;
        }
        // Independent termination cannot block on decoder teardown or output pipes.
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
        using var files = new ProbeFiles();
        var report = new Report();
        var images = new List<(string Phase, double Position, byte[] Pixels)>();
        var started = Stopwatch.GetTimestamp();
        var nextGuard = 0d; var nextMetrics = 0d;
        MpvNative? native = null; StreamWriter? progress = null;
        var ready = false; var created = false;
        try
        {
            Need(AppsClosed(), "apps-must-be-closed");
            files.Prepare(args[1], args[3], args[5]); created = true;
            progress = new StreamWriter(new FileStream(Path.Combine(files.Output, "progress.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Mark("load-pinned-runtime"); native = new MpvNative(files.Dll); report.ApiVersion = native.ApiVersion;
            foreach (var (name, value) in new (string, string)[]
            {
                ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
                ("input-terminal", "no"), ("input-default-bindings", "no"), ("osc", "no"),
                ("osd-level", "0"), ("access-references", "no"), ("sub-auto", "no"),
                ("audio-file-auto", "no"), ("vo", "null"), ("ao", "null"), ("hwdec", "no"),
                ("audio-display", "no"), ("pause", "yes"), ("keep-open", "yes"), ("idle", "yes"),
                ("framedrop", "no"), ("screenshot-sw", "yes"), ("cache", "no"), ("cache-on-disk", "no"),
                ("demuxer-max-bytes", "268435456"), ("demuxer-max-back-bytes", "0"),
                ("demuxer-readahead-secs", "1"), ("vd-queue-enable", "no"),
                ("save-position-on-quit", "no"), ("resume-playback", "no"), ("stop-screensaver", "no")
            }) native.Option(name, value);
            Mark("initialize"); native.Initialize();
            report.MpvVersion = Identifier(native.Text("mpv-version"));
            report.FfmpegVersion = Identifier(native.Text("ffmpeg-version"));
            Mark("open-paused"); native.Run("loadfile", files.Source, "replace");
            Until(() => native.Integer("video-params/w") == 3840 && native.Integer("video-params/h") == 2160 &&
                native.Flag("pause") == true && native.Flag("seeking") != true && native.Number("time-pos") is >= 0 and <= 0.034 && report.Restarts > 0);
            ready = true;
            report.PixelFormat = Identifier(native.Text("video-dec-params/pixelformat"));
            Need(report.PixelFormat == "gbrp", "full-color-decoder-required");
            report.Duration = native.Number("duration") ?? 0;
            Need(report.Duration is > 2.4 and < 2.6 && native.Text("current-vo") == "null" && native.Text("current-ao") == "null", "headless-media-shape-mismatch");
            var holdUntil = Elapsed() + 2;
            while (Elapsed() < holdUntil)
            {
                Step(); Need(native.Flag("pause") == true && native.Number("time-pos") is >= 0 and <= 0.034, "unexpected-autoplay");
            }
            report.PausedHoldPassed = true; Sample("initial");
            Mark("first-play-through"); native.Set("pause", "no");
            Until(() => native.Number("time-pos") is >= 0.7 || native.Flag("eof-reached") == true);
            Need(native.Flag("eof-reached") != true, "sample-one-missed"); native.Set("pause", "yes"); Sample("first-play-0.7");
            native.Set("pause", "no"); Until(() => native.Number("time-pos") is >= 1.25 || native.Flag("eof-reached") == true);
            Need(native.Flag("eof-reached") != true, "sample-two-missed"); native.Set("pause", "yes"); Sample("first-play-1.25");
            native.Set("pause", "no"); Until(() => native.Flag("eof-reached") == true); report.FirstEof = true;
            Mark("paused-exact-seek"); native.Set("pause", "yes");
            var beforeSeek = report.Restarts;
            native.Run("seek", "0.7", "absolute+exact");
            Until(() => report.Restarts > beforeSeek && native.Flag("seeking") != true && native.Number("time-pos") is >= 0.68 and <= 0.74);
            Need(native.Flag("pause") == true, "seek-autoplay"); Sample("paused-seek"); report.SeekPassed = true;
            beforeSeek = report.Restarts; native.Run("seek", "0", "absolute+exact");
            Until(() => report.Restarts > beforeSeek && native.Flag("seeking") != true && native.Number("time-pos") is >= 0 and <= 0.034);
            Mark("replay"); native.Set("pause", "no"); Until(() => native.Flag("eof-reached") == true); report.ReplayEof = true;
            while (!Step(0)) { }
            report.FinalEventsDrained = true;
            Metrics();
            Need(report.QueueOverflows == 0 && report.DecodeErrors == 0 && report.ErrorLogs == 0 && report.QueueLimitWarnings == 0 &&
                report.MaximumDecoderDrops == 0 && report.MaximumVoDrops == 0, "decoder-or-queue-failure");
            report.CompletedPlayback = true;
        }
        catch (Exception error)
        {
            report.Failure = error is ProbeFailure known ? known.Code : error.GetType().Name;
            report.HResult = $"0x{error.HResult:X8}";
        }
        finally
        {
            try
            {
                Mark("native-cleanup"); native?.Close(); report.CleanupCompleted = true; Mark("native-cleanup-complete");
            }
            catch (Exception error) { report.CleanupFailure = error.GetType().Name; }
            if (report.CleanupCompleted && images.Count > 0)
            {
                var rgb = GC.AllocateUninitializedArray<byte>(24883200);
                foreach (var sample in images)
                {
                    for (int source = 0, destination = 0; destination < rgb.Length; source += 4, destination += 3)
                    { rgb[destination] = sample.Pixels[source + 2]; rgb[destination + 1] = sample.Pixels[source + 1]; rgb[destination + 2] = sample.Pixels[source]; }
                    var hash = Convert.ToHexString(SHA256.HashData(rgb));
                    report.Samples.Add(new(sample.Phase, sample.Position, hash, files.Oracle.TryGetValue(hash, out var frames) ? frames.ToArray() : []));
                }
            }
            report.ElapsedSeconds = Elapsed();
            report.Passed = report.CompletedPlayback && report.CleanupCompleted && report.Failure is null && report.Samples.Count == 4 &&
                report.Samples.All(sample => sample.OracleFrames.Length > 0) && report.Samples.Select(sample => sample.RgbSha256).Distinct().Count() >= 2;
            if (!report.Passed && report.Failure is null) report.Failure = "sample-or-cleanup-not-verified";
            progress?.Dispose();
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (created) File.WriteAllText(Path.Combine(files.Output, "mpv-decode.json"), json);
            Console.WriteLine(json);
            GC.KeepAlive(native); GC.KeepAlive(files); GC.KeepAlive(images);
            if (!report.CleanupCompleted) Environment.Exit(2);
        }
        return report.Passed ? 0 : 1;

        double Elapsed() => Stopwatch.GetElapsedTime(started).TotalSeconds;
        void Mark(string stage)
        {
            report.Stage = stage;
            if (progress is null) return;
            progress.WriteLine(JsonSerializer.Serialize(new { Stage = stage, ElapsedSeconds = Elapsed() })); progress.Flush();
        }
        void Until(Func<bool> condition) { while (!condition()) Step(); }
        bool Step(double eventWaitSeconds = 0.01)
        {
            var elapsed = Elapsed(); Need(elapsed < 30, "soft-deadline");
            if (elapsed >= nextGuard)
            {
                Need(AppsClosed(), "apps-started-during-check"); nextGuard = elapsed + 0.25;
                using var process = Process.GetCurrentProcess(); process.Refresh();
                report.PeakWorkingSetBytes = Math.Max(report.PeakWorkingSetBytes, process.WorkingSet64);
                Need(process.PrivateMemorySize64 <= 3L * 1024 * 1024 * 1024, "process-memory-bound");
            }
            var drained = false;
            for (var count = 0; count < 128; count++)
            {
                var item = native!.Next(count == 0 ? eventWaitSeconds : 0);
                if (item.Id == 0) { drained = true; break; }
                report.Events++; Need(report.Events <= 100000, "event-count-bound");
                if (item.Id == 24) report.QueueOverflows++;
                if (item.Id == 21) report.Restarts++;
                if (item.Id == 2 && item.Data != IntPtr.Zero)
                {
                    var log = Marshal.PtrToStructure<MpvNative.Log>(item.Data);
                    // Native text is inspected only against fixed tokens; never serialized.
                    var text = MpvNative.ReadText(log.Text, 4096);
                    report.LogMessages++;
                    if (log.NumericLevel <= 20) report.ErrorLogs++;
                    if (text.StartsWith("Too many packets in the demuxer packet queues:", StringComparison.Ordinal)) report.QueueLimitWarnings++;
                    if (text.Contains("Error while decoding frame", StringComparison.Ordinal) || text.Contains("corrupt decoded frame", StringComparison.Ordinal)) report.DecodeErrors++;
                }
                if (item.Id == 7 && item.Data != IntPtr.Zero && Marshal.ReadInt32(item.Data) == 4) throw new ProbeFailure("native-end-file-error");
            }
            if (ready && elapsed >= nextMetrics) { Metrics(); nextMetrics = elapsed + 0.1; }
            return drained;
        }
        void Metrics()
        {
            var cache = native!.CacheNumbers();
            Need(cache.ContainsKey("fw-bytes"), "cache-metrics-unavailable");
            if (cache.TryGetValue("fw-bytes", out var forward)) report.PeakForwardBytes = Math.Max(report.PeakForwardBytes, forward);
            if (cache.TryGetValue("total-bytes", out var total)) report.PeakPacketBytes = Math.Max(report.PeakPacketBytes, total);
            if (cache.TryGetValue("raw-input-rate", out var rate)) report.PeakReadBytesPerSecond = Math.Max(report.PeakReadBytesPerSecond, rate);
            Need(report.PeakForwardBytes <= 384L * 1024 * 1024, "packet-cache-bound");
            report.MaximumDecoderDrops = Math.Max(report.MaximumDecoderDrops, native.Integer("decoder-frame-drop-count") ?? throw new ProbeFailure("decoder-drop-count-unavailable"));
            report.MaximumVoDrops = Math.Max(report.MaximumVoDrops, native.Integer("frame-drop-count") ?? throw new ProbeFailure("vo-drop-count-unavailable"));
            if (native.Number("avsync") is { } sync) report.MaximumAbsoluteAvSyncSeconds = Math.Max(report.MaximumAbsoluteAvSyncSeconds ?? 0, Math.Abs(sync));
            if (native.Flag("paused-for-cache") == true) report.BufferingObservations++;
            report.MetricObservations++;
        }
        void Sample(string phase)
        {
            var player = native ?? throw new ProbeFailure("decoder-not-created");
            Need(images.Count < 4 && player.Flag("pause") == true, "paused-sample-required");
            var position = player.Number("time-pos") ?? throw new ProbeFailure("sample-position-unavailable");
            var sampleStarted = Stopwatch.GetTimestamp();
            images.Add((phase, position, player.RawFrame()));
            report.SampleCopyElapsedMilliseconds += Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds;
        }
    }
    private static string? Identifier(string? value)
    {
        Need(value is null || (value.Length < 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || " ._+-/()".Contains(c))), "version-identifier-invalid"); return value;
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
    private sealed record SampleResult(string Phase, double Position, string RgbSha256, int[] OracleFrames);
    private sealed class Report
    {
        public string SourceSha256 { get; } = SourceHash;
        public string NativeSha256 { get; } = DllHash;
        public string HeaderSha256 { get; } = HeaderHash;
        public string OracleSha256 { get; } = OracleHash;
        public bool Passed { get; set; }
        public string Stage { get; set; } = "preflight";
        public string? Failure { get; set; }
        public string? HResult { get; set; }
        public string? CleanupFailure { get; set; }
        public uint ApiVersion { get; set; }
        public string? MpvVersion { get; set; }
        public string? FfmpegVersion { get; set; }
        public string? PixelFormat { get; set; }
        public double Duration { get; set; }
        public bool PausedHoldPassed { get; set; }
        public bool FirstEof { get; set; }
        public bool SeekPassed { get; set; }
        public bool ReplayEof { get; set; }
        public bool FinalEventsDrained { get; set; }
        public bool CompletedPlayback { get; set; }
        public bool CleanupCompleted { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public double PeakForwardBytes { get; set; }
        public double PeakPacketBytes { get; set; }
        public double PeakReadBytesPerSecond { get; set; }
        public double? MaximumAbsoluteAvSyncSeconds { get; set; }
        public long MaximumDecoderDrops { get; set; }
        public long MaximumVoDrops { get; set; }
        public int Restarts { get; set; }
        public int Events { get; set; }
        public int QueueOverflows { get; set; }
        public int QueueLimitWarnings { get; set; }
        public int DecodeErrors { get; set; }
        public int ErrorLogs { get; set; }
        public int LogMessages { get; set; }
        public int MetricObservations { get; set; }
        public int BufferingObservations { get; set; }
        public double SampleCopyElapsedMilliseconds { get; set; }
        public double ElapsedSeconds { get; set; }
        public List<SampleResult> Samples { get; } = [];
    }
}
