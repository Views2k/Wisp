using System.Diagnostics;
using System.Runtime.InteropServices;
using Wisp.App.NativeRendering;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CompositorNeedleGeometryTests
{
    private static readonly long Timestamp = Stopwatch.Frequency * 10;

    [Fact]
    public void GeometryMatchesTheNative120BytePackedContract()
    {
        Assert.Equal(LayoutKind.Sequential, typeof(CompositorNeedleGeometry).StructLayoutAttribute!.Value);
        Assert.Equal(4, typeof(CompositorNeedleGeometry).StructLayoutAttribute!.Pack);
        Assert.Equal(120, Marshal.SizeOf<CompositorNeedleGeometry>());
        Assert.Equal(0, Offset(nameof(CompositorNeedleGeometry.Command)));
        Assert.Equal(80, Offset(nameof(CompositorNeedleGeometry.PivotX)));
        Assert.Equal(84, Offset(nameof(CompositorNeedleGeometry.PivotY)));
        Assert.Equal(88, Offset(nameof(CompositorNeedleGeometry.ParentM11)));
        Assert.Equal(92, Offset(nameof(CompositorNeedleGeometry.ParentM12)));
        Assert.Equal(96, Offset(nameof(CompositorNeedleGeometry.ParentM21)));
        Assert.Equal(100, Offset(nameof(CompositorNeedleGeometry.ParentM22)));
        Assert.Equal(104, Offset(nameof(CompositorNeedleGeometry.OffsetX)));
        Assert.Equal(108, Offset(nameof(CompositorNeedleGeometry.OffsetY)));
        Assert.Equal(112, Offset(nameof(CompositorNeedleGeometry.Opacity)));
        Assert.Equal(116, Offset(nameof(CompositorNeedleGeometry.Reserved)));
    }

    [Fact]
    public void AuthoredNeedleKeepsItsExternalPivotInLocalSurfaceCoordinates()
    {
        var geometry = CompositorNeedleGeometry.Create(AnalogHudLayout.Authored);

        Assert.Equal(0f, geometry.Command.OriginX);
        Assert.Equal(0f, geometry.Command.OriginY);
        Assert.Equal(110f, geometry.Command.AxisXX);
        Assert.Equal(180f, geometry.Command.AxisYY);
        Assert.Equal(0f, geometry.Command.AxisXY);
        Assert.Equal(0f, geometry.Command.AxisYX);
        Assert.Equal(-34.5f, geometry.PivotX);
        Assert.Equal(90f, geometry.PivotY);
        Assert.Equal(178.5f, geometry.OffsetX);
        Assert.Equal(54f, geometry.OffsetY);
        Assert.Equal(DirectCompositionShader.Needle, geometry.Command.Shader);
        Assert.Equal(1f, geometry.ParentM11);
        Assert.Equal(1f, geometry.ParentM22);
        Assert.Equal(1f, geometry.Opacity);
        Assert.Equal(0u, geometry.Reserved);
    }

    [Theory]
    [InlineData(-190)]
    [InlineData(-90)]
    [InlineData(0)]
    [InlineData(37.25)]
    [InlineData(214.75)]
    [InlineData(359.5)]
    public void IndependentRotationMatchesTheOriginalQuadAfterNonuniformAffinePlacement(double angle)
    {
        var layouts = new[]
        {
            AnalogHudLayout.Authored,
            AnalogHudLayout.Authored with
            {
                Needle = new(38.25, -12.75, 121.5, 177.25),
                NeedlePivot = new(-4.5, 73.125)
            }
        };
        var snapshot = new ProbeSnapshot();
        HudLayerPlacement[] placements =
        [
            new(1, snapshot, 0, 0, 1, 0, 0, 1, 1),
            new(1, snapshot, 100.25f, -37.5f, 0, 2, -3, 0, .5f),
            new(1, snapshot, -22.5f, 105.75f, 1.75f, .35f, -.2f, .65f, .4f),
            new(1, snapshot, 60, 90, -1.25f, .15f, .4f, 2.5f, 1)
        ];
        (double U, double V)[] samples = [(0, 0), (1, 0), (0, 1), (1, 1), (.35, .6)];

        foreach (var layout in layouts)
            foreach (var placement in placements)
            {
                var geometry = CompositorNeedleGeometry.Create(layout);
                geometry.Place(placement.OriginX, placement.OriginY, placement.AxisXX, placement.AxisXY,
                    placement.AxisYX, placement.AxisYY, placement.Opacity);
                DirectCompositionDrawCommand[] original =
                [AnalogHudScene.Quad(0, layout.Needle, angle, layout.NeedlePivot, shader: DirectCompositionShader.Needle)];
                placement.Transform(original);

                foreach (var (u, v) in samples)
                {
                    var actual = SurfacePoint(geometry, angle, u, v);
                    var expected = QuadPoint(original[0], u, v);
                    Assert.InRange(Math.Abs(actual.X - expected.X), 0, .001);
                    Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, .001);
                }
            }
    }

    [Fact]
    public void SceneAppliesPlacementOnceAndKeepsWindowOpacityOutsideNeedleContent()
    {
        var probe = new ProbeSnapshot();
        var original = probe.Geometry;
        var placement = new HudLayerPlacement(7, probe, 100, 200, 0, 2, -3, 0, .4f);
        var scene = new HudScenePlayback();
        scene.Update(new(800, 600, true, [Place(2, new ProbeSnapshot(false)), placement], Opacity: .2f), Timestamp);
        Span<CompositorNeedlePoint> points = stackalloc CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];

        Assert.True(scene.SupportsCompositorNeedle);
        Assert.True(scene.TryCopyCompositorNeedle(Timestamp, points, out var curve, out var geometry));
        Assert.Equal(probe.Curve, curve);
        Assert.Equal(-62f, geometry.OffsetX);
        Assert.Equal(557f, geometry.OffsetY);
        Assert.Equal(0f, geometry.ParentM11);
        Assert.Equal(2f, geometry.ParentM12);
        Assert.Equal(-3f, geometry.ParentM21);
        Assert.Equal(0f, geometry.ParentM22);
        Assert.Equal(.4f, geometry.Opacity);
        Assert.Equal(original.Command, geometry.Command);
        Assert.Equal(original.PivotX, geometry.PivotX);
        Assert.Equal(original.PivotY, geometry.PivotY);
        AssertPoints(probe.Points, points[..curve.Count]);

        Assert.True(scene.TryCopyCompositorNeedle(Timestamp + 1, points, out var repeatedCurve, out var repeated));
        Assert.Equal(curve, repeatedCurve);
        Assert.Equal(geometry, repeated);
        Assert.Equal(original, probe.Geometry);
        Assert.Equal(Timestamp + 1, probe.LastPlayback!.LastRequestedTimestamp);
        var ordinary = Assert.Single(scene.Build(Timestamp), command => command.Shader == DirectCompositionShader.Needle);
        Assert.Equal(ordinary.TintA, geometry.Command.TintA * geometry.Opacity);
    }

    [Fact]
    public void MultipleEligibleNeedlesRejectExtractionWithoutConsumingEitherCurve()
    {
        var first = new ProbeSnapshot();
        var second = new ProbeSnapshot();
        var scene = new HudScenePlayback();
        scene.Update(new(800, 600, true, [Place(1, first), Place(2, second)]), Timestamp);
        Span<CompositorNeedlePoint> points = stackalloc CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];

        Assert.False(scene.SupportsCompositorNeedle);
        Assert.False(scene.TryCopyCompositorNeedle(Timestamp, points, out var curve, out var geometry));
        Assert.Equal(default, curve);
        Assert.Equal(default, geometry);
        Assert.Equal(0, first.LastPlayback!.CopyAttempts);
        Assert.Equal(0, second.LastPlayback!.CopyAttempts);
        Assert.Equal(2, scene.Build(Timestamp).Count(command => command.Shader == DirectCompositionShader.Needle));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrExpiredCurveLeavesOrdinarySceneRenderingAvailable(bool expired)
    {
        var probe = new ProbeSnapshot(hasCurve: expired);
        var scene = new HudScenePlayback();
        scene.Update(new(800, 600, true, [Place(1, probe)]), Timestamp);
        var requested = expired ? probe.Curve.FreshUntilTimestamp + 1 : Timestamp;
        Span<CompositorNeedlePoint> points = stackalloc CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];

        Assert.True(scene.SupportsCompositorNeedle);
        Assert.False(scene.TryCopyCompositorNeedle(requested, points, out var curve, out var geometry));
        Assert.Equal(default, curve);
        Assert.Equal(default, geometry);
        Assert.Equal(requested, probe.LastPlayback!.LastRequestedTimestamp);
        Assert.Single(scene.Build(requested), command => command.Shader == DirectCompositionShader.Needle);
    }

    [Fact]
    public void RemovingOrResettingTheNeedleDoesNotRetainCompositorState()
    {
        var scene = new HudScenePlayback();
        Span<CompositorNeedlePoint> points = stackalloc CompositorNeedlePoint[CompositorNeedleCurve.MaximumPoints];
        Assert.False(scene.TryCopyCompositorNeedle(Timestamp, points, out _, out _));
        scene.Update(new(800, 600, true, [Place(1, new ProbeSnapshot())]), Timestamp);
        Assert.True(scene.TryCopyCompositorNeedle(Timestamp, points, out _, out _));

        scene.Update(new(800, 600, true, [Place(2, new ProbeSnapshot(false))]), Timestamp + 1);
        Assert.False(scene.SupportsCompositorNeedle);
        Assert.False(scene.TryCopyCompositorNeedle(Timestamp + 1, points, out var curve, out var geometry));
        Assert.Equal(default, curve);
        Assert.Equal(default, geometry);

        scene.Update(new(800, 600, true, [Place(1, new ProbeSnapshot())]), Timestamp + 2);
        Assert.True(scene.SupportsCompositorNeedle);
        scene.Reset();
        Assert.False(scene.SupportsCompositorNeedle);
        Assert.False(scene.TryCopyCompositorNeedle(Timestamp + 3, points, out curve, out geometry));
        Assert.Equal(default, curve);
        Assert.Equal(default, geometry);
    }

    private static int Offset(string field) => Marshal.OffsetOf<CompositorNeedleGeometry>(field).ToInt32();
    private static HudLayerPlacement Place(int id, HudLayerSnapshot snapshot) => new(id, snapshot, 0, 0, 1, 0, 0, 1, 1);

    private static (double X, double Y) QuadPoint(DirectCompositionDrawCommand command, double u, double v) =>
        (command.OriginX + u * command.AxisXX + v * command.AxisYX,
            command.OriginY + u * command.AxisXY + v * command.AxisYY);

    private static (double X, double Y) SurfacePoint(CompositorNeedleGeometry geometry, double angle, double u, double v)
    {
        var point = QuadPoint(geometry.Command, u, v);
        var radians = angle * Math.PI / 180;
        var x = point.X - geometry.PivotX;
        var y = point.Y - geometry.PivotY;
        var rotatedX = geometry.PivotX + Math.Cos(radians) * x - Math.Sin(radians) * y;
        var rotatedY = geometry.PivotY + Math.Sin(radians) * x + Math.Cos(radians) * y;
        return (geometry.OffsetX + rotatedX * geometry.ParentM11 + rotatedY * geometry.ParentM21,
            geometry.OffsetY + rotatedX * geometry.ParentM12 + rotatedY * geometry.ParentM22);
    }

    private static void AssertPoints(ReadOnlySpan<CompositorNeedlePoint> expected, ReadOnlySpan<CompositorNeedlePoint> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].OffsetSeconds, actual[index].OffsetSeconds);
            Assert.Equal(expected[index].Angle, actual[index].Angle);
            Assert.Equal(expected[index].Blur, actual[index].Blur);
        }
    }

    private sealed class ProbeSnapshot(bool supports = true, bool hasCurve = true) : HudLayerSnapshot
    {
        internal bool Supports { get; } = supports;
        internal bool HasCurve { get; } = hasCurve;
        internal CompositorNeedleGeometry Geometry { get; } = CreateGeometry();
        internal CompositorNeedleCurve Curve { get; } = new(3, Timestamp, Timestamp + Stopwatch.Frequency / 50,
            Timestamp + Stopwatch.Frequency / 10, Timestamp - Stopwatch.Frequency / 100, 17, 2, 20, 20);
        internal CompositorNeedlePoint[] Points { get; } = [new(0, 214.75, -.3), new(.01, 218, .15), new(.02, 220, 0)];
        internal ProbePlayback? LastPlayback { get; private set; }
        internal override IReadOnlyList<AnalogHudTexture> Textures => [];
        internal override HudLayerPlayback CreatePlayback() => LastPlayback = new();

        private static CompositorNeedleGeometry CreateGeometry()
        {
            var geometry = CompositorNeedleGeometry.Create(AnalogHudLayout.Authored);
            geometry.Command.TintA = .6f;
            return geometry;
        }
    }

    private sealed class ProbePlayback : HudLayerPlayback
    {
        private ProbeSnapshot? _snapshot;
        internal int CopyAttempts { get; private set; }
        internal long LastRequestedTimestamp { get; private set; }
        internal override bool SupportsCompositorNeedle => _snapshot?.Supports == true;
        internal override void Update(HudLayerSnapshot snapshot, long timestamp) => _snapshot = (ProbeSnapshot)snapshot;
        internal override void AppendCommands(List<DirectCompositionDrawCommand> commands, long timestamp)
        {
            if (_snapshot?.Supports == true) commands.Add(_snapshot.Geometry.Command);
        }
        internal override bool TryCopyCompositorNeedle(long timestamp, Span<CompositorNeedlePoint> points,
            out CompositorNeedleCurve curve, out CompositorNeedleGeometry geometry)
        {
            CopyAttempts++;
            LastRequestedTimestamp = timestamp;
            curve = default;
            geometry = default;
            if (_snapshot is not { Supports: true, HasCurve: true } snapshot ||
                timestamp > snapshot.Curve.FreshUntilTimestamp || points.Length < snapshot.Points.Length) return false;
            snapshot.Points.AsSpan().CopyTo(points);
            curve = snapshot.Curve;
            geometry = snapshot.Geometry;
            return true;
        }
    }
}
