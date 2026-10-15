using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AutomaticUpdateCoordinatorTests
{
    private sealed class Scenario
    {
        internal bool Enabled = true, HasUpdate = true, GameOpen, Busy;
        internal int Downloads, Installs;
        internal bool InstallSucceeds = true;
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal TaskCompletionSource<VerifiedInstaller?>? Download;
        internal readonly VerifiedInstaller Installer = (VerifiedInstaller)Activator.CreateInstance(typeof(VerifiedInstaller),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["unused.exe", new SemanticVersion(2, 6, 9), 1L, new string('a', 64)], null)!;
        internal AutomaticUpdateCoordinator Create() => new(
            () => Enabled, () => HasUpdate, () => Task.FromResult(GameOpen), () => Busy,
            () => { Downloads++; return Download?.Task ?? Task.FromResult<VerifiedInstaller?>(Installer); },
            installer => { Assert.Same(Installer, installer); Installs++; return Task.FromResult(InstallSucceeds); },
            _ => { }, () => Now);
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
        var s = new Scenario { Busy = true, InstallSucceeds = false };
        var coordinator = s.Create();
        await coordinator.RunAsync();
        Assert.Equal(0, s.Downloads);
        s.Busy = false;
        await coordinator.RunAsync();
        await coordinator.RunAsync();
        Assert.Equal(1, s.Installs);
        s.Now += ApplicationUpdateCheckPolicy.RetryInterval;
        await coordinator.RunAsync();
        Assert.Equal(2, s.Installs);
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
