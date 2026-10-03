using System.Runtime.InteropServices;
using System.Windows;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    protected void CopyCrashDetails_Click(object sender, RoutedEventArgs e)
    {
        var model = _controller.ViewModel;
        if (!model.HasCrashReport) return;
        try { Clipboard.SetText(model.CrashReportDetails); model.ReportCrashDetailsCopied(true); }
        catch (ExternalException) { model.ReportCrashDetailsCopied(false); }
    }

    protected void DismissCrashReport_Click(object sender, RoutedEventArgs e) => _controller.ViewModel.DismissCrashReport();
}
