using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class StartupTrayIconTests
{
    [Fact]
    public void MenuHeadersUseTheWindowsMenuTextColor()
    {
        Color? actual = null;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var header = StartupTrayIcon.CreateMenuHeader("Open Wisp");
                Assert.Equal("Open Wisp", header.Text);
                actual = Assert.IsType<SolidColorBrush>(header.Foreground).Color;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        thread.Join();

        Assert.Null(failure);
        Assert.Equal(SystemColors.MenuTextColor, actual);
    }
}
