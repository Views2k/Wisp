using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wisp.App.Clips;

namespace Wisp.UiReview;

// Measures the production RV32 callback or proposed bounded mpv conversion. Returning pixels does not prove
// HDR tone mapping, displayed appearance, or any gameplay capture behavior.
internal static class HdrThumbnailReview
{
    internal const string ProposedFilter = "lavfi=[zscale=w=320:h=180:transfer=linear:npl=100,format=gbrpf32le,zscale=primaries=bt709,tonemap=mobius:param=0.3:desat=2,zscale=transfer=iec61966-2-1,format=bgra]";

    internal static int Run(string sourcePath, string fixtureSha, string output, bool mpvConversion = false, bool productionConversion = false)
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stage = "closed-app-guard";
        var clock = Stopwatch.StartNew();
        string? ownedCopy = null;
        FileStream? source = null;
        CompatibleClipExporter.Probe? probe = null;
        object[] patches = [];
        var decoded = false; var cleanup = false; var exclusiveReopen = false; var copyRemoved = false;
        string? failure = null; int? hresult = null; string? actualSha = null; string? nativeCode = null;
        try
        {
            Guard();
            stage = "fixture-validation"; Progress();
            var checkout = Checkout(output);
            var full = Path.GetFullPath(sourcePath);
            Need(full.StartsWith(Path.Combine(checkout, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full) == "fixture.mp4" && Regex.IsMatch(fixtureSha, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant), "generated-fixture-required");
            ClipLibrary.CheckPath(full);
            source = new(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            Need(source.Length is > 0 and <= 256 * 1024 * 1024, "fixture-size");
            actualSha = Convert.ToHexString(SHA256.HashData(source));
            Need(actualSha.Equals(fixtureSha, StringComparison.OrdinalIgnoreCase), "fixture-hash");
            source.Position = 0;
            stage = "source-inspection"; Progress();
            probe = CompatibleClipExporter.Inspect(full, deadline.Token);
            var lossless = LosslessClipPlayer.IsHdr444PixelFormat(probe.PixelFormat);
            Need(probe.Codec == "hevc" && (lossless || LosslessClipPlayer.IsHdr420PixelFormat(probe.PixelFormat)) &&
                probe.Gamma == "pq" && probe.Matrix == (lossless ? "rgb" : "bt.2020-ncl") &&
                probe.Range == (lossless ? "full" : "limited") && probe.Primaries == "bt.2020" &&
                probe.Width == 1920 && probe.Height == 1080 && double.IsFinite(probe.FrameRate) && Math.Abs(probe.FrameRate - 60) < .01 && probe.Duration is > 0 and < 1,
                "hdr-fixture-metadata");
            var id = Guid.NewGuid();
            ownedCopy = Path.Combine(output, id.ToString("N") + ".mp4");
            stage = "owned-source-copy"; Progress();
            using (var copy = new FileStream(ownedCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(copy);
            var clip = new ClipEntry(id, DateTimeOffset.UtcNow, new(60, 1080, 60, 75, LosslessVideo: lossless),
                new(source.Length, 1920, 1080, 60, 0, checked((long)Math.Round(probe.Duration * 10_000_000)), probe.HasAudio, LosslessVideo: lossless, HdrVideo: true));
            Guard(); deadline.Token.ThrowIfCancellationRequested();
            stage = productionConversion ? "production-hdr-thumbnail" : mpvConversion ? "proposed-mpv-thumbnail" : "production-thumbnail"; Progress();
            var pixels = productionConversion ? HdrThumbnailDecoder.DecodeAsync(clip, ownedCopy, deadline.Token).GetAwaiter().GetResult()
                : mpvConversion ? ReadMpvThumbnail(clip, ownedCopy, deadline.Token)
                : LosslessThumbnailDecoder.DecodeAsync(clip, ownedCopy, deadline.Token).GetAwaiter().GetResult();
            Need(pixels is { Length: ClipThumbnailWire.PixelBytes }, "thumbnail-unavailable");
            patches = Enumerable.Range(0, 16).Select(index =>
            {
                var x = ClipThumbnailWire.Width * (2 * (index % 4) + 1) / 8;
                var y = ClipThumbnailWire.Height * (2 * (index / 4) + 1) / 8;
                var offset = y * ClipThumbnailWire.Stride + x * 4;
                return (object)new { index, red = pixels![offset + 2], green = pixels[offset + 1], blue = pixels[offset] };
            }).ToArray();
            decoded = true;
        }
        catch (Exception error)
        {
            failure = error is ProbeFailure known ? known.Code : error is OperationCanceledException ? "deadline" : "diagnostic-failed";
            hresult = error.HResult;
            nativeCode = error is LosslessMpvException mpv ? mpv.Code : null;
        }
        finally
        {
            var decodeStage = stage;
            stage = "decoder-cleanup"; Progress();
            try
            {
                cleanup = productionConversion ? LosslessMpvRuntime.ShutdownAsync().GetAwaiter().GetResult()
                    : LosslessVlcRuntime.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception) { cleanup = false; }
            if (cleanup && ownedCopy is not null && File.Exists(ownedCopy))
            {
                try
                {
                    using (var file = new FileStream(ownedCopy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) exclusiveReopen = true;
                    File.Delete(ownedCopy); copyRemoved = true;
                }
                catch (Exception) { exclusiveReopen = false; }
            }
            Write("hdr-thumbnail-review.json", new
            {
                schema = 1,
                decoded,
                stage = decodeStage,
                failure,
                hresult,
                nativeCode,
                fixtureSha256 = actualSha,
                source = probe,
                sourceHeldReadOnly = source is not null,
                patches,
                decoder = productionConversion ? (object?)HdrThumbnailDecoder.LastDiagnostic : LosslessThumbnailDecoder.LastDiagnostic,
                mpvConversion,
                productionConversion,
                proposedFilter = productionConversion ? HdrThumbnailDecoder.Filter : mpvConversion ? ProposedFilter : null,
                cleanup,
                exclusiveReopen,
                copyRemoved,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                colorAssessment = "measurement-only",
                hdrToneMappingVerified = false,
                windowsOpened = false,
                captureStarted = false,
                gameplayVerified = false
            });
            source?.Dispose();
        }
        return decoded && cleanup && exclusiveReopen && copyRemoved ? 0 : 1;

        void Progress() => Write("hdr-thumbnail-progress.json", new { stage, elapsedSeconds = clock.Elapsed.TotalSeconds });
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] ReadMpvThumbnail(ClipEntry clip, string path, CancellationToken token)
    {
        using var held = LosslessVlcRuntime.HoldSource(clip, path);
        var native = new LosslessMpvNative();
        try
        {
            native.InitializeHeadless([("vid", "auto"), ("aid", "no"), ("pause", "yes"), ("screenshot-sw", "yes"),
                ("demuxer-max-bytes", "268435456"), ("vd-lavc-threads", "2"), ("vd-queue-enable", "no"), ("vf", ProposedFilter)]);
            native.Run("loadfile", path, "replace");
            while (native.Restarts == 0 || native.Integer("video-out-params/w") != ClipThumbnailWire.Width ||
                native.Integer("video-out-params/h") != ClipThumbnailWire.Height)
            {
                token.ThrowIfCancellationRequested(); native.DrainEvents(); token.WaitHandle.WaitOne(20);
            }
            Need(native.Text("video-dec-params/gamma") == "pq" && native.Text("video-dec-params/primaries") == "bt.2020" &&
                native.Text("video-out-params/gamma") == "srgb" && native.Text("video-out-params/primaries") == "bt.709",
                "converted-frame-color-metadata");
            token.ThrowIfCancellationRequested();
            var image = native.ReadDiagnosticFrame();
            Need(image.Width == ClipThumbnailWire.Width && image.Height == ClipThumbnailWire.Height &&
                image.Bgra.Length == ClipThumbnailWire.PixelBytes, "converted-frame-size");
            for (var offset = 3; offset < image.Bgra.Length; offset += 4) image.Bgra[offset] = 255;
            return image.Bgra;
        }
        finally { native.Close(); }
    }

    private static void Guard()
    {
        foreach (var name in new[] { "Wisp", "Wisp.Recorder", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { Need(processes.Length == 0, "apps-must-be-closed"); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static string Checkout(string output)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(output));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new ProbeFailure("workspace-output-required");
    }

    private static void Need(bool value, string code) { if (!value) throw new ProbeFailure(code); }
    private sealed class ProbeFailure(string code) : Exception { internal string Code { get; } = code; }
}
