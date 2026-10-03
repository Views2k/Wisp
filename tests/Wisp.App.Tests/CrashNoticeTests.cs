using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CrashNoticeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredExitNoticeDistinguishesUnconfirmedExitFromWindowsCrash(bool matched)
    {
        var marker = WindowsFaultEventReaderTests.Marker();
        var detected = marker.StartedAtUtc.AddMinutes(5);
        var report = marker.ToReport(detected) with
        {
            Origin = matched ? CrashOrigin.WindowsApplicationFault : CrashOrigin.UnexpectedExit,
            RecoveredExit = new(marker.StartedAtUtc, detected,
                matched ? WindowsFaultLookupStatus.Matched : WindowsFaultLookupStatus.NoMatchingEvent,
                matched ? new(marker.StartedAtUtc.AddMinutes(1), 0xc0000005, null, "ntdll.dll", false) : null)
        };
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(report, _ => true);
        Assert.True(model.HasCrashReport);
        Assert.Equal(matched ? "Windows recorded a Wisp crash" : "Wisp did not finish normal shutdown", model.CrashReportTitle);
        if (!matched) Assert.Contains("cause is not established", model.CrashReportNotice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncRecoveryCannotReplaceANoticeAfterDismissalOrDeletion(bool dismiss)
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(CrashReportTests.Report(), _ => true);
        var generation = model.CrashReportNoticeGeneration;
        if (dismiss) model.DismissCrashReport();
        else model.ClearCrashReportAfterDeletion();
        Assert.False(model.TryInitializeRecoveredCrashReport(CrashReportTests.Report(), generation, _ => true));
        Assert.False(model.HasCrashReport);
    }

    [Theory]
    [InlineData(true, "Wisp stopped after an error")]
    [InlineData(false, "Wisp recorded a background error")]
    public void NoticeDistinguishesFatalAndContinuingEvents(bool terminating, string title)
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(CrashReportTests.Report(terminating), _ => true);
        Assert.True(model.HasCrashReport); Assert.Equal(title, model.CrashReportTitle);
        if (!terminating) Assert.Contains("does not mean Wisp crashed", model.CrashReportNotice, StringComparison.Ordinal);
        var details = model.CrashReportDetails;
        model.ReportCrashDetailsCopied(true);
        Assert.StartsWith("Details copied.", model.CrashReportCopyStatus, StringComparison.Ordinal);
        Assert.Equal(details, model.CrashReportDetails); Assert.True(model.HasCrashReport);
        model.ReportCrashDetailsCopied(false);
        Assert.Contains("clipboard is busy", model.CrashReportCopyStatus, StringComparison.Ordinal);
        Assert.Equal(details, model.CrashReportDetails);
    }

    [Fact]
    public void DismissalPersistsBeforeHidingAndCanBeRetried()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        var report = CrashReportTests.Report(); var accepted = false; Guid? acknowledged = null;
        model.InitializeCrashReport(report, id => { acknowledged = id; return accepted; });
        model.DismissCrashReport();
        Assert.Equal(report.Id, acknowledged); Assert.True(model.HasCrashReport);
        Assert.Contains("could not be dismissed", model.CrashReportCopyStatus, StringComparison.Ordinal);
        accepted = true; model.DismissCrashReport();
        Assert.False(model.HasCrashReport); Assert.Empty(model.CrashReportDetails); Assert.Empty(model.CrashReportCopyStatus);
    }

    [Fact]
    public void NoPendingReportLeavesExistingDashboardUntouched()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(null, _ => throw new InvalidOperationException());
        model.ReportCrashDetailsCopied(true); model.DismissCrashReport();
        Assert.False(model.HasCrashReport); Assert.Empty(model.CrashReportDetails); Assert.Empty(model.CrashReportCopyStatus);
    }

    [Fact]
    public void CompletedLogDeletionClearsTheNoticeWithoutAcknowledgingNewReports()
    {
        var model = new DiagnosticsViewModel(new AppSettings());
        model.InitializeCrashReport(CrashReportTests.Report(), _ => throw new InvalidOperationException());
        model.ClearCrashReportAfterDeletion();
        Assert.False(model.HasCrashReport); Assert.Empty(model.CrashReportDetails);
    }
}
