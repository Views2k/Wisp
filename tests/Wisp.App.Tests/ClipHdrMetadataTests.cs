using System.IO;
using System.Text.Json;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipHdrMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispHdrMetadata", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    [Fact]
    public async Task HdrPreferenceRoundTripsWithoutClaimingAnHdrFallbackFile()
    {
        var library = new ClipLibrary(_directory);
        var recording = new ClipRecordingSpec(60, 1080, 60, 75, PreserveHdrRecording: true);
        var reservation = await library.ReserveSaveAsync(recording, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(reservation.MediaPath, Bytes, TestContext.Current.CancellationToken);
        await library.CommitFinalizedAsync(reservation.Id, new(Bytes.Length, 1920, 1080, 60, 0, 20_000_000, false), TestContext.Current.CancellationToken);
        var saved = Assert.Single((await new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.True(saved.Recording.PreserveHdrRecording);
        Assert.False(saved.Media.HdrVideo);
        Assert.False(saved.Media.RequiresMpvPlayer);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LibraryPreservesColorModeAndOldSdrRecordsOmitTheNewField(bool hdr, bool lossless)
    {
        var library = new ClipLibrary(_directory);
        var reservation = await library.ReserveSaveAsync(new(60, 1080, 60, 75, LosslessVideo: lossless), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(reservation.MediaPath, Bytes, TestContext.Current.CancellationToken);
        var media = new FinalizedClipMedia(Bytes.Length, 1920, 1080, 60, 0, 20_000_000, false, lossless, HdrVideo: hdr);
        await library.CommitFinalizedAsync(reservation.Id, media, TestContext.Current.CancellationToken);

        var entry = Assert.Single((await new ClipLibrary(_directory).GetPageAsync(0, TestContext.Current.CancellationToken)).Clips);
        Assert.Equal(media, entry.Media);
        Assert.Equal(hdr || lossless, entry.Media.RequiresMpvPlayer);
        Assert.False(entry.Recording.PreserveHdrRecording);
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, ClipLibrary.IndexFileName), TestContext.Current.CancellationToken));
        var recorded = index.RootElement.GetProperty("clips")[0].GetProperty("media");
        Assert.Equal(hdr, recorded.TryGetProperty("hdrVideo", out var flag));
        if (hdr) Assert.True(flag.GetBoolean());
        Assert.False(recorded.TryGetProperty("requiresMpvPlayer", out _));

        var destination = Path.Combine(_directory, "original.mp4");
        Assert.True((await library.ExportAsync(entry.Id, destination, TestContext.Current.CancellationToken)).FileCreated);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(media, Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).Media);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FinalizedWireResultDefaultsOldMessagesToSdr(bool? supplied, bool expected)
    {
        var session = Guid.NewGuid();
        var fields = Result(session);
        if (supplied is { } value) fields["hdrVideo"] = value;
        var reply = Assert.IsType<RecorderReply>(RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.Equal(expected, reply.Media!.HdrVideo);
        Assert.Equal(expected, reply.Media.RequiresMpvPlayer);
    }

    [Theory]
    [InlineData("true")]
    [InlineData(1)]
    [InlineData(null)]
    public void MalformedHdrFlagIsRejectedInsteadOfReinterpreted(object? supplied)
    {
        var session = Guid.NewGuid();
        var fields = Result(session);
        fields["hdrVideo"] = supplied;
        var error = Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.Equal("protocol_error", error.Reason);
    }

    [Fact]
    public void UnsupportedHdrEncoderRemainsARecognizedSpecificRecorderFailure()
    {
        var session = Guid.NewGuid();
        var fields = new Dictionary<string, object>
        {
            ["v"] = RecorderProtocol.Version,
            ["session"] = session.ToString("N"),
            ["request"] = 0,
            ["type"] = "state",
            ["state"] = "error",
            ["reason"] = "hdr_encoder_unsupported"
        };
        var result = Assert.IsType<RecorderStateUpdate>(RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.Equal("hdr_encoder_unsupported", result.Reason);
        Assert.Equal("HDR recording needs a supported NVIDIA 10-bit HEVC encoder. Standard SDR recording is still available on compatible hardware.",
            ClipRecorderService.ReasonText(result.Reason));
    }

    [Theory]
    [InlineData(false, "yuv420p10le", "bt.2020-ncl", "limited")]
    [InlineData(false, "yuv420p10", "bt.2020-ncl", "limited")]
    [InlineData(true, "gbrp10le", "rgb", "full")]
    [InlineData(true, "gbrp10", "rgb", "full")]
    public void HdrPlaybackAcceptsOnlyItsRecordedTenBitColorTuple(bool lossless, string pixel, string matrix, string range)
    {
        var media = new FinalizedClipMedia(4, 1920, 1080, 60, 0, 20_000_000, false, lossless, HdrVideo: true);
        Assert.True(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", pixel, "pq", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "h264", pixel, "pq", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", lossless ? "gbrp" : "yuv420p", "pq", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", lossless ? "gbrp10be" : "yuv420p10be", "pq", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", lossless ? "gbrp12le" : "yuv420p12le", "pq", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", pixel, "srgb", matrix, range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", pixel, "pq", "bt.709", range, "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", pixel, "pq", matrix, lossless ? "limited" : "full", "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", pixel, "pq", matrix, range, "bt.709"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media with { LosslessVideo = !lossless }, "hevc", pixel, "pq", matrix, range, "bt.2020"));
    }

    [Fact]
    public void ExistingSdrLosslessPlaybackRetainsItsLegacyFormatGate()
    {
        var media = new FinalizedClipMedia(4, 1920, 1080, 60, 0, 20_000_000, false, LosslessVideo: true);
        Assert.True(LosslessClipPlayer.MatchesDecodedFormat(media, "h264", "gbrp", "srgb", "rgb", "full", "bt.709"));
        Assert.True(LosslessClipPlayer.MatchesDecodedFormat(media, "h264", "gbrp", "bt.1886", "rgb", "full", "bt.709"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media, "hevc", "gbrp10le", "pq", "rgb", "full", "bt.2020"));
        Assert.False(LosslessClipPlayer.MatchesDecodedFormat(media with { LosslessVideo = false }, "h264", "yuv420p", "bt.1886", "bt.709", "limited", "bt.709"));
    }

    private static Dictionary<string, object?> Result(Guid session) => new()
    {
        ["v"] = RecorderProtocol.Version,
        ["session"] = session.ToString("N"),
        ["request"] = 1,
        ["type"] = "result",
        ["ok"] = true,
        ["reason"] = "audio_unavailable",
        ["clipId"] = Guid.NewGuid().ToString("N"),
        ["fileBytes"] = Bytes.Length,
        ["width"] = 1920,
        ["height"] = 1080,
        ["frameRate"] = 60,
        ["start100ns"] = 0,
        ["end100ns"] = 20_000_000,
        ["hasAudio"] = false,
        ["losslessVideo"] = false,
        ["sizeLimited"] = false
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
