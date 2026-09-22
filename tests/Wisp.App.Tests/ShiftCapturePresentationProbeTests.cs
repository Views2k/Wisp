using Wisp.App.ShiftCapture;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCapturePresentationProbeTests
{
    private static readonly ShiftCaptureRect Region = new(30, 40, 2, 2);
    private const uint Red = 0xFFFF2030;

    [Fact]
    public void PixelAnalysisUsesRgbAndDoesNotTreatReservedAlphaAsTransparency()
    {
        byte[] pixels = [48, 32, 255, 0, 48, 32, 255, 255, 255, 0, 0, 0, 0, 255, 0, 255];
        var summary = ShiftCapturePresentationProbe.AnalyzeBgra(pixels, Red);
        Assert.Equal(4, summary.PixelCount);
        Assert.Equal(2, summary.CompatiblePixelCount);
        Assert.Equal(.5, summary.CompatibleFraction);
        pixels[3] = 255;
        pixels[7] = 0;
        Assert.Equal(summary, ShiftCapturePresentationProbe.AnalyzeBgra(pixels, Red));
        pixels[0]++;
        Assert.NotEqual(summary.PixelHash, ShiftCapturePresentationProbe.AnalyzeBgra(pixels, Red).PixelHash);
    }

    [Fact]
    public void TranslucentColorAnalysisAllowsDarkBackgroundButRemainsOnlyCompatibilityEvidence()
    {
        byte[] pixels = [20, 20, 148, 0, 0, 0, 0, 0, 20, 148, 20, 0];
        var summary = ShiftCapturePresentationProbe.AnalyzeBgra(pixels, 0x80FF0000);
        Assert.Equal(1, summary.CompatiblePixelCount);
        Assert.Equal(0, ShiftCapturePresentationProbe.AnalyzeBgra(pixels, 0x00FF0000).CompatiblePixelCount);
        Assert.Throws<ArgumentException>(() => ShiftCapturePresentationProbe.AnalyzeBgra(new byte[3], Red));
    }

    [Fact]
    public void DefaultIsUnqualifiedAndEvenMatchingPixelsDoNotQualifyTheBackend()
    {
        var source = new FakeSource();
        using var probe = Probe(source);
        Assert.False(probe.SetQualified(true));
        var sample = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(ShiftCapturePresentationStatus.Observed, sample.Status);
        Assert.Equal(4, sample.Pixels.CompatiblePixelCount);
        Assert.False(sample.Qualified);
        Assert.Equal("conditional-desktop-roi-readback", sample.EvidenceKind);
        Assert.True(probe.SetQualified(true));
        source.Now += 20;
        Assert.True(Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Qualified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ForegroundContextRegionOrExpectedColorChangeInvalidatesQualification(int change)
    {
        var source = new FakeSource();
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        Assert.True(probe.SetQualified(true));
        source.Now += 20;
        var next = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(
            change == 0 ? Region with { X = 31 } : Region, change == 1 ? 0xFF00FF00 : Red, change == 2 ? 11 : 10));
        Assert.False(next.Qualified);
        Assert.Null(next.PreviousReadStartedQpc);
    }

    [Theory]
    [InlineData((int)ShiftCapturePresentationStatus.CaptureExcluded)]
    [InlineData((int)ShiftCapturePresentationStatus.OverlayUnavailable)]
    public void ExcludedOrHiddenOverlayCannotQualifyOrCapture(int statusValue)
    {
        var status = (ShiftCapturePresentationStatus)statusValue;
        var source = new FakeSource();
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        Assert.True(probe.SetQualified(true));
        source.Status = status;
        Assert.False(probe.SetQualified(true));
        source.Now += 20;
        var sample = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(status, sample.Status);
        Assert.False(sample.Qualified);
        Assert.Equal(1, source.Copies);
    }

    [Fact]
    public void TimingPreservesReadbackIntervalsAndPacesReadsFromMeasuredCost()
    {
        var source = new FakeSource { Now = 100, Cost = 2 };
        using var probe = Probe(source);
        var first = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(100, first.ReadStartedQpc);
        Assert.Equal(102, first.ReadFinishedQpc);
        Assert.Equal(2d, first.ReadbackMilliseconds);
        Assert.Equal(1000, first.QpcFrequency);
        Assert.Equal(40, first.RecommendedIntervalMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(40), probe.RecommendedInterval);
        Assert.Null(probe.TrySample(Region, Red, 10));
        Assert.Equal(1, source.Copies);
        source.Now = 117;
        Assert.Null(probe.TrySample(Region, Red, 10));
        source.Now = 139;
        Assert.Null(probe.TrySample(Region, Red, 10));
        Assert.Equal(1, source.Copies);
        source.Now = 140;
        var next = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(100, next.PreviousReadStartedQpc);
        Assert.Equal(102, next.PreviousReadFinishedQpc);
        Assert.Equal(140, next.ReadStartedQpc);
        Assert.Equal(142, next.ReadFinishedQpc);
    }

    [Fact]
    public void FailedReadInvalidatesQualificationAndDoesNotRetainRawPixels()
    {
        var source = new FakeSource();
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        Assert.All(Assert.IsType<byte[]>(source.LastBuffer), value => Assert.Equal(0, value));
        Assert.True(probe.SetQualified(true));
        source.CopySucceeds = false;
        source.Now += 20;
        var sample = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(ShiftCapturePresentationStatus.CaptureFailed, sample.Status);
        Assert.Equal(5, sample.ErrorCode);
        Assert.False(sample.Qualified);
        Assert.Equal(default, sample.Pixels);
        Assert.All(Assert.IsType<byte[]>(source.LastBuffer), value => Assert.Equal(0, value));
        Assert.False(probe.SetQualified(true));
    }

    [Fact]
    public void NineMillisecondReadsContinueAtFivePercentReadbackDuty()
    {
        var source = new FakeSource { Cost = 9 };
        using var probe = Probe(source);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            source.Now = attempt * 180;
            var sample = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
            Assert.Equal(ShiftCapturePresentationStatus.Observed, sample.Status);
            Assert.Equal(180, sample.RecommendedIntervalMilliseconds);
            Assert.Equal(.05, sample.ReadbackMilliseconds / sample.RecommendedIntervalMilliseconds, 12);
            Assert.Null(probe.TrySample(Region, Red, 10));
            source.Now = attempt * 180 + 179;
            Assert.Null(probe.TrySample(Region, Red, 10));
            Assert.Equal(attempt + 1, source.Copies);
        }
        Assert.True(probe.SetQualified(true));
    }

    [Fact]
    public void CheapReadbackStillCannotExceedSixtyReadsPerSecond()
    {
        var source = new FakeSource { Cost = 0 };
        using var probe = Probe(source);
        var first = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(1000d / 60, first.RecommendedIntervalMilliseconds);
        source.Now = 16;
        Assert.Null(probe.TrySample(Region, Red, 10));
        Assert.Equal(1, source.Copies);
        source.Now = 17;
        Assert.Equal(ShiftCapturePresentationStatus.Observed,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
        Assert.Equal(2, source.Copies);
    }

    [Fact]
    public void InvalidContextResetsQualificationWithoutResettingCostPacing()
    {
        var source = new FakeSource { Cost = 9 };
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        Assert.True(probe.SetQualified(true));
        Assert.Equal(ShiftCapturePresentationStatus.InvalidRegion,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region with { Width = 257 }, Red, 11)).Status);
        Assert.False(probe.SetQualified(true));
        source.Now = 20;
        Assert.Null(probe.TrySample(Region, Red, 12));
        Assert.Equal(1, source.Copies);
        Assert.Equal(TimeSpan.FromMilliseconds(180), probe.RecommendedInterval);
        source.Now = 180;
        source.Cost = 1;
        var recovered = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 12));
        Assert.False(recovered.Qualified);
        Assert.Null(recovered.PreviousReadStartedQpc);
        Assert.Equal(180, recovered.RecommendedIntervalMilliseconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneReadOverFiftyMillisecondsStopsPixelsAndReportsFailureOnlyOnce(bool succeeds)
    {
        var source = new FakeSource { Cost = 51, CopySucceeds = succeeds };
        using var probe = Probe(source);
        var first = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(ShiftCapturePresentationStatus.CostLimitExceeded, first.Status);
        Assert.Equal(51, first.ReadbackMilliseconds);
        Assert.Equal(1000, first.RecommendedIntervalMilliseconds);
        Assert.All(Assert.IsType<byte[]>(source.LastBuffer), value => Assert.Equal(0, value));
        for (var attempt = 0; attempt < 10; attempt++)
        {
            source.Now += 2000;
            Assert.Null(probe.TrySample(Region, Red, 10 + attempt));
        }
        Assert.Equal(1, source.Copies);
        Assert.False(probe.SetQualified(true));
    }

    [Fact]
    public void FiftyMillisecondReadsUseMaximumIntervalWithoutTrippingHardLimit()
    {
        var source = new FakeSource { Cost = 50 };
        using var probe = Probe(source);
        var first = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(ShiftCapturePresentationStatus.Observed, first.Status);
        Assert.Equal(1000, first.RecommendedIntervalMilliseconds);
        source.Now = 999;
        Assert.Null(probe.TrySample(Region, Red, 10));
        source.Now = 1000;
        Assert.Equal(ShiftCapturePresentationStatus.Observed,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
        Assert.Equal(2, source.Copies);
    }

    [Fact]
    public void UnchangedFailureDoesNotSpamEventsAndRecoveryAllowsNewFailure()
    {
        var source = new FakeSource { Status = ShiftCapturePresentationStatus.OverlayUnavailable };
        using var probe = Probe(source);
        Assert.Equal(ShiftCapturePresentationStatus.OverlayUnavailable,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            source.Now += 20;
            Assert.Null(probe.TrySample(Region, Red, 10));
        }
        Assert.Equal(0, source.Copies);
        source.Status = ShiftCapturePresentationStatus.Observed;
        Assert.Equal(ShiftCapturePresentationStatus.Observed,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
        source.Status = ShiftCapturePresentationStatus.OverlayUnavailable;
        source.Now += 20;
        Assert.Equal(ShiftCapturePresentationStatus.OverlayUnavailable,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
    }

    [Fact]
    public void SlowerReadRaisesIntervalAndFasterReadDoesNotImmediatelyReaccelerate()
    {
        var source = new FakeSource { Cost = 9 };
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        source.Now = 180;
        source.Cost = 11;
        var slower = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(220, slower.RecommendedIntervalMilliseconds);
        source.Now = 400;
        source.Cost = 2;
        var faster = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10));
        Assert.Equal(220, faster.RecommendedIntervalMilliseconds);
        Assert.Equal(3, source.Copies);
    }

    [Fact]
    public void HardCostStopSurvivesInvalidRegionAndContextChanges()
    {
        var source = new FakeSource { Cost = 100 };
        using var probe = Probe(source);
        Assert.Equal(ShiftCapturePresentationStatus.CostLimitExceeded,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 10)).Status);
        source.Now += 2000;
        Assert.Null(probe.TrySample(Region with { Width = 257 }, Red, 11));
        Assert.Null(probe.TrySample(Region, 0xFF00FF00, 12));
        Assert.Equal(1, source.Copies);
        Assert.False(probe.SetQualified(true));
    }

    [Fact]
    public void SourceReplacementPreservesPacingAndInvalidatesQualificationAndHistory()
    {
        var source = new FakeSource { Cost = 9 };
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        Assert.True(probe.SetQualified(true));
        var replacement = new FakeSource { Cost = 0 };
        probe.ReplaceSource(replacement);
        Assert.Equal(1, source.Disposals);
        Assert.False(probe.SetQualified(true));
        Assert.Equal(TimeSpan.FromMilliseconds(180), probe.RecommendedInterval);
        source.Now = 179;
        Assert.Null(probe.TrySample(Region, Red, 11));
        Assert.Equal(0, replacement.Copies);
        source.Now = 180;
        var sample = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 11));
        Assert.False(sample.Qualified);
        Assert.Null(sample.PreviousReadStartedQpc);
        Assert.Equal(180, sample.RecommendedIntervalMilliseconds);
        Assert.Equal(1, replacement.Copies);
    }

    [Fact]
    public void SourceReplacementCannotBypassHardCostStop()
    {
        var source = new FakeSource { Cost = 51 };
        using var probe = Probe(source);
        _ = probe.TrySample(Region, Red, 10);
        var replacement = new FakeSource();
        probe.ReplaceSource(replacement);
        source.Now = 2000;
        Assert.Null(probe.TrySample(Region, Red, 11));
        Assert.Equal(0, replacement.Copies);
        Assert.False(probe.SetQualified(true));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), probe.RecommendedInterval);
        Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public void ReplacingDisposedSourceReleasesIncomingResourcesWithoutReopeningProbe()
    {
        var source = new FakeSource();
        var probe = Probe(source);
        probe.Dispose();
        var replacement = new FakeSource();
        probe.ReplaceSource(replacement);
        Assert.Equal(1, replacement.Disposals);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(ShiftCapturePresentationStatus.Disposed,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 11)).Status);
        Assert.Equal(0, replacement.Copies);
    }

    [Fact]
    public void InvalidRegionNeverCopiesAndDisposeReleasesCaptureResources()
    {
        var source = new FakeSource();
        var probe = Probe(source);
        Assert.Equal(ShiftCapturePresentationStatus.InvalidRegion,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region with { Width = 257 }, Red, 1)).Status);
        Assert.Equal(0, source.Copies);
        probe.Dispose();
        probe.Dispose();
        Assert.Equal(1, source.Disposals);
        Assert.Equal(ShiftCapturePresentationStatus.Disposed,
            Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1)).Status);
        Assert.False(probe.SetQualified(true));
    }

    private static ShiftCapturePresentationProbe Probe(FakeSource source) => new(source, () => source.Now, 1000);

    private sealed class FakeSource : IShiftCapturePixelSource
    {
        internal long Now;
        internal long Cost = 1;
        internal int Copies;
        internal int Disposals;
        internal bool CopySucceeds = true;
        internal byte[]? LastBuffer;
        internal ShiftCapturePresentationStatus Status = ShiftCapturePresentationStatus.Observed;
        public ShiftCapturePresentationStatus OverlayStatus() => Status;
        public bool TryCopy(ShiftCaptureRect region, byte[] bgra, out int errorCode)
        {
            Copies++;
            LastBuffer = bgra;
            for (var offset = 0; offset < bgra.Length; offset += 4)
            {
                bgra[offset] = 48;
                bgra[offset + 1] = 32;
                bgra[offset + 2] = 255;
            }
            Now += Cost;
            errorCode = CopySucceeds ? 0 : 5;
            return CopySucceeds;
        }
        public void Dispose() => Disposals++;
    }
}
