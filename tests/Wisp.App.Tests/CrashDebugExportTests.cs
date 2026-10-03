using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Wisp.App.CrashDiagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CrashDebugExportTests
{
    [Fact]
    public async Task ExportIncludesSanitizedCrashHistoryWithDetailedLoggingOff()
    {
        using var fixture = new Fixture();
        var report = CrashReportTests.Report() with
        {
            Exceptions = [new("PRIVATE_TYPE", -1, ["C:\\PRIVATE_PATH", "private-person@example.invalid"])]
        };
        Assert.True(fixture.Crashes.TrySave(report));
        await using var service = fixture.Service();
        Assert.False(service.IsEnabled); Assert.True(service.HasLocalLogs);
        var destination = Path.Combine(fixture.Directory, "export.zip");
        Assert.True(await service.ExportAsync(destination, "2.6.1"));
        using var archive = ZipFile.OpenRead(destination);
        var text = Read(archive, "crash-reports.json");
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", text, StringComparison.Ordinal);
        using var crashes = JsonDocument.Parse(text);
        Assert.Equal(1, crashes.RootElement.GetArrayLength());
        Assert.Equal(report.Id, crashes.RootElement[0].GetProperty("id").GetGuid());
        using var manifest = JsonDocument.Parse(Read(archive, "manifest.json"));
        Assert.Equal(1, manifest.RootElement.GetProperty("crash_reports").GetInt32());
        Assert.True(manifest.RootElement.GetProperty("crash_reports_available").GetBoolean());
    }

    [Fact]
    public async Task DeleteClearsCrashHistoryAndPreservesNewReportsAndExportedCopies()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Crashes.TrySave(CrashReportTests.Report()));
        await using var service = fixture.Service();
        var destination = Path.Combine(fixture.Directory, "export.zip");
        Assert.True(await service.ExportAsync(destination, "2.6.1"));
        Assert.True(await service.DeleteLocalLogsAsync());
        Assert.Null(fixture.Crashes.LatestPending()); Assert.False(service.HasLocalLogs);
        Assert.True(File.Exists(destination));
        var newer = CrashReportTests.Report();
        Assert.True(fixture.Crashes.TrySave(newer));
        Assert.Equal(newer.Id, fixture.Crashes.LatestPending()!.Id);
        Assert.True(service.HasLocalLogs);
    }

    [Fact]
    public async Task LockedCrashFileMakesDeleteReportFailure()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Crashes.TrySave(CrashReportTests.Report()));
        await using var service = fixture.Service();
        using (var held = new FileStream(Path.Combine(fixture.CrashDirectory, "reports.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(await service.DeleteLocalLogsAsync());
            Assert.True(held.Length > 0);
        }
        Assert.NotNull(fixture.Crashes.LatestPending());
        Assert.True(await service.DeleteLocalLogsAsync());
    }

    [Fact]
    public async Task InvalidCrashHistoryIsExcludedAndMarkedUnavailable()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.CrashDirectory);
        File.WriteAllText(Path.Combine(fixture.CrashDirectory, "reports.json"), "PRIVATE_INVALID_JSON");
        await using var service = fixture.Service();
        var destination = Path.Combine(fixture.Directory, "export.zip");
        Assert.True(await service.ExportAsync(destination, "2.6.1"));
        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal("[]", Read(archive, "crash-reports.json"));
        using var manifest = JsonDocument.Parse(Read(archive, "manifest.json"));
        Assert.False(manifest.RootElement.GetProperty("crash_reports_available").GetBoolean());
    }

    [Fact]
    public async Task IsolatedLoggerDoesNotDiscoverANearbyUninjectedCrashStore()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Crashes.TrySave(CrashReportTests.Report()));
        await using var service = new DebugLogService(Path.Combine(fixture.Directory, "DebugLogs"), tachSnapshot: () => null);
        Assert.False(service.HasLocalLogs);
        var destination = Path.Combine(fixture.Directory, "isolated.zip");
        Assert.True(await service.ExportAsync(destination, "2.6.1"));
        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal("[]", Read(archive, "crash-reports.json"));
        Assert.True(await service.DeleteLocalLogsAsync());
        Assert.NotNull(fixture.Crashes.LatestPending());
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispCrashExportTests", Guid.NewGuid().ToString("N"));
        internal string CrashDirectory => Path.Combine(Directory, "CrashReports");
        internal CrashReportStore Crashes { get; }
        internal Fixture() => Crashes = new(CrashDirectory);
        internal DebugLogService Service() => new(Path.Combine(Directory, "DebugLogs"), tachSnapshot: () => null, crashReports: Crashes);
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
