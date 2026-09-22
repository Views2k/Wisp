using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCaptureSelfCheckTests
{
    [Fact]
    public void CurrentRecorderPassesItsBoundedInMemoryRegressionChecks()
    {
        var result = ShiftCaptureSelfCheck.Run();
        Assert.True(result.Passed, result.Status);
        Assert.Equal(new[]
        {
            "same-timestamp-observations", "parked-neutral-bindings",
            "context-and-clock-guards", "finite-summary-schema"
        }, result.Checks.Select(check => check.Name));
        Assert.All(result.Checks, check => Assert.Null(check.Failure));
        Assert.Contains("not live game", result.Scope);
        Assert.Contains("still require verification", result.Status);
    }

    [Fact]
    public void IndependentInvocationsDoNotInheritFixtureState()
    {
        var first = ShiftCaptureSelfCheck.Run();
        var second = ShiftCaptureSelfCheck.Run();
        Assert.True(first.Passed, first.Status);
        Assert.True(second.Passed, second.Status);
        Assert.NotSame(first.Checks, second.Checks);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void AFailureOrIncompleteResultCannotPassPreflight()
    {
        var healthy = ShiftCaptureSelfCheck.Run();
        var checks = healthy.Checks.ToArray();
        checks[1] = checks[1] with { Passed = false, Failure = "B/X binding fixture did not match its expected result." };
        var failed = new ShiftCaptureSelfCheckResult(checks);
        Assert.False(failed.Passed);
        Assert.Contains("Capture was not started", failed.Status);
        Assert.Contains("B/X binding fixture", failed.Status);
        var incomplete = new ShiftCaptureSelfCheckResult([]);
        Assert.False(incomplete.Passed);
        Assert.Contains("checks are incomplete", incomplete.Status);
    }
}
