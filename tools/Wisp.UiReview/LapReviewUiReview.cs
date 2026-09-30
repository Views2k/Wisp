using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.UiReview;

internal static class LapReviewUiReview
{
    internal static int Run(string output, Func<ResourceDictionary> loadResources)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        using var bindings = new BindingTrace();
        var failures = new List<string>(); var captures = new List<CaptureReport>();
        var application = new ResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            application.Resources = loadResources();
            LapReviewUiChecks.Run(Path.Combine(output, "unused-synthetic-run-store"),
                (condition, code) => { if (!condition) failures.Add(code); }, Capture);
        }
        catch (Exception error) { failures.Add(error.GetType().Name); }
        finally { application.Shutdown(); }
        if (bindings.TotalCount != 0) failures.Add("binding-diagnostics");
        File.WriteAllText(Path.Combine(output, "lap-review.json"), JsonSerializer.Serialize(new
        {
            input = "Two in-memory synthetic completed laps in the actual Runs page. No controller, shown window, UDP listener, game inspection, settings load or saved-run access.",
            method = "Detached 96-DPI software rendering; actual popup templates and item styles; routed keyboard events without OS input. Does not establish displayed focus or hover behavior.",
            captures,
            failures,
            bindingDiagnosticCount = bindings.TotalCount,
            bindingDiagnostics = bindings.Messages
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Lap review: {(failures.Count == 0 ? "PASS" : "FAIL")}; {captures.Count} offscreen images; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 2;

        void Capture(string name, FrameworkElement surface, Size size)
        {
            bindings.Phase = name;
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = new FileStream(Path.Combine(output, name + ".png"), FileMode.CreateNew, FileAccess.Write)) encoder.Save(file);
            var inspection = ReviewDiagnostics.Inspect(surface, name + ".png", "synthetic-laps", "runs", name,
                96, (int)size.Width, (int)size.Height, 0);
            captures.Add(inspection);
            if (inspection.Labels.OverflowCount > 0) failures.Add(name + "/text-overflow");
        }
    }
    private sealed class ResourceApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
}
