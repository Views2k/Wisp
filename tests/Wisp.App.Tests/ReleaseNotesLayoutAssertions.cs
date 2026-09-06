using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

internal static class ReleaseNotesLayoutAssertions
{
    internal static void Verify(MainWindow window, FrameworkElement surface, TabControl tabs)
    {
        tabs.SelectedItem = window.FindName("ReleaseNotesTab");
        foreach (var size in new[] { new Size(720, 440), new Size(980, 750), new Size(1440, 900) })
        {
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            surface.UpdateLayout();
            var versions = Descendants(surface).OfType<TextBlock>()
                .Where(text => text.Name == "ReleaseVersionText").ToArray();
            Assert.Equal(ReleaseNotesCatalog.Entries.Count, versions.Length);
            foreach (var version in versions)
            {
                Assert.Equal(TextWrapping.Wrap, version.TextWrapping);
                var natural = new TextBlock
                {
                    Text = version.Text,
                    FontFamily = version.FontFamily,
                    FontSize = version.FontSize,
                    FontWeight = version.FontWeight,
                    FontStyle = version.FontStyle,
                    FontStretch = version.FontStretch
                };
                natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(version.ActualWidth >= natural.DesiredSize.Width - 1,
                    $"{version.Text} is clipped at {size.Width}: {version.ActualWidth} < {natural.DesiredSize.Width}.");
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
