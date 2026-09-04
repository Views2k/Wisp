using Xunit;

namespace Wisp.App.Tests;

public sealed class OverlayInputContractTests
{
    [Theory]
    [InlineData("OverlayWindow.xaml.cs")]
    [InlineData("GForceWindow.xaml.cs")]
    public void OverlayWindowsUseSharedNonActivatingDrag(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), fileName));

        Assert.Contains("NonActivatingWindowDrag", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DragMove(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDragNeverUsesAnActivatingWindowOperation()
    {
        var source = File.ReadAllText(
            Path.Combine(AppSourceDirectory(), "NonActivatingWindowDrag.cs"));

        Assert.Contains("OverlayActivationPolicy.TryHandleWindowMessage", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(", source, StringComparison.Ordinal);
        Assert.Contains("NoActivate | NoOwnerZOrder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Activate(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DragMove(", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BoostGaugeWindow.xaml.cs")]
    [InlineData("TireTemperatureGaugeWindow.xaml.cs")]
    public void DetachedGaugeRestoreEstablishesMonitorBeforeClamping(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), fileName));
        var restoreStart = source.IndexOf("public void RestorePosition", StringComparison.Ordinal);
        var restoreEnd = source.IndexOf("public bool OwnsWindowHandle", restoreStart, StringComparison.Ordinal);
        var restore = source[restoreStart..restoreEnd];

        Assert.Contains("EnsureHandle()", restore, StringComparison.Ordinal);
        Assert.True(
            restore.IndexOf("Left = left;", StringComparison.Ordinal) <
            restore.IndexOf("CurrentMonitorWorkArea()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OverlayWindow.xaml")]
    [InlineData("GForceWindow.xaml")]
    public void OverlayWindowsAreNotGlobalTopmost(string fileName)
    {
        var xaml = File.ReadAllText(Path.Combine(AppSourceDirectory(), fileName));

        Assert.Contains("Topmost=\"False\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Topmost=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayZOrderIsOwnedByForzaAndNeverUsesTheGlobalTopmostBand()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "WindowZOrder.cs"));

        Assert.Contains("helper.Owner = gameWindow", source, StringComparison.Ordinal);
        Assert.Contains("window.Topmost = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new(-1)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureTopmost", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TelemetryMinimizeAssignmentIsGuardedByTransitionDecision()
    {
        var source = File.ReadAllText(Path.Combine(AppSourceDirectory(), "AppController.cs"));
        const string guardedAssignment =
            "if (autoMinimizeTransition.ShouldMinimizeControlPanel && ControlPanel is not null)";

        Assert.Contains(guardedAssignment, source, StringComparison.Ordinal);
        Assert.Contains("ControlPanel.WindowState = WindowState.Minimized;", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split("WindowState = WindowState.Minimized", StringSplitOptions.None).Length - 1);
    }

    private static string AppSourceDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "Wisp.App");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }
}
