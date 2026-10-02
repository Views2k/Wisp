using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core;
using Wisp.Core.Runs;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReviewViewModelTests
{
    [Fact]
    public void LoadingRaisesCommandStateAndCursorMovesKeepPreparedStatistics() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model();
        var notifications = 0;
        model.SectionStartCommand.CanExecuteChanged += (_, _) => notifications++;
        Assert.False(model.SectionStartCommand.CanExecute(null));
        model.SetRuns(Run(), null);
        Assert.True(model.IsBusy);
        await Ready(model);
        Assert.True(notifications > 0);
        Assert.True(model.SectionStartCommand.CanExecute(null));
        Assert.True(model.PinCommand.CanExecute(null));
        Assert.NotEmpty(model.Metrics);
        var prepared = model.Metrics[0];
        var changes = new List<string?>();
        model.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        model.Cursor = 25;
        Assert.False(model.IsBusy);
        Assert.Same(prepared, model.Metrics[0]);
        Assert.Contains("2.500", model.CursorText);
        Assert.Contains("2.500", model.CursorDetails!.Position);
        Assert.Equal(1, changes.Count(name => name == nameof(model.Plot)));
        Assert.Equal(1, changes.Count(name => name == nameof(model.CursorDetails)));
        Assert.DoesNotContain(nameof(model.IsBusy), changes);
        changes.Clear();
        model.Cursor = 25;
        Assert.Empty(changes);
    });

    [Fact]
    public void SavedTimingModeOverridesTheCurrentHudMode() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new() { LapTimingMode = LapTimingMode.TimeAttack });
        model.SetRuns(Run() with { LapTimingMode = LapTimingMode.GameLaps }, null);
        await Ready(model);
        Assert.Equal(LapTimingMode.GameLaps, model.Timing.Mode);
        Assert.True(model.Lap!.IsComplete);
    });

    [Fact]
    public void NewSelectionWinsAndClearingNotifiesAllVisibleState() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model();
        var first = Run(); var latest = Run() with { Name = "Latest" };
        model.SetRuns(first, null);
        model.SetRuns(latest, null);
        await Ready(model);
        Assert.Equal(latest.Id, model.Lap!.RunId);
        var properties = new HashSet<string>();
        model.PropertyChanged += (_, e) => { if (e.PropertyName is { } name) properties.Add(name); };
        model.SetRuns(null, null);
        await Ready(model);
        Assert.Null(model.Lap); Assert.Null(model.Reference);
        Assert.False(model.HasLap); Assert.Empty(model.Metrics); Assert.Empty(model.Events);
        Assert.Empty(model.CursorText); Assert.Empty(model.SectionText);
        Assert.Null(model.CursorDetails);
        Assert.Contains(nameof(model.CursorDetails), properties);
        Assert.Contains(nameof(model.HasLap), properties);
        Assert.Contains(nameof(model.Lap), properties);
        Assert.Contains(nameof(model.MaximumCursor), properties);
        Assert.False(model.WholeLapCommand.CanExecute(null));
    });

    [Fact]
    public void MissingPinnedRunFallsBackAndCanBeUnpinnedWithoutALap() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var settings = new AppSettings { LapReviewBenchmarkRunId = Guid.NewGuid() };
        using var model = fixture.Model(settings);
        var run = Run();
        model.SetRuns(run, null);
        await Ready(model);
        Assert.True(model.HasLap); Assert.Equal(run.Id, model.Reference!.RunId);
        Assert.Contains("unavailable", model.ReferenceStatus);
        model.SetRuns(run with { Samples = [] }, null);
        await Ready(model);
        Assert.False(model.HasLap);
        Assert.True(model.UnpinCommand.CanExecute(null));
        model.UnpinCommand.Execute(null);
        await Ready(model);
        Assert.Null(settings.LapReviewBenchmarkRunId);
        Assert.False(model.UnpinCommand.CanExecute(null));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitComparisonWinsOverPresentOrMissingPin(bool missing) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var pin = Run() with { Name = "Pinned run" };
        if (!missing) await fixture.Store.SaveAsync(pin);
        var settings = Pin(pin);
        using var model = fixture.Model(settings);
        var comparison = Run() with { Name = "Chosen comparison" };
        model.SetRuns(Run(), comparison);
        await Ready(model);
        Assert.Equal(comparison.Id, model.Reference!.RunId);
        Assert.All(model.ReferenceLaps, lap => Assert.Equal(comparison.Id, lap.RunId));
        Assert.Contains("Run B: Chosen comparison", model.ReferenceStatus);
        Assert.Contains("not used", model.ReferenceStatus);
        Assert.Equal(pin.Id, settings.LapReviewBenchmarkRunId);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompatiblePinKeepsCurrentLapReviewAndOwnRunReferences(bool otherCar) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var pin = Run();
        var run = Run();
        run = run with
        {
            Samples = run.Samples.Select(sample =>
        {
            var lap = sample.State.Lap!;
            return sample with
            {
                State = sample.State with
                {
                    CarOrdinal = sample.State.CarOrdinal + (otherCar ? 1 : 0),
                    Lap = lap with { Position = lap.Position with { X = lap.Position.X + (otherCar ? 0 : 1000) } }
                }
            };
        }).ToArray()
        };
        await fixture.Store.SaveAsync(pin);
        var settings = Pin(pin);
        using var model = fixture.Model(settings);
        model.SetRuns(run, null);
        await Ready(model);
        Assert.True(model.HasLap);
        Assert.Equal(run.Id, model.Reference!.RunId);
        Assert.True(model.Plot.Comparison!.CanCompare);
        Assert.Contains("Pinned benchmark is not used", model.ReferenceStatus);
        Assert.Equal(pin.Id, settings.LapReviewBenchmarkRunId);
        model.SetRuns(pin, null);
        await Ready(model);
        Assert.Equal(pin.Id, model.Reference!.RunId);
        Assert.StartsWith("Pinned:", model.ReferenceStatus);
    });

    private static AppSettings Pin(RecordedRun run)
    {
        var lap = LapReviewAnalysis.Build(run).Laps.First(lap => lap.IsComplete);
        return new()
        {
            LapReviewBenchmarkRunId = run.Id,
            LapReviewBenchmarkLapNumber = lap.Number,
            LapReviewBenchmarkSampleIndex = lap.Points[0].SampleIndex,
            LapReviewBenchmarkTimingMode = lap.TimingMode
        };
    }

    [Fact]
    public void PinnedLapUsesTheOriginalSampleIdentityWhenLapNumbersRepeat() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var run = Run(restart: true);
        await fixture.Store.SaveAsync(run);
        var laps = LapReviewAnalysis.Build(run, TestContext.Current.CancellationToken).Laps;
        var repeated = laps.Where(lap => lap.IsComplete && lap.Number == 2).ToArray();
        Assert.Equal(2, repeated.Length);
        var target = repeated[^1];
        var settings = new AppSettings
        {
            LapReviewBenchmarkRunId = run.Id,
            LapReviewBenchmarkLapNumber = target.Number,
            LapReviewBenchmarkSampleIndex = target.Points[0].SampleIndex,
            LapReviewBenchmarkTimingMode = target.TimingMode
        };
        using var model = fixture.Model(settings);
        model.SetRuns(run, null);
        await Ready(model);
        Assert.Equal(target.Points[0].SampleIndex, model.Reference!.Points[0].SampleIndex);
    });

    [Fact]
    public void DisposedViewModelDoesNotPublishPendingAnalysis() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var model = fixture.Model();
        model.SetRuns(Run(), null);
        model.Dispose();
        var changes = 0;
        model.PropertyChanged += (_, _) => changes++;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, changes);
        Assert.False(model.PinCommand.CanExecute(null));
    });

    private static RecordedRun Run(bool restart = false) => new()
    {
        Name = "Lap review fixture",
        StartedAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LapTimingMode = LapTimingMode.GameLaps,
        Samples = Enumerable.Range(0, restart ? 823 : 402).Select(index =>
        {
            var elapsed = index / 10d;
            var race = restart && index >= 402 ? (index - 402) / 10d : elapsed;
            var number = (ushort)Math.Floor((race + .00001) / 20);
            var phase = race / 20 * Math.Tau;
            var sample = RunPresentationTests.Sample(elapsed, 4000);
            return sample with
            {
                State = sample.State with
                {
                    GroundSpeedMetersPerSecond = (float)(Math.Tau * 100 / 20),
                    Lap = new(new((float)(100 * Math.Cos(phase)), 0, (float)(100 * Math.Sin(phase))),
                        (float)(race - number * 20), number > 0 ? 20 : 0, (float)race, number, 1)
                }
            };
        }).ToArray()
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.LapReviewViewModelTests", Guid.NewGuid().ToString("N"));
        internal RunStore Store { get; }
        internal Fixture() => Store = new(_directory);
        internal LapReviewViewModel Model(AppSettings? settings = null) => new(settings ?? new(), Store, () => { });
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private static async Task Ready(LapReviewViewModel model)
    {
        for (var step = 0; step < 500 && model.IsBusy; step++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(model.IsBusy);
        Assert.DoesNotContain("could not be prepared", model.Status);
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken), "Lap review test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
