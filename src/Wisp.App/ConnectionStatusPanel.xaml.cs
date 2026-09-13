using System.Windows;
using System.Windows.Controls;

namespace Wisp.App;

public partial class ConnectionStatusPanel : UserControl
{
    public ConnectionStatusPanel() => InitializeComponent();
    public event EventHandler? CloseRequested;
    public event EventHandler? HelpRequested;

    internal void Update(ConnectionReport report)
    {
        DataContext = report;
        HelpButton.Content = report.ShowSetup ? "Connection instructions" : "Open Diagnostics";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Help_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);
}
