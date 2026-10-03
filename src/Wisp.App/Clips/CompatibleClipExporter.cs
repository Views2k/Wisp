using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Wisp.App.Clips;

public enum ClipExportFormat { Original, Compatible }
internal enum CompatibleClipTarget { Sdr, PreserveHdr }

public interface ICompatibleClipExporter
{
    // The caller holds the original read-only. The destination belongs to a
    // private staging directory, never the user's final file. Complete only
    // after the encoder has closed its file and validation has succeeded.
    Task ExportAsync(ClipEntry clip, string sourcePath, string stagingPath,
        IProgress<double>? progress, CancellationToken cancellationToken);
}

internal sealed class CompatibleClipExporter : ICompatibleClipExporter
{
    private readonly int _decoderThreads;
    private readonly long _maximumOutputBytes;
    private readonly CompatibleClipTarget _target;

    internal CompatibleClipExporter(int decoderThreads = 2, long maximumOutputBytes = ClipLibrary.MaximumMediaBytes,
        CompatibleClipTarget target = CompatibleClipTarget.Sdr)
    {
        if (decoderThreads is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(decoderThreads));
        if (maximumOutputBytes is <= 0 or > ClipLibrary.MaximumMediaBytes) throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        _decoderThreads = decoderThreads;
        _maximumOutputBytes = maximumOutputBytes;
        _target = target;
    }

    internal sealed record Probe(int Width, int Height, double Duration, double FrameRate, bool HasAudio,
        string PixelFormat, string Gamma, string Matrix, string Range, string Primaries, string Codec = "h264");

