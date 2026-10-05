using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCompatibilityUpdateScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartupChecksOnceAndSuccessfulIdleChecksStayDaily()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        Assert.True(schedule.IsDue(Start));
        Assert.True(schedule.TryBeginCheck(Start));
        Assert.False(schedule.IsDue(Start.AddHours(2)));
        schedule.CompleteCheck(Start, success: true);
        Assert.False(schedule.IsDue(Start.AddDays(1).AddTicks(-1)));
        Assert.True(schedule.IsDue(Start.AddDays(1)));
    }

    [Fact]
    public void NewUnsupportedBuildDoesNotWaitUntilNextDay()
    {
        var schedule = CheckedSuccessfully();
        var observed = Start.AddHours(2);
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", observed);
        Assert.True(schedule.IsDue(observed));
    }

    [Fact]
    public void NewUnsupportedBuildCannotCauseImmediateDuplicateOfRecentCheck()
    {
        var schedule = CheckedSuccessfully();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start.AddSeconds(2));
        Assert.False(schedule.IsDue(Start.AddSeconds(59)));
        Assert.True(schedule.IsDue(Start.AddMinutes(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedUnsupportedBuildBacksOffEvenWhenDownloadSucceeds(bool success)
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        var now = Start;
        foreach (var minutes in new[] { 1, 5, 15, 60, 60 })
        {
            schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", now);
            Assert.True(schedule.IsDue(now));
            Assert.True(schedule.TryBeginCheck(now));
            schedule.CompleteCheck(now, success);
            Assert.False(schedule.IsDue(now.AddMinutes(minutes).AddTicks(-1)));
            now = now.AddMinutes(minutes);
            Assert.True(schedule.IsDue(now));
        }
    }

    [Fact]
    public void TransientStatusesDoNotResetUnsupportedBuildBackoff()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start);
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        Assert.True(schedule.TryBeginCheck(Start.AddMinutes(1)));
        schedule.CompleteCheck(Start.AddMinutes(1), success: true);

        foreach (var status in new[] { NativeAssistProviderStatus.GameNotRunning,
            NativeAssistProviderStatus.Unavailable, NativeAssistProviderStatus.PlayerNotUnique })
        {
            schedule.ObserveBuild(status, "temporary", Start.AddMinutes(2));
            Assert.False(schedule.IsDue(Start.AddMinutes(2)));
            schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start.AddMinutes(2));
            Assert.False(schedule.IsDue(Start.AddMinutes(2)));
        }
        Assert.True(schedule.IsDue(Start.AddMinutes(6)));
    }

    [Fact]
    public void FailedCheckRetriesWithoutAnUnsupportedGame()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: false);
        Assert.False(schedule.IsDue(Start.AddSeconds(59)));
        Assert.True(schedule.IsDue(Start.AddMinutes(1)));
    }

    [Fact]
    public void ReadyObservationsDoNotResetTransportFailureBackoff()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: false);
        schedule.ObserveBuild(NativeAssistProviderStatus.Ready, "verified", Start.AddSeconds(10));
        Assert.True(schedule.TryBeginCheck(Start.AddMinutes(1)));
        schedule.CompleteCheck(Start.AddMinutes(1), success: false);
        schedule.ObserveBuild(NativeAssistProviderStatus.Ready, "verified", Start.AddMinutes(2));
        Assert.False(schedule.IsDue(Start.AddMinutes(2)));
        Assert.True(schedule.IsDue(Start.AddMinutes(6)));
    }

    [Fact]
    public void LosingGameDuringCheckDoesNotForgetPendingBuildOrBackoff()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start);
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        Assert.True(schedule.TryBeginCheck(Start.AddMinutes(1)));
        schedule.ObserveBuild(NativeAssistProviderStatus.GameNotRunning, "waiting", Start.AddMinutes(1));
        schedule.CompleteCheck(Start.AddMinutes(2), success: true);
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start.AddMinutes(3));
        Assert.False(schedule.IsDue(Start.AddMinutes(3)));
        Assert.True(schedule.IsDue(Start.AddMinutes(7)));
    }

    [Fact]
    public void ChangedUnsupportedIdentityCanPromptCheckWithoutResettingGlobalMinimum()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start);
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        var second = Start.AddMinutes(1);
        Assert.True(schedule.TryBeginCheck(second));
        schedule.CompleteCheck(second, success: true);

        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-b", second.AddSeconds(1));
        Assert.False(schedule.IsDue(second.AddSeconds(59)));
        Assert.True(schedule.IsDue(second.AddMinutes(1)));
    }

    [Fact]
    public void ManualChecksBypassDelayButJoinOneScheduledOperation()
    {
        var schedule = CheckedSuccessfully();
        var manual = Start.AddSeconds(10);
        Assert.False(schedule.IsDue(manual));
        Assert.True(schedule.TryBeginCheck(manual));
        Assert.False(schedule.TryBeginCheck(manual));
        Assert.False(schedule.IsDue(manual.AddDays(2)));
        schedule.CompleteCheck(manual, success: true);
        Assert.False(schedule.IsDue(manual.AddHours(1)));
        Assert.True(schedule.IsDue(manual.AddDays(1)));
    }

    [Fact]
    public void VerifiedRecoveryStopsUnsupportedRetries()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start);
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        schedule.ObserveBuild(NativeAssistProviderStatus.Ready, "verified", Start.AddSeconds(2));
        Assert.False(schedule.IsDue(Start.AddMinutes(1)));
        Assert.False(schedule.IsDue(Start.AddHours(2)));
        Assert.True(schedule.IsDue(Start.AddDays(1)));
    }

    [Fact]
    public void MenuAndGameAbsenceDoNotRequestUnsupportedRetries()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start);
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        schedule.ObserveBuild(NativeAssistProviderStatus.GameNotRunning, "waiting", Start.AddSeconds(2));
        Assert.False(schedule.IsDue(Start.AddMinutes(10)));
        schedule.ObserveBuild(NativeAssistProviderStatus.UnsupportedBuild, "build-a", Start.AddMinutes(10));
        Assert.True(schedule.IsDue(Start.AddMinutes(10)));
    }

    private static NativeCompatibilityUpdateSchedule CheckedSuccessfully()
    {
        var schedule = new NativeCompatibilityUpdateSchedule();
        Assert.True(schedule.TryBeginCheck(Start));
        schedule.CompleteCheck(Start, success: true);
        return schedule;
    }
}
