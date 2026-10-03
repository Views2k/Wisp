using System.IO;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HdrThumbnailDecoderTests
{
    [Fact]
    public void PosterRequiresCompleteSdrColorMetadataAndFixedDimensions()
    {
        Assert.True(HdrThumbnailDecoder.IsSdrOutput(320, 180, "srgb", "bt.709", "rgb", "full"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(320, 180, "pq", "bt.2020", "rgb", "full"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(320, 180, "srgb", "bt.2020", "rgb", "full"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(320, 180, "srgb", "bt.709", "rgb", "limited"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(320, 180, "srgb", "bt.709", "bt.709", "full"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(1920, 1080, "srgb", "bt.709", "rgb", "full"));
        Assert.False(HdrThumbnailDecoder.IsSdrOutput(null, 180, null, "bt.709", "rgb", "full"));
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotOpenAFileOrNativeDecoder()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HdrThumbnailDecoder.DecodeAsync(Clip(true), "not-a-path", cancelled.Token));
    }

    [Fact]
    public async Task SdrClipCannotEnterTheHdrConversionPath()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => HdrThumbnailDecoder.DecodeAsync(Clip(false), "not-a-path", TestContext.Current.CancellationToken));
    }

    private static ClipEntry Clip(bool hdr) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, 1080, 60, 75),
        new(4, 1920, 1080, 60, 0, 20_000_000, false, HdrVideo: hdr));
}
