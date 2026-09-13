using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wisp.App;

public abstract partial class ControlPanelWindow
{
    private Popup? _connectionPopup;

    protected void ConnectionStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionPopup is { IsOpen: true }) { CloseConnectionPanel(); return; }
        var anchor = (FrameworkElement)sender;
        var panel = new ConnectionStatusPanel();
        panel.Resources.MergedDictionaries.Add(Resources);
        panel.Update(_controller.GetConnectionReport());
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 8,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = panel,
        };
        _connectionPopup = popup;
        var restoreFocus = false;
        void Update(ConnectionReport report) => panel.Update(report);
        _controller.ConnectionReportChanged += Update;
        panel.CloseRequested += (_, _) => { restoreFocus = true; CloseConnectionPanel(); };
        panel.HelpRequested += (_, _) =>
        {
            var setup = (panel.DataContext as ConnectionReport)?.ShowSetup == true;
            CloseConnectionPanel();
            if (setup) OpenSetup_Click(sender, e);
            else OpenDiagnostics_Click(sender, e);
        };
        panel.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            restoreFocus = true;
            CloseConnectionPanel();
        };
        popup.Closed += (_, _) =>
        {
            _controller.ConnectionReportChanged -= Update;
            _connectionPopup = null;
            if (restoreFocus && IsActive && anchor.IsVisible) anchor.Focus();
        };
        popup.Opened += (_, _) => panel.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (popup.IsOpen) panel.CloseButton.Focus();
        }));
        popup.IsOpen = true;
    }

    private void CloseConnectionPanel()
    {
        if (_connectionPopup is { } popup) popup.IsOpen = false;
    }
}
