using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunImageExporterTests
{
    [Fact]
    public void ExportsARealOffscreenGraphAndKeepsExistingFilesIntact() => OnSta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.ImageExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var run = ExampleRun();
            var report = RunAnalysis.BuildReport(run);
            var snapshot = new RunImageSnapshot("Example run", null, "Full recording · 0–2 seconds", report.QualityNote,
                report.Findings.ToArray(), RunPresentation.Metrics(report, null, SpeedUnit.MilesPerHour, TorqueUnit.NewtonMeters),
                RunPresentation.Charts(run, null, RunChartGroup.Speed, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit), [], 0, 2);
            var image = RunImageExporter.Render(snapshot);
            Assert.True(image.IsFrozen);
            Assert.Equal(1280, image.PixelWidth);
            Assert.InRange(image.PixelHeight, 500, RunImageExporter.MaximumImageHeight);
            // The lower section must contain the actual accent graph, not just a blank card or header text.
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            var accentPixels = 0;
            for (var y = Math.Max(0, image.PixelHeight - 350); y < image.PixelHeight - 100; y++)
                for (var x = 110; x < 1180; x++)
                {
                    var index = (y * image.PixelWidth + x) * 4;
                    if (pixels[index + 1] > 180 && pixels[index + 2] < 150 && pixels[index] > 150) accentPixels++;
                }
            Assert.True(accentPixels > 50, "The exported graph did not render its recorded speed line.");
            var path = Path.Combine(directory, "report.png");
            RunImageExporter.WriteAsync(image, path, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            var original = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, original[..8]);
            Assert.Throws<IOException>(() => RunImageExporter.WriteAsync(image, path, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
            Assert.Equal(original, File.ReadAllBytes(path));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => RunImageExporter.WriteAsync(image, Path.Combine(directory, "canceled.png"), canceled.Token).GetAwaiter().GetResult());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void AlternativeComparisonAndLongLabelsStayWithinTheImageBudget() => OnSta(() =>
    {
        var a = ExampleRun(); var b = ExampleRun();
        var report = RunAnalysis.BuildReport(a);
        foreach (var mode in new[] { RunPlotMode.PowerByRpm, RunPlotMode.GForce, RunPlotMode.TireChange })
        {
            var panels = RunAlternativePlots.Build(a, b, mode, TireTemperatureUnit.Celsius, TorqueUnit.PoundFeet, fullThrottleOnly: false);
            var image = RunImageExporter.Render(new RunImageSnapshot(new string('A', 120), new string('B', 120), "Selected interval · 0–2 seconds",
                report.QualityNote, report.Findings.ToArray(), RunPresentation.Metrics(report, report, SpeedUnit.KilometersPerHour, TorqueUnit.PoundFeet),
                [], panels, 0, 2));
            Assert.InRange(image.PixelHeight, 500, RunImageExporter.MaximumImageHeight);
            Assert.True(image.IsFrozen);
        }
    });

    [Fact]
    public void AllWorkspaceGraphTypesFitOneBoundedExport() => OnSta(() =>
    {
        var run = ExampleRun();
        var report = RunAnalysis.BuildReport(run);
        var time = new[] { RunChartGroup.Speed, RunChartGroup.Inputs, RunChartGroup.Engine, RunChartGroup.Tires, RunChartGroup.Handling }
            .SelectMany(group => RunPresentation.Charts(run, run, group, SpeedUnit.MilesPerHour, TireTemperatureUnit.Fahrenheit))
            .DistinctBy(panel => panel.Title).ToArray();
        var alternative = new[] { RunPlotMode.PowerByRpm, RunPlotMode.GForce, RunPlotMode.TireChange }
            .SelectMany(mode => RunAlternativePlots.Build(run, run, mode, TireTemperatureUnit.Fahrenheit, TorqueUnit.NewtonMeters, fullThrottleOnly: false)).ToArray();
        Assert.Equal(RunImageExporter.MaximumPlots, time.Length + alternative.Length);
        var snapshot = new RunImageSnapshot("Baseline", "Revised", "Whole run", report.QualityNote,
            report.Findings.ToArray(), RunPresentation.Metrics(report, report, SpeedUnit.MilesPerHour, TorqueUnit.NewtonMeters), time, alternative, 0, 2);
        var image = RunImageExporter.Render(snapshot);
        Assert.True(image.IsFrozen);
        Assert.InRange(image.PixelHeight, 4096, RunImageExporter.MaximumImageHeight);
        Assert.Throws<ArgumentException>(() => RunImageExporter.Render(snapshot with { Charts = [.. time, time[0]] }));
    });

    private static RecordedRun ExampleRun() => new()
    {
        Samples = Enumerable.Range(0, 201).Select(index =>
        {
            var sample = RunPresentationTests.Sample(index / 100d, 1000 + index * 20);
            return sample with { State = sample.State with { GroundSpeedMetersPerSecond = index / 10f } };
        }).ToArray()
    };

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Offscreen image export exceeded its bounded deadline.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
