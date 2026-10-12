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
    [Theory]
    [InlineData((int)CrashOrigin.UiDispatcher, "ui", "managed-exception")]
    [InlineData((int)CrashOrigin.BackgroundThread, "background", "managed-exception")]
    [InlineData((int)CrashOrigin.UnobservedTask, "task", "managed-exception")]
    [InlineData((int)CrashOrigin.UnexpectedExit, "recovered", "unexpected-exit")]
    [InlineData((int)CrashOrigin.WindowsApplicationFault, "windows", "windows-fault")]
    public void CrashProjectionPreservesOriginAndSanitizedFaultEvidence(int origin, string expected, string category)
    {
        var marker = WindowsFaultEventReaderTests.Marker();
        var now = marker.StartedAtUtc.AddMinutes(5);
        var fault = origin == (int)CrashOrigin.WindowsApplicationFault
            ? new WindowsFaultEvidence(now.AddSeconds(-1), 0xc0000005, 12, "ntdll.dll", false) : null;
        var report = CrashReport.Capture(new InvalidOperationException("PRIVATE_ERROR_TEXT"), (CrashOrigin)origin, true, now) with
        {
            RunId = marker.RunId,
            RecoveredExit = origin is (int)CrashOrigin.UnexpectedExit or (int)CrashOrigin.WindowsApplicationFault
                ? new(marker.StartedAtUtc, now, fault is null ? WindowsFaultLookupStatus.NoMatchingEvent : WindowsFaultLookupStatus.Matched, fault) : null
        };
        var incident = SupplementaryIncidentCapture.Crash(report);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal(3, incident.SchemaVersion);
        Assert.Equal(category, incident.Category);
        Assert.Equal(expected, incident.Crash!.Origin);
        Assert.Equal("crash-" + expected, incident.FailureStage);
        Assert.DoesNotContain("PRIVATE_ERROR_TEXT", JsonSerializer.Serialize(incident));
        if (fault is not null) { Assert.Equal("ntdll.dll", incident.Crash.Module); Assert.Equal(0xc0000005u, incident.Code); }
    }

    [Theory]
    [InlineData((int)ClipRecorderState.WaitingForGame, "waiting")]
    [InlineData((int)ClipRecorderState.Stopping, "stopping")]
    [InlineData((int)ClipRecorderState.Paused, "paused")]
    public void ContextRetainsObservedGameFocusRecorderAndRendering(int state, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var context = SupplementaryIncidentCapture.Context(new(now, [new()
        {
            TimestampUtc = now.AddSeconds(-2), Focus = DebugFocus.Game, CpuRendering = false,
            RecorderState = (ClipRecorderState)state, NativeGameVersion = "1.2.3", GameVerification = NativeGameVerification.Steam,
            GameActive = true, NativeExpected = true, OverlayExpectedVisible = true
        }], []), now);
        var sample = Assert.Single(context!.Value);
        Assert.Equal("game", sample.Foreground); Assert.Equal("gpu", sample.RenderMode);
        Assert.Equal(expected, sample.RecorderState); Assert.Equal("1.2.3", sample.GameBuild);
        Assert.Equal("verified", sample.GameVerification); Assert.True(sample.GameActive);
        Assert.True(sample.NativeExpected); Assert.True(sample.OverlayExpectedVisible);
        Assert.True(SupplementaryIncidentSchema.Valid(SupplementaryIncidentSchema.Create("renderer", context: context)));
    }

    [Fact]
    public void RendererFailuresRetainDistinctPhasesThroughTheHealthRingAndWireProjection()
    {
        var recorder = new HealthContextRecorder();
        var stages = Enum.GetValues<HealthFailureStage>().Where(stage => stage is >= HealthFailureStage.RendererState and <= HealthFailureStage.RendererHost).ToArray();
        foreach (var stage in stages) recorder.RecordRendererFailure(unchecked((int)0x887A0005), stage);
        var snapshot = recorder.CaptureForReport();
        Assert.True(HealthContextRecorder.IsValidSnapshot(snapshot));
        Assert.Equal(stages, snapshot.Breadcrumbs.Select(b => b.FailureStage));
        var incidents = snapshot.Breadcrumbs.Select(b => SupplementaryIncidentCapture.Breadcrumb("renderer", b, snapshot)).ToArray();
        Assert.All(incidents, incident =>
        {
            Assert.True(SupplementaryIncidentSchema.Valid(incident));
            Assert.Equal("native-failure", incident.Reason);
            Assert.Equal(0x887A0005u, incident.Code);
        });
        Assert.Equal(stages.Length, incidents.Select(i => i.FailureStage).Distinct().Count());
        Assert.Equal(stages.Length, incidents.Select(i => i.Signature).Distinct().Count());
        Assert.Equal("present-call", SupplementaryIncidentCapture.RendererEventStage(HealthFailureStage.RendererPresent));
        Assert.Equal("render-work", SupplementaryIncidentCapture.RendererEventStage(HealthFailureStage.RendererCreate));
    }

    [Fact]
    public void LapMapAndExportRemainDistinctAndInvalidPhasesCannotEnterTheRing()
    {
        var recorder = new HealthContextRecorder();
        recorder.RecordBreadcrumb(HealthEventCode.LapReviewFailed, 5, failureStage: HealthFailureStage.LapMap);
        recorder.RecordBreadcrumb(HealthEventCode.LapReviewFailed, 5, failureStage: HealthFailureStage.LapExport);
        recorder.RecordBreadcrumb(HealthEventCode.LapReviewFailed, 5, failureStage: (HealthFailureStage)999);
        var snapshot = recorder.CaptureForReport();
        var incidents = snapshot.Breadcrumbs.Select(b => SupplementaryIncidentCapture.Breadcrumb("saved-data", b, snapshot)).ToArray();
        Assert.Equal(new[] { "runs-map", "runs-export" }, incidents.Select(i => i.FailureStage));
        Assert.All(incidents, incident => Assert.True(SupplementaryIncidentSchema.Valid(incident)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), "disk-full")]
    [InlineData(unchecked((int)0x80070027), "disk-full")]
    [InlineData(unchecked((int)0x80070005), "access-denied")]
    [InlineData(unchecked((int)0x80070020), "storage")]
    public void ExportRetainsStorageCodeAndUsesTheLocalReportClassification(int code, string expected)
    {
        var error = new System.IO.IOException("PRIVATE_DO_NOT_COPY", code);
        var incident = Assert.IsType<SupplementaryIncident>(SupplementaryIncidentCapture.Export(error, DateTimeOffset.UtcNow));
        Assert.Equal(expected, incident.Reason);
        Assert.Equal(ClipExportFailureReport.FailureCategory(error), incident.Reason);
        Assert.Equal(unchecked((uint)code), incident.Code);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", JsonSerializer.Serialize(incident));
    }

    [Fact]
    public void EveryPublishedMethodIsAnActualProductMethod()
    { Assert.All(SupplementaryIncidentSchema.Methods, symbol => Assert.True(CrashReportSymbols.IsKnownMethod(symbol), symbol)); }

    [Fact]
    public void OtherCompiledProductMethodsHaveStablePrivateSafeLocations()
    {
        const string symbol = "Wisp.App.Supplementary.SupplementaryEnvironmentCollector.Snapshot";
        var result = SupplementaryIncidentCapture.Exceptions([new("InvalidOperationException", 5, [symbol, "Wisp.App.Forged.PrivateMethod"])]);
        var location = Assert.Single(Assert.Single(result!.Value).Methods);
        Assert.Equal(SupplementaryIncidentSchema.MethodId(symbol), location);
        Assert.True(SupplementaryIncidentSchema.ValidMethod(location));
        Assert.DoesNotContain(symbol, JsonSerializer.Serialize(result));
        Assert.True(SupplementaryIncidentSchema.Valid(SupplementaryIncidentSchema.Create("managed-exception", exceptions: result)));
    }

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
            TimestampUtc = now - TimeSpan.FromSeconds(i * 2),
            WorkingSetBytes = 100 + i,
            ManagedHeapBytes = -1,
            CpuPercent = double.PositiveInfinity,
            PacketAgeMs = 70000,
            ListenerRunning = true
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
            mode = "recorder_failure",
            v = 1,
            reason = "cleanup_failed",
            stage = "video_cleanup",
            hr = 5,
            videoPackets = ulong.MaxValue,
            audioPackets = 12,
            submittedFrames = 9,
            schedulerLagKnown = true,
            schedulerLag100ns = 12500,
            sourceAgeKnown = true,
            sourceAge100ns = -1
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
