using System.IO;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipCompatibleExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispCompatibleExportTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Original = [1, 2, 3, 4, 5, 6];
    private static readonly byte[] Compatible = [11, 12, 13];

    [Theory]
    [InlineData(30, "video_track_timescale=30")]
    [InlineData(60, "video_track_timescale=60")]
    public void CompatibleMuxUsesOneTickPerRecordedFrame(int rate, string expectedTimescale)
    {
        var options = CompatibleClipExporter.EncodingOptions("private-staging.mp4", rate, true, "iec61966-2-1")
            .ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal("mp4", options["of"]);
        Assert.Contains(expectedTimescale, options["ofopts"].Split(','));
        Assert.DoesNotContain("fps=", options["vf"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("iec61966-2-1")]
    [InlineData("bt709")]
    public void ColorConversionChangesMatrixAndRangeWithMatchingSourceTransferTag(string transfer)
    {
        var options = CompatibleClipExporter.EncodingOptions("private-staging.mp4", 60, true, transfer)
            .ToDictionary(item => item.Name, item => item.Value);
        Assert.StartsWith("lavfi=[scale=", options["vf"], StringComparison.Ordinal);
        Assert.Contains("in_range=full:out_range=limited:out_color_matrix=bt709", options["vf"], StringComparison.Ordinal);
        Assert.Contains("format=pix_fmts=yuv420p", options["vf"], StringComparison.Ordinal);
        Assert.Contains("color_trc=" + transfer, options["ovcopts"].Split(','));
    }

    [Fact]
    public void ExplicitHdrPlaybackCopyUsesHevcMain10AndPreservesPqWithoutToneMapping()
    {
        var options = CompatibleClipExporter.EncodingOptions("private-staging.mp4", 60, true, "smpte2084", hdrVideo: true,
            target: CompatibleClipTarget.PreserveHdr)
            .ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal("hevc_nvenc", options["ovc"]);
        Assert.Contains("profile=main10", options["ovcopts"].Split(','));
        Assert.Contains("color_primaries=bt2020", options["ovcopts"].Split(','));
        Assert.Contains("color_trc=smpte2084", options["ovcopts"].Split(','));
        Assert.Contains("colorspace=bt2020nc", options["ovcopts"].Split(','));
        Assert.Contains("color_range=tv", options["ovcopts"].Split(','));
        Assert.Equal("lavfi=[scale=in_range=full:out_range=limited:out_color_matrix=bt2020,format=pix_fmts=yuv420p10le]", options["vf"]);
        Assert.Equal("268435456", options["demuxer-max-bytes"]);
        Assert.Contains("video_track_timescale=60", options["ofopts"].Split(','));
        Assert.Equal("aac", options["oac"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdrExportValidatesActualCodecTransferAndTenBitPixelMetadata(bool compatible)
    {
        var clip = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, 1080, 60, 75, LosslessVideo: true),
            new(4, 1920, 1080, 60, 0, 20_000_000, true, LosslessVideo: true, HdrVideo: true));
        var correct = new CompatibleClipExporter.Probe(1920, 1080, 2, 60, true,
            compatible ? "yuv420p10le" : "gbrp10le", "pq", compatible ? "bt.2020-ncl" : "rgb",
            compatible ? "limited" : "full", "bt.2020", "hevc");
        CompatibleClipExporter.Validate(clip, correct, compatible, CompatibleClipTarget.PreserveHdr);
        CompatibleClipExporter.Validate(clip, correct with { PixelFormat = compatible ? "yuv420p10" : "gbrp10" }, compatible, CompatibleClipTarget.PreserveHdr);
        CompatibleClipExporter.Probe[] invalid =
        [
            correct with { Codec = "h264" }, correct with { Gamma = "srgb" }, correct with { Primaries = "bt.709" },
            correct with { PixelFormat = compatible ? "yuv420p" : "gbrp" }, correct with { Matrix = "bt.709" },
            correct with { PixelFormat = compatible ? "yuv420p10be" : "gbrp10be" },
            correct with { PixelFormat = compatible ? "yuv420p12le" : "gbrp12le" },
            correct with { Range = compatible ? "full" : "limited" }, correct with { FrameRate = 30 },
            correct with { Width = 1280 }, correct with { HasAudio = false }, correct with { Duration = 1 }
        ];
        foreach (var probe in invalid)
            Assert.Throws<InvalidDataException>(() => CompatibleClipExporter.Validate(clip, probe, compatible, CompatibleClipTarget.PreserveHdr));
        Assert.Throws<InvalidDataException>(() => CompatibleClipExporter.Validate(clip with { Media = clip.Media with { HdrVideo = false } }, correct, compatible, CompatibleClipTarget.PreserveHdr));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdrSdrExportUsesToneMappingForBothSourceFormats(bool lossless)
    {
        var clip = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, 1080, 60, 75, LosslessVideo: lossless),
            new(4, 1920, 1080, 60, 0, 20_000_000, true, LosslessVideo: lossless, HdrVideo: true));
        var source = new CompatibleClipExporter.Probe(1920, 1080, 2, 60, true,
            lossless ? "gbrp10le" : "yuv420p10le", "pq", lossless ? "rgb" : "bt.2020-ncl",
            lossless ? "full" : "limited", "bt.2020", "hevc");
        CompatibleClipExporter.Validate(clip, source, compatible: false);
        var output = source with { Codec = "h264", PixelFormat = "yuv420p", Gamma = "srgb", Matrix = "bt.709", Range = "limited", Primaries = "bt.709" };
        CompatibleClipExporter.Validate(clip, output, compatible: true);
        Assert.Throws<InvalidDataException>(() => CompatibleClipExporter.Validate(clip, source, compatible: true));
        Assert.Throws<InvalidDataException>(() => CompatibleClipExporter.Validate(clip, output with { Gamma = "pq" }, compatible: true));
        var options = CompatibleClipExporter.EncodingOptions("private-staging.mp4", 60, true, "smpte2084", hdrVideo: true,
            losslessVideo: lossless).ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal("h264_mf", options["ovc"]);
        Assert.Contains("hw_encoding=1", options["ovcopts"].Split(','));
        Assert.Contains("profile=100", options["ovcopts"].Split(','));
        Assert.Contains("color_trc=iec61966-2-1", options["ovcopts"].Split(','));
        Assert.Contains("color_primaries=bt709", options["ovcopts"].Split(','));
        Assert.StartsWith("lavfi=[zscale=transfer=linear:npl=300,format=gbrpf32le,geq=", options["vf"], StringComparison.Ordinal);
        foreach (var channel in new[] { 'r', 'g', 'b' })
            Assert.Contains(channel + "='st(0,max(" + channel + "(X,Y),0));st(0,pow(ld(0)/(1+ld(0)),1/2.4));" +
                "if(lte(ld(0),0.04045),ld(0)/12.92,pow((ld(0)+0.055)/1.055,2.4))'", options["vf"], StringComparison.Ordinal);
        Assert.EndsWith(":interpolation=nearest,zscale=primaries=bt709:transfer=iec61966-2-1:matrix=bt709:range=limited,format=pix_fmts=yuv420p,format=pix_fmts=nv12]",
            options["vf"], StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap=", options["vf"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinalizedCopyKeepsOriginalBytesAndRecordingMetadata(bool lossless, bool hdr)
    {
        var exporter = new Exporter();
        var library = new ClipLibrary(_directory, exporter);
        var clip = await AddAsync(library, lossless, hdr);
        var source = await library.GetMediaPathAsync(clip.Id, TestContext.Current.CancellationToken);
        var destination = Path.Combine(_directory, "compatible.mp4");
        Assert.Equal(new ClipExportResult(true, true), await library.ExportCompatibleAsync(clip.Id, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Original, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(Compatible, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        var saved = Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.Equal(clip.Media, saved.Media); Assert.Equal(clip.Recording, saved.Recording);
        Assert.NotNull(saved.ExportedAtUtc);
        Assert.True(exporter.SourceHeldReadOnly);
        Assert.Empty(Directory.EnumerateFiles(_directory, ".wisp-compatible-*"));
    }

    [Fact]
    public async Task NormalSdrKeepsItsOriginalExportPath()
    {
        var library = new ClipLibrary(_directory, new Exporter());
        var clip = await AddAsync(library, lossless: false);
        var destination = Path.Combine(_directory, "normal.mp4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.ExportCompatibleAsync(clip.Id, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
        Assert.True((await library.ExportAsync(clip.Id, destination, TestContext.Current.CancellationToken)).FileCreated);
        Assert.Equal(Original, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationWaitsForEncoderOwnershipAndPublishesNothing()
    {
        var exporter = new Exporter { WaitForCancellation = true };
        var library = new ClipLibrary(_directory, exporter);
        var clip = await AddAsync(library);
        using var cancel = new CancellationTokenSource();
        var destination = Path.Combine(_directory, "cancelled.mp4");
        var export = library.ExportCompatibleAsync(clip.Id, destination, cancellationToken: cancel.Token);
        await exporter.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(destination));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.True(exporter.Released);
        Assert.False(File.Exists(destination));
        Assert.Null(Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ExportedAtUtc);
        Assert.Equal(Original, await File.ReadAllBytesAsync(await library.GetMediaPathAsync(clip.Id, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".wisp-compatible-*"));
    }

    [Fact]
    public async Task EncoderFailureRemovesOwnedPartialAndKeepsOriginalUnexported()
    {
        var library = new ClipLibrary(_directory, new Exporter { Fail = true });
        var clip = await AddAsync(library);
        var destination = Path.Combine(_directory, "failed.mp4");
        await Assert.ThrowsAsync<IOException>(() => library.ExportCompatibleAsync(clip.Id, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination));
        Assert.Null(Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ExportedAtUtc);
        Assert.Empty(Directory.EnumerateFiles(_directory, ".wisp-compatible-*"));
    }

    [Fact]
    public async Task FileCreatedDuringEncodingIsNeverOverwritten()
    {
        var destination = Path.Combine(_directory, "raced.mp4");
        var exporter = new Exporter { AfterWriting = () => File.WriteAllBytes(destination, Original) };
        var library = new ClipLibrary(_directory, exporter);
        var clip = await AddAsync(library);
        await Assert.ThrowsAsync<IOException>(() => library.ExportCompatibleAsync(clip.Id, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Original, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Null(Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ExportedAtUtc);
        Assert.Empty(Directory.EnumerateFiles(_directory, ".wisp-compatible-*"));
    }

    [Fact]
    public async Task CompletedDestinationSurvivesIndexWriteFailure()
    {
        var library = new ClipLibrary(_directory, new Exporter());
        var clip = await AddAsync(library);
        var destination = Path.Combine(_directory, "finished.mp4");
        using var held = new FileStream(Path.Combine(_directory, ClipLibrary.IndexFileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(new ClipExportResult(true, false), await library.ExportCompatibleAsync(clip.Id, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Compatible, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    private static async Task<ClipEntry> AddAsync(ClipLibrary library, bool lossless = true, bool hdr = false)
    {
        var target = await library.ReserveSaveAsync(new(60, 1080, 60, 75, LosslessVideo: lossless), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(target.MediaPath, Original, TestContext.Current.CancellationToken);
        return await library.CommitFinalizedAsync(target.Id, new(Original.Length, 1920, 1080, 60, 0, 20_000_000, true, LosslessVideo: lossless, HdrVideo: hdr), TestContext.Current.CancellationToken);
    }

    private sealed class Exporter : ICompatibleClipExporter
    {
        public bool WaitForCancellation { get; init; }
        public bool Fail { get; init; }
        public Action? AfterWriting { get; init; }
        public bool SourceHeldReadOnly { get; private set; }
        public bool Released { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExportAsync(ClipEntry clip, string sourcePath, string stagingPath, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            // These are publication/lifetime tests, not playable-video fixtures.
            try
            {
                Assert.True(clip.Media.LosslessVideo || clip.Media.HdrVideo);
                Assert.Throws<IOException>(() => { using var write = File.OpenWrite(sourcePath); });
                SourceHeldReadOnly = true;
                await using var output = new FileStream(stagingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                await output.WriteAsync(Compatible, cancellationToken); progress?.Report(40); Entered.TrySetResult();
                if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (Fail) throw new IOException("Synthetic encoder failure.");
                AfterWriting?.Invoke();
            }
            finally { Released = true; }
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
