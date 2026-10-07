using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.UiReview;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewMapComparisonTests
{
    [Fact]
    public void SharedSpaceIsOptInAndTogglingRestoresTheOriginalMapAndLegend() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with
        {
            Samples = reference.Samples.Select(s => s with { State = s.State with { PowerWatts = s.State.PowerWatts * 2 } }).ToArray()
        };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Channel = model.Channels.Single(c => c.Channel == LapReviewChannel.Power);
        model.Cursor = 150;
        Assert.True(model.HasMapComparison);
        Assert.False(model.SharedSpace);
        Assert.False(model.CanUseSharedSpace);
        AssertOriginalMap();
        model.Is3D = true;
        Assert.True(model.CanUseSharedSpace);
        Assert.False(model.SharedSpace);
        AssertOriginalMap();
        var original = model.Plot;
        var minimum = model.LegendMinimum; var maximum = model.LegendMaximum;
        var elevation = model.ElevationSummary;
        var metrics = model.Metrics[0];

        model.SharedSpace = true;
        Assert.True(model.IsMapComparison);
        Assert.NotNull(model.ComparisonMapPlot);
        Assert.False(model.MapPlotA.ShowReferencePath);
        Assert.False(model.HasMapReference);
        Assert.True(model.MapPlotA.ColorRangeOverride!.Value.Maximum > LapReviewColorRange.From(original).Maximum);
        Assert.NotEqual(maximum, model.LegendMaximum);
        Assert.StartsWith("Both laps", model.ElevationSummary);
        Assert.Equal(original, model.Plot);
        Assert.Same(metrics, model.Metrics[0]);

        model.Is3D = false;
        Assert.False(model.CanUseSharedSpace);
        AssertOriginalMap();
        Assert.Equal(minimum, model.LegendMinimum); Assert.Equal(maximum, model.LegendMaximum);
        Assert.Equal(elevation, model.ElevationSummary);

        model.Is3D = true;
        model.SharedSpace = true;
        Assert.True(model.IsMapComparison);
        model.SharedSpace = false;
        AssertOriginalMap();
        Assert.Equal(original, model.MapPlotA);
        Assert.Equal(minimum, model.LegendMinimum); Assert.Equal(maximum, model.LegendMaximum);
        Assert.Equal(elevation, model.ElevationSummary);
        Assert.Same(metrics, model.Metrics[0]);

        void AssertOriginalMap()
        {
            Assert.False(model.IsMapComparison);
            Assert.Null(model.ComparisonMapPlot);
            Assert.Equal(model.Plot, model.MapPlotA);
            Assert.Null(model.MapPlotA.ColorRangeOverride);
            Assert.True(model.MapPlotA.ShowReferencePath);
            Assert.True(model.HasMapReference);
            var range = LapReviewColorRange.From(model.Plot);
            Assert.Equal($"{range.Minimum:0.##} {LapReviewPlot.Unit(model.Plot)}", model.LegendMinimum);
            Assert.Equal($"{range.Maximum:0.##} {LapReviewPlot.Unit(model.Plot)}", model.LegendMaximum);
        }
    });

    [Fact]
    public void DistinctComparableLapsUseOneScaleForBothMapsAndTheLegend() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with
        {
            Samples = reference.Samples.Select(s => s with { State = s.State with { PowerWatts = s.State.PowerWatts * 2 } }).ToArray()
        };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true;
        model.SharedSpace = true;
        Assert.True(model.HasMapComparison);
        Assert.True(model.IsMapComparison);
        Assert.False(model.MapPlotA.ShowReferencePath);
        Assert.False(model.MapPlotB.ShowReferencePath);
        foreach (var channel in new[] { LapReviewChannel.Speed, LapReviewChannel.Power, LapReviewChannel.Brake })
        {
            model.Channel = model.Channels.Single(c => c.Channel == channel);
            var a = model.MapPlotA; var b = model.MapPlotB;
            var first = LapReviewColorRange.From(a with { ColorRangeOverride = null });
            var second = LapReviewColorRange.From(b with { ColorRangeOverride = null });
            var expected = new LapReviewColorRange(Math.Min(first.Minimum, second.Minimum), Math.Max(first.Maximum, second.Maximum), true);
            Assert.Equal(expected, a.ColorRangeOverride);
            Assert.Equal(expected, b.ColorRangeOverride);
            Assert.Equal($"{expected.Minimum:0.##} {LapReviewPlot.Unit(a)}", model.LegendMinimum);
            Assert.Equal($"{expected.Maximum:0.##} {LapReviewPlot.Unit(a)}", model.LegendMaximum);
        }
        var scale = model.MapPlotA.ColorRangeOverride;
        var metrics = model.Metrics[0];
        model.Cursor = 150;
        Assert.Equal(scale, model.MapPlotA.ColorRangeOverride);
        Assert.Same(metrics, model.Metrics[0]);
        Assert.False(model.IsBusy);
    });

    [Fact]
    public void ReferenceCursorAndSectionUseSpatialMatchesAndPickingBMovesTheSharedCursor() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true));
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        model.Cursor = 150;
        Assert.Equal(model.Plot.Comparison!.Points[150].ReferencePointIndex, model.MapPlotB.Cursor);
        Assert.InRange(model.MapPlotB.Cursor, 0, model.Reference!.Points.Length - 1);
        model.PickReferencePoint(220);
        Assert.Equal(220, model.Cursor);
        model.Cursor = 75;
        model.SectionStartCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Cursor = 180;
        model.SectionEndCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.Equal(model.Plot.Comparison!.Points[75].ReferencePointIndex, model.MapPlotB.SectionStart);
        Assert.Equal(model.Plot.Comparison.Points[180].ReferencePointIndex, model.MapPlotB.SectionEnd);
        var selected = model.Cursor;
        model.PickReferencePoint(-1); model.PickReferencePoint(int.MaxValue);
        Assert.Equal(selected, model.Cursor);
    });

    [Fact]
    public void ReferenceCursorInterpolatesBetweenOriginalSamplesInsteadOfSnappingToTheEarlierOne() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with
        {
            Samples = reference.Samples.Select((sample, index) =>
            {
                var angle = (index % 600 + .5) / 600 * Math.Tau;
                var lap = sample.State.Lap!;
                return sample with
                {
                    State = sample.State with
                    {
                        Lap = lap with
                        {
                            Position = new((float)(210 * Math.Cos(angle)),
                                (float)(80 + 24 * Math.Sin(angle) + 7 * Math.Sin(angle * 3)), (float)(140 * Math.Sin(angle)))
                        }
                    }
                };
            }).ToArray()
        };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.HasMapComparison);
        model.Cursor = 150;
        var match = model.Plot.Comparison!.Points[model.Cursor];
        var lower = Assert.IsType<int>(match.ReferencePointIndex);
        var seconds = Assert.IsType<double>(match.ReferenceLapSeconds);
        var a = model.Reference!.Points[lower]; var b = model.Reference.Points[lower + 1];
        var fraction = (seconds - a.LapSeconds) / (b.LapSeconds - a.LapSeconds);
        Assert.InRange(fraction, .1, .9);
        var cursor = Assert.IsType<LapPosition>(model.MapPlotB.CursorPositionOverride);
        Assert.Equal(a.Position.X + (b.Position.X - a.Position.X) * fraction, cursor.X, 4);
        Assert.Equal(a.Position.Y + (b.Position.Y - a.Position.Y) * fraction, cursor.Y, 4);
        Assert.Equal(a.Position.Z + (b.Position.Z - a.Position.Z) * fraction, cursor.Z, 4);
        Assert.NotEqual(a.Position, cursor);
    });

    [Fact]
    public void UnmatchedSectionEndpointHidesReferenceSelectionAndUnmatchedCursor() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true));
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        model.Cursor = 75;
        model.SectionStartCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        // An accepted route may still contain individual unmatched positions.
        var comparison = model.Plot.Comparison!;
        comparison.Points[75] = comparison.Points[75] with
        { ReferencePointIndex = null, ReferenceLapSeconds = null, DeltaSeconds = null };
        model.Cursor = 180;
        model.SectionEndCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.True(model.HasMapComparison);
        Assert.Equal(-1, model.MapPlotB.SectionStart);
        Assert.Equal(-1, model.MapPlotB.SectionEnd);
        model.Cursor = 75;
        Assert.Equal(-1, model.MapPlotB.Cursor);
        Assert.Null(model.MapPlotB.CursorPositionOverride);
        Assert.Contains("Unavailable", model.MapBCursorValue);
    });

    [Fact]
    public void EachMapKeepsItsOwnContactsAndTheVisibilityToggleAppliesToBoth() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create() with { Markers = [new(10, "Contact")] },
            LapReviewFixtures.Create(reference: true) with { Markers = [new(15, "Contact")] });
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.HasMapComparison);
        var first = Assert.Single(model.MapPlotA.Contacts!, c => c.Kind == LapReviewContactKind.UserMarkedContact);
        var second = Assert.Single(model.MapPlotB.Contacts!, c => c.Kind == LapReviewContactKind.UserMarkedContact);
        Assert.Equal(10, first.RunSeconds); Assert.Equal(15, second.RunSeconds);
        Assert.InRange(first.PointIndex, 0, model.Lap!.Points.Length - 1);
        Assert.InRange(second.PointIndex, 0, model.Reference!.Points.Length - 1);
        model.ShowContacts = false;
        Assert.Empty(model.MapPlotA.Contacts ?? []); Assert.Empty(model.MapPlotB.Contacts ?? []);
        model.ShowContacts = true;
        Assert.Contains(first, model.MapPlotA.Contacts!); Assert.Contains(second, model.MapPlotB.Contacts!);
    });

    [Fact]
    public void SelfReferenceMissingReferenceAndClearDoNotCreateASecondMap() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), null);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.True(model.HasLap); Assert.NotNull(model.Reference);
        model.Is3D = true; model.SharedSpace = true;
        Assert.False(model.HasMapComparison);
        Assert.False(model.CanUseSharedSpace); Assert.False(model.IsMapComparison);
        Assert.Null(model.ComparisonMapPlot);
        model.Reference = null;
        await LapReviewComparisonTestSupport.Ready(model);
        model.SharedSpace = true;
        Assert.False(model.HasMapComparison); Assert.Null(model.MapPlotB.Lap);
        Assert.False(model.CanUseSharedSpace); Assert.False(model.IsMapComparison);
        Assert.Null(model.ComparisonMapPlot);
        Assert.Equal("", model.MapBCursorValue);
        model.SetRuns(null, null);
        await LapReviewComparisonTestSupport.Ready(model);
        model.SharedSpace = true;
        Assert.False(model.HasLap); Assert.False(model.HasMapComparison);
        Assert.False(model.CanUseSharedSpace); Assert.False(model.IsMapComparison);
        Assert.Null(model.ComparisonMapPlot);
        Assert.Null(model.MapPlotA.Lap); Assert.Null(model.MapPlotB.Lap);
    });

    [Fact]
    public void IncompatibleRoutesRenderIndependentlyWithoutMatchedReferenceNavigation() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with
        {
            Samples = reference.Samples.Select(s =>
            {
                var lap = s.State.Lap!;
                return s with { State = s.State with { Lap = lap with { Position = lap.Position with { X = lap.Position.X + 2000 } } } };
            }).ToArray()
        };
        fixture.Model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(fixture.Model);
        fixture.Model.Is3D = true; fixture.Model.SharedSpace = true;
        Assert.True(fixture.Model.HasMapComparison);
        Assert.True(fixture.Model.CanUseSharedSpace); Assert.True(fixture.Model.IsMapComparison);
        Assert.NotNull(fixture.Model.ComparisonMapPlot);
        Assert.False(fixture.Model.Plot.Comparison!.CanCompare);
        fixture.Model.ScrubB = true;
        fixture.Model.Cursor = 120;
        fixture.Model.PickReferencePoint(220);
        Assert.Equal(120, fixture.Model.Cursor);
        Assert.Equal(220, fixture.Model.MapPlotB.Cursor);
        Assert.Null(fixture.Model.MapPlotB.CursorPositionOverride);
        Assert.Equal(-1, fixture.Model.MapPlotB.SectionStart);
        Assert.Equal(-1, fixture.Model.MapPlotB.SectionEnd);
    });

    [Theory]
    [InlineData(4200)]
    [InlineData(1229)]
    public void DifferentCarsUseActualIndependentCursorsAndPreserveBenchmarkGuards(int referenceCar) => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport(new AppSettings { LapReviewBenchmarkRunId = Guid.NewGuid() });
        var model = fixture.Model;
        var run = WithCar(LapReviewFixtures.Create(), 3141);
        var reference = WithCar(LapReviewFixtures.Create(reference: true), referenceCar);
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.CanUseSharedSpace); Assert.True(model.IsMapComparison);
        Assert.Equal(reference.Id, model.MapPlotB.Lap!.RunId);
        Assert.Equal(referenceCar, model.MapPlotB.Lap.CarOrdinal);
        Assert.Contains("Pinned benchmark is not used", model.ReferenceStatus);
        Assert.False(model.Plot.Comparison!.CanCompare);
        Assert.Contains("different cars", model.SectionText);
        Assert.Contains("Matched sections and time delta are unavailable", model.MapComparisonHint);
        Assert.DoesNotContain("Choose Run B", model.MapComparisonHint);
        Assert.Null(model.MapPlotB.Comparison);

        model.ScrubB = true;
        model.Cursor = 120;
        model.PickReferencePoint(220);
        Assert.Equal(120, model.Cursor); Assert.Equal(220, model.MapPlotB.Cursor);
        Assert.Null(model.MapPlotB.CursorPositionOverride);
        var data = model.MapPlotB;
        var expected = LapReviewPlot.Value(data.Lap!.Points[220], data, 220);
        Assert.Equal($"Speed: {RunPresentation.Number(expected, " " + LapReviewPlot.Unit(data))}", model.MapBCursorValue);
        model.ScrubBoth = true;
        model.Cursor = 160;
        var paired = RelativeReferenceIndex(model, 160);
        Assert.Equal(paired, model.MapPlotB.Cursor);
        model.PickReferencePoint(-1); model.PickReferencePoint(int.MaxValue);
        Assert.Equal(paired, model.MapPlotB.Cursor); Assert.Equal(160, model.Cursor);
        model.SectionStartCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Cursor = 250;
        model.SectionEndCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.Equal(-1, model.MapPlotB.SectionStart); Assert.Equal(-1, model.MapPlotB.SectionEnd);
        Assert.Equal("Unavailable", model.Metrics[0].Reference);
        Assert.All(model.Metrics.Skip(1), metric => Assert.Equal("—", metric.Reference));
        model.Channel = model.Channels.Single(c => c.Channel == LapReviewChannel.Delta);
        Assert.Contains("Unavailable", model.MapBCursorValue);
        Assert.False(LapReviewColorRange.From(model.MapPlotA).HasValues);
        Assert.False(LapReviewColorRange.From(model.MapPlotB).HasValues);
        Assert.Equal("No match", model.CursorDetails!.Delta);

        reference = reference with { Id = Guid.NewGuid() };
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.Equal(reference.Id, model.MapPlotB.Lap!.RunId);
        Assert.Equal(RelativeReferenceIndex(model, model.Cursor), model.MapPlotB.Cursor);
    });

    [Fact]
    public void PartialRunBIsAvailableForVisualReviewButNotForTiming() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with { Samples = reference.Samples.Take(220).ToArray() };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.NotNull(model.Reference);
        Assert.False(model.Reference.IsComplete);
        Assert.Equal(reference.Id, model.Reference.RunId);
        Assert.True(model.IsMapComparison); Assert.NotNull(model.ComparisonMapPlot);
        Assert.False(model.Plot.Comparison!.CanCompare);
        model.ScrubB = true;
        model.Cursor = 100;
        model.PickReferencePoint(120);
        Assert.Equal(100, model.Cursor); Assert.Equal(120, model.MapPlotB.Cursor);
        Assert.Equal(-1, model.MapPlotB.SectionStart); Assert.Equal(-1, model.MapPlotB.SectionEnd);
        Assert.Null(model.MapPlotB.Comparison);
    });

    [Fact]
    public void RunBWithoutRecordedPositionsDoesNotCreateAModelOrAskToSelectRunBAgain() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        reference = reference with
        {
            Samples = reference.Samples.Select(sample => sample with { State = sample.State with { Lap = null } }).ToArray()
        };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.False(model.HasMapComparison); Assert.False(model.CanUseSharedSpace);
        Assert.Null(model.ComparisonMapPlot);
        Assert.Contains("Run B has no second lap with recorded positions", model.MapComparisonHint);
        Assert.DoesNotContain("Choose Run B", model.MapComparisonHint);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentSliderTargetsHoldTheOtherLapAndReadTheirOwnValues(bool differentCar) => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = LapReviewFixtures.Create(reference: true);
        if (differentCar) reference = WithCar(reference, 4200);
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.ScrubBoth);
        model.ScrubCursor = 150;
        var originalB = model.MapPlotB.Cursor;
        model.ScrubA = true;
        model.ScrubCursor = 250;
        Assert.Equal(250, model.Cursor);
        Assert.Equal(originalB, model.MapPlotB.Cursor);
        Assert.Null(model.MapPlotB.CursorPositionOverride);
        Assert.StartsWith("A · ", model.ScrubPosition);

        var cursorEvents = 0;
        model.CursorMoved += _ => cursorEvents++;
        var metrics = model.Metrics[0];
        model.ScrubB = true;
        model.ScrubCursor = 320;
        Assert.Equal(250, model.Cursor);
        Assert.Equal(250, model.Plot.Cursor);
        Assert.Equal(320, model.MapPlotB.Cursor);
        Assert.Equal(0, cursorEvents);
        Assert.Same(metrics, model.Metrics[0]);
        Assert.False(model.IsBusy);
        Assert.StartsWith("B · ", model.ScrubPosition);
        Assert.Equal(model.Reference!.Points.Length - 1, model.MaximumScrubCursor);
        foreach (var channel in new[] { LapReviewChannel.Speed, LapReviewChannel.Power, LapReviewChannel.LateralG, LapReviewChannel.Delta })
        {
            model.Channel = model.Channels.Single(c => c.Channel == channel);
            var data = model.MapPlotB;
            var expected = LapReviewPlot.Value(data.Lap!.Points[320], data, 320);
            Assert.Equal($"{model.Channel.Label}: {RunPresentation.Number(expected, " " + LapReviewPlot.Unit(data))}", model.MapBCursorValue);
        }
        model.PickReferencePoint(340);
        Assert.Equal(250, model.Cursor); Assert.Equal(340, model.ScrubCursor);
        model.Cursor = 270;
        Assert.Equal(340, model.MapPlotB.Cursor);
        model.ScrubBoth = true;
        model.ScrubCursor = 400;
        Assert.Equal(400, model.Cursor);
        Assert.NotEqual(340, model.MapPlotB.Cursor);
        if (!differentCar)
        {
            Assert.Equal(model.Plot.Comparison!.Points[400].ReferencePointIndex, model.MapPlotB.Cursor);
            Assert.NotNull(model.MapPlotB.CursorPositionOverride);
            Assert.Contains("matching track positions", model.ScrubHint);
        }
        else
        {
            Assert.Null(model.MapPlotB.Comparison);
            Assert.Contains("relative lap progress", model.ScrubHint);
            Assert.Contains("unavailable", model.ScrubHint);
        }
    });

    [Fact]
    public void BothFallbackUsesRecordedDistanceAndIndependentModesDoNotChangeBenchmarkValidity() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var reference = WithCar(LapReviewFixtures.Create(reference: true), 4200);
        reference = reference with { Samples = reference.Samples.Where((_, index) => index < 200 || index % 3 == 0).ToArray() };
        model.SetRuns(LapReviewFixtures.Create(), reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.False(model.Plot.Comparison!.CanCompare);
        Assert.True(model.Reference!.Points.Length < model.Lap!.Points.Length);
        var target = model.MaximumCursor * 2 / 3;
        model.ScrubCursor = target;
        var progress = model.Lap.Points[target].DistanceMeters / model.Lap.RecordedDistanceMeters;
        var distance = progress * model.Reference.RecordedDistanceMeters;
        var expected = Enumerable.Range(0, model.Reference.Points.Length)
            .MinBy(index => Math.Abs(model.Reference.Points[index].DistanceMeters - distance));
        Assert.Equal(expected, model.MapPlotB.Cursor);
        Assert.Null(model.MapPlotB.CursorPositionOverride);
        Assert.Null(model.MapPlotB.Comparison);
        Assert.Equal("No match", model.CursorDetails!.Delta);
        Assert.Equal(-1, model.MapPlotB.SectionStart);
        Assert.Equal(-1, model.MapPlotB.SectionEnd);
        Assert.True(model.Reference.Quality.HasFlag(LapReviewQuality.TelemetryGap));
        Assert.Equal(model.Reference.Points[^2].DistanceMeters, model.Reference.Points[^1].DistanceMeters);
        model.ScrubCursor = model.MaximumScrubCursor;
        Assert.Equal(model.Reference.Points.Length - 1, model.MapPlotB.Cursor);
        model.ScrubCursor = 0;
        Assert.Equal(0, model.MapPlotB.Cursor);
        Assert.Contains("A · ", model.ScrubPosition); Assert.Contains("B · ", model.ScrubPosition);
    });

    [Fact]
    public void LeavingSharedSpaceOrRemovingRunBRestoresTheNormalASlider() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true));
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        model.ScrubA = true; model.ScrubCursor = 120;
        model.ScrubB = true; model.ScrubCursor = 250;
        model.Is3D = false;
        Assert.Equal(120, model.ScrubCursor); Assert.Equal(model.MaximumCursor, model.MaximumScrubCursor);
        Assert.Equal(model.CursorDetails!.Position, model.ScrubPosition); Assert.Equal("", model.ScrubHint);
        model.ScrubCursor = 180;
        Assert.Equal(180, model.Cursor);
        model.Is3D = true;
        Assert.Equal(250, model.ScrubCursor);
        model.Reference = null;
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.False(model.IsMapComparison);
        Assert.Equal(180, model.ScrubCursor);
        model.ScrubCursor = 200;
        Assert.Equal(200, model.Cursor);
        model.SetRuns(null, null);
        await LapReviewComparisonTestSupport.Ready(model);
        Assert.Equal(0, model.ScrubCursor); Assert.Equal(0, model.MaximumScrubCursor);
    });

    [Fact]
    public void UnmatchedBothFollowsEveryPrimaryCursorChangeAndModeEntry() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var run = LapReviewFixtures.Create();
        var reference = WithCar(LapReviewFixtures.Create(reference: true), 4200);
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Cursor = 150;
        model.Is3D = true; model.SharedSpace = true;
        AssertPaired(150);

        // Map/trace picking, graph synchronization and A's keyboard events all set Cursor.
        foreach (var index in new[] { 151, 400, 0, model.MaximumCursor })
        {
            model.Cursor = index;
            AssertPaired(index);
        }
        model.Cursor = 120;
        model.SectionStartCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        AssertPaired(120);
        model.Cursor = 300;
        model.SectionEndCommand.Execute(null);
        await LapReviewComparisonTestSupport.Ready(model);
        AssertPaired(300);
        Assert.Equal("No match", model.CursorDetails!.Delta);
        Assert.Equal("Unavailable", model.Metrics[0].Reference);

        model.SharedSpace = false;
        model.Cursor = 250;
        model.SharedSpace = true;
        AssertPaired(250);
        model.Is3D = false;
        model.Cursor = 180;
        model.Is3D = true;
        AssertPaired(180);

        reference = reference with { Id = Guid.NewGuid(), Samples = reference.Samples.Take(220).ToArray() };
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        AssertPaired(180);
        Assert.False(model.Reference!.IsComplete);
        Assert.False(model.Plot.Comparison!.CanCompare);

        model.ScrubA = true;
        var held = model.MapPlotB.Cursor;
        model.Cursor = 200;
        Assert.Equal(held, model.MapPlotB.Cursor);
        model.ScrubBoth = true;
        AssertPaired(200);

        void AssertPaired(int index)
        {
            Assert.True(model.ScrubBoth); Assert.Equal(index, model.Cursor);
            Assert.Equal(RelativeReferenceIndex(model, index), model.MapPlotB.Cursor);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchedReferenceNavigationAdvancesAcrossUnequalSampleDensityAndFindsEndpoints(bool primaryIsSparse) => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var run = LapReviewFixtures.Create();
        var reference = LapReviewFixtures.Create(reference: true);
        if (primaryIsSparse) run = run with { Samples = run.Samples.Where((_, index) => index % 2 == 0).ToArray() };
        else reference = reference with { Samples = reference.Samples.Where((_, index) => index % 2 == 0).ToArray() };
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.Plot.Comparison!.CanCompare);
        Assert.NotEqual(model.Reference!.Points.Length, model.Lap!.Points.Length);
        model.Cursor = 100;
        var before = model.Cursor;
        var referenceBefore = model.Plot.Comparison.Points[before].ReferenceLapSeconds!.Value;
        for (var count = 0; count < 5; count++)
        {
            model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
            Assert.True(model.Cursor > before);
            var seconds = model.Plot.Comparison.Points[model.Cursor].ReferenceLapSeconds!.Value;
            Assert.True(seconds > referenceBefore);
            before = model.Cursor; referenceBefore = seconds;
        }
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Previous);
        Assert.True(model.Cursor < before);
        Assert.True(model.Plot.Comparison.Points[model.Cursor].ReferenceLapSeconds < referenceBefore);

        var expectedStart = Enumerable.Range(0, model.Lap.Points.Length)
            .First(index => LapReviewPlot.ReferencePosition(model.Plot, index) is not null);
        var expectedEnd = Enumerable.Range(0, model.Lap.Points.Length)
            .Last(index => LapReviewPlot.ReferencePosition(model.Plot, index) is not null);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Start);
        Assert.Equal(expectedStart, model.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Previous);
        Assert.Equal(expectedStart, model.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.End);
        Assert.Equal(expectedEnd, model.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
        Assert.Equal(expectedEnd, model.Cursor);

        model.ScrubB = true;
        var primary = model.Cursor;
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Start);
        Assert.Equal(0, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
        Assert.Equal(1, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.End);
        Assert.Equal(model.Reference.Points.Length - 1, model.MapPlotB.Cursor);
        Assert.Equal(primary, model.Cursor);
    });

    [Fact]
    public void MatchedReferenceClickChoosesTheNearestRecordedPrimarySampleInsteadOfAlwaysTheEarlierOne() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var run = LapReviewFixtures.Create();
        run = run with { Samples = run.Samples.Where((_, index) => index % 2 == 0).ToArray() };
        model.SetRuns(run, LapReviewFixtures.Create(reference: true));
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        // This B sample is exactly on A's next sample, but the match stores the preceding edge.
        var reverse = model.MapPlotB.Comparison!;
        var chosen = Enumerable.Range(1, model.Reference!.Points.Length - 2).First(index =>
        {
            var match = reverse.Points[index];
            if (match.ReferencePointIndex is not { } lower || match.ReferenceLapSeconds is not { } seconds ||
                lower + 1 >= model.Lap!.Points.Length) return false;
            return Math.Abs(model.Lap.Points[lower + 1].LapSeconds - seconds) < Math.Abs(seconds - model.Lap.Points[lower].LapSeconds) &&
                LapReviewPlot.ReferencePosition(model.Plot, lower + 1) is not null;
        });
        var expected = reverse.Points[chosen].ReferencePointIndex!.Value + 1;
        model.PickReferencePoint(chosen);
        Assert.Equal(expected, model.Cursor);
        Assert.NotNull(model.MapPlotB.CursorPositionOverride);
        Assert.Equal(model.Lap!.Points[expected].LapSeconds.ToString("0.000"), model.CursorDetails!.Position.Split(' ')[0]);
    });

    [Fact]
    public void MatchedReferenceNavigationSkipsUnmatchedSamplesWithoutCreatingTimingValues() => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        model.SetRuns(LapReviewFixtures.Create(), LapReviewFixtures.Create(reference: true));
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        var comparison = model.Plot.Comparison!;
        foreach (var index in new[] { 0, 101, model.MaximumCursor })
            comparison.Points[index] = comparison.Points[index] with
            { ReferencePointIndex = null, ReferenceLapSeconds = null, DeltaSeconds = null };
        model.Cursor = 100;
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
        Assert.Equal(102, model.Cursor);
        Assert.Null(comparison.Points[101].DeltaSeconds);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Start);
        Assert.Equal(1, model.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.End);
        Assert.Equal(model.MaximumCursor - 1, model.Cursor);
        Assert.Null(comparison.Points[0].DeltaSeconds);
        Assert.Null(comparison.Points[^1].DeltaSeconds);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnmatchedBothReferenceClicksAndKeysMoveBothWithoutEnablingMatchedTiming(bool primaryIsSparse) => LapReviewComparisonTestSupport.OnDispatcher(async () =>
    {
        using var fixture = new LapReviewComparisonTestSupport();
        var model = fixture.Model;
        var run = LapReviewFixtures.Create();
        var reference = WithCar(LapReviewFixtures.Create(reference: true), 4200);
        if (primaryIsSparse) run = run with { Samples = run.Samples.Where((_, index) => index % 2 == 0).ToArray() };
        else reference = reference with { Samples = reference.Samples.Where((_, index) => index % 2 == 0).ToArray() };
        model.SetRuns(run, reference);
        await LapReviewComparisonTestSupport.Ready(model);
        model.Is3D = true; model.SharedSpace = true;
        Assert.True(model.ScrubBoth);
        var selectedReference = model.Reference!.Points.Length / 3;
        var expectedPrimary = RelativeIndex(model.Reference, model.Lap!, selectedReference);
        model.PickReferencePoint(selectedReference);
        Assert.Equal(expectedPrimary, model.Cursor);
        AssertPaired();
        var primary = model.Cursor; var secondary = model.MapPlotB.Cursor;
        for (var count = 0; count < 5; count++)
        {
            model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
            Assert.True(model.Cursor > primary);
            Assert.True(model.MapPlotB.Cursor > secondary);
            primary = model.Cursor; secondary = model.MapPlotB.Cursor;
            AssertPaired();
        }
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Previous);
        Assert.True(model.Cursor < primary); Assert.True(model.MapPlotB.Cursor < secondary);
        AssertPaired();
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Start);
        Assert.Equal(0, model.Cursor); Assert.Equal(0, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Previous);
        Assert.Equal(0, model.Cursor); Assert.Equal(0, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.End);
        Assert.Equal(model.MaximumCursor, model.Cursor);
        Assert.Equal(model.Reference.Points.Length - 1, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
        Assert.Equal(model.MaximumCursor, model.Cursor);
        Assert.Equal(model.Reference.Points.Length - 1, model.MapPlotB.Cursor);

        model.ScrubB = true;
        primary = model.Cursor;
        model.PickReferencePoint(30);
        Assert.Equal(primary, model.Cursor); Assert.Equal(30, model.MapPlotB.Cursor);
        model.NavigateReferenceCursor(LapReviewCursorNavigation.Next);
        Assert.Equal(primary, model.Cursor); Assert.Equal(31, model.MapPlotB.Cursor);

        void AssertPaired()
        {
            Assert.Equal(RelativeReferenceIndex(model, model.Cursor), model.MapPlotB.Cursor);
            Assert.False(model.Plot.Comparison!.CanCompare);
            Assert.Null(model.MapPlotB.Comparison);
            Assert.Null(model.MapPlotB.CursorPositionOverride);
            Assert.Equal(-1, model.MapPlotB.SectionStart); Assert.Equal(-1, model.MapPlotB.SectionEnd);
            Assert.Equal("No match", model.CursorDetails!.Delta);
        }
    });

    private static int RelativeReferenceIndex(LapReviewViewModel model, int index) => RelativeIndex(model.Lap!, model.Reference!, index);

    private static int RelativeIndex(LapReviewLap lap, LapReviewLap reference, int index)
    {
        if (index == 0) return 0;
        if (index == lap.Points.Length - 1) return reference.Points.Length - 1;
        var progress = (lap.Points[index].DistanceMeters - lap.Points[0].DistanceMeters) /
            (lap.Points[^1].DistanceMeters - lap.Points[0].DistanceMeters);
        var distance = reference.Points[0].DistanceMeters + progress *
            (reference.Points[^1].DistanceMeters - reference.Points[0].DistanceMeters);
        return Enumerable.Range(0, reference.Points.Length).MinBy(candidate => Math.Abs(reference.Points[candidate].DistanceMeters - distance));
    }

    private static RecordedRun WithCar(RecordedRun run, int carOrdinal) => run with
    {
        Samples = run.Samples.Select(sample => sample with { State = sample.State with { CarOrdinal = carOrdinal } }).ToArray()
    };
}

internal sealed class LapReviewComparisonTestSupport : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.LapReviewComparisonTests", Guid.NewGuid().ToString("N"));
    internal LapReviewViewModel Model { get; }
    internal LapReviewComparisonTestSupport(AppSettings? settings = null) => Model = new(settings ?? new AppSettings(), new RunStore(_directory), () => { });
    public void Dispose()
    {
        Model.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
    internal static async Task Ready(LapReviewViewModel model)
    {
        for (var i = 0; i < 500 && model.IsBusy; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(model.IsBusy);
        Assert.DoesNotContain("could not be prepared", model.Status);
        Assert.DoesNotContain("could not be prepared", model.SectionText);
    }
    internal static void OnDispatcher(Func<Task> test)
    {
        Exception? error = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception failure) { error = failure; }
                finally { finished.Set(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "Lap map comparison exceeded the dispatcher deadline.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
