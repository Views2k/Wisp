using System.IO;
using Wisp.App.Supplementary;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneAssetStreamValidationTests
{
    public static TheoryData<byte, uint, uint, ulong, long, uint> RejectedStreamShapes => new()
    {
        { 1, 131072, 16221184, 16221184, 992, 0x8004A615 },
        { 0, 131072, 16221184, 16221183, 992, 0x8004A618 },
        { 0, 131072, 16221185, 16221185, 992, 0x8004A618 },
        { 0, 131072, 1047552, 1047552, 64, 0x8004A616 },
        { 0, 131072, 67109888, 67109888, 4104, 0x8004A617 },
        { 0, 131072, 67108865, 67108865, 992, 0x8004A617 },
        { 0, 1023, 16221184, 16221184, 992, 0x8004A619 },
        { 0, 67108865, 16221184, 16221184, 992, 0x8004A61A },
        { 0, 131072, 16221183, 16221184, 992, 0x8004A61B },
        { 0, 131072, 67108865, 16221184, 992, 0x8004A61C },
        { 0, 131072, 16221184, 16221184, -8, 0x8004A61D },
        { 0, 131072, 16221184, 16221184, 0, 0x8004A61D },
        { 0, 131072, 16221184, 16221184, 7, 0x8004A61D },
        { 0, 131072, 16221184, 16221184, 9, 0x8004A61F },
        { 0, 131072, 16221184, 16221184, 524296, 0x8004A61E }
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

    [Theory]
    [InlineData(1048576U)]
    [InlineData(16220160U)]
    [InlineData(17270784U)]
    [InlineData(67108864U)]
    public void MutableStreamLengthIsBoundedIndependentlyOfTheRetainedFixture(uint length)
    {
        TuneAssetCapture.ValidateStreamShape(0, 131072, length, length, ((length + 131071L) / 131072) * 8);
    }

    [Fact]
    public void BoundedMaximumCurrentStreamPasses()
    {
        TuneAssetCapture.ValidateStreamShape(0, 131072, TuneAssetCapture.MaximumLength,
            TuneAssetCapture.MaximumLength, 4096);
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
    public void EachRejectedConditionKeepsItsMeasurementsLocalAndReportsTheFirstFailedCheck(byte flag, uint chunkSize,
        uint allocated, ulong length, long vectorBytes, uint expectedCode)
    {
        var error = Assert.Throws<TuneAssetStreamValidationException>(() =>
            TuneAssetCapture.ValidateStreamShape(flag, chunkSize, allocated, length, vectorBytes));
        Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal(flag, error.Flag);
        Assert.Equal(chunkSize, error.ChunkSize);
        Assert.Equal(allocated, error.Allocated);
        Assert.Equal(length, error.Length);
        Assert.Equal(vectorBytes, error.VectorBytes);
        Assert.Equal(TuneAssetFailureCode.StreamShape, error.FailureCode);
        Assert.Equal(length, error.ActualSizeBytes);
        Assert.Equal(TuneAssetCapture.MaximumLength, error.MaximumSizeBytes);
        Assert.Null(error.ActualPageCount);
        Assert.Equal("The tuning asset stream shape does not match the supported layout.", error.Message);
        var incident = TuneCaptureService.TuneIncident(TuneCaptureStage.ReadTune, "tune-read-failed", error);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal(expectedCode, incident.Code);
        var evidence = Assert.Single(incident.Exceptions!.Value);
        Assert.Equal(nameof(TuneAssetStreamValidationException), evidence.Type);
        Assert.Equal(expectedCode, evidence.Code);
        Assert.Contains(SupplementaryIncidentSchema.MethodId("Wisp.App.Tunes.TuneAssetCapture.ValidateStreamShape"), evidence.Methods);
        var json = System.Text.Json.JsonSerializer.Serialize(incident);
        Assert.DoesNotContain(error.Message, json, StringComparison.Ordinal);
        Assert.DoesNotContain("ChunkSize", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VectorBytes", json, StringComparison.OrdinalIgnoreCase);
    }
}
