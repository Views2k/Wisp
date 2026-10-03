using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipExportFailureReportTests
{
    private const string PrivateMarker = "PRIVATE_DIAGNOSTIC_MARKER";
    private static readonly ClipEntry Clip = new(Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), new(60, 1080, 60, 75, true, true),
        new(123456789, 1920, 1080, 60, 10_000_000, 135_000_000, true, true));

    [Fact]
    public void DetailsContainOnlyTheClosedDiagnosticSchemaAndNumericMediaMetadata()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var error = new IOException(PrivateMarker, unchecked((int)0x80070070));
            var report = ClipExportFailureReport.Create(error, Clip, ClipExportFormat.Compatible);
            using var document = JsonDocument.Parse(report.Details);
            var data = document.RootElement;
            Assert.Equal(new[] { "report", "version", "diagnosticBuildId", "format", "stage", "category", "reason", "nativeCode", "hresult", "clip" },
                data.EnumerateObject().Select(property => property.Name));
            Assert.Equal("wisp-clip-export", data.GetProperty("report").GetString());
            Assert.Equal(ApplicationVersionInfo.MachineVersion, data.GetProperty("version").GetString());
            Assert.Matches("^[A-Za-z0-9_.-]{1,80}$", data.GetProperty("diagnosticBuildId").GetString()!);
            Assert.Equal("compatible", data.GetProperty("format").GetString());
            Assert.Equal(error.HResult, data.GetProperty("hresult").GetInt32());
            var media = data.GetProperty("clip");
            Assert.Equal(new[] { "lossless", "hdr", "width", "height", "frameRate", "hasAudio", "fileBytes", "durationSeconds" },
                media.EnumerateObject().Select(property => property.Name));
            Assert.True(media.GetProperty("lossless").GetBoolean());
            Assert.False(media.GetProperty("hdr").GetBoolean());
            Assert.True(media.GetProperty("hasAudio").GetBoolean());
            Assert.Equal(1920, media.GetProperty("width").GetInt32());
            Assert.Equal(1080, media.GetProperty("height").GetInt32());
            Assert.Equal(60, media.GetProperty("frameRate").GetInt32());
            Assert.Equal(123456789, media.GetProperty("fileBytes").GetInt64());
            Assert.Equal(12.5, media.GetProperty("durationSeconds").GetDouble());
            Assert.DoesNotContain(Clip.Id.ToString(), report.Details, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("source-inspection", "track-codec")]
    [InlineData("source-validation", "source-pixel-format")]
    [InlineData("encoding", "export-timeout")]
    [InlineData("output-inspection", "video-track-missing")]
    [InlineData("output-validation", "output-color-transfer")]
    public void RecognizedStagesAndReasonsSurviveWithoutIncludingProbeText(string stage, string reason)
    {
        var error = new InvalidDataException(PrivateMarker);
        error.Data["wisp-export-stage"] = stage;
        error.Data["wisp-export-reason"] = reason;
        error.Data["wisp-export-probe"] = new CompatibleClipExporter.Probe(1, 2, 3, 4, false,
            PrivateMarker, PrivateMarker, PrivateMarker, PrivateMarker, PrivateMarker);
        var report = ClipExportFailureReport.Create(error, Clip, ClipExportFormat.Compatible);
        using var document = JsonDocument.Parse(report.Details);
        Assert.Equal(stage, document.RootElement.GetProperty("stage").GetString());
        Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownDataExceptionMessagesAndInnerExceptionsAreNeverFormatted(bool objectValues)
    {
        var error = new PrivateExceptionMarker();
        error.Data["wisp-export-stage"] = objectValues ? new MustNotBeFormatted() : "encoding\n" + PrivateMarker;
        error.Data["wisp-export-reason"] = objectValues ? new MustNotBeFormatted() : "duration/" + PrivateMarker;
        error.Data["wisp-export-probe"] = new MustNotBeFormatted();
        error.Data[PrivateMarker] = new MustNotBeFormatted();
        var report = ClipExportFailureReport.Create(error, Clip, ClipExportFormat.Compatible);
        using var document = JsonDocument.Parse(report.Details);
        Assert.Equal("library-copy", document.RootElement.GetProperty("stage").GetString());
        Assert.Equal("unspecified", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal("none", document.RootElement.GetProperty("nativeCode").GetString());
        Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PrivateExceptionMarker), report.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("packet-queue-full", "packet-queue-full")]
    [InlineData("export-native-error", "export-native-error")]
    [InlineData("decode-failed", "decode-failed")]
    [InlineData("decoder-error", "decoder-error")]
    [InlineData("export-file-ended-with-error", "export-file-ended-with-error")]
    [InlineData("option-o/-0004", "option-o/-4")]
    [InlineData("command-loadfile/-2", "command-loadfile/-2")]
    [InlineData("command-seek/-0004", "command-seek/-4")]
    [InlineData("property-volume/-4", "property-volume/-4")]
    [InlineData("property-mute/-4", "property-mute/-4")]
    [InlineData("property-pause/-4", "property-pause/-4")]
    [InlineData("video-window/-4", "video-window/-4")]
    [InlineData("option-d3d11-output-csp/-4", "option-d3d11-output-csp/-4")]
    [InlineData("initialize/-2147483648", "initialize/-2147483648")]
    [InlineData("option-PRIVATE_DIAGNOSTIC_MARKER/-4", "unrecognized")]
    [InlineData("command-PRIVATE_DIAGNOSTIC_MARKER/-4", "unrecognized")]
    [InlineData("property-PRIVATE_DIAGNOSTIC_MARKER/-4", "unrecognized")]
    [InlineData("command-seek-PRIVATE_DIAGNOSTIC_MARKER/-4", "unrecognized")]
    [InlineData("property-volume/-4/PRIVATE_DIAGNOSTIC_MARKER", "unrecognized")]
    [InlineData("command-seek/-4\nPRIVATE_DIAGNOSTIC_MARKER", "unrecognized")]
    [InlineData("option-o/PRIVATE_DIAGNOSTIC_MARKER", "unrecognized")]
    [InlineData("option-o/-4\nPRIVATE_DIAGNOSTIC_MARKER", "unrecognized")]
    [InlineData("option-o/-2147483649", "unrecognized")]
    [InlineData("option-o/0", "unrecognized")]
    [InlineData("option-o/4", "unrecognized")]
    public void NativeCodesAreClosedAndNumericActionErrorsAreCanonicalized(string supplied, string expected)
    {
        var report = ClipExportFailureReport.Create(new LosslessMpvException(supplied), Clip, ClipExportFormat.Compatible);
        using var document = JsonDocument.Parse(report.Details);
        Assert.Equal(expected, document.RootElement.GetProperty("nativeCode").GetString());
        Assert.Equal(expected == "packet-queue-full" ? "media-queue-full" : "native-failure",
            document.RootElement.GetProperty("category").GetString());
        Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredPlayerOptionsRetainOnlyTheirFixedNamesAndNumericErrors()
    {
        foreach (var (name, _) in LosslessMpvNative.Options)
        {
            var code = "option-" + name + "/-4";
            Assert.Equal(code, ClipExportFailureReport.SafeNativeCode(code));
            Assert.Equal("unrecognized", ClipExportFailureReport.SafeNativeCode(code + "/" + PrivateMarker));
        }
    }

    [Theory]
    [InlineData("queue", "media-queue-full", "media queue filled")]
    [InlineData("timeout", "timeout", "timed out")]
    [InlineData("disk-full", "disk-full", "drive is full")]
    [InlineData("handle-disk-full", "disk-full", "drive is full")]
    [InlineData("access", "access-denied", "access was denied")]
    [InlineData("security", "access-denied", "access was denied")]
    [InlineData("native-access", "access-denied", "access was denied")]
    [InlineData("missing-file", "file-unavailable", "file or folder is unavailable")]
    [InlineData("missing-directory", "file-unavailable", "file or folder is unavailable")]
    [InlineData("size", "size-limit", "file size limit")]
    [InlineData("validation", "validation", "failed its media checks")]
    [InlineData("unsupported", "unsupported-format", "format could not be converted")]
    [InlineData("storage", "storage", "while accessing files")]
    public void ActionableMessagesUseKnownFailureCategoriesAndKeepTheOriginal(string kind, string category, string message)
    {
        Exception error = kind switch
        {
            "queue" => new LosslessMpvException("packet-queue-full"),
            "timeout" => new TimeoutException(PrivateMarker),
            "disk-full" => new IOException(PrivateMarker, unchecked((int)0x80070070)),
            "handle-disk-full" => new IOException(PrivateMarker, unchecked((int)0x80070027)),
            "access" => new UnauthorizedAccessException(PrivateMarker),
            "security" => new SecurityException(PrivateMarker),
            "native-access" => new IOException(PrivateMarker, unchecked((int)0x80070005)),
            "missing-file" => new FileNotFoundException(PrivateMarker, @"X:\PRIVATE_DIAGNOSTIC_MARKER\clip.mp4"),
            "missing-directory" => new DirectoryNotFoundException(PrivateMarker),
            "validation" => new InvalidDataException(PrivateMarker),
            "unsupported" => new NotSupportedException(PrivateMarker),
            _ => new IOException(PrivateMarker)
        };
        if (kind == "size") error.Data["wisp-export-reason"] = "export-size-limit";
        var report = ClipExportFailureReport.Create(error, Clip, ClipExportFormat.Compatible);
        using var document = JsonDocument.Parse(report.Details);
        Assert.Equal(category, document.RootElement.GetProperty("category").GetString());
        Assert.Contains(message, report.Message, StringComparison.Ordinal);
        Assert.EndsWith("The original clip is kept.", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClipExportFormat.Original, "original", "The clip could not be exported.")]
    [InlineData(ClipExportFormat.Compatible, "compatible", "The compatible copy could not be created.")]
    public void UnknownFailureGuidanceMatchesTheRequestedExport(ClipExportFormat format, string recordedFormat, string message)
    {
        var report = ClipExportFailureReport.Create(new InvalidOperationException(PrivateMarker), Clip, format);
        using var document = JsonDocument.Parse(report.Details);
        Assert.Equal(recordedFormat, document.RootElement.GetProperty("format").GetString());
        Assert.Equal("unknown", document.RootElement.GetProperty("category").GetString());
        Assert.StartsWith(message, report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateMarker, report.Details + report.Message, StringComparison.Ordinal);
    }

    private sealed class PrivateExceptionMarker() : Exception(PrivateMarker, new Exception(PrivateMarker))
    {
        public override string Message => throw new InvalidOperationException("Exception messages must not be read.");
    }

    private sealed class MustNotBeFormatted
    {
        public override string ToString() => throw new InvalidOperationException("Arbitrary diagnostic values must not be formatted.");
    }
}
