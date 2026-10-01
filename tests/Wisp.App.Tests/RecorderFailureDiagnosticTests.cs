using System.Text;
using System.Text.Json;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderFailureDiagnosticTests
{
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
