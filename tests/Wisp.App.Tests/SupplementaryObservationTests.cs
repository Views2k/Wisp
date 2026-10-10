using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryObservationTests
{
    [Fact]
    public void AnOperationHasExactlyOneTerminalOutcome()
    {
        var observations = new List<SupplementaryObservation>();
        using (var operation = new SupplementaryOperation(observations.Add, "clips", "clips", "export", TimeProvider.System))
        { operation.Complete("success"); operation.Complete("failure"); }
        Assert.Equal(new[] { "attempt", "success" }, observations.Select(v => v.Outcome));
        Assert.Null(observations[0].DurationMs);
        Assert.True(observations[1].DurationMs >= 0);
    }

    [Fact]
    public void UnclassifiedExitIsUnknownAndObserverFailureCannotFailTheFeature()
    {
        var observations = new List<SupplementaryObservation>();
        using (new SupplementaryOperation(observations.Add, "feature", "tune", "load", TimeProvider.System)) { }
        Assert.Equal("unknown", observations[1].Outcome);
        using var operation = new SupplementaryOperation(_ => throw new InvalidOperationException(), "feature", "tune", "load", TimeProvider.System);
        operation.Complete("failure");
    }

    [Fact]
    public void PlaybackRequiresObservedProgressAndRetainsExactlyOneTerminalPerStage()
    {
        var events = new List<SupplementaryObservation>();
        var playback = new SupplementaryPlaybackObservation(stage =>
            new SupplementaryOperation(events.Add, "clips", "clips", stage, TimeProvider.System));
        playback.Prepare();
        playback.Decoded();
        Assert.DoesNotContain(events, e => e.Stage == "playback" && e.Outcome == "success");
        playback.Progressed();
        playback.Progressed();
        playback.Failed();
        playback.Reset();
        Assert.Equal(new[] { "attempt", "success" }, events.Where(e => e.Stage == "decode").Select(e => e.Outcome));
        Assert.Equal(new[] { "attempt", "success" }, events.Where(e => e.Stage == "playback").Select(e => e.Outcome));
    }

    [Fact]
    public void ClosingAnUnplayedClipIsUnknownAndReplacingItBeginsANewObservation()
    {
        var events = new List<SupplementaryObservation>();
        var playback = new SupplementaryPlaybackObservation(stage =>
            new SupplementaryOperation(events.Add, "clips", "clips", stage, TimeProvider.System));
        playback.Prepare();
        playback.Decoded();
        playback.Prepare();
        playback.Failed();
        playback.Reset();
        Assert.Equal(new[] { "attempt", "success", "attempt", "failure" }, events.Where(e => e.Stage == "decode").Select(e => e.Outcome));
        Assert.Equal(new[] { "attempt", "unknown", "attempt", "failure" }, events.Where(e => e.Stage == "playback").Select(e => e.Outcome));
    }
}
