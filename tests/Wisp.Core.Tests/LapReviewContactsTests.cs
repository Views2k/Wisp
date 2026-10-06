using Wisp.Core.Runs;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class LapReviewContactsTests
{
    private static LapReviewLap Lap(Func<int, float>? speedAt = null, Func<int, float>? accelerationAt = null, int count = 100)
    {
        var position = 0f;
        var points = new LapReviewPoint[count];
        for (var index = 0; index < count; index++)
        {
            var time = index * .02;
            var speed = speedAt?.Invoke(index) ?? (index < 20 ? 30 : 20);
            position += speed * .02f;
            var location = new LapPosition(position, 0, 0);
            var state = TestVehicleState.Create(groundSpeed: speed) with
            {
                GameTimestampMilliseconds = (uint)(index * 20 + 1000),
                LocalVelocityYMetersPerSecond = 0,
                LongitudinalAccelerationMetersPerSecondSquared = accelerationAt?.Invoke(index) ?? (index == 20 ? -100 : 0),
                Lap = new(location, (float)time, 0, (float)time, 0, 1)
            };
            var sample = new RunSample { ElapsedSeconds = time, IsDriving = true, State = state };
            points[index] = new(index, time, time, position, location, index == 0, sample);
        }
        return new() { Points = points };
    }

    [Fact]
    public void CorroboratedAbruptSlowingIsExplicitlyOnlyAPossibleContact()
    {
        var contact = Assert.Single(LapReviewContacts.Find(Lap(), TestContext.Current.CancellationToken));
        Assert.Equal(20, contact.PointIndex);
        Assert.Equal("Possible contact", contact.Label);
        Assert.Equal(LapReviewContactKind.PossibleContact, contact.Kind);
        Assert.Contains("may miss or misidentify", LapReviewContacts.EvidenceNote);
    }

    [Fact]
    public void OrdinaryBrakingAndAccelerationAloneAreNotContacts()
    {
        Assert.Empty(LapReviewContacts.Find(Lap(index => 40 - index * .2f, _ => -10), TestContext.Current.CancellationToken));
        Assert.Empty(LapReviewContacts.Find(Lap(_ => 30, _ => -100), TestContext.Current.CancellationToken));
        Assert.Empty(LapReviewContacts.Find(Lap(accelerationAt: _ => 0), TestContext.Current.CancellationToken));
        Assert.Empty(LapReviewContacts.Find(Lap(index => index < 20 ? 5 : 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnePacketSpeedErrorIsNotAContact()
    {
        Assert.Empty(LapReviewContacts.Find(Lap(index => index == 20 ? 15 : 30), TestContext.Current.CancellationToken));
    }

    private static LapReviewLap WithImpactFields(LapReviewLap lap, Func<int, float?> lossAt, Func<int, float?> massAt)
    {
        for (var index = 0; index < lap.Points.Length; index++)
        {
            var point = lap.Points[index];
            lap.Points[index] = point with
            {
                Sample = point.Sample with
                {
                    State = point.Sample.State with
                    {
                        SmashableVelocityLossMetersPerSecond = lossAt(index),
                        SmashableMassKilograms = massAt(index)
                    }
                }
            };
        }
        return lap;
    }

    [Fact]
    public void DirectBreakableObjectSignalDoesNotRequireAHeuristicSpeedDrop()
    {
        var lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index >= 20 ? 2 : 0, index => index >= 20 ? 150 : 0);
        var contact = Assert.Single(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
        Assert.Equal(20, contact.PointIndex);
        Assert.Equal(LapReviewContactKind.SmashableObject, contact.Kind);
        Assert.Equal("Object contact", contact.Label);
    }

    [Fact]
    public void AReportedObjectImpactDoesNotAlsoBecomeAnInferredContact()
    {
        var lap = WithImpactFields(Lap(), index => index >= 20 ? 2 : 0, index => index >= 20 ? 150 : 0);
        Assert.Equal(LapReviewContactKind.SmashableObject,
            Assert.Single(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public void LatchedMassAndDecayingImpulseDoNotCreateRepeatedObjectContacts()
    {
        var lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index >= 20 ? 4 - index * .02f : 0, _ => 150);
        Assert.Single(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
        lap = WithImpactFields(Lap(_ => 30, _ => 0), _ => 0, _ => 150);
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
        lap = WithImpactFields(Lap(_ => 30, _ => 0), _ => 2, _ => 150);
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FreshObjectImpulsesAfterAnObservedResetAreSeparateContacts()
    {
        var lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index is 20 or 25 ? 2 : 0, _ => 150);
        Assert.Equal([20, 25], LapReviewContacts.Find(lap, TestContext.Current.CancellationToken).Select(contact => contact.PointIndex));
    }

    [Fact]
    public void DirectObjectMarkerCannotCrossATelemetryGapOrStartFromUnavailableData()
    {
        var lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index >= 20 ? 2 : 0, _ => 150);
        lap.Points[20] = lap.Points[20] with { BreakBefore = true };
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
        lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index >= 20 ? 2 : null, _ => 150);
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
        lap = WithImpactFields(Lap(_ => 30, _ => 0), index => index >= 20 ? 2 : 0, _ => float.NaN);
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("break")]
    [InlineData("segment")]
    [InlineData("clock")]
    [InlineData("rewind")]
    [InlineData("jump")]
    [InlineData("airborne")]
    [InlineData("missingVertical")]
    [InlineData("invalidAcceleration")]
    public void MissingOrDiscontinuousEvidenceDoesNotProduceAContact(string kind)
    {
        var lap = Lap();
        var points = lap.Points;
        for (var index = 20; index < points.Length; index++)
        {
            var point = points[index];
            points[index] = kind switch
            {
                "break" => point with { BreakBefore = true },
                "segment" => point with { Sample = point.Sample with { Segment = 1 } },
                "clock" => point with { Sample = point.Sample with { State = point.Sample.State with { GameTimestampMilliseconds = (uint)(100000 + index * 20) } } },
                "rewind" => point with { LapSeconds = point.LapSeconds - .1 },
                "jump" => point with { Position = point.Position with { X = point.Position.X + 100 } },
                "airborne" => point with { Sample = point.Sample with { State = point.Sample.State with { LocalVelocityYMetersPerSecond = 4 } } },
                "missingVertical" => point with { Sample = point.Sample with { State = point.Sample.State with { LocalVelocityYMetersPerSecond = null } } },
                _ => point with { Sample = point.Sample with { State = point.Sample.State with { LongitudinalAccelerationMetersPerSecondSquared = float.NaN } } }
            };
        }
        Assert.Empty(LapReviewContacts.Find(lap, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CooldownGroupsOneImpactButAllowsASeparateLaterImpact()
    {
        var lap = Lap(index => index < 20 ? 80 : index < 30 ? 70 : index < 90 ? 60 : 40,
            index => index is 20 or 30 or 90 ? -100 : 0, count: 110);
        Assert.Equal([20, 90], LapReviewContacts.Find(lap, TestContext.Current.CancellationToken).Select(contact => contact.PointIndex));
    }

    [Fact]
    public void ExplicitMarkersAreDistinctAndMapOnlyToTheirLap()
    {
        var lap = Lap();
        RunMarker[] markers = [new(.4, "Contact"), new(.4, "contact"), new(.8, "Apex"), new(100, "Contact"), new(double.NaN, "Contact")];
        var contact = Assert.Single(LapReviewContacts.FromMarkers(lap, markers, TestContext.Current.CancellationToken));
        Assert.Equal(20, contact.PointIndex);
        Assert.Equal(LapReviewContactKind.UserMarkedContact, contact.Kind);
        Assert.Equal("Marked contact", contact.Label);
    }

    [Fact]
    public void MarkerDoesNotJumpAnUnrecordedGap()
    {
        var lap = Lap();
        lap.Points[20] = lap.Points[20] with { BreakBefore = true };
        Assert.Empty(LapReviewContacts.FromMarkers(lap, [new(.39, "Contact")], TestContext.Current.CancellationToken));
        Assert.Single(LapReviewContacts.FromMarkers(lap, [new(.4, "Contact")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AContactBetweenSamplesKeepsItsStoredTimestampForExactRemoval()
    {
        var contact = Assert.Single(LapReviewContacts.FromMarkers(Lap(), [new(.391, "Contact")], TestContext.Current.CancellationToken));
        Assert.Equal(20, contact.PointIndex);
        Assert.Equal(.391, contact.RunSeconds);
    }

    [Fact]
    public void EmptyAndCancelledAnalysisDoNotStartWork()
    {
        Assert.Empty(LapReviewContacts.Find(new(), TestContext.Current.CancellationToken));
        Assert.Empty(LapReviewContacts.FromMarkers(new(), [], TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LapReviewContacts.Find(Lap(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LapReviewContacts.FromMarkers(Lap(), [], cancellation.Token));
    }
}
