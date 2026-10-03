using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneFailureDetailsViewModelTests
{
    [Fact]
    public void CopyFeedbackPreservesFailureAndRefreshClearsOldDetails() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var next = Task.FromResult(Failure());
        using var model = fixture.Model(_ => next);
        await model.InitializeAsync(); await model.RefreshAsync();
        Assert.True(model.HasFailureDetails); Assert.True(model.CanCopyFailureDetails);
        var originalStatus = model.Status; var originalDetails = model.FailureDetails;
        model.ReportCopyCompleted(true);
        Assert.StartsWith("Details copied.", model.CopyDetailsStatus, StringComparison.Ordinal);
        Assert.Equal(originalStatus, model.Status); Assert.Equal(originalDetails, model.FailureDetails);
        model.ReportCopyCompleted(false);
        Assert.Contains("clipboard is busy", model.CopyDetailsStatus, StringComparison.Ordinal);
        Assert.Equal(originalStatus, model.Status); Assert.Equal(originalDetails, model.FailureDetails);
        var pending = new TaskCompletionSource<TuneCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        next = pending.Task;
        var refreshing = model.RefreshAsync();
        Assert.True(model.IsRefreshing); Assert.False(model.HasFailureDetails); Assert.False(model.CanCopyFailureDetails);
        Assert.Empty(model.CopyDetailsStatus);
        pending.SetResult(new(TuneUiTestData.ValidSnapshot(), TuneCaptureStatus.Ready, ""));
        await refreshing;
        Assert.False(model.HasFailureDetails); Assert.False(model.CanCopyFailureDetails); Assert.Empty(model.FailureDetails);
    });

    [Fact]
    public void InvalidationAndSavedWorkspaceRemoveStaleDetails() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(_ => Task.FromResult(Failure()));
        await model.InitializeAsync(); await model.RefreshAsync();
        model.InvalidateCurrent();
        Assert.Empty(model.FailureDetails); Assert.False(model.CanCopyFailureDetails);
        await model.RefreshAsync(); Assert.True(model.HasFailureDetails);
        model.SetWorkspace(TuneWorkspace.Saved);
        Assert.Empty(model.FailureDetails); Assert.False(model.HasFailureDetails);
    });

    [Fact]
    public void SupersededReadCannotPublishFailureDetails() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<TuneCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = fixture.Model(_ => pending.Task);
        await model.InitializeAsync(); var refreshing = model.RefreshAsync();
        model.SetWorkspace(TuneWorkspace.Saved);
        pending.SetResult(Failure()); await refreshing;
        Assert.Empty(model.FailureDetails); Assert.False(model.HasFailureDetails); Assert.False(model.CanCopyFailureDetails);
    });

    [Fact]
    public void UnexpectedReadExceptionAlsoProducesSafeDetails() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(_ => throw new InvalidOperationException("PRIVATE_VALUE"));
        await model.InitializeAsync(); await model.RefreshAsync();
        Assert.True(model.HasError); Assert.True(model.HasFailureDetails); Assert.True(model.CanCopyFailureDetails);
        Assert.DoesNotContain("PRIVATE_VALUE", model.FailureDetails, StringComparison.Ordinal);
        var error = model.Error;
        model.ReportCopyCompleted(true);
        Assert.Equal(error, model.Error);
    });

    private static TuneCaptureResult Failure() => new(null, TuneCaptureStatus.Unavailable, "The current tune could not be read.",
        TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: new IOException("The current car could not be read.")));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "WispTuneDetailsTests", Guid.NewGuid().ToString("N"));
        internal TuneViewModel Model(Func<CancellationToken, Task<TuneCaptureResult>> capture) =>
            new(new TuneStore(_directory), capture, _ => true, Dispatcher.CurrentDispatcher);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
