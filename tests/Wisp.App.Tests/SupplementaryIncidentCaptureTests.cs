using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.Clips;
using Wisp.App.CrashDiagnostics;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

[Collection("Supplementary instrumentation")]
public sealed class SupplementaryIncidentCaptureTests
{
    [Fact]
    public void EveryPublishedMethodIsAnActualProductMethod()
    { Assert.All(SupplementaryIncidentSchema.Methods, symbol => Assert.True(CrashReportSymbols.IsKnownMethod(symbol), symbol)); }

    [Fact]
    public void ExceptionProjectionDropsMessagesAndUncataloguedOrForgedSymbols()
    {
        var result = SupplementaryIncidentCapture.Exceptions([
            new("PrivateException", 5, ["fixture-private-path", "Wisp.App.NotInCatalog.Method", "Wisp.App.Clips.CompatibleClipExporter.Inspect"])]);
        var error = Assert.Single(result!.Value);
        Assert.Equal("Exception", error.Type);
        Assert.Equal("Wisp.App.Clips.CompatibleClipExporter.Inspect", Assert.Single(error.Methods));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextUsesOnlyThreePriorMeasurementsAndDoesNotClampInvalidData()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 20).Select(i => new HealthContextSample
        {
            TimestampUtc = now - TimeSpan.FromSeconds(i * 2), WorkingSetBytes = 100 + i, ManagedHeapBytes = -1,
            CpuPercent = double.PositiveInfinity, PacketAgeMs = 70000, ListenerRunning = true
        }).Append(new() { TimestampUtc = now + TimeSpan.FromSeconds(1) }).ToImmutableArray();
        var context = SupplementaryIncidentCapture.Context(new(now, samples, []), now)!.Value;
        Assert.Equal(new[] { 30000, 14000, 0 }, context.Select(c => c.AgeMs));
        Assert.All(context, c => { Assert.Null(c.CpuPercent); Assert.Null(c.ManagedHeapBytes); Assert.Null(c.PacketAgeMs); });
        Assert.True(SupplementaryIncidentSchema.Valid(SupplementaryIncidentSchema.Create("renderer", context: context)));
    }

    [Fact]
    public void NativeRecorderEvidenceHasRealUnitsAndDropsUnboundedValues()
    {
        var parsed = RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mode = "recorder_failure", v = 1, reason = "cleanup_failed", stage = "video_cleanup", hr = 5,
            videoPackets = ulong.MaxValue, audioPackets = 12, submittedFrames = 9,
            schedulerLagKnown = true, schedulerLag100ns = 12500, sourceAgeKnown = true, sourceAge100ns = -1
        }));
        Assert.NotNull(parsed);
        var incident = SupplementaryIncidentCapture.Recorder(parsed.Reason, parsed, null, DateTimeOffset.UtcNow);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Null(incident.Recorder!.VideoPackets);
        Assert.Null(incident.Recorder.SourceAgeMs);
        Assert.Equal(1.25, incident.Recorder.SchedulerLagMs);
        Assert.Equal(12, incident.Recorder.AudioPackets);
    }

    [Fact]
    public void ExportAndNativeFailuresKeepTheirDistinctEvidenceWithoutRawText()
    {
        var error = new TimeoutException("fixture-private-message");
        error.Data["wisp-export-stage"] = "encoding"; error.Data["wisp-export-reason"] = "export-timeout";
        var incident = SupplementaryIncidentCapture.Export(error, DateTimeOffset.UtcNow)!;
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal("encoding", incident.FailureStage); Assert.Equal("export-timeout", incident.Reason);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(incident));
        var now = DateTimeOffset.UtcNow;
        var context = new HealthContextSnapshot(now, [], []);
        Assert.Equal("native-access", SupplementaryIncidentCapture.Native(new() { TimestampUtc = now, NativeStatus = NativeAssistProviderStatus.AccessDenied }, context).Category);
        Assert.Equal("native-unsupported", SupplementaryIncidentCapture.Native(new() { TimestampUtc = now, NativeStatus = NativeAssistProviderStatus.UnsupportedBuild }, context).Category);
    }

    [Fact]
    public void ExpectedGameExitAndCancellationAreNotReportedAsRecorderFailures()
    {
        var observed = new List<SupplementaryObservation>();
        var previous = SupplementaryObservations.Observer;
        SupplementaryObservations.Observer = observed.Add;
        try
        {
            foreach (var reason in new[] { "target_exited", "target_changed", "window_closed", "focus_lost", "cancelled", "stopped", "parent_closed", "none" })
                SupplementaryIncidentCapture.RecordRecorder(reason);
            Assert.Empty(observed);
            SupplementaryIncidentCapture.RecordRecorder("audio_unavailable");
            var audio = Assert.Single(observed);
            Assert.Equal("audio", audio.Stage); Assert.Equal("unsupported", audio.Outcome);
        }
        finally { SupplementaryObservations.Observer = previous; }
    }
}
