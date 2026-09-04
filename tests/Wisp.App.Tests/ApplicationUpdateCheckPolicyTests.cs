using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateCheckPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DisabledAutomaticChecksNeverRun()
    {
        Assert.False(ApplicationUpdateCheckPolicy.IsDue(false, null, Now));
    }

    [Fact]
    public void FirstAutomaticCheckRuns()
    {
        Assert.True(ApplicationUpdateCheckPolicy.IsDue(true, null, Now));
    }

    [Fact]
    public void CheckInsideDailyWindowDoesNotRun()
    {
        Assert.False(ApplicationUpdateCheckPolicy.IsDue(true, Now.AddHours(-23), Now));
    }

    [Fact]
    public void CheckRunsWhenDailyWindowExpires()
    {
        Assert.True(ApplicationUpdateCheckPolicy.IsDue(true, Now.AddHours(-24), Now));
    }
}
