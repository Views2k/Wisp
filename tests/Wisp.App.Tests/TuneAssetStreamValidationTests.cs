using System.IO;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneAssetStreamValidationTests
{
    public static TheoryData<byte, uint, uint, ulong, long> RejectedStreamShapes => new()
    {
        { 1, 131072, 16221184, 16221184, 992 },
        { 0, 131072, 16221184, 16221183, 992 },
        { 0, 131072, 16221185, 16221185, 992 },
        { 0, 131072, 17270784, 17270784, 1056 },
        { 0, 131072, 67108865, 67108865, 992 },
        { 0, 1023, 16221184, 16221184, 992 },
        { 0, 67108865, 16221184, 16221184, 992 },
        { 0, 131072, 16221183, 16221184, 992 },
        { 0, 131072, 67108865, 16221184, 992 },
        { 0, 131072, 16221184, 16221184, -8 },
        { 0, 131072, 16221184, 16221184, 0 },
        { 0, 131072, 16221184, 16221184, 7 },
        { 0, 131072, 16221184, 16221184, 9 },
        { 0, 131072, 16221184, 16221184, 524296 }
    };

    [Fact]
    public void RetainedResearchStreamShapeStillPasses()
    {
        TuneAssetCapture.ValidateStreamShape(0, 131072, 16221184, 16221184, 124 * 8);
    }

    [Fact]
    public void ObservedLiveStreamWithOneExtraPagePasses()
    {
        TuneAssetCapture.ValidateStreamShape(0, 131072, 16222208, 16222208, 992);
    }

    [Fact]
    public void BoundedMaximumCurrentStreamPasses()
    {
        TuneAssetCapture.ValidateStreamShape(0, 131072, TuneAssetCapture.MaximumLength,
            TuneAssetCapture.MaximumLength, 1056);
    }

    [Fact]
    public void OriginalHeaderBoundsStillAllowTheirInclusiveEdgesAndSpareCapacity()
    {
        TuneAssetCapture.ValidateStreamShape(0, 1024, 16221184, 16221184, 8);
        TuneAssetCapture.ValidateStreamShape(0, 67108864, 67108864, 16221184, 524288);
        TuneAssetCapture.ValidateStreamShape(0, 131072, 16352256, 16221184, 992);
    }

    [Theory]
    [MemberData(nameof(RejectedStreamShapes))]
    public void RejectedOriginalGuardConditionsExposeOnlyTheFiveScalars(byte flag, uint chunkSize,
        uint allocated, ulong length, long vectorBytes)
    {
        var error = Assert.Throws<TuneAssetStreamValidationException>(() =>
            TuneAssetCapture.ValidateStreamShape(flag, chunkSize, allocated, length, vectorBytes));
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(flag, error.Flag);
        Assert.Equal(chunkSize, error.ChunkSize);
        Assert.Equal(allocated, error.Allocated);
        Assert.Equal(length, error.Length);
        Assert.Equal(vectorBytes, error.VectorBytes);
        Assert.Equal("The tuning asset stream shape does not match the supported layout.", error.Message);
    }
}
