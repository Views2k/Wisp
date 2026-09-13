using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Wisp.App.Runs;
using Wisp.Core.Runs;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RunsAutosaveTests
{
    [Fact]
    public void EditsDuringAnEarlierSaveCommitInOrderWithoutReplacingTheNewerDraft() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        var changedDuringSave = false;
        model.PropertyChanged += UpdateDuringCommit;
        model.Name = "First revision";
        model.Notes = "First notes";
        Assert.True(await model.FlushMetadataAsync());
        var saved = await fixture.Service.Store.LoadAsync(fixture.A.Id);
        Assert.True(changedDuringSave);
        Assert.Equal("Latest revision", saved.Name);
        Assert.Equal("Latest notes", saved.Notes);
        Assert.Equal("Latest revision", model.Name);
        Assert.Equal("Latest notes", model.Notes);
        Assert.Equal("Saved", model.MetadataSaveStatus);
        Assert.False(model.IsBusy);
        Assert.Equal(fixture.A.Samples, saved.Samples);

        void UpdateDuringCommit(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(RunsViewModel.RunALabel) || changedDuringSave) return;
            changedDuringSave = true;
            model.Name = "Latest revision"; model.Notes = "Latest notes";
        }
    });

    [Fact]
    public void AutomaticSaveRunsAfterTypingStopsWithoutAnExplicitSaveCommand() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        fixture.Model.Notes = "Saved after typing.";
        await WaitUntil(() => !fixture.Model.HasPendingMetadata);
        Assert.Equal("Saved after typing.", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Notes);
        Assert.Equal("Saved", fixture.Model.MetadataSaveStatus);
    });

    [Fact]
    public void RapidRunSwitchesAndLeavingThePageSaveEachRunsOwnDetails() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        model.Name = "A revised"; model.Notes = "Keep these A notes.";
        model.SelectedRun = model.Library.Single(item => item.Id == fixture.B.Id);
        await Ready(model);
        model.Name = "B revised"; model.Tune = "Long gearing";
        model.SelectedRun = model.Library.Single(item => item.Id == fixture.A.Id);
        model.SelectedRun = model.Library.Single(item => item.Id == fixture.B.Id);
        await Ready(model);
        model.Notes = "Keep these B notes.";
        model.SetPageVisible(false);
        Assert.True(await model.FlushMetadataAsync());
        var a = await fixture.Service.Store.LoadAsync(fixture.A.Id);
        var b = await fixture.Service.Store.LoadAsync(fixture.B.Id);
        Assert.Equal("A revised", a.Name); Assert.Equal("Keep these A notes.", a.Notes);
        Assert.Equal("B revised", b.Name); Assert.Equal("Long gearing", b.Tune); Assert.Equal("Keep these B notes.", b.Notes);
        Assert.Equal(fixture.B.Id, model.SelectedRun!.Id);
        Assert.Equal("B revised", model.Name);
    });

    [Fact]
    public void FailedRunWriteKeepsItsRecoveryDraftAndRetrySavesTheNewestText() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        using (var locked = new FileStream(fixture.RunPath(fixture.A.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            model.Notes = "First failed draft";
            Assert.False(await model.FlushMetadataAsync());
            Assert.True(model.HasMetadataSaveError);
            Assert.Contains("this PC", model.MetadataSaveHelp);
            Assert.Equal("First failed draft", (await fixture.Service.Store.ReadMetadataDraftAsync(fixture.A.Id))!.Notes);
            model.Notes = "Latest failed draft";
            Assert.False(await model.FlushMetadataAsync());
            Assert.Equal("Latest failed draft", (await fixture.Service.Store.ReadMetadataDraftAsync(fixture.A.Id))!.Notes);
        }
        model.RetryMetadataSaveCommand.Execute(null);
        await WaitUntil(() => !model.HasPendingMetadata);
        Assert.Equal("Latest failed draft", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Notes);
        Assert.Null(await fixture.Service.Store.ReadMetadataDraftAsync(fixture.A.Id));
        Assert.False(model.HasMetadataSaveError);
    });

    [Fact]
    public void RecoveryDraftRestoresAfterModelRestartAndDoesNotChangeTelemetry() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        using (var locked = new FileStream(fixture.RunPath(fixture.A.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            fixture.Model.Name = "Recovered name"; fixture.Model.Notes = "Recovered notes";
            Assert.False(await fixture.Model.FlushMetadataAsync());
        }
        fixture.Model.Dispose();
        using var restored = new RunsViewModel(fixture.Service, new AppSettings(), Dispatcher.CurrentDispatcher);
        await restored.InitializeAsync();
        restored.SelectedRun = restored.Library.Single(item => item.Id == fixture.A.Id);
        await Ready(restored);
        Assert.Equal("Recovered name", restored.Name); Assert.Equal("Recovered notes", restored.Notes);
        Assert.True(await restored.FlushMetadataAsync());
        var saved = await fixture.Service.Store.LoadAsync(fixture.A.Id);
        Assert.Equal("Recovered name", saved.Name); Assert.Equal(fixture.A.Samples, saved.Samples);
    });

    [Fact]
    public void BlankNameIsRetainedAsDraftAndNeverOverwritesTheValidRun() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        fixture.Model.Name = ""; fixture.Model.Notes = "Still editing this run.";
        Assert.False(await fixture.Model.FlushMetadataAsync());
        Assert.Equal("Enter a run name.", fixture.Model.MetadataSaveStatus);
        Assert.Equal(fixture.A.Name, (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Name);
        fixture.Model.SelectedRun = fixture.Model.Library.Single(item => item.Id == fixture.B.Id);
        await Ready(fixture.Model);
        fixture.Model.SelectedRun = fixture.Model.Library.Single(item => item.Id == fixture.A.Id);
        await Ready(fixture.Model);
        Assert.Equal("", fixture.Model.Name); Assert.Equal("Still editing this run.", fixture.Model.Notes);
        fixture.Model.Name = "Finished name";
        Assert.True(await fixture.Model.FlushMetadataAsync());
        Assert.Equal("Finished name", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Name);
    });

    [Fact]
    public void SearchKeepsBoundSelectionAndComparisonWhileBulkExportUsesTheWholeLibrary() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        model.ComparisonChoice = model.Library.Single(item => item.Id == fixture.B.Id);
        model.CompareCommand.Execute(null); await Ready(model);
        var aList = new ListBox(); var bList = new ComboBox();
        aList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(RunsViewModel.FilteredLibrary)) { Source = model });
        aList.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(RunsViewModel.SelectedRun)) { Source = model, Mode = BindingMode.TwoWay });
        bList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(RunsViewModel.Library)) { Source = model });
        bList.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(RunsViewModel.ComparisonChoice)) { Source = model, Mode = BindingMode.TwoWay });
        model.LibrarySearch = "Road grip";
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(fixture.A.Id, ((SavedRunItem)aList.SelectedItem).Id);
        Assert.Equal(fixture.B.Id, ((SavedRunItem)bList.SelectedItem).Id);
        Assert.Equal(2, model.FilteredLibrary.Count);
        model.LibrarySearch = "nothing matches this";
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(fixture.A.Id, Assert.Single(model.FilteredLibrary).Id);
        Assert.Equal(fixture.A.Id, model.SelectedRun!.Id);
        Assert.Equal(fixture.B.Id, model.ComparisonChoice!.Id);
        Assert.True(model.HasComparison);
        Assert.Contains("0 matches", model.LibrarySearchSummary);
        var archive = Path.Combine(fixture.DirectoryPath, "all-runs.zip");
        await model.ExportAllAsync(archive);
        Assert.True(File.Exists(archive));
        var imported = new RunStore(Path.Combine(fixture.DirectoryPath, "imported"));
        await imported.ImportManyAsync([archive]);
        Assert.Equal(2, (await imported.ListAsync()).Count);
        BindingOperations.ClearAllBindings(aList); BindingOperations.ClearAllBindings(bList);
    });

    [Fact]
    public void ExportIncludesPendingMetadataAndDoesNotExportWhenSavingFails() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        fixture.Model.Name = "Shared revision"; fixture.Model.Notes = "Include these notes.";
        var exported = Path.Combine(fixture.DirectoryPath, "shared.wisprun");
        await fixture.Model.ExportSelectedAsync(exported);
        var imported = await fixture.Service.Store.ImportAsync(exported);
        Assert.Equal("Shared revision", imported.Name); Assert.Equal("Include these notes.", imported.Notes);
        using var locked = new FileStream(fixture.RunPath(fixture.A.Id), FileMode.Open, FileAccess.Read, FileShare.None);
        fixture.Model.Notes = "Not saved yet";
        var failed = Path.Combine(fixture.DirectoryPath, "failed.wisprun");
        await fixture.Model.ExportSelectedAsync(failed);
        Assert.False(File.Exists(failed)); Assert.True(fixture.Model.HasMetadataSaveError);
    });

    [Fact]
    public void ClosePreparationIsSharedAndCompletesOnlyAfterTheLatestDetailsAreSaved() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var model = fixture.Model;
        model.Notes = "Keep this before exiting.";
        var first = model.PrepareToCloseMetadataAsync();
        Assert.False(first.IsCompleted);
        Assert.Same(first, model.PrepareToCloseMetadataAsync());
        Assert.False(model.CanManageLibrary);
        model.Notes = "An editor cannot write after close preparation.";
        Assert.True(await first);
        Assert.False(model.HasUnprotectedMetadata);
        Assert.Equal("Keep this before exiting.", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Notes);
        Assert.False(model.IsBusy);
        model.CancelMetadataClosePreparation();
        Assert.True(model.CanManageLibrary);
    });

    [Fact]
    public void CloseMayProceedWithADurableRecoveryDraftWhenTheRunFileIsLocked() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        using (var locked = new FileStream(fixture.RunPath(fixture.A.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            fixture.Model.Notes = "Durable recovery before exit.";
            Assert.True(await fixture.Model.PrepareToCloseMetadataAsync());
            Assert.True(fixture.Model.HasPendingMetadata);
            Assert.False(fixture.Model.HasUnprotectedMetadata);
            Assert.Equal("Durable recovery before exit.", (await fixture.Service.Store.ReadMetadataDraftAsync(fixture.A.Id))!.Notes);
        }
        fixture.Model.CancelMetadataClosePreparation();
        Assert.True(await fixture.Model.PrepareToCloseMetadataAsync());
        Assert.Equal("Durable recovery before exit.", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Notes);
    });

    [Fact]
    public void CloseAndRemovalStayBlockedWhenEvenTheRecoveryDraftCannotBeWritten() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var blockedPath = Path.Combine(fixture.DirectoryPath, $"{fixture.A.Id:N}.metadata-draft.json");
        Directory.CreateDirectory(blockedPath);
        fixture.Model.Notes = "Only in memory until storage recovers.";
        Assert.False(await fixture.Model.PrepareToCloseMetadataAsync());
        Assert.True(fixture.Model.HasUnprotectedMetadata);
        Assert.True(fixture.Model.CanManageLibrary);
        await fixture.Model.DeleteSelectedAsync();
        Assert.True(File.Exists(fixture.RunPath(fixture.A.Id)));
        Assert.True(fixture.Model.HasUnprotectedMetadata);
        await fixture.Model.DeleteAllAsync();
        Assert.Equal(2, (await fixture.Service.Store.ListAsync()).Count);
        Assert.Equal("Only in memory until storage recovers.", fixture.Model.Notes);
        Directory.Delete(blockedPath);
        Assert.True(await fixture.Model.PrepareToCloseMetadataAsync());
        Assert.Equal("Only in memory until storage recovers.", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Notes);
    });

    [Fact]
    public void ExportAllRestoresAnUnopenedRunsRecoveryDraftAfterRestart() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        using (var locked = new FileStream(fixture.RunPath(fixture.B.Id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await fixture.Service.Store.SaveMetadataDraftAsync(new(fixture.B.Id, "Recovered B", "Long gears", "Must be in the archive."));
            Assert.True(result.DraftPreserved); Assert.Null(result.Summary);
        }
        fixture.Model.Dispose();
        using var restored = new RunsViewModel(fixture.Service, new AppSettings(), Dispatcher.CurrentDispatcher);
        await restored.InitializeAsync();
        Assert.False(restored.HasRun);
        var archive = Path.Combine(fixture.DirectoryPath, "recovered-library.zip");
        await restored.ExportAllAsync(archive);
        Assert.True(File.Exists(archive));
        var imported = new RunStore(Path.Combine(fixture.DirectoryPath, "restored-archive"));
        await imported.ImportManyAsync([archive]);
        var b = await imported.LoadAsync(fixture.B.Id);
        Assert.Equal("Recovered B", b.Name); Assert.Equal("Long gears", b.Tune); Assert.Equal("Must be in the archive.", b.Notes);
        Assert.Equal(fixture.B.Samples, b.Samples);
    });

    [Fact]
    public void ExportAllRefusesAnUnreadableRecoveryDraftWithoutRemovingIt() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        var draftPath = Path.Combine(fixture.DirectoryPath, $"{fixture.B.Id:N}.metadata-draft.json");
        await File.WriteAllTextAsync(draftPath, "{incomplete");
        var archive = Path.Combine(fixture.DirectoryPath, "unreadable-library.zip");
        await fixture.Model.ExportAllAsync(archive);
        Assert.False(File.Exists(archive)); Assert.True(File.Exists(draftPath));
        Assert.Contains("draft could not be read", fixture.Model.Error);
    });

    [Fact]
    public void ImageExportUsesSavedNamesAndRefusesToExportAFailedDraft() => OnDispatcher(async () =>
    {
        await using var fixture = await Fixture.Create();
        fixture.Model.SetPageVisible(true);
        await Ready(fixture.Model);
        Assert.True(fixture.Model.CanExportImage);
        fixture.Model.Name = "Image revision"; fixture.Model.Tune = "Image tune";
        var image = Path.Combine(fixture.DirectoryPath, "report.png");
        await fixture.Model.ExportImageAsync(image);
        Assert.True(File.Exists(image), fixture.Model.Error);
        Assert.Equal("Image revision · Image tune", fixture.Model.CreateImageSnapshot().RunA);
        Assert.Equal("Image revision", (await fixture.Service.Store.LoadAsync(fixture.A.Id)).Name);
        using var locked = new FileStream(fixture.RunPath(fixture.A.Id), FileMode.Open, FileAccess.Read, FileShare.None);
        fixture.Model.Name = "Not saved";
        var failedImage = Path.Combine(fixture.DirectoryPath, "failed-report.png");
        await fixture.Model.ExportImageAsync(failedImage);
        Assert.False(File.Exists(failedImage)); Assert.True(fixture.Model.HasMetadataSaveError);
    });

    [Fact]
    public void UserExitAndUpdaterPrepareMetadataBeforeClosingTheDispatcher()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "App.xaml.cs"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Wisp.App", "App.xaml.cs"));
        var close = Between("private async Task CloseApplicationAsync", "private async Task<bool> PrepareRunMetadataForExitAsync");
        Assert.True(close.IndexOf("await PrepareRunMetadataForExitAsync()", StringComparison.Ordinal) < close.IndexOf("_exiting = true", StringComparison.Ordinal));
        Assert.Contains("closingWindow.Close();", close); Assert.Contains("else Shutdown();", close);
        var closing = Between("private void OnControlPanelClosing", "private async Task SuspendRuntimeAsync");
        Assert.Contains("e.Cancel = true;\n        if (sender is Window closingWindow)", closing.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("_closePreparationActive", closing);
        var update = Between("internal async Task<(bool Started, string Error)> TryBeginApplicationUpdateAsync", "private static void SignalExistingInstance");
        Assert.True(update.IndexOf("await PrepareRunMetadataForExitAsync()", StringComparison.Ordinal) < update.IndexOf("ApplicationUpdateLauncher.Launch(installer)", StringComparison.Ordinal));
        Assert.Contains("_controller.Runs.CancelMetadataClosePreparation();", update);
        var exit = Between("protected override void OnExit", "private async Task ListenForActivationAsync");
        Assert.DoesNotContain("FlushMetadataAsync", exit); Assert.DoesNotContain("PrepareToCloseMetadataAsync", exit);

        string Between(string start, string end)
        {
            var first = source.IndexOf(start, StringComparison.Ordinal); var last = source.IndexOf(end, first + 1, StringComparison.Ordinal);
            Assert.True(first >= 0 && last > first, start);
            return source[first..last];
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Wisp.RunsAutosaveTests", Guid.NewGuid().ToString("N"));
        private readonly TelemetryUdpReceiver _receiver = new();
        internal RunRecordingService Service { get; }
        internal RunsViewModel Model { get; }
        internal RecordedRun A { get; } = RunTestData.CreateRun() with { Name = "Baseline", Tune = "Road baseline" };
        internal RecordedRun B { get; } = RunTestData.CreateRun() with { Id = Guid.NewGuid(), Name = "Revised", Tune = "Road grip" };
        private Fixture()
        {
            Service = new RunRecordingService(_receiver, DirectoryPath);
            Model = new RunsViewModel(Service, new AppSettings(), Dispatcher.CurrentDispatcher);
        }
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Service.Store.SaveAsync(fixture.A); await fixture.Service.Store.SaveAsync(fixture.B);
            await fixture.Model.InitializeAsync();
            fixture.Model.SelectedRun = fixture.Model.Library.Single(item => item.Id == fixture.A.Id);
            await Ready(fixture.Model);
            return fixture;
        }
        internal string RunPath(Guid id) => Path.Combine(DirectoryPath, $"{id:N}.wisprun");
        public async ValueTask DisposeAsync()
        {
            await Model.FlushMetadataAsync(); Model.Dispose();
            await Service.DisposeAsync(); await _receiver.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
    private static async Task Ready(RunsViewModel model)
    {
        await WaitUntil(() => !model.IsBusy && !model.IsPreparingCharts);
        Assert.False(model.HasError, model.Error);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition(), "The bounded run-details operation did not settle.");
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20)), "Run autosave check exceeded its dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
