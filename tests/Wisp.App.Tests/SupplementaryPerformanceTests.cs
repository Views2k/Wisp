using System.IO;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryPerformanceTests
{
    [Fact]
    public void SampledAgesUseSameBinsAndNeverTurnMissingOrOutOfRangeSamplesIntoZero()
    {
        var projected = Assert.IsType<SupplementaryMeasurements>(SupplementaryPerformanceProjection.SampledMeasurements([0, 1, 2, 4, 60000]));
        Assert.Equal(5, projected.Count);
        Assert.Equal(2, projected.Histogram!.Value[0]);
        Assert.Equal(1, projected.Histogram.Value[1]);
        Assert.Equal(1, projected.Histogram.Value[2]);
        Assert.Equal(1, projected.Histogram.Value[15]);
        Assert.Equal(60000, projected.MaxMs);
        Assert.Null(SupplementaryPerformanceProjection.SampledMeasurements([]));
        Assert.Null(SupplementaryPerformanceProjection.SampledMeasurements([double.NaN]));
        Assert.Null(SupplementaryPerformanceProjection.SampledMeasurements([60001]));
    }

    [Fact]
    public void HistogramPercentilesAreClampedUpperBoundsAndKeepActualRange()
    {
        var buckets = new long[16]; buckets[2] = 1; buckets[3] = 3;
        var d = new ToolsPerformanceDistribution(ToolsPerformanceMetric.PresentCall, false, 4, 24, 3, 7, buckets, 0);
        var projected = Assert.IsType<SupplementaryMeasurements>(SupplementaryPerformanceProjection.Measurements(d));
        Assert.Equal(7, projected.P95Ms);
        Assert.Equal(3, projected.MinMs);
        Assert.Equal(7, projected.MaxMs);
        Assert.Equal(6, projected.AverageMs);
        Assert.Equal(buckets.Select(v => (int)v), projected.Histogram!.Value);
        Assert.Equal("present-call", SupplementaryPerformanceProjection.Stage(d.Metric));
    }

    [Fact]
    public void EmptyAndInconsistentHistogramsCannotBecomePerformanceEvidence()
    {
        var d = new ToolsPerformanceDistribution(ToolsPerformanceMetric.PresentCall, true, 0, 0, 0, 0, new long[16], 4);
        Assert.Null(SupplementaryPerformanceProjection.Measurements(d));
        Assert.Null(SupplementaryPerformanceProjection.Measurements(d with { Count = 1 }));
        Assert.Null(SupplementaryPerformanceProjection.Measurements(d with { Count = 1_000_001 }));
        Assert.Null(SupplementaryPerformanceProjection.Stage((ToolsPerformanceMetric)999));
    }

    [Fact]
    public void AllCompiledRendererStagesAreExplicitlyAllowedAndBinsAgree()
    {
        Assert.Equal(ToolsPerformanceRecorder.BucketUpperBoundsMs.ToArray(), SupplementaryMeasurements.BucketUpperBounds);
        foreach (var metric in Enum.GetValues<ToolsPerformanceMetric>())
            Assert.Contains(SupplementaryPerformanceProjection.Stage(metric)!, SupplementarySchema.Stages);
    }

    [Fact]
    public void RandomIdentityPersistsSeparatelyAndCorruptionDoesNotSilentlyRotateIt()
    {
        using var fixture = new SupplementaryFixture();
        var first = Assert.IsType<SupplementaryInstallationIdentity>(SupplementaryIdentityStore.LoadOrCreate(fixture.Directory));
        Assert.True(first.NewlyCreated);
        Assert.True(SupplementarySchema.Uuid4(first.Id));
        var second = Assert.IsType<SupplementaryInstallationIdentity>(SupplementaryIdentityStore.LoadOrCreate(fixture.Directory));
        Assert.False(second.NewlyCreated);
        Assert.Equal(first.Id, second.Id);
        File.WriteAllText(Path.Combine(fixture.Directory, "reporting-identity.txt"), "corrupt");
        Assert.Null(SupplementaryIdentityStore.LoadOrCreate(fixture.Directory));
        Assert.Equal("corrupt", File.ReadAllText(Path.Combine(fixture.Directory, "reporting-identity.txt")));
    }
}
