using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsLibraryManagementViewModelTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    [Fact]
    public void SearchFiltersTheGalleryAndExplainsAnEmptyResult() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var named = await fixture.SaveAsync();
        await fixture.SaveAsync();
        await new ClipLibrary(fixture.Directory).RenameAsync(named.Id, "Goliath sprint", TestContext.Current.CancellationToken);
        using var model = fixture.Model();
        await model.InitializeAsync();
        Assert.Equal(2, model.Clips.Count);
        Assert.True(model.ShowsManagementHint);

        model.SearchText = "goliath";
        await model.ApplySearchAsync();
        var match = Assert.Single(model.Clips);
        Assert.Equal("Goliath sprint", match.Title);
        Assert.True(match.HasName);
        Assert.Equal("Page 1 of 1 · 1 of 2 clips", model.PageText);

        model.SearchText = "nothing like this";
        await model.ApplySearchAsync();
        Assert.Empty(model.Clips);
        Assert.True(model.IsEmpty);
        Assert.Contains("No saved clips match “nothing like this”", model.EmptyText, StringComparison.Ordinal);

        model.ClearSearchCommand.Execute(null);
        await WaitUntilAsync(() => model.Clips.Count == 2 && !model.IsBusy);
        Assert.False(model.HasSearchText);
        Assert.Equal("Page 1 of 1 · 2 clips", model.PageText);
    });

    [Fact]
    public void RenameUpdatesTheCardAndCanBeCleared() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        await fixture.SaveAsync();
        using var model = fixture.Model();
        await model.InitializeAsync();
        var card = Assert.Single(model.Clips);
        var savedTitle = card.Title;

        model.BeginRename(card);
        Assert.True(model.IsRenaming);
        Assert.False(model.ShowsManagementHint);
        Assert.Equal("", model.RenameText);
        model.RenameText = "Best lap";
        await model.ConfirmRenameAsync();
        Assert.False(model.HasManagement);
        Assert.Equal("Best lap", card.Title);
        Assert.Equal(savedTitle, card.SavedTitle);
        Assert.Equal("Clip renamed.", model.Notice);

        model.BeginRename(card);
        Assert.Equal("Best lap", model.RenameText);
        model.RenameText = "";
        await model.ConfirmRenameAsync();
        Assert.Equal(savedTitle, card.Title);
        Assert.False(card.HasName);

        model.BeginRename(card);
        model.RenameText = "two\nlines";
        await model.ConfirmRenameAsync();
        Assert.True(model.IsRenaming);
        Assert.Equal("Use a name of up to 80 characters on one line.", model.ManagementError);
        Assert.False(model.HasError);
        model.RenameText = "two lines";
        Assert.False(model.HasManagementError);
        model.CancelManagement();
        Assert.False(model.HasManagement);
    });

    [Fact]
    public void RenameExplainsANameAnotherClipUses() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var first = await fixture.SaveAsync();
        var second = await fixture.SaveAsync();
        await new ClipLibrary(fixture.Directory).RenameAsync(first.Id, "Drift", TestContext.Current.CancellationToken);
        using var model = fixture.Model();
        await model.InitializeAsync();
        var card = model.Clips.Single(item => item.Id == second.Id);

        model.BeginRename(card);
        model.RenameText = "DRIFT";
        await model.ConfirmRenameAsync();
        Assert.True(model.IsRenaming);
        Assert.Equal("Another clip is already named “Drift”. Choose a different name.", model.ManagementError);
        Assert.False(card.HasName);

        model.RenameText = "Drift 2";
        await model.ConfirmRenameAsync();
        Assert.False(model.HasManagement);
        Assert.Equal("Drift 2", card.Title);
    });

    [Fact]
    public void DeleteNeedsConfirmationAndRemovesOnlyThatClip() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var kept = await fixture.SaveAsync();
        var removed = await fixture.SaveAsync();
        using var model = fixture.Model();
        await model.InitializeAsync();
        var card = model.Clips.Single(item => item.Id == removed.Id);

        model.BeginDelete(card);
        Assert.True(model.IsConfirmingDelete);
        Assert.StartsWith("Delete “", model.ManagementTitle, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.MediaPath(removed.Id)));
        model.CancelManagement();
        Assert.True(File.Exists(fixture.MediaPath(removed.Id)));

        model.BeginDelete(card);
        await model.ConfirmDeleteAsync();
        Assert.False(File.Exists(fixture.MediaPath(removed.Id)));
        Assert.True(File.Exists(fixture.MediaPath(kept.Id)));
        Assert.Equal(kept.Id, Assert.Single(model.Clips).Id);
        Assert.Equal("Clip deleted.", model.Notice);
        Assert.False(model.HasManagement);
    });

    [Fact]
    public void DeleteReportsAClipThatStaysInUseAndKeepsIt() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var clip = await fixture.SaveAsync();
        using var model = fixture.Model();
        await model.InitializeAsync();
        model.BeginDelete(Assert.Single(model.Clips));
        await using (new FileStream(fixture.MediaPath(clip.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
            await model.ConfirmDeleteAsync();
        Assert.True(File.Exists(fixture.MediaPath(clip.Id)));
        Assert.True(model.IsConfirmingDelete);
        Assert.Contains("still in use", model.ManagementError, StringComparison.Ordinal);
        Assert.Single(model.Clips);
    });

    [Fact]
    public void SearchingAndManagingOtherClipsKeepsTheOpenClipPlaying() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var playing = await fixture.SaveAsync();
        var named = await fixture.SaveAsync();
        var removed = await fixture.SaveAsync();
        await new ClipLibrary(fixture.Directory).RenameAsync(named.Id, "Goliath sprint", TestContext.Current.CancellationToken);
        using var model = fixture.Model();
        await model.InitializeAsync();
        var open = model.Clips.Single(item => item.Id == playing.Id);
        Assert.NotNull(await model.SelectForPlaybackAsync(open));

        model.SearchText = "goliath";
        await model.ApplySearchAsync();
        Assert.Equal(named.Id, Assert.Single(model.Clips).Id);
        Assert.Same(open, model.SelectedClip);

        // The player's Rename works while its clip is filtered out of the list.
        model.BeginRename(open);
        Assert.True(model.IsRenaming);
        model.RenameText = "Opening lap";
        await model.ConfirmRenameAsync();
        Assert.Same(open, model.SelectedClip);
        Assert.Equal("Opening lap", open.Title);
        Assert.Equal(named.Id, Assert.Single(model.Clips).Id);

        model.ClearSearchCommand.Execute(null);
        await WaitUntilAsync(() => model.Clips.Count == 3 && !model.IsBusy);
        Assert.Same(open, model.SelectedClip);
        Assert.Contains(open, model.Clips);

        model.BeginDelete(model.Clips.Single(item => item.Id == removed.Id));
        await model.ConfirmDeleteAsync();
        Assert.Equal(2, model.Clips.Count);
        Assert.Same(open, model.SelectedClip);

        model.BeginDelete(open);
        await model.ConfirmDeleteAsync();
        Assert.False(model.HasSelection);
        Assert.False(File.Exists(fixture.MediaPath(playing.Id)));
        Assert.Equal(named.Id, Assert.Single(model.Clips).Id);
    });

    [Fact]
    public void OnlyHdrAndLosslessClipsAskWhichFormatToExport() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var normal = await fixture.SaveAsync();
        var lossless = await fixture.SaveAsync(lossless: true);
        using var model = fixture.Model();
        await model.InitializeAsync();

        Assert.NotNull(await model.SelectForPlaybackAsync(model.Clips.Single(item => item.Id == normal.Id)));
        Assert.False(model.RequiresExportChoice);
        model.BeginExportChoice();
        Assert.False(model.IsChoosingExportFormat);

        Assert.NotNull(await model.SelectForPlaybackAsync(model.Clips.Single(item => item.Id == lossless.Id)));
        Assert.True(model.RequiresExportChoice);
        model.BeginExportChoice();
        Assert.True(model.IsChoosingExportFormat);
        Assert.Equal("Original lossless recording", model.OriginalExportTitle);
        Assert.Contains("VLC", model.OriginalExportDescription, StringComparison.Ordinal);
        Assert.Contains("Discord", model.CompatibleExportDescription, StringComparison.Ordinal);
        model.ClosePlayback();
        Assert.False(model.IsChoosingExportFormat);
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispClipsManagement", Guid.NewGuid().ToString("N"));
        public ClipsViewModel Model() => new(new() { StorageDirectory = Directory }, new IdleRecorder(), Dispatcher.CurrentDispatcher, libraryDirectory: Directory);
        public string MediaPath(Guid id) => Path.Combine(Directory, $"{id:N}.mp4");

        public async Task<ClipEntry> SaveAsync(bool lossless = false)
        {
            var library = new ClipLibrary(Directory);
            var target = await library.ReserveSaveAsync(new(60, 1080, 60, 75, LosslessVideo: lossless), TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(target.MediaPath, Bytes, TestContext.Current.CancellationToken);
            return await library.CommitFinalizedAsync(target.Id,
                new(Bytes.Length, 1920, 1080, 60, 0, 600_000_000, true, LosslessVideo: lossless), TestContext.Current.CancellationToken);
        }

        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    private sealed class IdleRecorder : IClipRecorder
    {
        public ClipRecorderSnapshot Snapshot { get; } = new(ClipRecorderState.Disabled, false, true, false, "Clipping is off.");
        public event EventHandler? StateChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken, bool showCaptureBorder = false) =>
            Task.CompletedTask;
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No recording in management tests.");
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "Clip management test exceeded its deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
