using System.Globalization;
using System.IO;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunCsvExporterTests
{
    [Fact]
    public async Task RawCsvPreservesSourceValuesDuplicatesAndMissingCellsWithoutPrivateMetadata()
    {
        var directory = TemporaryDirectory(); var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = RunPresentationTests.Sample(.1, 4000) with
            {
                RearRadiusMeters = .333,
                WheelSpeedMetersPerSecond = 12.5,
                State = RunPresentationTests.Sample(.1, 4000).State with { BoostPressurePsi = -12, PowerWatts = float.NaN, Steering = -128, NumCylinders = 8 }
            };
            var second = first with
            {
                IsDriving = false,
                Segment = 1,
                RearRadiusMeters = null,
                WheelSpeedMetersPerSecond = null,
                State = first.State with { NumCylinders = 0, EngineRpm = 0, TireTemperatureFahrenheit = new(float.NaN, 212, 212, 212) }
            };
            var run = new RecordedRun { Name = "=FORMULA()", Tune = "+COMMAND", Notes = "private note", Samples = [first, second] };
            var path = Path.Combine(directory, "raw.csv");
            await RunCsvExporter.WriteAsync(run, path, TestContext.Current.CancellationToken);
            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(3, lines.Length); var headers = lines[0].Split(','); Assert.Equal(43, headers.Length);
            var a = headers.Zip(lines[1].Split(',')).ToDictionary(pair => pair.First, pair => pair.Second);
            var b = headers.Zip(lines[2].Split(',')).ToDictionary(pair => pair.First, pair => pair.Second);
            Assert.Equal("0.1", a["time_s"]); Assert.Equal(a["time_s"], b["time_s"]);
            Assert.Equal("12.5", a["wheel_speed_mps"]); Assert.Equal("", b["wheel_speed_mps"]);
            Assert.Equal("", a["power_w"]); Assert.Equal("", b["tire_temperature_fl_f"]);
            Assert.Equal("-12", a["boost_psi"]); Assert.Equal("-12", b["boost_psi"]);
            Assert.Equal("1", a["is_driving"]); Assert.Equal("0", b["is_driving"]); Assert.Equal("1", b["segment"]);
            Assert.Equal("-128", a["steering_raw"]); Assert.Equal("0", b["num_cylinders"]);
            foreach (var line in lines.Skip(1))
            {
                var columns = line.Split(','); Assert.Equal(headers.Length, columns.Length);
                Assert.All(columns.Where(value => value.Length > 0), value => Assert.True(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
            }
            Assert.DoesNotContain("private", string.Join('\n', lines)); Assert.DoesNotContain("FORMULA", string.Join('\n', lines));
            Assert.DoesNotContain(headers, header => header.Contains("utc") || header.Contains("received"));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ExistingFileAndConcurrentExportAreNeverOverwritten()
    {
        var directory = TemporaryDirectory();
        try
        {
            var run = new RecordedRun { Samples = [RunPresentationTests.Sample(0, 4000)] };
            var path = Path.Combine(directory, "run.csv");
            var tasks = new[] { RunCsvExporter.WriteAsync(run, path, TestContext.Current.CancellationToken), RunCsvExporter.WriteAsync(run, path, TestContext.Current.CancellationToken) };
            var outcomes = await Task.WhenAll(tasks.Select(async task => { try { await task; return true; } catch (IOException) { return false; } }));
            Assert.Single(outcomes, success => success);
            var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => RunCsvExporter.WriteAsync(new RecordedRun(), path, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledExportCannotLeaveAPartialFinalFile()
    {
        var directory = TemporaryDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCsvExporter.WriteAsync(new RecordedRun(), Path.Combine(directory, "run.csv"), cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WispCsvTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
}
