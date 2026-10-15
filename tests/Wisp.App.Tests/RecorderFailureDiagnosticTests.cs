using System.Text;
using System.Text.Json;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderFailureDiagnosticTests
{
    [Fact]
    public void ErrorDetailsPreserveTheActualCaptureAdapterInsteadOfGuessingFromOtherGpus()
    {
        var fields = Fields();
        fields["adapterKnown"] = true;
        fields["adapterVendorId"] = 0x1002u;
        fields["adapterDeviceId"] = 0x164Eu;
        fields["hardwareCandidatesKnown"] = true;
        fields["hardwareCandidates"] = 1u;
        var diagnostic = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        var report = ClipFailureReport.Build("encoder_failed", null, diagnostic);
        Assert.Contains("0x1002", report, StringComparison.Ordinal);
        Assert.Contains("0x164E", report, StringComparison.Ordinal);
        var withoutAdapter = ClipFailureReport.Build("encoder_failed", null, diagnostic with { AdapterVendorId = null, AdapterDeviceId = null });
        Assert.DoesNotContain("0x1002", withoutAdapter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hdr_encoder_unsupported", "video_initialize", "sdr-fallback")]
    [InlineData("audio_unavailable", "audio_initialize", "video-only")]
    public void StartupAdaptationsPreserveHardwareAndReasonWithoutBecomingTerminalFailures(string reason, string stage, string resolution)
    {
        var fields = Fields();
        fields["reason"] = reason; fields["stage"] = stage; fields["notice"] = true;
        fields["adapterKnown"] = true; fields["adapterVendorId"] = 4318; fields["adapterDeviceId"] = 1234;
        fields["hardwareCandidatesKnown"] = true; fields["hardwareCandidates"] = 2;
        var diagnostic = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        var incident = DebugLogging.SupplementaryIncidentCapture.Recorder(reason, diagnostic, null, DateTimeOffset.UtcNow);
        Assert.Equal(resolution, incident.Recorder!.Resolution);
        Assert.Equal(4318u, incident.Recorder.AdapterVendorId);
        Assert.Equal(1234u, incident.Recorder.AdapterDeviceId);
        Assert.Equal(2u, incident.Recorder.HardwareCandidates);
        Assert.Equal(3, incident.SchemaVersion);
        Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(incident));
        Assert.False(Supplementary.SupplementaryIncidentSchema.Valid(incident with { SchemaVersion = 2 }));
        Assert.NotEqual(incident.Signature, Supplementary.SupplementaryIncidentSchema.Signature(incident with { Recorder = incident.Recorder with { Resolution = null } }));
        fields["reason"] = "encoder_failed";
        Assert.Null(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }

    [Fact]
    public void ComponentFailureSurvivesParsingProjectionAndSignature()
    {
        var fields = Fields();
        fields["capture"] = new { reason = "display_color_changed", hr = 0x80070057u };
        fields["video"] = new { reason = "live_hardware_encoder_ready", hr = 0u };
        fields["spool"] = new { reason = "PRIVATE_DO_NOT_COPY", hr = 0x80004005u };
        var diagnostic = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        var incident = DebugLogging.SupplementaryIncidentCapture.Recorder(diagnostic.Reason, diagnostic, null, DateTimeOffset.UtcNow);
        var component = Assert.Single(incident.Recorder!.Components!.Value);
        Assert.Equal("capture", component.Component);
        Assert.Equal("display_color_changed", component.Reason);
        Assert.Equal(0x80070057u, component.Code);
        Assert.Equal(2, incident.SchemaVersion);
        Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(incident));
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", JsonSerializer.Serialize(incident));
        var changed = incident with { Recorder = incident.Recorder with { Components = [component with { Reason = "target_monitor_changed" }] } };
        Assert.NotEqual(incident.Signature, Supplementary.SupplementaryIncidentSchema.Signature(changed));
        Assert.Equal(incident.Signature, Supplementary.SupplementaryIncidentSchema.Signature(incident with { Recorder = incident.Recorder with { VideoPackets = 200 } }));
        Assert.False(Supplementary.SupplementaryIncidentSchema.Valid(incident with { SchemaVersion = 1 }));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("unknown")]
    public void MalformedComponentCannotDiscardTheMainFailureOrLeakText(string variant)
    {
        var json = Encoding.UTF8.GetString(Line());
        var component = variant switch
        {
            "extra" => "{\"reason\":\"display_color_changed\",\"hr\":2147942487,\"path\":\"PRIVATE_DO_NOT_COPY\"}",
            "duplicate" => "{\"reason\":\"display_color_changed\",\"reason\":\"PRIVATE_DO_NOT_COPY\",\"hr\":2147942487}",
            "negative" => "{\"reason\":\"display_color_changed\",\"hr\":-1}",
            _ => "{\"reason\":\"PRIVATE_DO_NOT_COPY\",\"hr\":2147942487}"
        };
        var parsed = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(Encoding.UTF8.GetBytes(json[..^1] + ",\"capture\":" + component + "}")));
        Assert.Equal("encoder_failed", parsed.Reason);
        Assert.Null(parsed.Components);
    }

    [Fact]
    public void EveryNativeRecorderStageCanReachAnIncident()
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "tools", "Wisp.RecorderPrototype", "RecorderHost.cpp"))) root = root.Parent;
        Assert.NotNull(root);
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "tools", "Wisp.RecorderPrototype", "RecorderHost.cpp"));
        var stages = System.Text.RegularExpressions.Regex.Matches(source, "diagnosticStage_\\s*=\\s*\"([a-z_]+)\"")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.True(stages.Length > 30);
        foreach (var stage in stages)
        {
            var fields = Fields(); fields["stage"] = stage;
            var parsed = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
            Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(DebugLogging.SupplementaryIncidentCapture.Recorder(parsed.Reason, parsed, null, DateTimeOffset.UtcNow)), stage);
        }
    }

    [Theory]
    [InlineData(false, "Forza only")]
    [InlineData(true, "system playback")]
    public void ReportIdentifiesSelectedAudioScope(bool systemAudio, string expected)
    {
        var report = ClipFailureReport.Build("audio_failed", new(60, 1080, 60, 75, systemAudio), null);
        Assert.Contains("audio: " + expected, report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "compressed")]
    [InlineData(true, "lossless")]
    public void ReportIdentifiesSelectedVideoMode(bool lossless, string expected)
    {
        var report = ClipFailureReport.Build("encoder_failed", new(60, 1080, 60, 100, LosslessVideo: lossless), null);
        Assert.Contains("video: " + expected, report, StringComparison.Ordinal);
    }

    internal static Dictionary<string, object> Fields() => new()
    {
        ["mode"] = "recorder_failure",
        ["v"] = 1,
        ["reason"] = "encoder_failed",
        ["stage"] = "video_submit",
        ["hr"] = 2147500037u,
        ["videoPackets"] = 3u,
        ["audioPackets"] = 6u,
        ["submittedFrames"] = 4u,
        ["schedulerLagKnown"] = true,
        ["schedulerLag100ns"] = 1500L,
        ["sourceAgeKnown"] = false,
        ["sourceAge100ns"] = 0L
    };

    internal static byte[] Line() => JsonSerializer.SerializeToUtf8Bytes(Fields());

    [Theory]
    [InlineData("capture_pause", "capture_failed", "capture")]
    [InlineData("capture_resume", "capture_failed", "capture")]
    [InlineData("save_storage_preflight", "storage_failed", "save")]
    [InlineData("storage_preflight", "storage_failed", "buffer")]
    public void LifecycleDiagnosticReachesValidatedIncident(string stage, string reason, string eventStage)
    {
        var fields = Fields();
        fields["stage"] = stage;
        fields["reason"] = reason;
        var parsed = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        var incident = DebugLogging.SupplementaryIncidentCapture.Recorder(reason, parsed, null, DateTimeOffset.UtcNow);
        Assert.Equal(stage, incident.FailureStage);
        Assert.Equal(2147500037u, incident.Code);
        Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal(eventStage, Supplementary.SupplementaryIncidentSchema.RecorderStage(reason, stage));
    }

    [Fact]
    public void ReportIncludesOnlyKnownTokensAndNumericEvidence()
    {
        var fields = Fields();
        fields["path"] = @"C:\PRIVATE_DO_NOT_COPY\secret.mp4";
        fields["capture"] = new { reason = "PRIVATE_DO_NOT_COPY", hr = 1 };
        var diagnostic = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        var report = ClipFailureReport.Build("helper_exited", new(60, 1080, 60, 100), diagnostic);
        Assert.Contains("Reason: encoder_failed", report, StringComparison.Ordinal);
        Assert.Contains("Stage: video_submit", report, StringComparison.Ordinal);
        Assert.Contains("HRESULT: 0x80004005", report, StringComparison.Ordinal);
        Assert.Contains("60s, 1080p, 60fps, quality 100", report, StringComparison.Ordinal);
        Assert.Contains("Scheduler lag (100ns): 1500", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Source frame age", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", report, StringComparison.Ordinal);
        Assert.DoesNotContain("helper_exited", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostConfigurationDiagnosticAcceptsOptionalLocalFrameTiming(bool includeLocalTiming)
    {
        var fields = Fields();
        fields["stage"] = "host_configuration";
        fields["reason"] = "storage_failed";
        fields["videoPackets"] = 0;
        fields["audioPackets"] = 0;
        fields["submittedFrames"] = 0;
        fields["schedulerLagKnown"] = false;
        if (includeLocalTiming)
        {
            fields["localFrameAgeKnown"] = true;
            fields["localFrameAge100ns"] = 456L;
        }
        var diagnostic = Assert.IsType<RecorderFailureDiagnostic>(RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields)));
        Assert.Equal("host_configuration", diagnostic.Stage);
        Assert.Equal(includeLocalTiming ? 456L : (long?)null, diagnostic.LocalFrameAge100ns);
        var report = ClipFailureReport.Build("helper_exited", new(60, 1080, 60, 100), diagnostic);
        Assert.Equal(includeLocalTiming, report.Contains("Local frame age (100ns): 456", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("stage")]
    [InlineData("reason")]
    [InlineData("numeric-type")]
    [InlineData("overflow")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("local-known-type")]
    [InlineData("local-age-type")]
    [InlineData("local-age-overflow")]
    [InlineData("local-age-missing-known")]
    public void MalformedDiagnosticsAreDiscardedWithoutRawFallback(string kind)
    {
        var fields = Fields();
        if (kind == "version") fields["v"] = 2;
        if (kind == "stage") fields["stage"] = "PRIVATE_DO_NOT_COPY";
        if (kind == "reason") fields["reason"] = "PRIVATE_DO_NOT_COPY";
        if (kind == "numeric-type") fields["hr"] = true;
        if (kind == "overflow") fields["hr"] = 4294967296UL;
        if (kind.StartsWith("local-", StringComparison.Ordinal))
        {
            fields["localFrameAgeKnown"] = true;
            fields["localFrameAge100ns"] = 10L;
        }
        if (kind == "local-known-type") fields["localFrameAgeKnown"] = "PRIVATE_DO_NOT_COPY";
        if (kind == "local-age-type") fields["localFrameAge100ns"] = "PRIVATE_DO_NOT_COPY";
        if (kind == "local-age-overflow") fields["localFrameAge100ns"] = ulong.MaxValue;
        if (kind == "local-age-missing-known") fields.Remove("localFrameAgeKnown");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(fields);
        if (kind == "duplicate") bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{", "{\"v\":1,", StringComparison.Ordinal));
        if (kind == "truncated") bytes = bytes[..^1];
        if (kind == "oversized") bytes = new byte[RecorderFailureDiagnostic.MaximumBytes + 1];
        Assert.Null(RecorderFailureDiagnostic.Parse(bytes));
    }

    [Fact]
    public void ManagedStorageReportRetainsOnlyFixedStagesAndNumericCodes()
    {
        var error = new RecorderClientException("clip_publish_failed")
        { StorageStage = "rename_publication", StorageHResult = unchecked((int)0x80070057) };
        var report = ClipFailureReport.Build(error.Reason, new(60, 1080, 60, 100), null, ClipStorageDiagnostic.From(error));
        Assert.Contains("Storage stage: rename_publication", report, StringComparison.Ordinal);
        Assert.Contains("Storage HRESULT: 0x80070057", report, StringComparison.Ordinal);
        Assert.Null(ClipStorageDiagnostic.From(new RecorderClientException("clip_publish_failed")
        { StorageStage = "PRIVATE_DO_NOT_COPY", StorageHResult = 5 }));
        Assert.Null(ClipStorageDiagnostic.From(new RecorderClientException("clip_publish_failed")
        { StorageStage = "copy_media" }));
    }

    [Fact]
    public void LegacyFallbackIncludesSettingsButNeverCopiesAnUnknownReason()
    {
        var report = ClipFailureReport.Build("PRIVATE_DO_NOT_COPY", new(60, 1080, 60, 75), null);
        Assert.Contains("Reason: recorder_failed", report, StringComparison.Ordinal);
        Assert.Contains("Native detail: not available", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", report, StringComparison.Ordinal);
    }
}
