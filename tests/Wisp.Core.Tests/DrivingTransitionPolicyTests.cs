using Xunit;

namespace Wisp.Core.Tests;

public sealed class DrivingTransitionPolicyTests
{
    [Fact]
    public void DisabledAutoMinimizeNeverMinimizesOnTelemetryTransition()
    {
        var decision = DrivingTransitionPolicy.Evaluate(
            wasDriving: false,
            DrivingTelemetrySignal.Driving,
            autoMinimizeOnTelemetry: false);

        Assert.True(decision.StartedDriving);
        Assert.False(decision.ShouldMinimizeControlPanel);
    }

    [Fact]
    public void EnabledAutoMinimizeRunsOnlyOnDrivingStart()
    {
        var initial = DrivingTransitionPolicy.Evaluate(
            wasDriving: false,
            DrivingTelemetrySignal.Driving,
            autoMinimizeOnTelemetry: true);
        var continuing = DrivingTransitionPolicy.Evaluate(
            wasDriving: true,
            DrivingTelemetrySignal.Driving,
            autoMinimizeOnTelemetry: true);

        Assert.True(initial.ShouldMinimizeControlPanel);
        Assert.False(continuing.StartedDriving);
        Assert.False(continuing.ShouldMinimizeControlPanel);
    }

    [Fact]
    public void NonDrivingTelemetryCannotMinimizeControlPanel()
    {
        var decision = DrivingTransitionPolicy.Evaluate(
            wasDriving: false,
            DrivingTelemetrySignal.NotDriving,
            autoMinimizeOnTelemetry: true);

        Assert.False(decision.StartedDriving);
        Assert.False(decision.ShouldMinimizeControlPanel);
    }

    [Fact]
    public void TelemetryLossDoesNotCreateAFakeDrivingTransitionOnReconnect()
    {
        var unavailable = DrivingTransitionPolicy.Evaluate(
            wasDriving: true,
            DrivingTelemetrySignal.Unavailable,
            autoMinimizeOnTelemetry: true);
        var reconnected = DrivingTransitionPolicy.Evaluate(
            wasDriving: unavailable.IsDriving,
            DrivingTelemetrySignal.Driving,
            autoMinimizeOnTelemetry: true);

        Assert.True(unavailable.IsDriving);
        Assert.False(unavailable.ShouldMinimizeControlPanel);
        Assert.False(reconnected.StartedDriving);
        Assert.False(reconnected.ShouldMinimizeControlPanel);
    }

    [Fact]
    public void ExplicitRaceOffArmsTheNextDrivingTransition()
    {
        var stopped = DrivingTransitionPolicy.Evaluate(
            wasDriving: true,
            DrivingTelemetrySignal.NotDriving,
            autoMinimizeOnTelemetry: true);
        var restarted = DrivingTransitionPolicy.Evaluate(
            wasDriving: stopped.IsDriving,
            DrivingTelemetrySignal.Driving,
            autoMinimizeOnTelemetry: true);

        Assert.False(stopped.IsDriving);
        Assert.True(restarted.StartedDriving);
        Assert.True(restarted.ShouldMinimizeControlPanel);
    }
}
