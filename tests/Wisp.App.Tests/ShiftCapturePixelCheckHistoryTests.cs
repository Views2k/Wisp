using Wisp.App.ShiftCapture;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCapturePixelCheckHistoryTests
{
    private static readonly ShiftCaptureRect Region = new(30, 40, 4, 4);
    private const uint Red = 0xFFFF5364;

    [Fact]
    public void PassedCheckSurvivesUnavailableOverlayAndOpacityFadeWithoutQualifyingLaterPixels()
    {
        var history = new ShiftCapturePixelCheckHistory();
        var source = new PixelSource();
        using var probe = new ShiftCapturePresentationProbe(source, () => source.Now, 1000);
        CompleteCanary(history, source, probe);
        source.Now += 100;
        Assert.True(Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1)).Qualified);

        source.Status = ShiftCapturePresentationStatus.OverlayUnavailable;
        source.Now += 100;
        var unavailable = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1));
        Assert.False(unavailable.Qualified);
        Assert.True(history.RecordInvalidation(true, unavailable.Status.ToString(), unavailable.ReadFinishedQpc));

        source.Status = ShiftCapturePresentationStatus.Observed;
        source.Now += 100;
        var resumed = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1));
        Assert.False(resumed.Qualified);
        // The real session clears current qualification on target changes; a
        // returned rectangle/color does not restore the completed check's scope.
        probe.SetQualified(false);
        Assert.False(history.RecordInvalidation(false, "Pixel-target-changed", source.Now));
        source.Now += 100;
        var fading = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, 0x80FF5364, 2));
        Assert.False(fading.Qualified);
        source.Now += 100;
        var restored = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 3));
        Assert.False(restored.Qualified);

        var stopped = history.Snapshot(restored.Qualified, observerFault: true);
        Assert.True(stopped.CanaryPassed);
        Assert.False(stopped.CurrentContextQualified);
        Assert.True(stopped.ObserverFault);
        Assert.True(stopped.QualificationLostDuringSession);
        Assert.Equal("OverlayUnavailable", stopped.FirstLossReason);
        Assert.Equal(unavailable.ReadFinishedQpc, stopped.FirstLossQpc);
        Assert.True(stopped.Incomplete);
    }

    [Fact]
    public void ChangedTargetAlonePreservesPriorPassButKeepsCoverageIncomplete()
    {
        var history = new ShiftCapturePixelCheckHistory();
        var source = new PixelSource();
        using var probe = new ShiftCapturePresentationProbe(source, () => source.Now, 1000);
        CompleteCanary(history, source, probe);
        Assert.True(history.RecordInvalidation(true, "Pixel-target-changed", source.Now));
        probe.SetQualified(false);
        source.Now += 100;
        var next = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 2));

        var summary = history.Snapshot(next.Qualified, observerFault: false);
        Assert.True(summary.CanaryPassed);
        Assert.False(summary.CurrentContextQualified);
        Assert.False(summary.ObserverFault);
        Assert.True(summary.QualificationLostDuringSession);
        Assert.True(summary.Incomplete);
    }

    [Fact]
    public void SuccessfulRetryDoesNotEraseAnEarlierObservationGap()
    {
        var history = new ShiftCapturePixelCheckHistory();
        history.RecordCanary(true);
        Assert.True(history.RecordInvalidation(true, "OverlayUnavailable", 62_520));
        history.RecordCanary(true);

        var summary = history.Snapshot(currentlyQualified: true, observerFault: false);
        Assert.True(summary.CanaryPassed);
        Assert.True(summary.CurrentContextQualified);
        Assert.False(summary.ObserverFault);
        Assert.True(summary.QualificationLostDuringSession);
        Assert.True(summary.Incomplete);
        Assert.Equal(62_520, summary.FirstLossQpc);
    }

    [Fact]
    public void GenuineContrastFailureDoesNotBecomeAPassedCanary()
    {
        var history = new ShiftCapturePixelCheckHistory();
        var samples = Enumerable.Range(0, 5).SelectMany(phase => Enumerable.Repeat((Phase: phase, Matches: 0), 6));
        var qualifies = ShiftCaptureSession.CanaryPasses(samples);
        Assert.False(qualifies);
        history.RecordCanary(qualifies);
        var summary = history.Snapshot(currentlyQualified: false, observerFault: true);

        Assert.False(summary.CanaryPassed);
        Assert.False(summary.QualificationLostDuringSession);
        Assert.Null(summary.FirstLossReason);
        Assert.True(summary.ObserverFault);
        Assert.True(summary.Incomplete);
    }

    [Fact]
    public void LaterFailedCanaryDoesNotEraseHistoricalPassOrClearCurrentFailure()
    {
        var history = new ShiftCapturePixelCheckHistory();
        history.RecordCanary(true);
        history.RecordInvalidation(true, "Canary-retry", 100);
        history.RecordCanary(false);
        var summary = history.Snapshot(currentlyQualified: false, observerFault: true);

        Assert.True(summary.CanaryPassed);
        Assert.False(summary.CurrentContextQualified);
        Assert.True(summary.ObserverFault);
        Assert.True(summary.Incomplete);
    }

    [Fact]
    public void ReaderCostFailureRemainsIncompleteAfterHistoricalSuccess()
    {
        var history = new ShiftCapturePixelCheckHistory();
        var source = new PixelSource();
        using var probe = new ShiftCapturePresentationProbe(source, () => source.Now, 1000);
        CompleteCanary(history, source, probe);
        source.Now += 100;
        source.Cost = 51;
        var failed = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1));
        Assert.Equal(ShiftCapturePresentationStatus.CostLimitExceeded, failed.Status);
        Assert.False(failed.Qualified);
        history.RecordInvalidation(true, failed.Status.ToString(), failed.ReadFinishedQpc);
        var summary = history.Snapshot(failed.Qualified, observerFault: true);

        Assert.True(summary.CanaryPassed);
        Assert.True(summary.ObserverFault);
        Assert.Equal("CostLimitExceeded", summary.FirstLossReason);
        Assert.True(summary.Incomplete);
        Assert.False(probe.SetQualified(true));
    }

    [Fact]
    public void UndisturbedQualifiedCheckIsReportedComplete()
    {
        var history = new ShiftCapturePixelCheckHistory();
        var source = new PixelSource();
        using var probe = new ShiftCapturePresentationProbe(source, () => source.Now, 1000);
        CompleteCanary(history, source, probe);
        var summary = history.Snapshot(currentlyQualified: true, observerFault: false);

        Assert.True(summary.CanaryPassed);
        Assert.True(summary.CurrentContextQualified);
        Assert.False(summary.QualificationLostDuringSession);
        Assert.False(summary.Incomplete);
    }

    private static void CompleteCanary(ShiftCapturePixelCheckHistory history, PixelSource source, ShiftCapturePresentationProbe probe)
    {
        var samples = new List<(int Phase, int Matches)>();
        int[] counts = [5, 6, 6, 6, 6];
        for (var phase = 0; phase < counts.Length; phase++)
        {
            source.On = phase is 1 or 3;
            for (var sample = 0; sample < counts[phase]; sample++)
            {
                source.Now += 100;
                var read = Assert.IsType<ShiftCapturePresentationSample>(probe.TrySample(Region, Red, 1));
                Assert.Equal(ShiftCapturePresentationStatus.Observed, read.Status);
                samples.Add((phase, read.Pixels.CompatiblePixelCount));
            }
        }
        var qualifies = probe.SetQualified(ShiftCaptureSession.CanaryPasses(samples));
        Assert.True(qualifies);
        history.RecordCanary(qualifies);
    }

    private sealed class PixelSource : IShiftCapturePixelSource
    {
        internal long Now = 1;
        internal long Cost = 1;
        internal bool On;
        internal ShiftCapturePresentationStatus Status = ShiftCapturePresentationStatus.Observed;
        public ShiftCapturePresentationStatus OverlayStatus() => Status;
        public bool TryCopy(ShiftCaptureRect region, byte[] bgra, out int errorCode)
        {
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = On ? (byte)0x64 : (byte)0;
                bgra[i + 1] = On ? (byte)0x53 : (byte)0;
                bgra[i + 2] = On ? (byte)0xFF : (byte)0;
            }
            Now += Cost;
            errorCode = 0;
            return true;
        }
        public void Dispose() { }
    }
}
