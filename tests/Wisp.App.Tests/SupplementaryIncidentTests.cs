using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryIncidentTests
{
    [Fact]
    public void SignatureRetainsFailureIdentityButExcludesChangingContext()
    {
        var first = SupplementaryIncidentSchema.Create("recorder", "encoder_failed", "video_submit", 0x80004005,
            recorder: new(VideoPackets: 4), context: [new(2000, "fresh", WorkingSetBytes: 100)]);
        var second = first with { Recorder = new(VideoPackets: 9), Context = [new(0, "idle", WorkingSetBytes: 200)] };
        Assert.True(SupplementaryIncidentSchema.Valid(first));
        Assert.True(SupplementaryIncidentSchema.Valid(second));
        Assert.Equal(first.Signature, SupplementaryIncidentSchema.Signature(second));
        Assert.NotEqual(first.Signature, SupplementaryIncidentSchema.Signature(first with { Code = 5 }));
        Assert.NotEqual(first.Signature, SupplementaryIncidentSchema.Signature(first with { FailureStage = "video_initialize" }));
    }

    [Fact]
    public void ArbitraryTextAndSymbolsCannotEnterIncident()
    {
        var valid = SupplementaryIncidentSchema.Create("managed-exception", exceptions:
            [new("InvalidOperationException", 1, ["Wisp.App.Clips.CompatibleClipExporter.Inspect"])]);
        Assert.True(SupplementaryIncidentSchema.Valid(valid));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Reason = "fixture-private-text" }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { FailureStage = "fixture-private-path" }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Exceptions = [new("PrivateException", 1, [])] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Exceptions = [new("Exception", 1, ["Wisp.App.NotInCatalog.Method"])] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Signature = new('0', 64) }));
    }

    [Fact]
    public void ContextAndExceptionBoundsRejectInvalidOrUnorderedEvidence()
    {
        var valid = SupplementaryIncidentSchema.Create("managed-exception", context: [new(30000, "unknown"), new(2000, "idle"), new(0, "fresh")]);
        Assert.True(SupplementaryIncidentSchema.Valid(valid));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [new(30001, "idle")] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [new(0, "idle"), new(2000, "idle")] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [new(0, "idle"), new(0, "idle")] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [new(0, "idle", CpuPercent: double.NaN)] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [new(0, "idle", WorkingSetBytes: -1)] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Exceptions = Enumerable.Repeat(new SupplementaryIncidentException("Exception", 0, []), 4).ToImmutableArray() }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Context = [] }));
        Assert.False(SupplementaryIncidentSchema.Valid(valid with { Recorder = new(VideoPackets: 1) }));
    }

    [Theory]
    [InlineData("audio_failed", "host_loop", "audio", "failure")]
    [InlineData("audio_unavailable", null, "audio", "unsupported")]
    [InlineData("encoder_failed", "video_submit", "encode", "failure")]
    [InlineData("hdr_encoder_unsupported", "video_initialize", "encode", "unsupported")]
    [InlineData("cleanup_failed", "video_cleanup", "encode", "failure")]
    [InlineData("mux_failed", "save_finalize", "save", "failure")]
    [InlineData("helper_timeout", null, "capture", "timeout")]
    public void StagesDescribeTheObservedFailure(string reason, string? stage, string expectedStage, string outcome)
    {
        Assert.Equal(expectedStage, SupplementaryIncidentSchema.RecorderStage(reason, stage));
        Assert.Equal(outcome, SupplementaryIncidentSchema.RecorderOutcome(reason));
    }

    [Fact]
    public void EventRejectsIncidentOnSuccessAndPreservesTerminalExactlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var incident = SupplementaryIncidentSchema.Create("native-access", "access-denied");
        var value = new SupplementaryEvent(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, "2.6.6", "fixture", "stable",
            "connection", "native", "failure", "steam", Stage: "validation", Incident: incident);
        Assert.True(SupplementarySchema.Valid(value, now));
        Assert.False(SupplementarySchema.Valid(value with { Outcome = "success" }, now));
        Assert.False(SupplementarySchema.Valid(value with { Outcome = "attempt" }, now));
        var observations = new List<SupplementaryObservation>();
        using (var operation = new SupplementaryOperation(observations.Add, "clips", "clips", "encode", TimeProvider.System))
        { operation.Complete("failure", incident); operation.Complete("success"); }
        Assert.Equal(2, observations.Count);
        Assert.Same(incident, observations[1].Incident);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(incident, SupplementarySchema.Json).Length < SupplementaryIncidentSchema.MaximumBytes);
    }
}
