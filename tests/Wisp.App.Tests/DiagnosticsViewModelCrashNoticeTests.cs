using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

// Named after the DiagnosticsViewModel.CrashReports partial so the pull request gate runs it with that file.
public sealed class DiagnosticsViewModelCrashNoticeTests
{
    [Theory]
    [InlineData(false, false, "Wisp did not finish normal shutdown")]
    [InlineData(true, false, "Windows stopped while Wisp was running")]
    [InlineData(false, true, null)]
    public void OnlyARunThatEndedUnexpectedlyGetsACard(bool stopped, bool shutDown, string? title)
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(RecoveredReport(stopped, shutDown), _ => throw new InvalidOperationException());
        Assert.Equal(title is not null, model.HasCrashReport);
        if (title is not null) { Assert.Equal(title, model.CrashReportTitle); return; }
        model.ReportCrashDetailsCopied(true); model.DismissCrashReport();
        Assert.False(model.HasCrashReport); Assert.Empty(model.CrashReportDetails); Assert.Empty(model.CrashReportCopyStatus);
    }

    [Fact]
    public void ARecoveredNormalWindowsShutdownLeavesTheDashboardUntouched()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(null, _ => true);
        var generation = model.CrashReportNoticeGeneration;
        Assert.False(model.TryInitializeRecoveredCrashReport(RecoveredReport(shutDown: true), generation, _ => true));
        Assert.False(model.HasCrashReport); Assert.Equal(generation, model.CrashReportNoticeGeneration);
        model.InitializeCrashReport(CrashReportTests.Report(), _ => true);
        generation = model.CrashReportNoticeGeneration; var details = model.CrashReportDetails;
        Assert.False(model.TryInitializeRecoveredCrashReport(RecoveredReport(shutDown: true), generation, _ => true));
        Assert.True(model.HasCrashReport); Assert.Equal(details, model.CrashReportDetails);
        Assert.True(model.TryInitializeRecoveredCrashReport(RecoveredReport(stopped: true), generation, _ => true));
        Assert.Equal("Windows stopped while Wisp was running", model.CrashReportTitle);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ARunThatEndedOnASleepingOrLockedPcGetsNoCard(bool suspended, bool locked)
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        var report = RecoveredReport();
        report = report with { RecoveredExit = report.RecoveredExit! with { Suspended = suspended, SessionLocked = locked } };
        model.InitializeCrashReport(report, _ => throw new InvalidOperationException());
        Assert.False(model.HasCrashReport);
        Assert.False(model.TryInitializeRecoveredCrashReport(report, model.CrashReportNoticeGeneration, _ => true));
        Assert.False(model.HasCrashReport);
    }

    private static CrashReport RecoveredReport(bool stopped = false, bool shutDown = false)
    {
        var marker = WindowsFaultEventReaderTests.Marker();
        var detected = marker.StartedAtUtc.AddMinutes(5);
        return marker.ToReport(detected) with
        {
            RecoveredExit = new(marker.StartedAtUtc, detected, WindowsFaultLookupStatus.NoMatchingEvent, null)
            { WindowsStopped = stopped, WindowsShutDown = shutDown }
        };
    }
}
