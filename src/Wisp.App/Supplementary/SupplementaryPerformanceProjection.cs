using System.Collections.Immutable;
using Wisp.App.DebugLogging;

namespace Wisp.App.Supplementary;

internal static class SupplementaryPerformanceProjection
{
    internal static SupplementaryMeasurements? SampledMeasurements(IReadOnlyList<double> samples)
    {
        if (samples.Count is <= 0 or > 1_000_000 || samples.Any(v => !double.IsFinite(v) || v is < 0 or > 60000)) return null;
        var buckets = new long[16];
        foreach (var sample in samples)
        {
            var index = 0;
            while (sample > SupplementaryMeasurements.BucketUpperBounds[index]) index++;
            buckets[index]++;
        }
        return Measurements(new(ToolsPerformanceMetric.FrameWait, false, samples.Count, samples.Sum(), samples.Min(), samples.Max(), buckets, 0));
    }

    internal static string? Stage(ToolsPerformanceMetric metric) => metric switch
    {
        ToolsPerformanceMetric.FrameWait => "frame-wait",
        ToolsPerformanceMetric.RenderWork => "render-work",
        ToolsPerformanceMetric.SceneBuild => "scene-build",
        ToolsPerformanceMetric.PresentCall => "present-call",
        ToolsPerformanceMetric.SubmissionInterval => "submission-interval",
        ToolsPerformanceMetric.QueueToSubmit => "queue-to-submit",
        ToolsPerformanceMetric.ReceiveToSubmit => "receive-to-submit",
        ToolsPerformanceMetric.CompositorUpdate => "compositor-update",
        ToolsPerformanceMetric.RetryWait => "retry-wait",
        ToolsPerformanceMetric.CompositorMotionWork => "compositor-motion-work",
        ToolsPerformanceMetric.CompositorMotionSourceAge => "compositor-motion-source-age",
        ToolsPerformanceMetric.TelemetryParseWork => "telemetry-parse-work",
        ToolsPerformanceMetric.ParseToUiAdoption => "parse-to-ui-adoption",
        ToolsPerformanceMetric.ReceiveToUiAdoption => "receive-to-ui-adoption",
        ToolsPerformanceMetric.UiUpdateWork => "ui-update-work",
        ToolsPerformanceMetric.NativeUiUpdateWork => "native-ui-update-work",
        ToolsPerformanceMetric.NativeObservationToUiAdoption => "native-observation-to-ui-adoption",
        ToolsPerformanceMetric.PublishToUiAdoption => "publish-to-ui-adoption",
        _ => null
    };

    internal static bool HasRenderMode(ToolsPerformanceMetric metric) => metric < ToolsPerformanceMetric.TelemetryParseWork;
    internal static string Feature(ToolsPerformanceMetric metric) => metric switch
    {
        ToolsPerformanceMetric.TelemetryParseWork or ToolsPerformanceMetric.ParseToUiAdoption or ToolsPerformanceMetric.ReceiveToUiAdoption or ToolsPerformanceMetric.PublishToUiAdoption => "telemetry",
        ToolsPerformanceMetric.UiUpdateWork => "app",
        ToolsPerformanceMetric.NativeUiUpdateWork or ToolsPerformanceMetric.NativeObservationToUiAdoption => "native",
        _ => "hud"
    };
    internal static SupplementaryMeasurements? Measurements(ToolsPerformanceDistribution d)
    {
        if (d.Count is <= 0 or > 1_000_000 || d.Buckets.Length != 16 || d.Buckets.Any(v => v is < 0 or > 1_000_000) ||
            d.Buckets.Sum() != d.Count || !double.IsFinite(d.MinimumMs) || !double.IsFinite(d.MaximumMs) ||
            !double.IsFinite(d.SumMs) || d.MinimumMs < 0 || d.MaximumMs > 60000 || d.MinimumMs > d.MaximumMs) return null;
        double Quantile(double fraction)
        {
            var needed = (long)Math.Ceiling(d.Count * fraction); long total = 0;
            for (var i = 0; i < d.Buckets.Length; i++)
            {
                total += d.Buckets[i];
                // A bucket's upper edge intersected with the observed range remains an upper bound, not an exact percentile.
                if (total >= needed) return Math.Clamp(SupplementaryMeasurements.BucketUpperBounds[i], d.MinimumMs, d.MaximumMs);
            }
            return d.MaximumMs;
        }
        var result = new SupplementaryMeasurements((int)d.Count, d.MinimumMs, Quantile(.5), Quantile(.95), Quantile(.99),
            d.MaximumMs, Math.Clamp(d.SumMs / d.Count, d.MinimumMs, d.MaximumMs), d.Buckets.Select(v => (int)v).ToImmutableArray());
        return result.IsValid ? result : null;
    }
}
