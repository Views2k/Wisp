using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsPlaybackStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void PlaybackCopyPreparationHasProgressButCannotOpenOrTimeOutAsNativeLoading(int progress)
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        var revision = state.Revision;
        state.CopyPreparationChanged(revision, true, progress);
        Assert.Equal($"Preparing HDR playback copy… {progress}%", state.Status);
        Assert.True(state.Loading);
        Assert.True(state.Paused);
        Assert.False(state.CanTogglePlayback);
        Assert.False(state.CanSeek);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(3)));
        Assert.False(state.TryOpen(revision, true, 3840, 2160, 10));

        state.CopyPreparationChanged(revision, false, null);
        Assert.True(state.OpeningTimedOut(TimeSpan.FromSeconds(15)));
        Assert.True(state.TryOpen(revision, true, 3840, 2160, 10));
        Assert.True(state.Paused);
        Assert.Equal("Ready · press Play.", state.Status);
        Assert.False(state.ObservePosition(revision, 1));
    }

    [Fact]
    public void ChangingSelectionRejectsLateCopyProgressAndRestoresNormalLoading()
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        var oldRevision = state.Revision;
        state.CopyPreparationChanged(oldRevision, true, 10);
        state.Prepare();
        state.CopyPreparationChanged(oldRevision, true, 80);
        Assert.False(state.PreparingPlaybackCopy);
        Assert.Null(state.PreparationProgress);
        Assert.Equal("Preparing clip…", state.Status);
        Assert.True(state.OpeningTimedOut(TimeSpan.FromSeconds(15)));
        state.CopyPreparationChanged(state.Revision, true, double.NaN);
        Assert.Equal("Preparing HDR playback copy…", state.Status);
        state.Reset();
        Assert.False(state.Loading);
        Assert.False(state.PreparingPlaybackCopy);
        Assert.Null(state.PreparationProgress);
    }

    [Theory]
    [InlineData(false, 1920, 1080, 60)]
    [InlineData(true, 0, 1080, 60)]
    [InlineData(true, 1920, 0, 60)]
    [InlineData(true, 1920, 1080, 0)]
    [InlineData(true, 1920, 1080, -1)]
    [InlineData(true, 1920, 1080, double.NaN)]
    [InlineData(true, 1920, 1080, double.PositiveInfinity)]
    public void InvalidMediaCannotLeavePreparation(bool hasVideo, int width, int height, double seconds)
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        Assert.Equal("Preparing clip…", state.Status);
        Assert.False(state.TryOpen(state.Revision, hasVideo, width, height, seconds));
        Assert.True(state.Preparing);
        Assert.False(state.Ready);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        Assert.False(state.CanTogglePlayback);
        Assert.False(state.ObservePosition(state.Revision, 1));
    }

    [Fact]
    public void CurrentClipOpensOnceAndOnlyAdvancementMarksItViewed()
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        var revision = state.Revision;
        Assert.True(state.TryOpen(revision, true, 3840, 2160, 300));
        Assert.False(state.TryOpen(revision, true, 1920, 1080, 60));
        Assert.Equal(300, state.DurationSeconds);
        Assert.True(state.Ready);
        Assert.False(state.Preparing);
        Assert.False(state.Loading);
        Assert.True(state.CanSeek);
        Assert.True(state.Paused);
        Assert.True(state.CanTogglePlayback);
        Assert.Equal("Ready · press Play.", state.Status);
        Assert.False(state.ObservePosition(revision, .25));
        state.SetPaused(false);
        Assert.False(state.ObservePosition(revision, 0));
        Assert.False(state.ObservePosition(revision, double.NaN));
        Assert.True(state.ObservePosition(revision, .25));
        Assert.False(state.ObservePosition(revision, .5));
    }

    [Fact]
    public void OpeningAndBufferingCompletionNeverStartPlayback()
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        var revision = state.Revision;
        state.SetPaused(false);
        Assert.True(state.Paused);
        Assert.False(state.CanTogglePlayback);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        state.BufferingChanged(revision, true);
        Assert.True(state.TryOpen(revision, true, 1920, 1080, 60));
        Assert.Equal("Loading clip…", state.Status);
        Assert.False(state.CanTogglePlayback);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        state.SetPaused(false);
        Assert.True(state.Paused);
        Assert.False(state.ObservePosition(revision, 1));

        state.BufferingChanged(revision, false);
        Assert.True(state.Paused);
        Assert.True(state.CanTogglePlayback);
        Assert.False(state.Loading);
        Assert.True(state.CanSeek);
        Assert.Equal("Ready · press Play.", state.Status);
        Assert.False(state.ObservePosition(revision, 1));
        Assert.False(state.TryOpen(revision, true, 1920, 1080, 60));
        Assert.True(state.Paused);

        state.SetPaused(false);
        Assert.False(state.Paused);
        Assert.True(state.ObservePosition(revision, .25));
    }

    [Fact]
    public void BufferingBeforeFirstPlayKeepsThePreparedClipPaused()
    {
        var state = Open(play: false);
        var revision = state.Revision;
        state.BufferingChanged(revision, true);
        Assert.Equal("Loading clip…", state.Status);
        Assert.False(state.CanTogglePlayback);
        state.BufferingChanged(revision, false);
        Assert.Equal("Ready · press Play.", state.Status);
        Assert.True(state.Paused);
        Assert.True(state.CanTogglePlayback);
        Assert.False(state.ObservePosition(revision, .5));
    }

    [Fact]
    public void BufferingCallbackAfterExplicitPlayPreservesTransportIntent()
    {
        var state = Open(play: false);
        state.SetPaused(false);
        state.BufferingChanged(state.Revision, true);
        Assert.False(state.Paused);
        Assert.False(state.WaitingForInitialBuffer);
        Assert.True(state.CanTogglePlayback);
        Assert.Equal("Buffering clip…", state.Status);
        Assert.False(state.ObservePosition(state.Revision, .25));
        state.SetPaused(true);
        state.BufferingChanged(state.Revision, false);
        Assert.True(state.Paused);
        Assert.Equal("Paused", state.Status);
    }

    [Fact]
    public void InitialBufferingRemainsBoundedButReadyIdleTimeDoesNotTimeOut()
    {
        var state = Open(play: false);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(1)));
        state.BufferingChanged(state.Revision, true);
        Assert.True(state.WaitingForInitialBuffer);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMilliseconds(14_999)));
        Assert.True(state.OpeningTimedOut(TimeSpan.FromSeconds(15)));
        state.BufferingChanged(state.Revision, false);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(1)));
        state.SetPaused(false);
        state.BufferingChanged(state.Revision, true);
        Assert.False(state.WaitingForInitialBuffer);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void UserPauseSurvivesBufferingAndResumeKeepsTheSameClip()
    {
        var state = Open();
        var revision = state.Revision;
        state.BufferingChanged(revision, true);
        Assert.False(state.Paused);
        Assert.True(state.CanTogglePlayback);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        Assert.Equal("Buffering clip…", state.Status);
        Assert.False(state.ObservePosition(revision, .5));
        state.SetPaused(true);
        Assert.False(state.CanTogglePlayback);
        Assert.False(state.CanSeek);
        Assert.Equal("Paused · buffering clip…", state.Status);
        state.SetPaused(false);
        Assert.True(state.Paused);
        state.BufferingChanged(revision, false);
        Assert.True(state.Paused);
        Assert.True(state.CanTogglePlayback);
        Assert.False(state.Loading);
        Assert.True(state.CanSeek);
        Assert.Equal("Paused", state.Status);
        Assert.False(state.ObservePosition(revision, .5));
        state.SetPaused(false);
        Assert.Equal(revision, state.Revision);
        Assert.Equal("", state.Status);
        Assert.True(state.ObservePosition(revision, .75));
    }

    [Fact]
    public void SeekJumpDoesNotCountAsPlaybackAdvancement()
    {
        var state = Open();
        state.SeekTo(40);
        Assert.False(state.ObservePosition(state.Revision, 40));
        Assert.False(state.ObservePosition(state.Revision, 40));
        Assert.True(state.ObservePosition(state.Revision, 40.25));
    }

    [Fact]
    public void PausedSeekAndReplayPreserveUserIntent()
    {
        var state = Open();
        var revision = state.Revision;
        state.ReachEnd(revision);
        Assert.True(state.Paused);
        Assert.True(state.Ended);
        Assert.Equal("Clip finished.", state.Status);
        state.SeekTo(30);
        Assert.True(state.Paused);
        Assert.False(state.Ended);
        Assert.False(state.ObservePosition(revision, 30));
        state.ReachEnd(revision);
        state.SetPaused(false);
        Assert.False(state.Paused);
        Assert.False(state.Ended);
        Assert.True(state.ObservePosition(revision, .25));
    }

    [Fact]
    public void ReplacedClipEventsCannotStartBufferOrEndTheNewClip()
    {
        var state = Open();
        var previous = state.Revision;
        state.Prepare();
        var current = state.Revision;
        Assert.NotEqual(previous, current);
        Assert.False(state.TryOpen(previous, true, 1920, 1080, 60));
        state.BufferingChanged(previous, true);
        state.ReachEnd(previous);
        Assert.True(state.Preparing);
        Assert.False(state.Buffering);
        Assert.False(state.Ended);
        Assert.True(state.TryOpen(current, true, 1920, 1080, 60));
        Assert.False(state.ObservePosition(previous, 1));
        Assert.True(state.Paused);
        Assert.False(state.ObservePosition(current, .25));
        state.SetPaused(false);
        Assert.True(state.ObservePosition(current, .25));
    }

    [Fact]
    public void PreviousClipBufferCompletionCannotEnableControlsForTheCurrentClip()
    {
        var state = Open();
        var previous = state.Revision;
        state.Prepare();
        var current = state.Revision;
        state.BufferingChanged(current, true);
        Assert.True(state.TryOpen(current, true, 1920, 1080, 60));

        state.BufferingChanged(previous, false);
        state.ReachEnd(previous);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        Assert.False(state.CanTogglePlayback);
        Assert.True(state.Paused);
        Assert.False(state.Ended);

        state.BufferingChanged(current, false);
        Assert.False(state.Loading);
        Assert.True(state.CanSeek);
        Assert.True(state.CanTogglePlayback);
        Assert.True(state.Paused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingOrFailingWhileLoadingClearsBusyStateAndRejectsLateEvents(bool fail)
    {
        var state = Open(play: false);
        var revision = state.Revision;
        state.BufferingChanged(revision, true);
        Assert.True(state.Loading);
        if (fail) state.Fail("Clip could not open.");
        else state.Reset();

        state.BufferingChanged(revision, true);
        state.BufferingChanged(revision, false);
        Assert.False(state.TryOpen(revision, true, 1920, 1080, 60));
        Assert.False(state.Loading);
        Assert.False(state.Ready);
        Assert.False(state.CanSeek);
        Assert.False(state.CanTogglePlayback);
        Assert.Equal(fail ? "Clip could not open." : "", state.Status);
    }

    [Fact]
    public void LoadingASeekFromTheEndKeepsPlayDisabledUntilReadyWithoutResuming()
    {
        var state = Open();
        var revision = state.Revision;
        state.ReachEnd(revision);
        state.BufferingChanged(revision, true);
        Assert.Equal("Paused · buffering clip…", state.Status);
        Assert.True(state.Loading);
        Assert.False(state.CanSeek);
        Assert.False(state.CanTogglePlayback);

        state.SeekTo(20);
        state.SetPaused(false);
        Assert.False(state.Ended);
        Assert.True(state.Paused);
        Assert.True(state.Loading);
        state.BufferingChanged(revision, false);
        Assert.False(state.Loading);
        Assert.True(state.CanSeek);
        Assert.True(state.CanTogglePlayback);
        Assert.True(state.Paused);
        Assert.False(state.ObservePosition(revision, 20));
    }

    [Fact]
    public void OpeningTimeoutAppliesOnlyUntilReadyAndFailureDoesNotRevive()
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMilliseconds(14_999)));
        Assert.True(state.OpeningTimedOut(TimeSpan.FromSeconds(15)));
        var revision = state.Revision;
        state.Fail("Clip could not open.");
        Assert.Equal("Clip could not open.", state.Status);
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(1)));
        Assert.False(state.TryOpen(revision, true, 1920, 1080, 60));
        state.BufferingChanged(revision, true);
        Assert.False(state.Buffering);
        state.Prepare();
        Assert.True(state.TryOpen(state.Revision, true, 1920, 1080, 60));
        Assert.False(state.OpeningTimedOut(TimeSpan.FromMinutes(1)));
        state.Reset();
        Assert.Equal("", state.Status);
        Assert.False(state.Ready);
        Assert.Equal(0, state.DurationSeconds);
        Assert.False(state.ObservePosition(state.Revision, 1));
    }

    private static ClipsPlaybackState Open(bool play = true)
    {
        var state = new ClipsPlaybackState();
        state.Prepare();
        Assert.True(state.TryOpen(state.Revision, true, 1920, 1080, 60));
        if (play) state.SetPaused(false);
        return state;
    }
}
