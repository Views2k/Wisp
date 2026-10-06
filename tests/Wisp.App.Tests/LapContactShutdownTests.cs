using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App;
using Wisp.App.Runs;
using Wisp.Core.Runs;
using Wisp.Telemetry;
using Wisp.UiReview;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapContactShutdownTests
{
    [Fact]
    public void CloseWaitsForPendingContactAndLatestNotesBeforeApprovingExit() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        var review = model.LapReview;
        review.Cursor = 80;
        var seconds = review.Lap!.Points[review.Cursor].RunSeconds;
        model.Notes = "Keep these notes with the contact.";
        await fixture.Gate.WaitAsync();
        Task<bool> close;
        try
        {
            review.MarkContactCommand.Execute(null);
            close = model.PrepareToCloseMetadataAsync();
            Assert.Same(close, model.PrepareToCloseMetadataAsync());
            Assert.False(review.MarkContactCommand.CanExecute(null));
            Assert.False(review.RemoveContactCommand.CanExecute(null));
            await Task.Delay(30);
            Assert.False(close.IsCompleted);
        }
        finally { fixture.Gate.Release(); }
        Assert.True(await close);
        var saved = await fixture.Service.Store.LoadAsync(fixture.Run.Id);
        Assert.Equal("Keep these notes with the contact.", saved.Notes);
        Assert.Equal(new RunMarker(seconds, "Contact"), Assert.Single(saved.Markers));
        Assert.Single(model.PlotMarkers);
        Assert.False(review.MarkContactCommand.CanExecute(null));
        model.CancelMetadataClosePreparation();
        Assert.True(review.MarkContactCommand.CanExecute(null));
        Assert.True(review.RemoveContactCommand.CanExecute(null));
    });

    [Fact]
    public void SaveFailureDuringCloseKeepsWispOpenAndContactCanBeRetried() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        var review = model.LapReview;
        review.Cursor = 80;
        Task<bool> close;
        using (var locked = new FileStream(fixture.RunPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await fixture.Gate.WaitAsync();
            try
            {
                review.MarkContactCommand.Execute(null);
                close = model.PrepareToCloseMetadataAsync();
                await Task.Delay(30);
                Assert.False(close.IsCompleted);
            }
            finally { fixture.Gate.Release(); }
            Assert.False(await close);
        }
        Assert.Empty((await fixture.Service.Store.LoadAsync(fixture.Run.Id)).Markers);
        await WaitUntil(() => review.MarkContactCommand.CanExecute(null));
        Assert.True(model.CanManageLibrary);
        review.MarkContactCommand.Execute(null);
        Assert.True(await model.PrepareToCloseMetadataAsync());
        Assert.Single((await fixture.Service.Store.LoadAsync(fixture.Run.Id)).Markers);
    });

    [Fact]
    public void CancellingCloseDoesNotCancelTheContactOrApproveTheOldCloseAttempt() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        var review = model.LapReview;
        review.Cursor = 80;
        await fixture.Gate.WaitAsync();
        Task<bool> close;
        try
        {
            review.MarkContactCommand.Execute(null);
            close = model.PrepareToCloseMetadataAsync();
            model.CancelMetadataClosePreparation();
            await Task.Delay(30);
            Assert.False(close.IsCompleted);
            Assert.False(review.MarkContactCommand.CanExecute(null));
        }
        finally { fixture.Gate.Release(); }
        Assert.False(await close);
        Assert.Single((await fixture.Service.Store.LoadAsync(fixture.Run.Id)).Markers);
        await WaitUntil(() => review.MarkContactCommand.CanExecute(null));
        Assert.True(await model.PrepareToCloseMetadataAsync());
    });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.LapContactShutdownTests", Guid.NewGuid().ToString("N"));
        private readonly TelemetryUdpReceiver _receiver = new();
        internal RecordedRun Run { get; } = LapReviewFixtures.Create();
        internal RunRecordingService Service { get; }
        internal RunsViewModel Model { get; }
        internal string RunPath => Path.Combine(_directory, $"{Run.Id:N}.wisprun");
        internal SemaphoreSlim Gate => (SemaphoreSlim)typeof(RunStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Service.Store)!;

        private Fixture()
        {
            Service = new RunRecordingService(_receiver, _directory);
            Model = new RunsViewModel(Service, new AppSettings(), Dispatcher.CurrentDispatcher);
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Service.Store.SaveAsync(fixture.Run);
            await fixture.Model.ShowReviewAsync(fixture.Run);
            await WaitUntil(() => !fixture.Model.IsBusy && !fixture.Model.IsPreparingCharts && !fixture.Model.LapReview.IsBusy);
            Assert.False(fixture.Model.HasError);
            Assert.True(fixture.Model.LapReview.HasLap);
            // Allow the real store's metadata restore queued by ShowReview to finish.
            await fixture.Service.Store.LoadAsync(fixture.Run.Id);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Model.LapReview.PrepareToCloseContactsAsync(); }
            finally
            {
                Model.Dispose(); await Service.DisposeAsync(); await _receiver.DisposeAsync();
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition(), "The bounded contact operation did not settle.");
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20)), "Contact shutdown check exceeded its dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
