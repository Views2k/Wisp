using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;
using Wisp.Core;

namespace Wisp.UiReview;

internal static class ConnectionPanelReview
{
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        using var bindings = new BindingTrace();
        var app = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var failures = new List<string>();
        var captures = new List<string>();
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            app.Resources = loadResources();
            var ready = new ConnectionEvidence(false, false, true, true, true, true, false, true,
                true, false, true, 1, NativeGameplayVisibility.Visible, true, NativeAssistProviderStatus.Ready, 5602, "Ctrl + Shift + H");
            var states = new[]
            {
                ("ready", ready),
                ("no-game", ready with { HudRequested = false, GameRunning = false, TelemetryFresh = false }),
                ("no-data", ready with { HudRequested = false, TelemetryFresh = false, ReceivedPackets = false }),
                ("unsupported", ready with { HudRequested = false, NativeStatus = NativeAssistProviderStatus.UnsupportedBuild }),
                ("shortcut", ready with { HudRequested = false, ManuallyHidden = true }),
                ("zero-opacity", ready with { Opacity = 0 }),
                ("listener-failed", ready with { ListenerFailed = true }),
            };
            foreach (var (name, evidence) in states)
            {
                bindings.Phase = name;
                var panel = new ConnectionStatusPanel();
                panel.Update(ConnectionExplanation.Describe(evidence));
                Capture(panel, name);
                if (panel.CloseButton.ActualWidth < 24 || panel.HelpButton.ActualHeight < 30)
                    failures.Add(name + "/small-action");
            }
            foreach (var expanded in new[] { false, true })
            {
                bindings.Phase = "disclosure";
                var expander = new Expander
                {
                    Header = "More options",
                    IsExpanded = expanded,
                    Style = (Style)app.Resources["MoreOptionsStyle"],
                    Content = new TextBlock { Text = "Detailed adjustments stay available here.", TextWrapping = TextWrapping.Wrap }
                };
                Capture(expander, expanded ? "more-options-open" : "more-options-closed");
                if (expander.Template.FindName("Disclosure", expander) is not System.Windows.Controls.Primitives.ToggleButton toggle
                    || !Equals(toggle.IsChecked, expanded)) failures.Add("disclosure/state");
            }
        }
        finally { app.Shutdown(); }
        if (bindings.TotalCount > 0) failures.Add("binding-errors");
        File.WriteAllText(Path.Combine(output, "connection-panel-review.json"), JsonSerializer.Serialize(new
        {
            method = "Actual WPF controls and resources, detached software render; no live game or displayed windows.",
            captures,
            failures,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Connection panel: {captures.Count} captures; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void Capture(FrameworkElement element, string name)
        {
            var host = new Border { Background = (Brush)app.Resources["WindowBrush"], Padding = new Thickness(16), Child = element };
            host.Measure(new Size(402, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 402, host.DesiredSize.Height));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(402, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            captures.Add(name);
        }
    }
}
