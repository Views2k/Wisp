using Wisp.App.CrashDiagnostics;

namespace Wisp.App;

public sealed partial class DiagnosticsViewModel
{
    private CrashReport? _crashReport;
    private Func<Guid, bool>? _acknowledgeCrashReport;
    private string _crashReportCopyStatus = "";
    internal long CrashReportNoticeGeneration { get; private set; }
    public bool HasCrashReport => _crashReport is not null;
    public string CrashReportTitle => _crashReport?.Origin == CrashOrigin.UnexpectedExit
        ? "Wisp did not finish normal shutdown" : _crashReport?.Origin == CrashOrigin.WindowsApplicationFault
        ? "Windows recorded a Wisp crash" : _crashReport?.IsTerminating == true
        ? "Wisp stopped after an error" : "Wisp recorded a background error";
    public string CrashReportNotice => _crashReport?.Origin == CrashOrigin.UnexpectedExit
        ? "The previous session ended unexpectedly. No matching Windows crash event was confirmed, so the cause is not established."
        : _crashReport?.IsTerminating == true
        ? "A local report from the previous session is available. Copy its details when reporting the problem."
        : "A background operation reported an error in the previous session. This report does not mean Wisp crashed.";
    public string CrashReportDetails => _crashReport?.Format() ?? "";
    public string CrashReportCopyStatus => _crashReportCopyStatus;

    internal void InitializeCrashReport(CrashReport? report, Func<Guid, bool> acknowledge)
    {
        CrashReportNoticeGeneration++;
        _crashReport = report?.Sanitize();
        _acknowledgeCrashReport = acknowledge;
        _crashReportCopyStatus = "";
        NotifyCrashReport();
    }

    internal bool TryInitializeRecoveredCrashReport(CrashReport report, long expectedGeneration, Func<Guid, bool> acknowledge)
    {
        if (CrashReportNoticeGeneration != expectedGeneration) return false;
        InitializeCrashReport(report, acknowledge);
        return true;
    }

    public void ReportCrashDetailsCopied(bool copied)
    {
        if (_crashReport is null) return;
        _crashReportCopyStatus = copied
            ? "Details copied. You can include them when reporting this problem."
            : "The clipboard is busy. Try copying the details again.";
        OnPropertyChanged(nameof(CrashReportCopyStatus));
    }

    public void DismissCrashReport()
    {
        if (_crashReport is not { } report) return;
        if (_acknowledgeCrashReport?.Invoke(report.Id) != true)
        {
            _crashReportCopyStatus = "The report could not be dismissed. Try again.";
            OnPropertyChanged(nameof(CrashReportCopyStatus));
            return;
        }
        ClearCrashReportAfterDeletion();
    }

    internal void ClearCrashReportAfterDeletion()
    {
        CrashReportNoticeGeneration++;
        _crashReport = null; _crashReportCopyStatus = "";
        NotifyCrashReport();
    }

    private void NotifyCrashReport()
    {
        foreach (var property in new[] { nameof(HasCrashReport), nameof(CrashReportTitle), nameof(CrashReportNotice),
            nameof(CrashReportDetails), nameof(CrashReportCopyStatus) }) OnPropertyChanged(property);
    }
}
