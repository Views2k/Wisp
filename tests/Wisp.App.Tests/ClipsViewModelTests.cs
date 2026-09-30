using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsViewModelTests
{
    [Fact]
    public void FailedShortcutRegistrationPreservesWorkingPreferenceAndDoesNotPersist() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        model.SetShortcutRegistration((_, _, _) => null);
        var original = model.Preferences.ToggleShortcut;
        Assert.True(model.ConfigureShortcut(false, true, original));
        var commits = 0;
        model.PreferencesChanged += (_, _) => commits++;
        model.SetShortcutRegistration((_, _, _) => "another app is already using it");
        var replacement = original with { Key = System.Windows.Input.Key.J };
        Assert.False(model.ConfigureShortcut(false, true, replacement));
        Assert.Equal(original, model.Preferences.ToggleShortcut);
        Assert.True(model.ToggleShortcutEnabled);
        Assert.Equal(0, commits);
        Assert.Contains("Shortcut unchanged", model.Error, StringComparison.Ordinal);
    });

    [Fact]
    public void SuspendedRuntimePreventsToggleAndSaveActions() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        model.SetRuntimeActive(false);
        Assert.False(model.CanToggle);
        Assert.False(model.CanSave);
        await model.ToggleAsync();
        await model.SaveClipAsync();
        Assert.Equal(0, recorder.ToggleCalls);
        Assert.Empty(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public void ConcurrentInitializationSharesCompletion() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new FakeRecorder());
        var first = model.InitializeAsync();
        Assert.Same(first, model.InitializeAsync());
        await first;
        Assert.False(model.IsBusy);
    });

    [Fact]
    public void UnavailableRecorderCannotPretendClippingStarted() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        Assert.False(model.CanToggle);
        Assert.False(model.CanSave);
        await model.ToggleAsync();
        Assert.False(model.ClippingEnabled);
        Assert.False(model.IsRecording);
        Assert.Equal(0, recorder.ToggleCalls);
        Assert.Equal("Recorder unavailable", model.RecorderStatus);
    });

    [Fact]
    public void ArmedWaitingAndObservedBufferingAreDifferentStates() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.ToggleAsync();
        Assert.True(model.ClippingEnabled);
        Assert.False(model.IsRecording);
        Assert.False(model.CanSave);
        Assert.False(model.CanEditSettings);
        model.LengthSeconds = 300;
        Assert.Equal(60, model.LengthSeconds);
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        Assert.True(model.IsRecording);
        Assert.True(model.CanSave);
        await model.ToggleAsync();
        Assert.False(model.ClippingEnabled);
        Assert.False(model.Preferences.Enabled);
        Assert.True(model.CanEditSettings);
    });

    [Fact]
    public void FailedNativeSaveRemainsPendingAndNeverClaimsSuccess() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder { FailSave = true };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        Assert.True(model.HasError);
        Assert.Empty(model.Clips);
        Assert.DoesNotContain("Clip saved", model.Notice, StringComparison.Ordinal);
        Assert.Contains("unfinished", model.PendingText, StringComparison.Ordinal);
        model.RemindersEnabled = false;
        Assert.False(model.HasReminder); Assert.True(model.HasDashboardNotice);
        Assert.Equal("Clips need attention", model.DashboardNoticeTitle);
        Assert.Equal(model.Error, model.DashboardNoticeText);
        var previousError = model.Error;
        Assert.Equal(fixture.Directory, await model.GetClipFolderForOpenAsync());
        Assert.Equal(previousError, model.Error);
        await model.LoadPageAsync(0);
        Assert.False(model.HasError); Assert.True(model.HasDashboardNotice);
        Assert.Equal("Unfinished clip saves", model.DashboardNoticeTitle);
        Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Theory]
    [InlineData(ClipRecorderState.Disabled, false, "Clipping is off.", "Clipping is off. Enable clipping before saving a clip.")]
    [InlineData(ClipRecorderState.WaitingForGame, true, "Preparing game capture…", "No clip is ready yet. Preparing game capture…")]
    [InlineData(ClipRecorderState.Error, false, "Video encoding failed. Enable clipping to try again.", "Video encoding failed. Enable clipping to try again.")]
    public void UnavailableSaveAttemptsExplainWhyWithoutCreatingReservations(ClipRecorderState state, bool enabled, string status, string expected) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(state, enabled, true, false, status));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();

        for (var attempt = 0; attempt < 3; attempt++) await model.SaveClipAsync();

        Assert.Equal(expected, model.Error);
        Assert.Equal(expected, model.DashboardNoticeText);
        Assert.True(model.HasDashboardNotice);
        Assert.Equal(0, recorder.SaveCalls);
        Assert.Empty(model.Clips);
        Assert.Empty(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public void BusySaveAttemptPreservesTheCurrentSaveAndErrorWithoutQueuingAnother() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new FakeRecorder { SaveGate = gate };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips."));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        var saving = model.SaveClipAsync();
        try
        {
            await recorder.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            model.PlaybackFailed();
            var previousError = model.Error;
            await model.SaveClipAsync();
            Assert.True(model.IsBusy);
            Assert.True(model.ClippingEnabled);
            Assert.Equal(previousError, model.Error);
            Assert.Contains("Wait for it to finish", model.Notice, StringComparison.Ordinal);
            Assert.Equal(1, recorder.SaveCalls);
            Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
        }
        finally { gate.TrySetResult(); await saving; }
        Assert.Equal(1, recorder.SaveCalls);
        Assert.Single(model.Clips);
        Assert.False(model.IsBusy);
    });

    [Theory]
    [InlineData("no_keyframe", "No playable clip is ready yet.")]
    [InlineData("not_ready", "No clip is ready yet.")]
    [InlineData("save_in_progress", "A clip is already being saved.")]
    [InlineData("storage_failed", "The clip folder could not be written.")]
    [InlineData("mux_failed", "The clip could not be finalized.")]
    public void NativeSaveRefusalKeepsItsSafeReasonAndRetainsPendingSave(string reason, string expected) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder { SaveFailure = new RecorderClientException(reason) };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips."));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();

        await model.SaveClipAsync();

        Assert.StartsWith(expected, model.Error, StringComparison.Ordinal);
        Assert.True(model.ClippingEnabled);
        Assert.Equal(1, recorder.SaveCalls);
        Assert.Empty(model.Clips);
        Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public void RecorderFaultTakesPrecedenceOverSaveCancellation() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        const string specificFailure = "Video encoding failed. Enable clipping to try again.";
        var recorder = new FakeRecorder
        {
            SaveFailure = new RecorderClientException("cancelled"),
            SaveFault = new(ClipRecorderState.Error, false, true, false, specificFailure)
        };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips."));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();

        await model.SaveClipAsync();

        Assert.Equal(specificFailure, model.Error);
        Assert.Equal(specificFailure, model.RecorderStatus);
        Assert.False(model.ClippingEnabled);
        Assert.Empty(model.Clips);
        Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public void RecorderFailureNeedsAttentionEvenWithRemindersDisabled() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        using var model = fixture.Model(recorder);
        await model.InitializeAsync(); model.RemindersEnabled = false;
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        recorder.Set(new(ClipRecorderState.Error, false, true, false, "The recorder stopped. Turn clipping on to retry."));
        Assert.True(model.HasDashboardNotice);
        Assert.Equal(recorder.Snapshot.Status, model.DashboardNoticeText);
        Assert.Contains(nameof(ClipsViewModel.HasDashboardNotice), changed);
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        Assert.False(model.HasDashboardNotice);
    });

    [Fact]
    public void SelectionAndPlaybackFailureRemainUnviewedUntilSuccessfulOpen() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var library = new ClipLibrary(fixture.Directory);
        var target = await library.ReserveSaveAsync(new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(target.MediaPath, FakeRecorder.Bytes, TestContext.Current.CancellationToken);
        await library.CommitFinalizedAsync(target.Id, FakeRecorder.Media, TestContext.Current.CancellationToken);
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        var card = Assert.Single(model.Clips);
        Assert.NotNull(await model.SelectForPlaybackAsync(card));
        model.PlaybackFailed();
        Assert.Equal(1, model.NewClipCount);
        Assert.Null(Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ViewedAtUtc);
        await model.PlaybackOpenedAsync(card.Id);
        Assert.Equal(0, model.NewClipCount);
        Assert.Equal("Viewed", card.ReviewState);
        Assert.NotNull(Assert.Single((await library.GetPageAsync(0, TestContext.Current.CancellationToken)).Clips).ViewedAtUtc);
        model.ClosePlayback();
        Assert.False(model.HasSelection);
    });

    [Fact]
    public void PendingOldFolderReminderCannotReplaceNewFolderCount() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var library = new ClipLibrary(fixture.Directory);
        var target = await library.ReserveSaveAsync(new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(target.MediaPath, FakeRecorder.Bytes, TestContext.Current.CancellationToken);
        await library.CommitFinalizedAsync(target.Id, FakeRecorder.Media, TestContext.Current.CancellationToken);
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        Assert.Equal(1, model.NewClipCount);

        var activeLibrary = Assert.IsType<ClipLibrary>(typeof(ClipsViewModel)
            .GetField("_library", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model));
        var gate = Assert.IsType<SemaphoreSlim>(typeof(ClipLibrary)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(activeLibrary));
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Task pending;
        try
        {
            pending = Assert.IsAssignableFrom<Task>(typeof(ClipsViewModel)
                .GetMethod("RefreshReminderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null));
            Assert.False(pending.IsCompleted);
            await model.SetStorageDirectoryAsync(Path.Combine(fixture.Directory, "new-library"));
            Assert.Equal(0, model.NewClipCount);
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, model.NewClipCount);
        Assert.Empty(model.Clips);
    });

    [Fact]
    public void QualityDragNotifiesPersistenceOnlyAtCommit() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        var commits = 0;
        model.PreferencesChanged += (_, _) => commits++;
        model.Quality = 40; model.Quality = 50; model.Quality = 65;
        Assert.Equal(0, commits);
        model.CommitQuality();
        Assert.Equal(1, commits);
        Assert.Equal(65, model.Preferences.Quality);
        model.CommitQuality();
        Assert.Equal(1, commits);
    });

    [Fact]
    public void LongNewFolderIsRefusedBeforeCreatingItsLibrary() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        var directory = Path.Combine(fixture.Directory, new string('a', 260 - fixture.Directory.Length - 38));
        Assert.Equal(260, Path.Combine(directory, Guid.Empty.ToString("N") + ".mp4").Length);
        var commits = 0;
        model.PreferencesChanged += (_, _) => commits++;

        await model.SetStorageDirectoryAsync(directory);

        Assert.Equal(ClipsSettings.RecordingPathTooLongMessage, model.Error);
        Assert.Equal(fixture.Directory, model.StorageDirectory);
        Assert.Equal(fixture.Directory, model.Preferences.StorageDirectory);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(0, commits);
    });

    [Fact]
    public void LongExistingLibraryCannotResumeRecordingButCanStillBrowseAndExport() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Directory, new string('a', 260 - fixture.Directory.Length - 38));
        var library = new ClipLibrary(directory);
        var target = await library.ReserveSaveAsync(new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        Assert.Equal(260, target.MediaPath.Length);
        await File.WriteAllBytesAsync(target.MediaPath, FakeRecorder.Bytes, TestContext.Current.CancellationToken);
        await library.CommitFinalizedAsync(target.Id, FakeRecorder.Media, TestContext.Current.CancellationToken);
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = new ClipsViewModel(new() { StorageDirectory = directory, Enabled = true },
            recorder, Dispatcher.CurrentDispatcher);
        await model.InitializeAsync();
        var card = Assert.Single(model.Clips);
        Assert.Equal(target.MediaPath, await model.SelectForPlaybackAsync(card));
        var commits = 0;
        model.PreferencesChanged += (_, _) => commits++;

        await model.RestoreEnabledPreferenceAsync();
        Assert.Equal(ClipsSettings.RecordingPathTooLongMessage, model.Error);
        await model.ToggleAsync();
        Assert.Equal(ClipsSettings.RecordingPathTooLongMessage, model.Error);
        Assert.Equal(0, recorder.ToggleCalls);
        Assert.False(model.ClippingEnabled);
        Assert.Equal(directory, model.Preferences.StorageDirectory);
        Assert.True(model.Preferences.Enabled);
        Assert.Equal(0, commits);
        Assert.True(model.HasSelection);
        Assert.True(model.CanExport);

        var destination = Path.Combine(fixture.Directory, "export.mp4");
        await model.ExportSelectedAsync(destination);
        Assert.False(model.HasError);
        Assert.Equal(FakeRecorder.Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(target.MediaPath));
        Assert.Equal(directory, model.StorageDirectory);
        Assert.Equal("Exported", card.ReviewState);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedClipAndExportUpdateTheSameBoundedLibrary(bool silent) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder { SilentSave = silent };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        Assert.False(model.HasError);
        Assert.Equal(silent ? "Clip saved without audio. Game audio was unavailable." : "Clip saved.", model.Notice);
        var card = Assert.Single(model.Clips);
        Assert.Equal(!silent, card.Entry.Media.HasAudio);
        Assert.Equal(1, model.NewClipCount);
        await model.SelectForPlaybackAsync(card);
        var destination = Path.Combine(fixture.Directory, "export.mp4");
        await model.ExportSelectedAsync(destination);
        Assert.Equal(FakeRecorder.Bytes, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal("Exported", card.ReviewState);
        Assert.Equal(0, model.NewClipCount);
    });

    [Fact]
    public void FailureReportChangesNotifyThePageAndCopyFeedbackKeepsTheErrorVisible() => OnDispatcher(() =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        using var model = fixture.Model(recorder);
        var changes = new List<string?>();
        model.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        Assert.False(model.HasFailureReport);
        recorder.FailureReport = ClipFailureReport.Build("encoder_failed", new(60, 1080, 60, 100), null);
        recorder.Set(new(ClipRecorderState.Error, false, true, false, "Video encoding failed."));
        Assert.True(model.HasFailureReport);
        Assert.Contains(nameof(ClipsViewModel.HasFailureReport), changes);
        Assert.Contains(nameof(ClipsViewModel.FailureReport), changes);
        model.ReportCopyCompleted(true);
        Assert.Contains("Error details copied", model.Notice, StringComparison.Ordinal);
        Assert.Equal("Video encoding failed.", model.RecorderStatus);
        Assert.True(model.HasDashboardNotice);
        model.ReportCopyCompleted(false);
        Assert.Contains("clipboard is busy", model.Notice, StringComparison.Ordinal);
        Assert.Equal("Video encoding failed.", model.RecorderStatus);
        recorder.FailureReport = "";
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips."));
        Assert.False(model.HasFailureReport);
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispClipsViewModel", Guid.NewGuid().ToString("N"));
        public ClipsViewModel Model(IClipRecorder recorder) => new(new() { StorageDirectory = Directory }, recorder, Dispatcher.CurrentDispatcher);
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }

    private sealed class FakeRecorder : IClipRecorder
    {
        public static byte[] Bytes { get; } = [1, 2, 3, 4];
        public static FinalizedClipMedia Media => new(Bytes.Length, 1920, 1080, 60, 0, 600_000_000, true);
        public ClipRecorderSnapshot Snapshot { get; private set; } = new(ClipRecorderState.Unavailable, false, false, false, "Recorder unavailable");
        public string FailureReport { get; set; } = "";
        public event EventHandler? StateChanged;
        public int ToggleCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public bool FailSave { get; init; }
        public Exception? SaveFailure { get; init; }
        public ClipRecorderSnapshot? SaveFault { get; init; }
        public TaskCompletionSource? SaveGate { get; init; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SilentSave { get; init; }
        public void Set(ClipRecorderSnapshot snapshot) { Snapshot = snapshot; StateChanged?.Invoke(this, EventArgs.Empty); }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); ToggleCalls++;
            Set(enabled ? new(ClipRecorderState.WaitingForGame, true, true, false, "Waiting for Forza") :
                new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
            return Task.CompletedTask;
        }
        public async Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken)
        {
            SaveCalls++; SaveEntered.TrySetResult();
            if (SaveGate is not null) await SaveGate.Task.WaitAsync(cancellationToken);
            if (SaveFault is not null) Set(SaveFault);
            if (SaveFailure is not null) throw SaveFailure;
            if (FailSave) throw new IOException("Synthetic failure");
            await using var file = new FileStream(target.MediaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await file.WriteAsync(Bytes, cancellationToken);
            return Media with { HasAudio = !SilentSave };
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
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "Clip model test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
