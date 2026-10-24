using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AutomaticUpdateCoordinatorTests
{
    private sealed class Scenario
    {
        internal bool Enabled = true, HasUpdate = true, GameOpen, Busy, Network = true;
        internal int Downloads, Installs;
        internal AutomaticUpdateApplyOutcome InstallOutcome = AutomaticUpdateApplyOutcome.Started;
        internal Func<int, AutomaticUpdateApplyOutcome>? OutcomeForAttempt;
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal TaskCompletionSource<VerifiedInstaller?>? Download;
        internal VerifiedInstaller Installer = CreateInstaller(2, 6, 9);
        internal static VerifiedInstaller CreateInstaller(int major, int minor, int patch) => (VerifiedInstaller)Activator.CreateInstance(typeof(VerifiedInstaller),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["unused.exe", new SemanticVersion(major, minor, patch), 1L, new string('a', 64)], null)!;
        internal AutomaticUpdateCoordinator Create() => new(
            () => Enabled, () => HasUpdate, () => Task.FromResult(GameOpen), () => Busy,
            () => { Downloads++; return Download?.Task ?? Task.FromResult<VerifiedInstaller?>(Installer); },
            installer => { Assert.Same(Installer, installer); Installs++; return Task.FromResult(OutcomeForAttempt?.Invoke(Installs) ?? InstallOutcome); },
            _ => { }, () => Now, pendingVersion: () => Installer.Version.ToString(), networkAvailable: () => Network);

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
    public async Task TransientHandoffFailureRetriesOncePerRetryIntervalThenStopsForThatRelease()
    {
        // One PC on Wisp 2.6.12 failed its handoff verification 1,273 times in a week, once every
        // RetryInterval. A third failure for the same release now ends automatic attempts for it.
        var s = new Scenario { InstallOutcome = AutomaticUpdateApplyOutcome.Retry };
        var coordinator = s.Create();
        // The first attempt is at the first five-second tick; the retry follows one RetryInterval later.
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.RetryInterval);
        Assert.Equal(1, s.Installs);
        Assert.False(coordinator.HandoffUnavailable);
        await s.TickAsync(coordinator, TimeSpan.FromHours(1));
        Assert.Equal(AutomaticUpdateCoordinator.MaximumRetries, s.Installs);
        Assert.False(coordinator.HandoffUnavailable);
        Assert.True(coordinator.IsExhausted("2.6.9"));
        s.HasUpdate = false;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        s.HasUpdate = true;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.MinimumInterval);
        Assert.Equal(AutomaticUpdateCoordinator.MaximumRetries, s.Installs);

        // A newer release published while Wisp keeps running is tried again; the old one stays exhausted.
        s.Installer = Scenario.CreateInstaller(2, 6, 10);
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.RetryInterval);
        Assert.Equal(AutomaticUpdateCoordinator.MaximumRetries + 1, s.Installs);
        Assert.False(coordinator.IsExhausted("2.6.10"));
        Assert.True(coordinator.IsExhausted("2.6.9"));
    }

    [Fact]
    public async Task FailedDownloadsSpendTheSameBudgetAsFailedHandoffs()
    {
        // 2.7.1 retried a failing download every RetryInterval for the whole session, with one
        // failure report per attempt; the apply budget applied to the handoff only.
        var s = new Scenario { Download = new TaskCompletionSource<VerifiedInstaller?>() };
        s.Download.SetResult(null);
        var coordinator = s.Create();
        await s.TickAsync(coordinator, TimeSpan.FromHours(1));
        Assert.Equal(AutomaticUpdateCoordinator.MaximumRetries, s.Downloads);
        Assert.Equal(0, s.Installs);
        Assert.True(coordinator.IsExhausted("2.6.9"));

        // A newer release gets a fresh budget, and a download that succeeds is handed off at once.
        s.Installer = Scenario.CreateInstaller(2, 6, 10);
        s.Download = null;
        s.Now += TimeSpan.FromSeconds(5);
        await coordinator.RunAsync();
        Assert.Equal(AutomaticUpdateCoordinator.MaximumRetries + 1, s.Downloads);
        Assert.Equal(1, s.Installs);
        Assert.False(coordinator.IsExhausted("2.6.10"));
    }

    [Fact]
    public async Task DownloadsThatCannotHappenWithoutAConnectionDoNotSpendTheBudget()
    {
        var s = new Scenario { Network = false, Download = new TaskCompletionSource<VerifiedInstaller?>() };
        s.Download.SetResult(null);
        var coordinator = s.Create();
        await s.TickAsync(coordinator, TimeSpan.FromHours(1));
        Assert.True(s.Downloads > AutomaticUpdateCoordinator.MaximumRetries);
        Assert.False(coordinator.IsExhausted("2.6.9"));

        // The connection returns: the download succeeds and the release is handed off at once.
        s.Network = true;
        s.Download = null;
        await s.TickAsync(coordinator, ApplicationUpdateCheckPolicy.RetryInterval);
        Assert.Equal(1, s.Installs);
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
