using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AutomaticUpdateCoordinatorTests
{
    private sealed class Scenario
    {
        internal bool Enabled = true, HasUpdate = true, GameOpen, Busy;
        internal int Downloads, Installs;
        internal AutomaticUpdateApplyOutcome InstallOutcome = AutomaticUpdateApplyOutcome.Started;
        internal Func<int, AutomaticUpdateApplyOutcome>? OutcomeForAttempt;
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal TaskCompletionSource<VerifiedInstaller?>? Download;
        internal readonly VerifiedInstaller Installer = (VerifiedInstaller)Activator.CreateInstance(typeof(VerifiedInstaller),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["unused.exe", new SemanticVersion(2, 6, 9), 1L, new string('a', 64)], null)!;
        internal AutomaticUpdateCoordinator Create() => new(
            () => Enabled, () => HasUpdate, () => Task.FromResult(GameOpen), () => Busy,
            () => { Downloads++; return Download?.Task ?? Task.FromResult<VerifiedInstaller?>(Installer); },
            installer => { Assert.Same(Installer, installer); Installs++; return Task.FromResult(OutcomeForAttempt?.Invoke(Installs) ?? InstallOutcome); },
            _ => { }, () => Now);

        // The dashboard install timer ticks every five seconds for as long as Wisp runs.
        internal async Task TickAsync(AutomaticUpdateCoordinator coordinator, TimeSpan duration)
        {
            var end = Now + duration;
            while (Now < end)
            {
                Now += TimeSpan.FromSeconds(5);
                await coordinator.RunAsync();
            }
        }
    }

    [Fact]
    public async Task OpenGameDefersDownloadAndRestartUntilItCloses()
    {
        var s = new Scenario { GameOpen = true };
        var coordinator = s.Create();
        await coordinator.RunAsync();
        Assert.Equal(0, s.Downloads);
        Assert.Equal(0, s.Installs);
        s.GameOpen = false;
        await coordinator.RunAsync();
        Assert.Equal(1, s.Downloads);
        Assert.Equal(1, s.Installs);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GameOpeningOrEmergencyPauseDuringDownloadPreventsRestart(bool game, bool pause)
    {
        var s = new Scenario { Download = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var coordinator = s.Create();
        var pending = coordinator.RunAsync();
        await coordinator.RunAsync();
        Assert.Equal(1, s.Downloads);
        s.GameOpen = game;
        s.Enabled = !pause;
        s.Download.SetResult(s.Installer);
        await pending;
        Assert.Equal(0, s.Installs);
    }

    [Fact]
    public async Task PendingWorkIsNotInterruptedAndFailureHasBoundedRetry()
    {
        var s = new Scenario { Busy = true, InstallOutcome = AutomaticUpdateApplyOutcome.Retry };
        var coordinator = s.Create();
        await coordinator.RunAsync();
        Assert.Equal(0, s.Downloads);
        s.Busy = false;
        await coordinator.RunAsync();
        await coordinator.RunAsync();
        Assert.Equal(1, s.Installs);
        Assert.False(coordinator.HandoffUnavailable);
        s.Now += ApplicationUpdateCheckPolicy.RetryInterval;
        await coordinator.RunAsync();
        Assert.Equal(2, s.Installs);
    }

    [Fact]
    public async Task TransientHandoffFailureRetriesOncePerRetryInterval()
    {
        var s = new Scenario { InstallOutcome = AutomaticUpdateApplyOutcome.Retry };
        var coordinator = s.Create();
        await s.TickAsync(coordinator, TimeSpan.FromHours(1));
        // One attempt at the first five-second tick, then one every RetryInterval:
        // 1 + floor((3600 s - 5 s) / 120 s) = 30 handoff attempts in an hour.
        Assert.Equal(30, s.Installs);
        Assert.False(coordinator.HandoffUnavailable);
    }

    [Fact]
    public async Task MissingUpdateHelperStopsAutomaticHandoffForTheSession()
    {
        // Wisp 2.6.11 retried the handoff every RetryInterval for more than eight hours when
        // Wisp.Updater.exe was missing, reporting the same failure to Tools on every attempt.
        var s = new Scenario { InstallOutcome = AutomaticUpdateApplyOutcome.Unavailable };
        var coordinator = s.Create();
        await coordinator.RunAsync();
        Assert.Equal(1, s.Downloads);
        Assert.Equal(1, s.Installs);
        Assert.True(coordinator.HandoffUnavailable);

        await s.TickAsync(coordinator, TimeSpan.FromHours(8));
        Assert.Equal(1, s.Downloads);
        Assert.Equal(1, s.Installs);
    }

    [Fact]
    public async Task MissingUpdateHelperIsNotRetriedForALaterRelease()
    {
        var s = new Scenario { InstallOutcome = AutomaticUpdateApplyOutcome.Unavailable };
        var coordinator = s.Create();
        await coordinator.RunAsync();
        Assert.Equal(1, s.Installs);

        // A later 15-minute check finds a newer release; the helper is still missing, so the
        // coordinator neither downloads nor hands off until the user runs the full installer.
        s.HasUpdate = false;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        s.HasUpdate = true;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        Assert.Equal(1, s.Downloads);
        Assert.Equal(1, s.Installs);
        Assert.True(coordinator.HandoffUnavailable);
    }

    [Fact]
    public async Task RejectedHandoffAfterTransientRetriesStopsForTheSession()
    {
        // Wisp 2.6.12 retried a handoff every RetryInterval for a day while the helper kept rejecting
        // the installed location. Two transient failures, then the helper's rejection: no further
        // attempts for the session, and a later 15-minute check does not restart them.
        var s = new Scenario
        {
            OutcomeForAttempt = attempt => attempt < 3 ? AutomaticUpdateApplyOutcome.Retry : AutomaticUpdateApplyOutcome.Unavailable
        };
        var coordinator = s.Create();
        await s.TickAsync(coordinator, TimeSpan.FromHours(8));
        Assert.Equal(3, s.Downloads);
        Assert.Equal(3, s.Installs);
        Assert.True(coordinator.HandoffUnavailable);

        s.HasUpdate = false;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        s.HasUpdate = true;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        Assert.Equal(3, s.Downloads);
        Assert.Equal(3, s.Installs);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PausedOrCurrentInstallationDoesNothing(bool enabled, bool available)
    {
        var s = new Scenario { Enabled = enabled, HasUpdate = available };
        await s.Create().RunAsync();
        Assert.Equal(0, s.Downloads);
    }
}
