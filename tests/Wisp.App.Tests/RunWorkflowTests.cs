using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunWorkflowTests
{
    [Fact]
    public void RecordStopLibraryAndComparisonSurviveAServiceAndViewModelRestart() => OnDispatcher(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.RunWorkflowTests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { SpeedUnit = SpeedUnit.KilometersPerHour };
            Guid baselineId;
            Guid revisedId;
            await using (var receiver = new TelemetryUdpReceiver())
            await using (var service = new RunRecordingService(receiver, directory))
            using (var model = new RunsViewModel(service, settings, Dispatcher.CurrentDispatcher))
            using (var sender = new UdpClient(AddressFamily.InterNetwork))
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, AvailablePort());
                await receiver.StartAsync(endpoint.Port, TestContext.Current.CancellationToken);
                await model.InitializeAsync();
                model.SetPageVisible(true);
                model.BeforeStart = PublishContext;
                RecordedRun? deliveredRun = null;
                service.RunSaved += run => deliveredRun = run;

                baselineId = await RecordRun(1000, 1, "Baseline", "Road tune", 1);
                revisedId = await RecordRun(4000, 2, "Revised", "Shorter gearing", 2);
                Assert.NotEqual(baselineId, revisedId);
                Assert.Equal(2, Directory.EnumerateFiles(directory, "*.wisprun").Count());
                Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
                Assert.True(receiver.IsRunning);

                async Task<Guid> RecordRun(int gameStart, int slope, string name, string tune, int expectedLibraryCount)
                {
                    await Send(gameStart - 100, 10, 1000);
                    await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == gameStart - 100);
                    model.RefreshStatus();
                    Assert.True(model.ToggleRecordingCommand.CanExecute(null));
                    await model.ToggleRecordingAsync();
                    Assert.True(model.IsRecording, model.Error);
                    Assert.False(model.CanManageLibrary);

                    for (var index = 0; index <= 20; index++)
                    {
                        await Send(gameStart + index * 100, 10 + index * slope, 1000 + index * 100);
                        if (index == 10) await Send(gameStart + index * 100, 10 + index * slope, 2050);
                    }
                    await WaitUntil(() => receiver.Latest?.GameTimestampMilliseconds == gameStart + 2000);
                    await model.ToggleRecordingAsync();
                    await WaitUntil(() => model.Library.Count == expectedLibraryCount && model.SelectedRun is not null &&
                        model.HasRun && !model.IsBusy && model.Metrics.Count > 0 && model.Charts.Count > 0 &&
                        model.SelectedRun.Id == model.Library[0].Id);
                    Assert.False(model.HasError, model.Error);
                    Assert.True(model.CanManageRun);
                    Assert.NotNull(deliveredRun);
                    Assert.Same(deliveredRun, typeof(RunsViewModel).GetField("_runA", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model));
                    Assert.Equal("2.0s", Metric(model, "Recorded time").Value);
                    Assert.Equal("0", Metric(model, "Telemetry gaps").Value);
                    Assert.Equal(slope == 1 ? "108.0 km/h" : "180.0 km/h", Metric(model, "Peak ground speed").Value);

                    var id = model.SelectedRun!.Id;
                    model.Name = name;
                    model.Tune = tune;
                    model.Notes = "Same car and tire calibration.";
                    await model.SaveDetailsAsync();
                    Assert.False(model.HasError, model.Error);
                    Assert.StartsWith("A · " + name, model.RunALabel);
                    var saved = await service.Store.LoadAsync(id);
                    AssertRecordedData(saved, gameStart, slope);
                    Assert.Equal(tune, saved.Tune);
                    return id;
                }

                void PublishContext() => service.UpdateContext(new(Stopwatch.GetTimestamp(), 2468,
                    DrivetrainType.RearWheelDrive, true, .35, .35));

                async Task Send(int gameTime, float speed, float rpm)
                {
                    PublishContext();
                    await sender.SendAsync(Packet(gameTime, speed, rpm), endpoint, TestContext.Current.CancellationToken);
                }
            }

            await using var reopenedReceiver = new TelemetryUdpReceiver();
            await using var reopenedService = new RunRecordingService(reopenedReceiver, directory);
            using var reopened = new RunsViewModel(reopenedService, settings, Dispatcher.CurrentDispatcher);
            await reopened.InitializeAsync();
            reopened.SetPageVisible(true);
            Assert.Equal(2, reopened.Library.Count);
            var baseline = Assert.Single(reopened.Library, item => item.Id == baselineId);
            var revised = Assert.Single(reopened.Library, item => item.Id == revisedId);
            Assert.Equal("Road tune", baseline.Tune);
            Assert.Equal("Shorter gearing", revised.Tune);
            reopened.SelectedRun = revised;
            await WaitUntil(() => reopened.HasRun && !reopened.IsBusy && reopened.Charts.Count > 0);
            Assert.False(reopened.HasError, reopened.Error);
            Assert.Equal("Revised", reopened.Name);
            Assert.Equal("Same car and tire calibration.", reopened.Notes);

            reopened.ComparisonChoice = baseline;
            Assert.True(reopened.CompareCommand.CanExecute(null));
            reopened.CompareCommand.Execute(null);
            await ComparisonReady();
            AssertComparison(reopened);

            reopened.RemoveComparisonCommand.Execute(null);
            await WaitUntil(() => !reopened.HasComparison && !reopened.IsBusy);
            Assert.True(reopened.ComparePreviousCommand.CanExecute(null));
            reopened.ComparePreviousCommand.Execute(null);
            await ComparisonReady();
            AssertComparison(reopened);
            Assert.Equal(revisedId, reopened.SelectedRun!.Id);
            AssertRecordedData(await reopenedService.Store.LoadAsync(baselineId), 1000, 1);
            AssertRecordedData(await reopenedService.Store.LoadAsync(revisedId), 4000, 2);

            Task ComparisonReady() => WaitUntil(() => reopened.HasComparison && !reopened.IsBusy &&
                reopened.Metrics.Any(metric => metric.Comparison is not null) &&
                reopened.Charts.SelectMany(panel => panel.Series).Any(series => series.Comparison));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    private static void AssertComparison(RunsViewModel model)
    {
        Assert.False(model.HasError, model.Error);
        Assert.StartsWith("A · Revised", model.RunALabel);
        Assert.StartsWith("B · Baseline", model.RunBLabel);
        var peak = Metric(model, "Peak ground speed");
        Assert.Equal("180.0 km/h", peak.Value);
        Assert.Equal("108.0 km/h (-72.0 km/h)", peak.Comparison);
        var average = Metric(model, "Average car speed");
        Assert.Equal("108.0 km/h", average.Value);
        Assert.Equal("72.0 km/h (-36.0 km/h)", average.Comparison);
        Assert.Contains("routes", model.ComparisonNote);
        var speed = Assert.Single(model.Charts);
        Assert.Equal("km/h", speed.Unit);
        Assert.Equal(180, Assert.Single(speed.Series, series => !series.Comparison && series.ColorIndex == 0).Points[^1].Value, 5);
        Assert.Equal(108, Assert.Single(speed.Series, series => series.Comparison && series.ColorIndex == 0).Points[^1].Value, 5);
    }

    private static void AssertRecordedData(RecordedRun run, int gameStart, int slope)
    {
        Assert.False(run.IsIncomplete);
        Assert.Equal(0, run.DroppedDatagrams);
        Assert.Equal(0, run.RejectedDatagrams);
        Assert.Equal(22, run.Samples.Length);
        Assert.Equal(2, run.Samples[^1].ElapsedSeconds, 6);
        Assert.All(run.Samples, sample =>
        {
            Assert.True(sample.IsDriving);
            Assert.Equal(0, sample.Segment);
            Assert.Equal(2468, sample.State.CarOrdinal);
            Assert.Null(sample.State.ReceivedTimestamp);
            Assert.Equal(.35, sample.FrontRadiusMeters);
            Assert.Equal(.35, sample.RearRadiusMeters);
            Assert.InRange(Math.Abs(sample.WheelSpeedMetersPerSecond!.Value - sample.State.GroundSpeedMetersPerSecond), 0, .00001);
        });
        var duplicate = run.Samples.Where(sample => sample.State.GameTimestampMilliseconds == gameStart + 1000).ToArray();
        Assert.Equal(2, duplicate.Length);
        Assert.Equal(duplicate[0].ElapsedSeconds, duplicate[1].ElapsedSeconds);
        Assert.Equal(2050, duplicate[1].State.EngineRpm);
        Assert.Equal(10 + 20 * slope, run.Samples[^1].State.GroundSpeedMetersPerSecond);
    }

    private static RunMetric Metric(RunsViewModel model, string label) => Assert.Single(model.Metrics, metric => metric.Label == label);

    private static byte[] Packet(int gameTime, float speed, float rpm)
    {
        var bytes = new byte[324];
        Write(0, 1);
        Write(4, gameTime);
        Write(8, BitConverter.SingleToInt32Bits(8000));
        Write(16, BitConverter.SingleToInt32Bits(rpm));
        Write(212, 2468);
        Write(224, 1);
        Write(228, 8);
        Write(256, BitConverter.SingleToInt32Bits(speed));
        Write(260, BitConverter.SingleToInt32Bits(150000));
        Write(264, BitConverter.SingleToInt32Bits(300));
        for (var offset = 100; offset <= 112; offset += 4) Write(offset, BitConverter.SingleToInt32Bits(speed / .35f));
        for (var offset = 268; offset <= 280; offset += 4) Write(offset, BitConverter.SingleToInt32Bits(212));
        bytes[315] = 255;
        bytes[319] = 3;
        return bytes;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static int AvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4;
        while (!ready() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(ready(), "The real recording workflow did not reach the expected state within four seconds.");
    }

    private static void OnDispatcher(Func<Task> test)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); finished.Set(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20)), "Recording workflow exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
