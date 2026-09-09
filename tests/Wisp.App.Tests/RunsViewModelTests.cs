using System.Runtime.ExceptionServices;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunsViewModelTests
{
    [Fact]
    public void GraphNavigationPreservesTheReviewAndNewRunsReturnToSummary() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        model.ShowGraphs();
        Assert.True(model.IsSummaryVisible); Assert.False(model.IsGraphWorkspaceOpen);
        await model.ShowReviewAsync(Run("Baseline", 0, 4), Run("Revised", 0, 4));
        model.SelectInterval(.5, 1.5); await Ready(model);
        model.SelectedGraph = model.GraphChoices.Single(choice => choice.Mode == RunPlotMode.PowerByRpm);
        await Ready(model);
        var metrics = model.Metrics.ToArray();
        model.ShowGraphs(); Assert.True(model.IsGraphWorkspaceOpen); Assert.False(model.IsSummaryVisible);
        model.ShowSummary(); Assert.True(model.IsSummaryVisible);
        model.ShowGraphs();
        Assert.Equal(RunPlotMode.PowerByRpm, model.SelectedGraph.Mode);
        Assert.True(model.HasComparison); Assert.True(model.HasSelection);
        Assert.Equal(metrics, model.Metrics.ToArray());
        await model.ShowReviewAsync(Run("Next run", 0, 3));
        Assert.True(model.IsSummaryVisible); Assert.False(model.IsGraphWorkspaceOpen);
        Assert.False(model.HasComparison); Assert.False(model.HasSelection);
    });

    [Fact]
    public void IdleWithoutTelemetryExplainsWhyRecordingIsUnavailable() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        model.RefreshStatus();
        Assert.False(model.CanToggleRecording);
        Assert.False(model.ToggleRecordingCommand.CanExecute(null));
        Assert.Contains("Open Forza and return to free roam", model.RecordingStatus);
        Assert.DoesNotContain("Ready to record", model.RecordingStatus);
        Assert.False(service.Start());
        Assert.Equal(service.Status, model.RecordingStatus);
    });

    [Fact]
    public void ADisplayedReportCannotBeRelabeledWhileRecordingOrCountingDown() => OnDispatcher(async () =>
    {
        await WithLiveReceiver(async (receiver, refreshTelemetry) =>
        {
            await using var service = new RunRecordingService(receiver, TemporaryDirectory());
            var clock = new ManualClock();
            using var model = new RunsViewModel(service, new AppSettings { RecordingCountdownSeconds = 3 }, Dispatcher.CurrentDispatcher, clock);
            model.BeforeStart = () => service.UpdateContext(new(Stopwatch.GetTimestamp(), 1, DrivetrainType.RearWheelDrive, true));
            await model.ShowReviewAsync(Run("Original A", 0, 2), Run("Original B", 0, 3));
            model.ChartGroup = RunChartGroup.Engine; model.GraphView = model.AvailableGraphViews[1]; await Ready(model);
            var metrics = model.Metrics.ToArray(); var runId = model.SelectedRun!.Id; var comparison = model.ComparisonChoice;
            await refreshTelemetry(); await model.ToggleRecordingAsync(); Assert.True(model.IsCountingDown);
            AssertUnchanged();
            await refreshTelemetry(); clock.Advance(TimeSpan.FromSeconds(3)); model.RefreshStatus(); Assert.True(model.IsRecording, model.Error);
            AssertUnchanged();

            void AssertUnchanged()
            {
                Assert.False(model.RemoveComparisonCommand.CanExecute(null)); Assert.False(model.ApplyIntervalCommand.CanExecute(null));
                Assert.False(model.ApplySpeedRangeCommand.CanExecute(null)); Assert.False(model.CanManageRun);
                model.RemoveComparisonCommand.Execute(null); model.SelectInterval(.5, 1.5); model.SameSpeed = true;
                model.Purpose = RunPurpose.Drifting; model.FromSpeed = "5"; model.ToSpeed = "15";
                model.SelectionFrom = "7"; model.SelectionTo = "8";
                model.Name = "Changed"; model.Tune = "Changed"; model.Notes = "Changed";
                model.SelectedRun = model.Library.Single(item => item.Id != runId); model.ComparisonChoice = model.SelectedRun;
                model.GraphView = model.AvailableGraphViews[0]; model.ChartGroup = RunChartGroup.Handling;
                model.SelectedGraph = model.GraphChoices[0];
                model.FullThrottleOnly = false; model.GearFilter = model.GearOptions[1];
                Assert.True(model.HasComparison); Assert.False(model.HasSelection); Assert.False(model.SameSpeed);
                Assert.Equal(RunPurpose.General, model.Purpose); Assert.Equal("20", model.FromSpeed); Assert.Equal("60", model.ToSpeed);
                Assert.Equal("0", model.SelectionFrom); Assert.Equal("0", model.SelectionTo);
                Assert.Equal("Original A", model.Name); Assert.Empty(model.Tune); Assert.Empty(model.Notes);
                Assert.Equal(runId, model.SelectedRun!.Id); Assert.Equal(comparison, model.ComparisonChoice);
                Assert.Equal(RunChartGroup.Engine, model.ChartGroup); Assert.Equal(RunPlotMode.PowerByRpm, model.GraphView.Mode);
                Assert.True(model.FullThrottleOnly); Assert.Null(model.GearFilter.Gear);
                Assert.Equal(metrics, model.Metrics.ToArray()); Assert.Equal("Whole run", model.IntervalLabel);
                Assert.All(model.Findings, finding => Assert.False(finding.ShowCommand.CanExecute(null)));
                model.CursorSeconds = .5; Assert.Equal(.5, model.CursorSeconds);
                Assert.True(model.ResetViewCommand.CanExecute(null)); model.ResetViewCommand.Execute(null); Assert.Equal(0, model.ViewStart);
            }
        });
    });

    [Fact]
    public void CountdownStartsAtItsDeadlineWithFreshContextAndKeepsStopAvailable() => OnDispatcher(async () =>
    {
        await WithLiveReceiver(async (receiver, refreshTelemetry) =>
        {
            var directory = TemporaryDirectory();
            await using var service = new RunRecordingService(receiver, directory);
            var clock = new ManualClock();
            using var model = new RunsViewModel(service, new AppSettings { RecordingCountdownSeconds = 3, RecordingStopAfterSeconds = 30 }, Dispatcher.CurrentDispatcher, clock);
            var starts = 0;
            model.BeforeStart = () => { starts++; service.UpdateContext(new(Stopwatch.GetTimestamp(), 1, DrivetrainType.RearWheelDrive, true)); };
            var arming = model.ToggleRecordingAsync();
            Assert.True(arming.IsCompleted); await arming;
            Assert.True(model.IsCountingDown); Assert.False(model.CanManageLibrary); Assert.False(model.CanEditRecordingOptions);
            Assert.True(model.ToggleRecordingCommand.CanExecute(null)); Assert.False(model.CanMarkMoment); Assert.Equal(0, starts);
            clock.Advance(TimeSpan.FromSeconds(2.9)); model.RefreshStatus();
            Assert.Equal(1, model.CountdownRemainingSeconds); Assert.False(model.IsRecording);
            await refreshTelemetry(); clock.Advance(TimeSpan.FromSeconds(.1)); model.RefreshStatus();
            Assert.False(model.IsCountingDown); Assert.True(model.IsRecording, model.Error); Assert.Equal(1, starts);
            Assert.True(model.ToggleRecordingCommand.CanExecute(null)); Assert.True(model.CanMarkMoment);
            Assert.Contains("stops at 30.0s", model.RecordingStatus);
            await refreshTelemetry(); model.RefreshStatus(); Assert.Equal(1, starts);
        });
    });

    [Theory]
    [InlineData("button")]
    [InlineData("suspend")]
    [InlineData("telemetry")]
    [InlineData("dispose")]
    public void CountdownCancellationNeverStartsARecording(string cause) => OnDispatcher(async () =>
    {
        await WithLiveReceiver(async (receiver, _) =>
        {
            var directory = TemporaryDirectory();
            await using var service = new RunRecordingService(receiver, directory);
            var clock = new ManualClock();
            using var model = new RunsViewModel(service, new AppSettings { RecordingCountdownSeconds = 3 }, Dispatcher.CurrentDispatcher, clock);
            var starts = 0; model.BeforeStart = () => starts++;
            await model.ToggleRecordingAsync(); Assert.True(model.IsCountingDown);
            switch (cause)
            {
                case "button": await model.ToggleRecordingAsync(); break;
                case "suspend": model.SuspendHotkeyStatus(); break;
                case "telemetry": await receiver.StopAsync(); model.RefreshStatus(); break;
                case "dispose": model.Dispose(); break;
            }
            clock.Advance(TimeSpan.FromSeconds(10)); model.RefreshStatus();
            Assert.False(model.IsCountingDown); Assert.False(model.IsRecording); Assert.Equal(0, starts);
            Assert.False(Directory.Exists(directory));
        });
    });

    [Fact]
    public void MarkerJumpUsesTheCorrectRunsMatchedOrigin() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        var a = Run("A", 3, 5); var b = Run("B", 7, 8);
        var factor = RunPresentation.SpeedFactor(SpeedUnit.MilesPerHour);
        var matchA = RunAnalysis.MeasureAcceleration(a, 20 / factor, 60 / factor)!;
        var matchB = RunAnalysis.MeasureAcceleration(b, 20 / factor, 60 / factor)!;
        a = a with { Markers = [new(.1, "Outside"), new(matchA.Interval.StartSeconds + .5, "A marker")] };
        b = b with { Markers = [new(matchB.Interval.StartSeconds + 1, "B marker")] };
        await model.ShowReviewAsync(a, b); model.SameSpeed = true; await Ready(model);
        Assert.Equal(2, model.Markers.Count); Assert.Equal(2, model.PlotMarkers.Length);
        Assert.Equal("A marker", Assert.Single(model.PlotMarkers, item => !item.Comparison).Label);
        Assert.Equal("B marker", Assert.Single(model.PlotMarkers, item => item.Comparison).Label);
        Assert.Equal("B · B marker", Assert.Single(model.Markers, item => item.Comparison).Label);
        var marker = Assert.Single(model.Markers, item => item.Comparison);
        Assert.Equal(1, marker.Seconds, 6); marker.JumpCommand.Execute(null);
        Assert.Equal(1, model.CursorSeconds, 6); Assert.InRange(model.CursorSeconds, model.ViewStart, model.ViewEnd);
        Assert.True(model.IsTimeGraph);
    });

    [Fact]
    public void AlternativePointPreservesTheSelectedDuplicateAndItsRunIdentity() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        var a = Run("A", 0, 1);
        var b = Run("B", 0, 1) with
        {
            Samples = [RunPresentationTests.Sample(0, 1000), RunPresentationTests.Sample(.1, 6200),
                RunPresentationTests.Sample(.1, 7000), RunPresentationTests.Sample(.2, 7500)]
        };
        await model.ShowReviewAsync(a, b);
        model.SelectAlternativePoint(new(true, .1, 1));
        Assert.Equal(.1, model.CursorSeconds);
        Assert.Contains("Selected point · B", model.SelectedPointContext);
        Assert.Contains(6200.ToString("N0") + " RPM", model.SelectedPointContext);
        Assert.DoesNotContain(7000.ToString("N0") + " RPM", model.SelectedPointContext);
        model.CursorSeconds = .2; Assert.Empty(model.SelectedPointContext);
        model.SelectAlternativePoint(new(true, .1, 1));
        await model.ShowReviewAsync(a); Assert.Empty(model.SelectedPointContext);
    });

    [Fact]
    public void GraphChoicesKeepTheLatestModeAndDoNotChangeReportStatistics() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        await model.ShowReviewAsync(Run("Engine", 0, 2));
        var before = model.Metrics.ToArray();
        model.ChartGroup = RunChartGroup.Engine;
        model.GraphView = model.AvailableGraphViews[1];
        model.FullThrottleOnly = false;
        model.GearFilter = model.GearOptions.Single(option => option.Gear == TransmissionGear.Third);
        model.ChartGroup = RunChartGroup.Handling;
        model.GraphView = model.AvailableGraphViews[1];
        await Ready(model);
        Assert.False(model.IsTimeGraph); Assert.False(model.IsRpmGraph);
        Assert.Equal(RunPlotMode.GForce, model.GraphView.Mode);
        Assert.NotEmpty(model.AlternativeCharts); Assert.Empty(model.Charts);
        Assert.Equal(before, model.Metrics.ToArray());
        model.ChartGroup = RunChartGroup.Speed; await Ready(model);
        Assert.True(model.IsTimeGraph); Assert.Single(model.AvailableGraphViews);
        Assert.Empty(model.AlternativeCharts); Assert.NotEmpty(model.Charts);
    });

    [Fact]
    public void ShowMeSelectsTheRelevantChannelsAndInterval() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        var run = Run("Throttle test", 0, 1);
        await model.ShowReviewAsync(run);
        var finding = Assert.Single(model.Findings, item => item.Title == "Longest full-throttle stretch");
        finding.ShowCommand.Execute(null); await Ready(model);
        Assert.Equal(RunChartGroup.Inputs, model.ChartGroup);
        Assert.True(model.HasSelection);
        Assert.Equal(model.SelectionStart, model.ViewStart); Assert.Equal(model.SelectionEnd, model.ViewEnd);
        Assert.Contains(model.Charts, chart => chart.Unit == "%"); Assert.Contains(model.Charts, chart => chart.Unit == "mph");
        Assert.Contains("RPM", model.CursorVehicleContext); Assert.Contains("gear 3", model.CursorVehicleContext);
    });

    [Fact]
    public void MatchedIntervalsAndSelectionsUseEachRunsOwnOrigin() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        var a = Run("Baseline", 3, 5); var b = Run("Revised", 7, 8);
        await model.ShowReviewAsync(a, b, RunPurpose.Acceleration);
        model.SameSpeed = true; await Ready(model);
        Assert.True(model.SameSpeed);
        var expectedA = RunAnalysis.MeasureAcceleration(a, 20 / RunPresentation.SpeedFactor(SpeedUnit.MilesPerHour), 60 / RunPresentation.SpeedFactor(SpeedUnit.MilesPerHour))!;
        var expectedB = RunAnalysis.MeasureAcceleration(b, expectedA.FromMetersPerSecond, expectedA.ToMetersPerSecond)!;
        var duration = Assert.Single(model.Metrics, metric => metric.Label == "Recorded time");
        Assert.Equal(RunPresentation.Time(expectedA.DurationSeconds), duration.Value);
        Assert.StartsWith(RunPresentation.Time(expectedB.DurationSeconds) + " (", duration.Comparison);
        Assert.All(model.Charts[0].Series.Where(line => line.Comparison).SelectMany(line => line.Points), point =>
            Assert.InRange(point.Seconds, 0, expectedB.DurationSeconds));

        model.SelectInterval(.25, 1.25); await Ready(model);
        duration = Assert.Single(model.Metrics, metric => metric.Label == "Recorded time");
        Assert.Equal("1.0s", duration.Value); Assert.StartsWith("1.0s (", duration.Comparison);
        Assert.DoesNotContain(model.Findings, finding => finding.Title.Contains("interval is missing", StringComparison.Ordinal));
        var average = Assert.Single(model.Metrics, metric => metric.Label == "Average car speed");
        Assert.NotEqual(average.Value, average.Comparison);
    });

    [Fact]
    public void UnavailableRangeCanBeRetriedAndUnitsRemainCoherent() => OnDispatcher(async () =>
    {
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, TemporaryDirectory());
        var settings = new AppSettings();
        using var model = new RunsViewModel(service, settings, Dispatcher.CurrentDispatcher);
        await model.ShowReviewAsync(Run("A", 0, 1), Run("B", 1, 1.1));
        model.SameSpeed = true; await Ready(model);
        Assert.False(model.SameSpeed); Assert.Contains("unavailable", model.ComparisonNote);
        model.FromSpeed = "5"; model.ToSpeed = "15"; model.ApplySpeedRangeCommand.Execute(null); await Ready(model);
        Assert.True(model.SameSpeed);
        var before = model.ViewEnd;
        settings.SpeedUnit = SpeedUnit.KilometersPerHour; model.RefreshStatus(); await Ready(model);
        Assert.Equal("km/h", model.SpeedUnitText);
        Assert.InRange(Math.Abs(model.ViewEnd - before), 0, .01);
        Assert.Contains("km/h", Assert.Single(model.Metrics, metric => metric.Label == "Average car speed").Value);
    });

    [Fact]
    public void FailedSelectionRestoresTheDisplayedRunAndPreservesItsData() => OnDispatcher(async () =>
    {
        var directory = TemporaryDirectory();
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, directory);
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        try
        {
            var a = RunTestData.CreateRun() with { Name = "Retained run" };
            var b = RunTestData.CreateRun() with { Name = "Unreadable run" };
            await service.Store.SaveAsync(a); await service.Store.SaveAsync(b); await model.InitializeAsync();
            model.SelectedRun = model.Library.Single(item => item.Id == a.Id); await Ready(model);
            var metrics = model.Metrics.ToArray();
            var original = await File.ReadAllBytesAsync(Path.Combine(directory, $"{a.Id:N}.wisprun"), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{b.Id:N}.wisprun"), "damaged", TestContext.Current.CancellationToken);

            model.SelectedRun = model.Library.Single(item => item.Id == b.Id);
            await SelectionFailed(model);

            Assert.Equal(a.Id, model.SelectedRun!.Id);
            Assert.Equal("A · Retained run", model.RunALabel);
            Assert.Equal("Retained run", model.Name);
            Assert.Equal(metrics, model.Metrics.ToArray());
            Assert.True(model.CanManageRun);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, $"{a.Id:N}.wisprun"), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void FailedFirstSelectionLeavesNoRunSelectedOrManageable() => OnDispatcher(async () =>
    {
        var directory = TemporaryDirectory();
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, directory);
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        try
        {
            var run = RunTestData.CreateRun();
            await service.Store.SaveAsync(run); await model.InitializeAsync();
            File.Delete(Path.Combine(directory, $"{run.Id:N}.wisprun"));

            model.SelectedRun = Assert.Single(model.Library);
            await SelectionFailed(model);

            Assert.Null(model.SelectedRun);
            Assert.False(model.HasRun); Assert.False(model.CanManageRun);
            Assert.False(model.SaveDetailsCommand.CanExecute(null));
            Assert.False(model.CompareCommand.CanExecute(null));
            Assert.Empty(model.RunALabel); Assert.Empty(model.Metrics);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void ComparePreviousUsesAnEarlierRunAndStaysUnavailableForTheOldest() => OnDispatcher(async () =>
    {
        var directory = TemporaryDirectory();
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, directory);
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        try
        {
            var first = RunTestData.CreateRun() with { Name = "First", StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2) };
            var second = RunTestData.CreateRun() with { Name = "Second", StartedAtUtc = first.StartedAtUtc.AddMinutes(1) };
            await service.Store.SaveAsync(first); await service.Store.SaveAsync(second); await model.InitializeAsync();
            model.SelectedRun = model.Library.Single(item => item.Id == first.Id); await Ready(model);

            Assert.False(model.ComparePreviousCommand.CanExecute(null));
            model.ComparePreviousCommand.Execute(null); await Ready(model);
            Assert.False(model.HasComparison);

            model.SelectedRun = model.Library.Single(item => item.Id == second.Id); await Ready(model);
            Assert.True(model.ComparePreviousCommand.CanExecute(null));
            model.ComparePreviousCommand.Execute(null); await Ready(model);
            Assert.Equal(first.Id, model.ComparisonChoice!.Id);
            Assert.Equal("B · First", model.RunBLabel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void RapidSelectionsAndRefreshKeepTheDisplayedRunAndSelectorTogether() => OnDispatcher(async () =>
    {
        var directory = TemporaryDirectory();
        await using var receiver = new TelemetryUdpReceiver();
        await using var service = new RunRecordingService(receiver, directory);
        using var model = new RunsViewModel(service, new AppSettings(), Dispatcher.CurrentDispatcher);
        try
        {
            var a = Run("First", 0, 2); var b = Run("Second", 0, 3);
            await service.Store.SaveAsync(a); await service.Store.SaveAsync(b); await model.InitializeAsync();
            var first = Assert.Single(model.Library, item => item.Id == a.Id); var second = Assert.Single(model.Library, item => item.Id == b.Id);
            model.SelectedRun = first; model.SelectedRun = second; await Ready(model);
            Assert.Equal(b.Id, model.SelectedRun!.Id); Assert.StartsWith("A · Second", model.RunALabel);
            model.RefreshLibraryCommand.Execute(null); model.SelectedRun = first;
            await Ready(model); await Task.Delay(40);
            Assert.Equal(a.Id, model.SelectedRun!.Id); Assert.StartsWith("A · First", model.RunALabel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    private static RecordedRun Run(string name, double delay, double rate) => new()
    {
        Name = name,
        StartedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Samples = Enumerable.Range(0, 2001).Select(index =>
        {
            var sample = RunPresentationTests.Sample(index / 100d, 3000);
            return sample with { State = sample.State with { GroundSpeedMetersPerSecond = (float)Math.Max(0, (sample.ElapsedSeconds - delay) * rate) } };
        }).ToArray()
    };
    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "Wisp.RunsViewModelTests", Guid.NewGuid().ToString("N"));
    private static async Task Ready(RunsViewModel model)
    {
        for (int step = 0; step < 250 && (model.IsBusy || model.IsPreparingCharts); step++) await Task.Delay(10);
        Assert.False(model.IsBusy); Assert.False(model.IsPreparingCharts); Assert.False(model.HasError, model.Error);
    }
    private static async Task SelectionFailed(RunsViewModel model)
    {
        for (int step = 0; step < 250 && model.IsBusy; step++) await Task.Delay(10);
        Assert.False(model.IsBusy);
        Assert.True(model.HasError);
        Assert.Contains("could not be opened", model.Error);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
    private static async Task WithLiveReceiver(Func<TelemetryUdpReceiver, Func<Task>, Task> test)
    {
        await using var receiver = new TelemetryUdpReceiver();
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        int port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        { socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); port = ((IPEndPoint)socket.LocalEndPoint!).Port; }
        await receiver.StartAsync(port, TestContext.Current.CancellationToken);
        var timestamp = 0;
        await Refresh(); await test(receiver, Refresh);
        async Task Refresh()
        {
            var bytes = new byte[324]; timestamp += 10;
            Write(0, 1); Write(4, timestamp); Write(8, BitConverter.SingleToInt32Bits(8000)); Write(16, BitConverter.SingleToInt32Bits(3000));
            Write(212, 1); Write(224, 1); Write(228, 8); bytes[319] = 3;
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port));
            for (var index = 0; index < 200 && receiver.Latest?.GameTimestampMilliseconds != timestamp; index++) await Task.Delay(5);
            Assert.Equal((uint)timestamp, receiver.Latest?.GameTimestampMilliseconds);
            void Write(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }
    }
    private static void OnDispatcher(Func<Task> test)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(15)), "Run view-model test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
