using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MissingAnyOffOrOnPhaseFails(int missingPhase)
    {
        Assert.False(ShiftCaptureSession.CanaryPasses(Pattern().Where(sample => sample.Phase != missingPhase)));
    }

    [Fact]
    public void EmptyObservationFails()
    {
        Assert.False(ShiftCaptureSession.CanaryPasses([]));
    }

    [Fact]
    public void ConstantRedImageCannotQualifyAsFlashingHud()
    {
        Assert.False(ShiftCaptureSession.CanaryPasses(Pattern(off: 100, on: 100)));
    }

    [Fact]
    public void NoRedImageCannotQualify()
    {
        Assert.False(ShiftCaptureSession.CanaryPasses(Pattern(off: 0, on: 0)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OnlyOneVisiblePulseFails(int missingPulse)
    {
        var samples = Pattern().Select(sample => sample.Phase == missingPulse ? (sample.Phase, Matches: 0) : sample);
        Assert.False(ShiftCaptureSession.CanaryPasses(samples));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FewerThanFiveReadbacksInAnyPhaseFails(int shortPhase)
    {
        var samples = Pattern().GroupBy(sample => sample.Phase)
            .SelectMany(group => group.Key == shortPhase ? group.Take(4) : group);
        Assert.False(ShiftCaptureSession.CanaryPasses(samples));
    }

    [Fact]
    public void OffPhasesMustRemainAtMostQuarterOfWeakerPulse()
    {
        Assert.False(ShiftCaptureSession.CanaryPasses(Pattern(off: 4, on: 12)));
        Assert.True(ShiftCaptureSession.CanaryPasses(Pattern(off: 3, on: 12)));
    }

    [Fact]
    public void InsufficientOnPixelsFailEvenWithPerfectOffContrast()
    {
        Assert.False(ShiftCaptureSession.CanaryPasses(Pattern(off: 0, on: 11)));
    }

    [Fact]
    public void BothPulsesMustHaveEnoughContrastAgainstEveryOffPhase()
    {
        var samples = Pattern(off: 0, on: 100).Select(sample => sample.Phase switch
        {
            3 => (sample.Phase, Matches: 12),
            4 => (sample.Phase, Matches: 4),
            _ => sample
        });
        Assert.False(ShiftCaptureSession.CanaryPasses(samples));
    }

    [Fact]
    public void FivePhasesOfOffOnOffOnOffPassWithMinimumReadbackCounts()
    {
        Assert.True(ShiftCaptureSession.CanaryPasses(Pattern()));
    }

    private static IEnumerable<(int Phase, int Matches)> Pattern(int off = 0, int on = 24) =>
        Enumerable.Range(0, 5).SelectMany(phase => Enumerable.Repeat((Phase: phase, Matches: phase is 1 or 3 ? on : off), 5));
}
