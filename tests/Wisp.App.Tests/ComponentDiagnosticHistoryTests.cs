using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.Clips;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ComponentDiagnosticHistoryTests
{
    [Fact]
    public void RepeatedReportsCannotEvictDifferentFailuresAndSnapshotsAreIndependent()
    {
        var history = new ComponentDiagnosticHistory();
        history.RecordPlaybackFailure(new LosslessMpvException("packet-queue-full"), "playing");
        var first = history.Snapshot();
        for (var index = 0; index < 100; index++)
            history.RecordPlaybackFailure(new LosslessMpvException("packet-queue-full"), "playing");
        Assert.Single(history.Snapshot());
        for (var index = 1; index <= 12; index++)
            history.RecordPlaybackFailure(new IOException("PRIVATE_MESSAGE", index), "playing");
        Assert.Single(first); Assert.Equal(ComponentDiagnosticHistory.MaximumReports, history.Snapshot().Length);
        first[0] = null!;
        Assert.All(history.Snapshot(), Assert.NotNull);
        history.RecordGenerated(DiagnosticComponent.Tune, new string('x', ComponentDiagnosticHistory.MaximumReportCharacters + 1));
        Assert.Equal(ComponentDiagnosticHistory.MaximumReports, history.Snapshot().Length);
        history.Clear(); Assert.Empty(history.Snapshot());
    }

    [Fact]
    public void PlaybackDetailsRejectArbitraryNativeCodesStagesAndMessages()
    {
        var history = new ComponentDiagnosticHistory();
        history.RecordPlaybackFailure(new LosslessMpvException("PRIVATE_NATIVE_PATH"), "PRIVATE_STAGE_PATH");
        history.RecordPlaybackFailure(new IOException("PRIVATE_EXCEPTION_PATH"), "playing");
        var reports = JsonSerializer.Serialize(history.Snapshot());
        Assert.DoesNotContain("PRIVATE_", reports, StringComparison.Ordinal);
        Assert.Contains("unrecognized", reports, StringComparison.Ordinal);
        Assert.Contains("IOException", reports, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledLoggingExportsCurrentIdentityAndCaughtFailureWithoutCrashFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "WispComponentReportsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var history = new ComponentDiagnosticHistory();
            history.RecordPlaybackFailure(new LosslessMpvException("packet-queue-full"), "playing");
            await using var logger = new DebugLogService(Path.Combine(root, "logs"), componentReports: history);
            var destination = Path.Combine(root, "debug.zip");
            Assert.True(await logger.ExportAsync(destination, "2.6.1"));
            Assert.False(logger.IsEnabled); Assert.False(Directory.Exists(Path.Combine(root, "logs")));
            using (var zip = ZipFile.OpenRead(destination))
            {
                using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
                var value = manifest.RootElement;
                Assert.Equal(typeof(App).Assembly.ManifestModule.ModuleVersionId, value.GetProperty("module_version_id").GetGuid());
                Assert.Equal(ApplicationVersionInfo.MachineVersion, value.GetProperty("current_build_version").GetString());
                Assert.Equal(Environment.Version.ToString(), value.GetProperty("runtime_version").GetString());
                Assert.Equal(Environment.OSVersion.Version.ToString(), value.GetProperty("windows_version").GetString());
                Assert.True(value.TryGetProperty("private_build_id", out _));
                Assert.Equal(1, value.GetProperty("component_reports").GetInt32());
                Assert.Equal(0, value.GetProperty("crash_reports").GetInt32());
                Assert.Contains("packet-queue-full", Read(zip, "component-reports.json"), StringComparison.Ordinal);
            }
            Assert.True(await logger.DeleteLocalLogsAsync()); Assert.Empty(history.Snapshot());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AutomaticNativeIdentityAcceptsVersionAndPlatformButRejectsUntrustedText()
    {
        var history = new HealthContextRecorder();
        history.RecordSample(new DebugHealthSample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            NativeGameVersion = "PRIVATE_PATH",
            NativeGamePlatform = (DiagnosticGamePlatform)999,
            NativeCompatibilityRevision = -1
        });
        var sample = Assert.Single(history.Snapshot().Samples);
        Assert.Null(sample.NativeGameVersion); Assert.Null(sample.NativeGamePlatform); Assert.Null(sample.NativeCompatibilityRevision);
        Assert.True(HealthContextRecorder.IsValidSnapshot(history.Snapshot()));
        Assert.Null(HealthContextRecorder.SafeGameVersion("1.2.3.4/PRIVATE_PATH"));
        Assert.Equal("6.440.853.0", HealthContextRecorder.SafeGameVersion("6.440.853.0"));
    }

    private static string Read(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
}
