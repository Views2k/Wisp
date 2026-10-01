using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneViewModelTests
{
    [Theory]
    [InlineData(TuneFieldStatus.UnsupportedUnit, "imperial game units", "Imperial units required")]
    [InlineData(TuneFieldStatus.UnsupportedConversion, "does not support yet", "Unsupported conversion")]
    [InlineData(TuneFieldStatus.UnresolvedDefault, "could not be read", "Default not read")]
    [InlineData(TuneFieldStatus.UnresolvedPartLevel, "could not be read", "Part setting not read")]
    public void IncompleteTuneExplainsWhyItCannotBeSaved(TuneFieldStatus status, string explanation, string value) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var source = Snapshot();
        var field = source.Fields[0] with { Status = status, DisplayValue = null, DisplayText = null };
        var snapshot = source with { Fields = source.Fields.SetItem(0, field) };
        using var model = fixture.Model(() => snapshot);
        await model.InitializeAsync(); await model.RefreshAsync();
        Assert.False(model.CanSave); Assert.Contains(explanation, model.Status, StringComparison.Ordinal);
        Assert.Equal(value, model.Rows[0].Value);
    });

    [Fact]
    public void SaveCloseReopenLoadAndEditPreservesSnapshotAndDescription() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot();
        using (var model = fixture.Model(() => snapshot))
        {
            await model.InitializeAsync(); await model.RefreshAsync();
            Assert.True(model.CanSave);
            model.BeginSave(); model.DialogName = "  Road setup  "; model.DialogDescription = "First line\nSecond line";
            await model.ConfirmDialogAsync();
            Assert.False(model.IsDialogOpen); Assert.True(model.IsSavedMode);
            var saved = Assert.Single(model.Library);
            Assert.Equal("Road setup", saved.Name); Assert.Equal("First line\nSecond line", saved.Description);
            Assert.Equal(snapshot.Id, saved.Snapshot.Id);
        }
        using var reopened = fixture.Model(() => null);
        await reopened.InitializeAsync();
        reopened.SetWorkspace(TuneWorkspace.Saved); reopened.SelectedTune = Assert.Single(reopened.Library);
        await reopened.LoadSelectedAsync();
        Assert.Equal("Road setup", reopened.Heading); Assert.Contains("Saved tune", reopened.Context, StringComparison.Ordinal);
        reopened.BeginEdit(); reopened.DialogName = "New name"; reopened.DialogDescription = "Updated\nnotes";
        await reopened.ConfirmDialogAsync();
        Assert.False(reopened.IsDialogOpen, reopened.DialogError);
        Assert.Equal("Tune details saved.", reopened.Status);
        var updated = Assert.Single(reopened.Library);
        Assert.Equal(snapshot.Id, updated.Snapshot.Id); Assert.Equal(snapshot.CapturedAtUtc, updated.Snapshot.CapturedAtUtc);
        Assert.Equal("Updated\nnotes", updated.Description);
    });

    [Fact]
    public void SaveRevalidatesWithoutReplacingReviewedSnapshot() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var original = Snapshot(); var current = original;
        using var model = fixture.Model(() => current);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave();
        current = original with { Id = Guid.NewGuid(), CapturedAtUtc = original.CapturedAtUtc.AddSeconds(1) };
        model.DialogName = "Reviewed setup"; await model.ConfirmDialogAsync();
        Assert.Equal(original.Id, Assert.Single(model.Library).Snapshot.Id);
        Assert.Equal(original.CapturedAtUtc, Assert.Single(model.Library).Snapshot.CapturedAtUtc);
    });

    [Fact]
    public void ChangedCarAtSaveKeepsDraftAndDoesNotSaveWrongCar() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var current = Snapshot(); using var model = fixture.Model(() => current);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave();
        model.DialogName = "Keep this name"; model.DialogDescription = "Keep these notes";
        current = current with { Identity = current.Identity with { CarOrdinal = current.Identity.CarOrdinal + 1 } };
        await model.ConfirmDialogAsync();
        Assert.True(model.IsDialogOpen); Assert.NotEmpty(model.DialogError); Assert.Empty(model.Library);
        Assert.Equal("Keep this name", model.DialogName); Assert.Equal("Keep these notes", model.DialogDescription);
    });

    [Fact]
    public void InvalidatedSessionRejectsSaveWithoutRecapture() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(); var current = true; var captures = 0;
        using var model = new TuneViewModel(fixture.Store, _ => { captures++; return Task.FromResult(new TuneCaptureResult(snapshot, TuneCaptureStatus.Ready, "Ready")); }, _ => current, Dispatcher.CurrentDispatcher);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave(); model.DialogName = "Review";
        current = false; model.InvalidateCurrent(); await model.ConfirmDialogAsync();
        Assert.True(model.IsDialogOpen); Assert.Equal(1, captures); Assert.Empty(model.Library);
    });

    [Fact]
    public void OutdatedRefreshCannotReplaceSavedSelection() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(); var saved = await fixture.Store.SaveAsync(snapshot, "Offline setup", "Description");
        var gate = new TaskCompletionSource<TuneCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new TuneViewModel(fixture.Store, _ => gate.Task, _ => true, Dispatcher.CurrentDispatcher);
        await model.InitializeAsync(); var refresh = model.RefreshAsync();
        model.SetWorkspace(TuneWorkspace.Saved); model.SelectedTune = Assert.Single(model.Library); await model.LoadSelectedAsync();
        gate.SetResult(new(snapshot, TuneCaptureStatus.Ready, "Ready")); await refresh;
        Assert.True(model.IsSavedMode); Assert.Equal(saved.Name, model.Heading); Assert.False(model.IsRefreshing);
    });

    [Fact]
    public void SortingRetainsSelectionAndComparisonIdentities() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); var snapshot = Snapshot();
        var z = await fixture.Store.SaveAsync(snapshot, "Zulu", ""); var a = await fixture.Store.SaveAsync(snapshot, "Alpha", "");
        using var model = fixture.Model(() => null); await model.InitializeAsync();
        model.SelectedTune = model.Library.Single(item => item.Id == z.Id);
        model.CompareA = model.SelectedTune; model.CompareB = model.Library.Single(item => item.Id == a.Id);
        model.SelectedSort = model.SortOptions.Single(item => item.Sort == TuneSort.NameAscending);
        Assert.Equal(a.Id, model.Library[0].Id); Assert.Equal(z.Id, model.SelectedTune!.Id);
        Assert.Equal(z.Id, model.CompareA!.Id); Assert.Equal(a.Id, model.CompareB!.Id);
        model.SelectedSort = model.SortOptions.Single(item => item.Sort == TuneSort.NameDescending);
        Assert.Equal(z.Id, model.Library[0].Id); Assert.Equal(z.Id, model.SelectedTune.Id);
    });

    [Fact]
    public void CompareAlignsGearCountsAndNeverShowsMissingAsZero() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(Snapshot(), "Six gears", ""); await fixture.Store.SaveAsync(Snapshot("editable"), "Seven gears", "");
        using var model = fixture.Model(() => null); await model.InitializeAsync(); model.SetWorkspace(TuneWorkspace.Compare);
        model.CompareA = model.Library.Single(item => item.Name == "Six gears"); model.CompareB = model.Library.Single(item => item.Name == "Seven gears");
        model.SelectedCategory = model.Categories.Single(item => item.Category == TuneCategory.Gearing);
        var seventh = Assert.Single(model.Rows, item => item.Label == "Gear 7");
        Assert.Equal("Not applicable", seventh.Value); Assert.NotEqual("Unavailable", seventh.OtherValue);
        Assert.DoesNotContain(model.Rows, item => item.Label == "Gear 8");
    });

    [Fact]
    public void MetadataValidationPreservesOverlongDraft() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); var snapshot = Snapshot(); using var model = fixture.Model(() => snapshot);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave();
        model.DialogName = new string('x', 41); model.DialogDescription = "Notes";
        await model.ConfirmDialogAsync(); Assert.Equal(41, model.DialogName.Length); Assert.NotEmpty(model.DialogError); Assert.True(model.IsDialogOpen);
        model.DialogName = "Valid name"; model.DialogDescription = new string('n', 2001);
        await model.ConfirmDialogAsync(); Assert.Equal(2001, model.DialogDescription.Length); Assert.NotEmpty(model.DialogError); Assert.Empty(model.Library);
    });

    [Fact]
    public void AttachedComparisonStaysOfflineAndSurvivesLibrarySort() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); var snapshot = Snapshot(); var captures = 0;
        await fixture.Store.SaveAsync(snapshot, "Library setup", "");
        using var model = new TuneViewModel(fixture.Store, _ => { captures++; return Task.FromResult(new TuneCaptureResult(null, TuneCaptureStatus.GameNotRunning, "")); }, _ => false, Dispatcher.CurrentDispatcher);
        model.OpenComparisonSnapshots(snapshot, "Attached setup", "Run notes");
        await model.InitializeAsync();
        Assert.True(model.IsCompareMode); Assert.Equal("Attached setup", model.CompareA!.Name); Assert.Null(model.CompareB);
        Assert.Single(model.Library); Assert.Equal(2, model.ComparisonChoices.Count);
        model.CompareB = Assert.Single(model.Library);
        model.SelectedSort = model.SortOptions.Single(item => item.Sort == TuneSort.NameDescending);
        Assert.Equal(snapshot.Id, model.CompareA.Snapshot.Id); Assert.Equal("Attached setup", model.CompareA.Name);
        Assert.Equal("Library setup", model.CompareB!.Name); Assert.Equal(0, captures);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingExternalViewAfterLibraryLoadUsesLatestRequest(bool compare) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var pause = new PausedStoreWrite(fixture.Directory);
        var snapshot = Snapshot(); var captures = 0;
        var write = pause.Begin(snapshot);
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var model = new TuneViewModel(pause.Store, _ =>
        {
            captures++; return Task.FromResult(new TuneCaptureResult(null, TuneCaptureStatus.GameNotRunning, ""));
        }, _ => false, Dispatcher.CurrentDispatcher);
        var initialize = model.InitializeAsync();
        Assert.True(model.IsBusy);
        model.SetPageVisible(false);
        model.OpenSnapshot(snapshot, "Superseded view", "Old notes");
        model.OpenComparisonSnapshots(snapshot, "Superseded comparison", "Old comparison notes");
        if (compare) model.OpenComparisonSnapshots(snapshot, "Final A", "A notes", Snapshot("rwd"), "Final B", "B notes");
        else model.OpenSnapshot(snapshot, "Final attached view", "Final notes");
        model.SetPageVisible(true);
        Assert.False(model.HasSnapshot);
        pause.Release(); await write; await initialize;
        Assert.False(model.IsBusy); Assert.Equal(0, captures); Assert.Single(model.Library);
        if (compare)
        {
            Assert.True(model.IsCompareMode); Assert.Equal("Final A", model.CompareA!.Name);
            Assert.Equal("A notes", model.CompareA.Description); Assert.Equal("Final B", model.CompareB!.Name);
            Assert.Equal("B notes", model.CompareB.Description); Assert.Equal(3, model.ComparisonChoices.Count);
        }
        else
        {
            Assert.True(model.IsSavedMode); Assert.Equal("Final attached view", model.Heading);
            Assert.Equal("Final notes", model.Description); Assert.Contains("Attached tune", model.Context, StringComparison.Ordinal);
        }
    });

    [Fact]
    public void PendingExternalViewAfterSavedTuneLoadOverridesCompletedLoad() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var pause = new PausedStoreWrite(fixture.Directory);
        var snapshot = Snapshot();
        await pause.Store.SaveAsync(snapshot, "Library selection", "");
        using var model = new TuneViewModel(pause.Store, _ => Task.FromResult(new TuneCaptureResult(null, TuneCaptureStatus.GameNotRunning, "")),
            _ => false, Dispatcher.CurrentDispatcher);
        await model.InitializeAsync(); model.SetWorkspace(TuneWorkspace.Saved); model.SelectedTune = Assert.Single(model.Library);
        var write = pause.Begin(snapshot);
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var load = model.LoadSelectedAsync(); Assert.True(model.IsBusy);
        model.OpenSnapshot(snapshot, "Requested run", "Attached notes");
        pause.Release(); await write; await load;
        Assert.Equal("Requested run", model.Heading); Assert.Equal("Attached notes", model.Description);
        Assert.Null(model.SelectedTune); Assert.Contains("Attached tune", model.Context, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingExternalViewPreservesDraftUntilSaveFinishesOrDialogIsCancelled(bool succeeds) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); var snapshot = Snapshot(); var captures = 0;
        var confirmation = new TaskCompletionSource<TuneCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var model = new TuneViewModel(fixture.Store, _ => ++captures == 1
            ? Task.FromResult(new TuneCaptureResult(snapshot, TuneCaptureStatus.Ready, "")) : confirmation.Task,
            _ => true, Dispatcher.CurrentDispatcher);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave();
        model.DialogName = "Reviewed draft"; model.DialogDescription = "Keep these notes";
        var save = model.ConfirmDialogAsync(); Assert.True(model.IsBusy);
        model.OpenComparisonSnapshots(snapshot, "Requested A", "Run notes");
        Assert.True(model.IsDialogOpen && model.IsCurrentMode);
        Assert.Equal("Reviewed draft", model.DialogName); Assert.Equal("Keep these notes", model.DialogDescription);
        confirmation.SetResult(succeeds ? new(snapshot, TuneCaptureStatus.Ready, "") : new(null, TuneCaptureStatus.Unavailable, "Unavailable"));
        await save;
        if (!succeeds)
        {
            Assert.True(model.IsDialogOpen && model.IsCurrentMode); Assert.NotEmpty(model.DialogError);
            Assert.Equal("Reviewed draft", model.DialogName); Assert.Equal("Keep these notes", model.DialogDescription);
            Assert.Empty(model.Library); model.CancelDialog();
        }
        else
        {
            var saved = Assert.Single(model.Library);
            Assert.Equal("Reviewed draft", saved.Name); Assert.Equal("Keep these notes", saved.Description);
            Assert.Equal(snapshot.Id, saved.Snapshot.Id);
        }
        Assert.False(model.IsDialogOpen); Assert.True(model.IsCompareMode);
        Assert.Equal("Requested A", model.CompareA!.Name); Assert.Equal(2, captures);
        model.SetWorkspace(TuneWorkspace.Current);
        Assert.True(model.IsCurrentMode);
    });

    [Fact]
    public void PendingExternalViewIsDiscardedOnDisposal() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); using var pause = new PausedStoreWrite(fixture.Directory);
        var snapshot = Snapshot(); var write = pause.Begin(snapshot);
        await pause.Entered.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var model = new TuneViewModel(pause.Store, _ => Task.FromResult(new TuneCaptureResult(null, TuneCaptureStatus.GameNotRunning, "")),
            _ => false, Dispatcher.CurrentDispatcher);
        var initialize = model.InitializeAsync(); Assert.True(model.IsBusy);
        model.OpenSnapshot(snapshot, "Never opened", ""); model.Dispose();
        pause.Release(); await write; await initialize;
        Assert.False(model.HasSnapshot); Assert.Empty(model.Library); Assert.True(model.IsCurrentMode);
        model.OpenComparisonSnapshots(snapshot, "Also ignored", ""); Assert.Null(model.CompareA);
    });

    [Theory]
    [InlineData(0.0000001, "Below display precision")]
    [InlineData(0, "Raw setting differs")]
    public void RawDifferenceIsNotShownAsIdenticalOrFakeZeroDelta(double delta, string description)
    {
        var field = Snapshot().Fields.First(value => value.Status == TuneFieldStatus.Available);
        var row = TunePresentation.Row(field, field, field.Id, comparison: true, displayDelta: delta, rawEqual: false);
        Assert.True(row.Changed); Assert.Equal(description, row.Difference);
    }

    [Fact]
    public void WriteFailurePreservesDialogAndDoesNotClaimSuccess() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture(); var snapshot = Snapshot(); using var model = fixture.Model(() => snapshot);
        await model.InitializeAsync(); await model.RefreshAsync(); model.BeginSave(); model.DialogName = "Road";
        Directory.Delete(fixture.Directory); await File.WriteAllTextAsync(fixture.Directory, "occupied", TestContext.Current.CancellationToken);
        await model.ConfirmDialogAsync();
        Assert.True(model.IsDialogOpen); Assert.NotEmpty(model.DialogError); Assert.Equal("Road", model.DialogName); Assert.Empty(model.Library);
    });

    private static TuneSnapshot Snapshot(string name = "miata") => TuneUiTestData.ValidSnapshot(name);

    private sealed class PausedStoreWrite : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseNextWrite;
        internal TuneStore Store { get; }
        internal Task Entered => _entered.Task;
        internal PausedStoreWrite(string directory) => Store = new TuneStore(directory, () =>
        {
            if (Interlocked.Exchange(ref _pauseNextWrite, 0) == 1)
            {
                _entered.TrySetResult();
                if (!_release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Store test gate was not released.");
            }
            return DateTimeOffset.UtcNow;
        });
        internal Task Begin(TuneSnapshot snapshot)
        {
            Interlocked.Exchange(ref _pauseNextWrite, 1);
            return Store.SaveAsync(snapshot, "Library fixture", "");
        }
        internal void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispTuneUiTests", Guid.NewGuid().ToString("N"));
        internal TuneStore Store { get; }
        internal Fixture() => Store = new TuneStore(Directory);
        internal TuneViewModel Model(Func<TuneSnapshot?> snapshot) => new(Store, _ =>
        {
            var value = snapshot(); return Task.FromResult(new TuneCaptureResult(value, value is null ? TuneCaptureStatus.GameNotRunning : TuneCaptureStatus.Ready, value is null ? "Start Forza to read the current tune." : "Ready"));
        }, _ => true, Dispatcher.CurrentDispatcher);
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
            if (File.Exists(Directory)) File.Delete(Directory);
        }
    }

    private static void OnDispatcher(Func<Task> test)
    {
        Exception? failure = null; using var finished = new ManualResetEventSlim();
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "Tune workflow test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal static class TuneUiTestData
{
    internal static TuneSnapshot ValidSnapshot(string name = "miata") => Wisp.UiReview.TuneUiFixture.Read(name);
}