    public Task ExportAsync(ClipEntry clip, string sourcePath, string stagingPath,
        IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        LosslessMpvNative? native = null;
        var stage = "source-inspection";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((!clip.Media.LosslessVideo && !clip.Media.HdrVideo) || (_target == CompatibleClipTarget.PreserveHdr && !clip.Media.HdrVideo) ||
                !Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(stagingPath) ||
                string.Equals(sourcePath, stagingPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(stagingPath) || new FileInfo(stagingPath).Length != 0)
                throw new ArgumentException("Choose a new compatible-copy destination.");
            ClipLibrary.CheckPath(sourcePath); ClipLibrary.CheckPath(stagingPath);
            var source = Inspect(sourcePath, cancellationToken, out var encoders);
            stage = "source-validation";
            Validate(clip, source, compatible: false);
            var encoder = EncoderFor(clip.Media.HdrVideo, _target);
            if (!encoders.Contains(encoder, StringComparer.Ordinal) || (clip.Media.HasAudio && !encoders.Contains("aac", StringComparer.Ordinal)))
                throw WithReason(new NotSupportedException("The compatible export encoder is unavailable."), "encoder-unavailable");
            var transfer = source.Gamma switch
            {
                "srgb" => "iec61966-2-1",
                "bt.1886" => "bt709",
                "pq" when clip.Media.HdrVideo => "smpte2084",
                _ => throw WithReason(new NotSupportedException("The clip's color format cannot be exported as a compatible copy."), "source-color-transfer")
            };
            if (clip.Media.HdrVideo && _target == CompatibleClipTarget.Sdr) transfer = "iec61966-2-1";
            stage = "encoding";
            native = new LosslessMpvNative();
            native.InitializeHeadless(EncodingOptions(stagingPath, clip.Media.FrameRate, clip.Media.HasAudio, transfer,
                clip.Media.HdrVideo, _decoderThreads, _target, clip.Media.LosslessVideo, clip.Media.Width, clip.Media.Height));
            native.Run("loadfile", sourcePath, "replace");
            var clock = Stopwatch.StartNew();
            var lastChange = TimeSpan.Zero;
            double position = 0;
            var ended = false; var quit = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = native.ReadExportEvents();
                ended |= state.Ended;
                if (state.Shutdown)
                {
                    if (!ended) throw Failure("export-stopped-before-end");
                    break;
                }
                var next = native.Number("time-pos") ?? position;
                if (next > position) { position = next; lastChange = clock.Elapsed; }
                progress?.Report(Math.Clamp(position / source.Duration * 95, 0, 95));
                if (ended && !quit) { native.Run("quit"); quit = true; lastChange = clock.Elapsed; }
                if (clock.Elapsed - lastChange > TimeSpan.FromSeconds(60) || clock.Elapsed > TimeSpan.FromMinutes(30))
                    throw WithReason(new TimeoutException("The compatible export stopped making progress."), "export-timeout");
                if (File.Exists(stagingPath) && new FileInfo(stagingPath).Length > _maximumOutputBytes)
                    throw WithReason(new IOException("The compatible export reached the clip size limit."), "export-size-limit");
                cancellationToken.WaitHandle.WaitOne(50);
            }
            // Shutdown follows mux finalization; destroy joins all native work.
            native.Close(); native = null;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(96);
            stage = "output-inspection";
            var output = Inspect(stagingPath, cancellationToken);
            stage = "output-validation";
            Validate(clip, output, compatible: true, _target);
            var expectedGamma = clip.Media.HdrVideo && _target == CompatibleClipTarget.Sdr ? "srgb" : source.Gamma;
            if (output.Gamma != expectedGamma) throw Failure("output-color-transfer", output);
            using var finished = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (finished.Length <= 0 || finished.Length > _maximumOutputBytes)
                throw Failure(finished.Length <= 0 ? "output-empty" : "export-size-limit", output);
            progress?.Report(98);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            error.Data["wisp-export-stage"] = stage;
            throw;
        }
        finally { native?.Close(); }
    }, cancellationToken);

    internal static string EncoderFor(bool hdrVideo, CompatibleClipTarget target) =>
        target == CompatibleClipTarget.PreserveHdr ? "hevc_nvenc" : hdrVideo ? "h264_mf" : "h264_nvenc";

    internal static (string Name, string Value)[] EncodingOptions(string destination, int frameRate, bool audio, string transfer, bool hdrVideo = false,
        int decoderThreads = 2, CompatibleClipTarget target = CompatibleClipTarget.Sdr, bool losslessVideo = true,
        int width = 1920, int height = 1080)
    {
        var outputHdr = target == CompatibleClipTarget.PreserveHdr;
        var toneMap = hdrVideo && !outputHdr;
        if (toneMap) transfer = "iec61966-2-1";
        var bitrate = Math.Clamp((long)(32_000_000d * width * height * frameRate / (1920d * 1080 * 60)), 100_000, 120_000_000);
        var codecOptions = toneMap
            ? "hw_encoding=1,rate_control=quality,quality=90,b=" + bitrate.ToString(CultureInfo.InvariantCulture) + ",profile=100"
            : "preset=p5,tune=hq,rc=constqp,qp=18,profile=" + (outputHdr ? "main10" : "high");
        return
        [
            // Wisp timestamps are on this frame grid. A frame-sized mux
            // time base also gives the final NVENC packet its missing duration.
            ("o", destination), ("of", "mp4"),
            ("ofopts", "movflags=+faststart,video_track_timescale=" + frameRate.ToString(CultureInfo.InvariantCulture)),
            ("ovc", EncoderFor(hdrVideo, target)),
            ("ovcopts", codecOptions + ",bf=0,g=" + (frameRate * 2).ToString(CultureInfo.InvariantCulture) +
                ",color_primaries=" + (outputHdr ? "bt2020" : "bt709") + ",color_trc=" + transfer + ",colorspace=" + (outputHdr ? "bt2020nc" : "bt709") + ",color_range=tv"),
            ("oac", "aac"), ("oacopts", "b=192000"), ("ocopy-metadata", "no"),
            ("vid", "auto"), ("aid", audio ? "auto" : "no"), ("vo", "lavc"), ("ao", "lavc"),
            ("demuxer-max-bytes", "268435456"),
            ("pause", "no"), ("idle", "yes"), ("audio-display", "no"), ("vd-lavc-threads", decoderThreads.ToString(CultureInfo.InvariantCulture)), ("vd-queue-enable", "no"),
            // HDR sharing uses linear-light tone mapping before SDR transfer and
            // quantization. The playback copy deliberately retains PQ instead.
            ("vf", toneMap
                ? HdrToSdrFilter()
                : outputHdr ? "lavfi=[scale=in_range=" + (losslessVideo ? "full" : "limited") + ":out_range=limited:out_color_matrix=bt2020,format=pix_fmts=yuv420p10le]"
                : "lavfi=[scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=pix_fmts=yuv420p]")
        ];
    }

    private static string HdrToSdrFilter()
    {
        // Match HdrFrameConverter's 300-nit SDR reference and per-channel
        // Rec.2020 Reinhard/display shaping. No scene peak is inferred.
        static string Channel(char channel) => channel + "='st(0,max(" + channel + "(X,Y),0));" +
            "st(0,pow(ld(0)/(1+ld(0)),1/2.4));" +
            "if(lte(ld(0),0.04045),ld(0)/12.92,pow((ld(0)+0.055)/1.055,2.4))'";
        return "lavfi=[zscale=transfer=linear:npl=300,format=gbrpf32le,geq=" +
            Channel('r') + ":" + Channel('g') + ":" + Channel('b') + ":interpolation=nearest," +
            "zscale=primaries=bt709:transfer=iec61966-2-1:matrix=bt709:range=limited,format=pix_fmts=yuv420p,format=pix_fmts=nv12]";
    }

    internal static Probe Inspect(string path, CancellationToken token) => Inspect(path, token, out _);

    private static Probe Inspect(string path, CancellationToken token, out string[] encoders)
    {
        LosslessMpvNative? native = null;
        try
        {
            token.ThrowIfCancellationRequested();
            native = new LosslessMpvNative();
            // Inspect video without queuing high-bitrate packets while waiting
            // for an interleaved audio chunk; track-list still includes audio.
            native.InitializeHeadless([("vid", "auto"), ("aid", "no"), ("pause", "yes"),
                ("vd-lavc-threads", "2"), ("vd-queue-enable", "no")]);
            encoders = native.ReadEncoderNames();
            native.Run("loadfile", path, "replace");
            var clock = Stopwatch.StartNew();
            while (native.Integer("video-params/w") is null)
            {
                token.ThrowIfCancellationRequested(); native.DrainEvents();
                if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw WithReason(new TimeoutException("The clip could not be prepared for export."), "preparation-timeout");
                token.WaitHandle.WaitOne(25);
            }
            native.DrainEvents();
            var count = native.Integer("track-list/count");
            if (count is not (1 or 2)) throw Failure("track-count");
            var hasVideo = false; var hasAudio = false; string videoCodec = "";
            for (var index = 0; index < count.Value; index++)
            {
                var prefix = "track-list/" + index.ToString(CultureInfo.InvariantCulture);
                var type = native.Text(prefix + "/type"); var codec = native.Text(prefix + "/codec");
                if (type == "video" && (codec is "h264" or "hevc") && !hasVideo) { hasVideo = true; videoCodec = codec; }
                else if (type == "audio" && codec == "aac" && !hasAudio) hasAudio = true;
                else throw Failure("track-codec");
            }
            if (!hasVideo) throw Failure("video-track-missing");
            return new(checked((int)(native.Integer("video-params/w") ?? 0)), checked((int)(native.Integer("video-params/h") ?? 0)),
                native.Number("duration") ?? 0, native.Number("container-fps") ?? 0, hasAudio,
                native.Text("video-params/pixelformat") ?? "", native.Text("video-params/gamma") ?? "",
                native.Text("video-params/colormatrix") ?? "", native.Text("video-params/colorlevels") ?? "",
                native.Text("video-params/primaries") ?? "", videoCodec);
        }
        finally { native?.Close(); }
    }

    internal static void Validate(ClipEntry clip, Probe probe, bool compatible, CompatibleClipTarget target = CompatibleClipTarget.Sdr)
    {
        if (compatible && target == CompatibleClipTarget.PreserveHdr && !clip.Media.HdrVideo) throw Failure("color-primaries", probe);
        if (probe.Width != clip.Media.Width || probe.Height != clip.Media.Height) throw Failure("dimensions", probe);
        if (probe.HasAudio != clip.Media.HasAudio) throw Failure("audio-track", probe);
        if (!double.IsFinite(probe.Duration) || probe.Duration <= 0 || Math.Abs(probe.Duration - clip.DurationSeconds) > .1)
            throw Failure("duration", probe);
        if (!double.IsFinite(probe.FrameRate) || Math.Abs(probe.FrameRate - clip.Media.FrameRate) > .01)
            throw Failure("frame-rate", probe);
        var hdr = compatible ? target == CompatibleClipTarget.PreserveHdr : clip.Media.HdrVideo;
        var lossless = !compatible && clip.Media.LosslessVideo;
        if (probe.Codec != (hdr ? "hevc" : "h264")) throw Failure("track-codec", probe);
        if (probe.Primaries != (hdr ? "bt.2020" : "bt.709")) throw Failure("color-primaries", probe);
        if (hdr ? probe.Gamma != "pq" : (probe.Gamma is not ("srgb" or "bt.1886") ||
            compatible && clip.Media.HdrVideo && probe.Gamma != "srgb"))
            throw Failure(compatible ? "output-color-transfer" : "source-color-transfer", probe);
        if (lossless ? (hdr ? !LosslessClipPlayer.IsHdr444PixelFormat(probe.PixelFormat) : probe.PixelFormat != "gbrp") ||
                probe.Matrix != "rgb" || probe.Range != "full"
            : (hdr ? !LosslessClipPlayer.IsHdr420PixelFormat(probe.PixelFormat) : probe.PixelFormat != "yuv420p") ||
                probe.Matrix != (hdr ? "bt.2020-ncl" : "bt.709") || probe.Range != "limited")
            throw Failure(compatible ? "output-pixel-format" : "source-pixel-format", probe);
    }

    private static InvalidDataException Failure(string reason, Probe? probe = null)
    {
        var error = new InvalidDataException("The compatible export does not match the saved clip.");
        error.Data["wisp-export-reason"] = reason;
        if (probe is not null) error.Data["wisp-export-probe"] = probe;
        return error;
    }

    private static T WithReason<T>(T error, string reason) where T : Exception
    {
        error.Data["wisp-export-reason"] = reason;
        return error;
    }
}
