using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewTrackGeometryTests
{
    [Fact]
    public void NormalizationPreservesActualElevationAndUsesTheSameScaleForEveryAxis()
    {
        var data = Data([new(1000, 400, -100), new(2000, 500, 400)]);
        var bounds = LapReviewTrackBounds.From(data);
        var first = bounds.Normalize(data.Lap!.Points[0].Position);
        var last = bounds.Normalize(data.Lap.Points[1].Position);
        Assert.Equal(1, last.X - first.X, 10);
        Assert.Equal(.1, last.Y - first.Y, 10);
        Assert.Equal(-.5, last.Z - first.Z, 10);
        Assert.Equal(1000, bounds.Span);
        Assert.Equal(400, bounds.MinimumHeight);
    }

    [Fact]
    public void OnlyAnAcceptedReferenceCanExpandTheBounds()
    {
        var data = Data([new(0, 0, 0), new(10, 1, 0)]);
        var reference = Data([new(0, 0, 0), new(1000, 100, 0)]).Lap! with { RunId = Guid.NewGuid() };
        Assert.Equal(10, LapReviewTrackBounds.From(data with { Reference = reference }).Span);
        Assert.Equal(1000, LapReviewTrackBounds.From(data with { Reference = reference, Comparison = new([], 1, true, string.Empty) }).Span);
    }

    [Fact]
    public void SeparateComparisonViewsKeepUnionBoundsWithoutDrawingTheOtherLapsPath()
    {
        var first = Data([new(0, 0, 0), new(100, 10, 50)]);
        var second = Data([new(0, 0, 0), new(1000, 100, 0)]).Lap! with { RunId = Guid.NewGuid() };
        var combined = first with { Reference = second, Comparison = new([], 1, true, string.Empty) };
        var split = combined with { ShowReferencePath = false };
        var overlay = LapReviewTrackGeometry.Build(combined, TestContext.Current.CancellationToken);
        var separate = LapReviewTrackGeometry.Build(split, TestContext.Current.CancellationToken);
        Assert.Equal(overlay.Bounds, separate.Bounds);
        Assert.Equal(overlay.Fit, separate.Fit);
        Assert.Equal(1000, separate.Bounds.Span);
        Assert.Equal(overlay.Model.Children.Count - 1, separate.Model.Children.Count);
        Assert.Equal(overlay.SegmentCount, separate.SegmentCount);

        var sameLap = first with { Reference = first.Lap! with { }, Comparison = new([], 1, true, string.Empty) };
        Assert.False(sameLap.HasDistinctReference);
        Assert.Equal(LapReviewTrackBounds.From(first), LapReviewTrackBounds.From(sameLap));
    }

    [Fact]
    public void SideBySidePlacementPreservesEqualScaleAndTrueElevationInOneCoordinateSystem()
    {
        var first = Data([new(0, 20, 0), new(100, 35, 50)]);
        var second = Data([new(0, 40, 0), new(200, 80, 75)]);
        var bounds = LapReviewTrackBounds.From(first, second.Lap);
        var placement = LapReviewTrackArrangement.Create(first, second, bounds);
        Assert.Equal(0, placement.PrimaryOffset.Y); Assert.Equal(0, placement.ReferenceOffset.Y);
        var a = first.Lap!.Points.Select(point => bounds.Normalize(point.Position) + placement.PrimaryOffset).ToArray();
        var b = second.Lap!.Points.Select(point => bounds.Normalize(point.Position) + placement.ReferenceOffset).ToArray();
        Assert.Equal(2, (b[1].X - b[0].X) / (a[1].X - a[0].X), 8);
        Assert.Equal(15, (a[1].Y - a[0].Y) * bounds.Span, 8);
        Assert.Equal(40, (b[1].Y - b[0].Y) * bounds.Span, 8);
        Assert.Equal(8, placement.PrimaryCorners.Length); Assert.Equal(8, placement.ReferenceCorners.Length);
    }

    [Fact]
    public void NonFinitePositionsDoNotPoisonBoundsOrCreateABridge()
    {
        var data = Data([new(0, 2, 0), new(float.NaN, 10000, 0), new(10, 3, 0), new(20, 4, 0)]);
        var points = data.Lap!.Points;
        var bounds = LapReviewTrackBounds.From(data);
        Assert.Equal(20, bounds.Span);
        Assert.Equal([(2, 3)], LapReviewTrackGeometry.Segments(points, [0, 1, 2, 3]).ToArray());
        Assert.Empty(LapReviewTrackGeometry.Segments(points, [0, 3]));
    }

    [Fact]
    public void DownsamplingRetainsHeightExtremaAndBothEndsWithinItsFixedBudget()
    {
        var points = Data(Enumerable.Range(0, 36000).Select(i => new LapPosition(i, i == 17 ? 900 : i == 23 ? -500 : 0, 0)).ToArray()).Lap!.Points;
        var retained = LapReviewTrackGeometry.RetainedIndices(points, cancellationToken: TestContext.Current.CancellationToken);
        Assert.InRange(retained.Length, 2, LapReviewTrackGeometry.MaximumPathPoints);
        Assert.Equal(0, retained[0]); Assert.Equal(points.Length - 1, retained[^1]);
        Assert.Contains(17, retained); Assert.Contains(23, retained);
        Assert.Equal(retained.Distinct().Order(), retained);
    }

    [Fact]
    public void BreaksInsideADownsampledSpanAreNeverConnected()
    {
        var points = Data(Enumerable.Range(0, 100).Select(i => new LapPosition(i, 0, 0)).ToArray()).Lap!.Points;
        points[23] = points[23] with { BreakBefore = true };
        Assert.Equal([(0, 10), (30, 40)], LapReviewTrackGeometry.Segments(points, [0, 10, 30, 40]).ToArray());
    }

    [Fact]
    public void DensePathRetainsBriefBrakingAndPowerExtremaAsWellAsElevation()
    {
        var points = Data(Enumerable.Range(0, 36000).Select(i => new LapPosition(i, i == 7 ? -50 : i == 9 ? 80 : 0, 0)).ToArray()).Lap!.Points;
        var retained = LapReviewTrackGeometry.RetainedIndices(points, i => i == 17 ? -200 : i == 23 ? 800 : 0, TestContext.Current.CancellationToken);
        Assert.InRange(retained.Length, 2, LapReviewTrackGeometry.MaximumPathPoints);
        Assert.Contains(7, retained); Assert.Contains(9, retained);
        Assert.Contains(17, retained); Assert.Contains(23, retained);
        Assert.Equal(0, retained[0]); Assert.Equal(points.Length - 1, retained[^1]);
    }

    [Theory]
    [InlineData(0, 90, 0)]
    [InlineData(25, 35, 0)]
    [InlineData(150, -90, 120)]
    [InlineData(-180, 0, -170)]
    public void OrbitAndRollKeepAnOrthonormalCameraEvenAtThePoles(double yaw, double pitch, double roll)
    {
        var frame = LapReviewCameraFrame.From(yaw, pitch, roll);
        Assert.Equal(1, frame.Right.Length, 10); Assert.Equal(1, frame.Up.Length, 10); Assert.Equal(1, frame.Forward.Length, 10);
        Assert.Equal(0, Vector3D.DotProduct(frame.Right, frame.Up), 10);
        Assert.Equal(0, Vector3D.DotProduct(frame.Right, frame.Forward), 10);
        Assert.Equal(0, Vector3D.DotProduct(frame.Up, frame.Forward), 10);
    }

    [Fact]
    public void TopViewKeepsTheExistingMapHandednessAndSideViewShowsActualHeight()
    {
        var bounds = LapReviewTrackBounds.From(Data([new(0, 0, 0), new(100, 20, 50)]));
        var zero = bounds.Normalize(new(0, 0, 0)); var point = bounds.Normalize(new(100, 20, 50));
        var top = LapReviewCameraFrame.From(0, 90, 0);
        var a = top.Project(zero, new(), 2, new Size(800, 400)); var b = top.Project(point, new(), 2, new Size(800, 400));
        Assert.True(b.X > a.X); Assert.True(b.Y < a.Y);
        var side = LapReviewCameraFrame.From(0, 0, 0);
        a = side.Project(zero, new(), 2, new Size(800, 400)); b = side.Project(point, new(), 2, new Size(800, 400));
        Assert.Equal(80, a.Y - b.Y, 10);
    }

    [Theory]
    [InlineData(1600, 360)]
    [InlineData(500, 480)]
    public void DefaultCameraFitsTheActualLapWithEqualWorldScaleInsteadOfASquarePlaceholder(double width, double height)
    {
        var data = Data([new(0, 0, 0), new(400, 10, 100), new(1000, 20, 0)]);
        var bounds = LapReviewTrackBounds.From(data); var fit = LapReviewTrackFit.From(data, bounds);
        var frame = LapReviewCameraFrame.From(fit.Yaw, LapReviewTrackFit.DefaultPitch, 0); var cameraWidth = fit.Width(width / height);
        var floor = (bounds.MinimumHeight - bounds.Center.Y) / bounds.Span - .025;
        var path = data.Lap!.Points.Select(p => bounds.Normalize(p.Position)).ToArray();
        var projected = path.Concat(path.Select(p => new Point3D(p.X, floor, p.Z)))
            .Select(p => frame.Project(p, fit.Target, cameraWidth, new Size(width, height))).ToArray();
        Assert.All(projected, p => { Assert.InRange(p.X, 0, width); Assert.InRange(p.Y, 0, height); });
        var occupiedWidth = projected.Max(p => p.X) - projected.Min(p => p.X);
        var occupiedHeight = projected.Max(p => p.Y) - projected.Min(p => p.Y);
        Assert.True(occupiedWidth / width > .85 || occupiedHeight / height > .85);
    }

    [Fact]
    public void DefaultCameraShowsAnElongatedTrackHorizontallyWithoutChangingElevation()
    {
        var positions = Enumerable.Range(0, 361).Select(i =>
        {
            var angle = i * Math.PI / 180;
            return new LapPosition((float)(100 * Math.Cos(angle)), (float)(20 * Math.Cos(angle)), (float)(1000 * Math.Sin(angle)));
        }).ToArray();
        var data = Data(positions); var bounds = LapReviewTrackBounds.From(data); var fit = LapReviewTrackFit.From(data, bounds);
        Assert.InRange(Math.Abs(fit.Yaw), 89, 91);
        Assert.True(fit.HorizontalSpan > fit.VerticalSpan * 4);
        Assert.Equal(40, (bounds.Normalize(positions[0]).Y - bounds.Normalize(positions[180]).Y) * bounds.Span, 6);
        // Stopping in one part of the lap must not bias the automatic orientation.
        var duplicates = Data(positions.Take(40).Concat(Enumerable.Repeat(positions[39], 2000)).Concat(positions.Skip(40)).ToArray());
        Assert.Equal(fit.Yaw, LapReviewTrackFit.From(duplicates, LapReviewTrackBounds.From(duplicates)).Yaw, 8);
    }

    [Fact]
    public void AdjacentColoredSegmentsShareTheirEntireRingAtABend()
    {
        var data = Data([new(0, 0, 0), new(100, 10, 0), new(150, 30, 80)]);
        var scene = LapReviewTrackGeometry.Build(data, TestContext.Current.CancellationToken);
        var color = ((SolidColorBrush)LapReviewPalette.GetBrush(.5)).Color;
        var path = scene.Model.Children.OfType<GeometryModel3D>().Single(model =>
            model.Material is DiffuseMaterial { Brush: SolidColorBrush brush } && brush.Color == color);
        var mesh = Assert.IsType<MeshGeometry3D>(path.Geometry);
        Assert.Equal(8, mesh.Positions.GroupBy(p => p).Count(group => group.Count() == 2));
    }

    [Fact]
    public void BuiltGeometryIsFrozenAndCancelledPreparationCannotPublishAPartialScene()
    {
        var data = Data([new(0, 0, 0), new(100, 25, 50), new(200, 30, 100)]);
        var scene = LapReviewTrackGeometry.Build(data, CancellationToken.None);
        Assert.True(scene.Model.IsFrozen); Assert.Equal(2, scene.SegmentCount); Assert.NotEmpty(scene.Model.Children);
        Assert.Throws<OperationCanceledException>(() => LapReviewTrackGeometry.Build(data, new CancellationToken(true)));
    }

    [Fact]
    public void MissingChannelStillDrawsTheRecordedPathAndContactsAreNotBakedIntoTheMesh()
    {
        var data = Data([new(0, 0, 0), new(100, 10, 50)]) with
        { Channel = LapReviewChannel.Delta, Contacts = [new(1, 1, 100, LapReviewContactKind.PossibleContact)] };
        var without = LapReviewTrackGeometry.Build(data with { Contacts = null }, CancellationToken.None);
        var with = LapReviewTrackGeometry.Build(data, CancellationToken.None);
        Assert.Equal(1, without.SegmentCount);
        Assert.Equal(without.Model.Children.Count, with.Model.Children.Count);
    }

    private static LapReviewPlotData Data(LapPosition[] positions) => new(new LapReviewLap
    {
        Points = positions.Select((position, i) => new LapReviewPoint(i, i, i, i, position, i == 0,
            new RunSample { State = RunTestData.State() })).ToArray()
    }, null, null, LapReviewChannel.Speed, SpeedUnit.MilesPerHour, 0, 0, positions.Length - 1);
}
