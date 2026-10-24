using Xunit;

namespace Wisp.App.Tests;

public sealed class TelemetryListenerRecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RestartsAtOnceThenWithGrowingDelaysUntilTheBudgetIsSpent()
    {
        var recovery = new TelemetryListenerRecovery();
        Assert.True(recovery.ShouldRestart(Start));
        Assert.False(recovery.ShouldRestart(Start.AddMilliseconds(500)));
        Assert.True(recovery.ShouldRestart(Start.AddSeconds(1)));
        Assert.False(recovery.ShouldRestart(Start.AddSeconds(2)));
        Assert.True(recovery.ShouldRestart(Start.AddSeconds(3)));
        Assert.True(recovery.ShouldRestart(Start.AddSeconds(7)));
        Assert.True(recovery.ShouldRestart(Start.AddSeconds(15)));
        Assert.Equal(TelemetryListenerRecovery.MaximumAttempts, recovery.Attempts);
        Assert.True(recovery.Exhausted);
        Assert.False(recovery.ShouldRestart(Start.AddHours(1)));
    }

    [Fact]
    public void AClockThatSteppedBackwardsDoesNotPostponeTheNextAttempt()
    {
        var recovery = new TelemetryListenerRecovery();
        Assert.True(recovery.ShouldRestart(Start));
        Assert.False(recovery.ShouldRestart(Start.AddMilliseconds(500)));
        // The next attempt was due one second after Start; a wait longer than the largest delay
        // can only mean the clock moved, so the attempt is due now.
        Assert.True(recovery.ShouldRestart(Start.AddHours(-1)));
        Assert.Equal(2, recovery.Attempts);
    }

    [Fact]
    public void ASettledListenerGetsAFreshBudget()
    {
        var recovery = new TelemetryListenerRecovery();
        for (var attempt = 0; attempt < TelemetryListenerRecovery.MaximumAttempts; attempt++)
            Assert.True(recovery.ShouldRestart(Start.AddMinutes(attempt)));
        Assert.True(recovery.Exhausted);
        recovery.Reset();
        Assert.Equal(0, recovery.Attempts);
        Assert.True(recovery.ShouldRestart(Start.AddHours(1)));
    }
}
