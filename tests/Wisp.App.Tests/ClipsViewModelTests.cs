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
    public void LosslessChoiceReachesRecorderAndPreservesCompressedQuality() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        Assert.False(model.LosslessVideo);
        model.Quality = 100;
        model.LosslessVideo = true;
        Assert.False(model.CanEditCompressionQuality);
        model.Quality = 10;
        Assert.Equal(100, model.Quality);
        await model.ToggleAsync();
        Assert.True(recorder.LastRecording!.LosslessVideo);
        model.LosslessVideo = false;
        Assert.True(model.LosslessVideo);
        await model.ToggleAsync();
        model.LosslessVideo = false;
        Assert.True(model.CanEditCompressionQuality);
        Assert.Equal(100, model.Quality);
        await model.ToggleAsync();
        Assert.False(recorder.LastRecording!.LosslessVideo);
    });

    [Fact]
    public void AudioDefaultsToForzaAndCannotChangeDuringRecording() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder { BorderlessAccess = ClipBorderlessAccessResult.Allowed };
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        Assert.False(model.CaptureSystemAudio);
        model.CaptureSystemAudio = true;
        Assert.True(model.Preferences.CaptureSystemAudio);
        await model.ToggleAsync();
        Assert.True(recorder.LastRecording!.CaptureSystemAudio);
        Assert.False(recorder.LastShowCaptureBorder);
        Assert.Equal(0, recorder.PermissionCalls);
        Assert.Equal(0, recorder.PermissionChecks);
        model.CaptureSystemAudio = false;
        Assert.True(model.CaptureSystemAudio);
        await model.ToggleAsync();
        model.CaptureSystemAudio = false;
        await model.ToggleAsync();
        Assert.Equal(0, recorder.PermissionCalls);
        Assert.False(recorder.LastRecording!.CaptureSystemAudio);
    });

    [Fact]
    public void SavedClipStaysPrivateAndOnlyExplicitExportCreatesOneFile() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var privateDirectory = Path.Combine(fixture.Directory, "private");
        var exportDirectory = Path.Combine(fixture.Directory, "exports");
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = new ClipsViewModel(new(), recorder, Dispatcher.CurrentDispatcher, libraryDirectory: privateDirectory);
        await model.InitializeAsync();
        Assert.True(model.CanToggle);
        await model.ToggleAsync();
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording"));
        await model.SaveClipAsync();
        var card = Assert.Single(model.Clips);
        Assert.Single(Directory.GetFiles(privateDirectory, "*.mp4"));
        Assert.False(Directory.Exists(exportDirectory));
        var playback = await model.SelectForPlaybackAsync(card);
        await model.ExportSelectedToFolderAsync();
        Assert.Contains("Choose an export folder", model.Error, StringComparison.Ordinal);
        await model.SetStorageDirectoryAsync(exportDirectory);
        Assert.True(model.ClippingEnabled);
        Assert.Same(card, model.SelectedClip);
        Assert.False(Directory.Exists(exportDirectory));
        await model.ExportSelectedToFolderAsync();
        var exported = Assert.Single(Directory.GetFiles(exportDirectory, "*.mp4"));
        Assert.Equal(await File.ReadAllBytesAsync(playback!, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(exported, TestContext.Current.CancellationToken));
        Assert.False(model.CanExport);
        Assert.Equal("Export successful.", model.PreviewExportStatus);
        await model.ExportSelectedToFolderAsync();
        Assert.Single(Directory.GetFiles(exportDirectory, "*.mp4"));
        Assert.Equal("Export successful.", model.PreviewExportStatus);
        model.ClosePlayback();
        Assert.False(model.HasPreviewExportStatus);
        await model.SelectForPlaybackAsync(card);
        Assert.True(model.CanExport);
        await model.ExportSelectedToFolderAsync();
        Assert.Single(Directory.GetFiles(exportDirectory, "*.mp4"));
        Assert.Contains("no duplicate", model.PreviewExportStatus, StringComparison.Ordinal);
        Assert.False(model.CanExport);
        Assert.Same(card, model.SelectedClip);
    });

    [Fact]
    public void FailedExportShowsPreviewFeedbackAndAllowsRetry() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        await model.SelectForPlaybackAsync(Assert.Single(model.Clips));
        var occupied = Path.Combine(fixture.Directory, "occupied.mp4");
        await File.WriteAllTextAsync(occupied, "keep", TestContext.Current.CancellationToken);
        await model.ExportSelectedAsync(occupied);
        Assert.StartsWith("Export failed.", model.PreviewExportStatus, StringComparison.Ordinal);
        Assert.True(model.CanExport);
        Assert.Equal("keep", await File.ReadAllTextAsync(occupied, TestContext.Current.CancellationToken));
        await model.ExportSelectedAsync(Path.Combine(fixture.Directory, "retry.mp4"));
        Assert.Equal("Export successful.", model.PreviewExportStatus);
        Assert.False(model.CanExport);
    });

    [Fact]
    public void CompletingExportAfterPreviewClosesDoesNotDisableTheNextPreview() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        var card = Assert.Single(model.Clips);
        await model.SelectForPlaybackAsync(card);
        var closed = false;
        model.PropertyChanged += (_, change) =>
        {
            if (!closed && change.PropertyName == nameof(model.IsBusy) && model.IsBusy)
            { closed = true; model.ClosePlayback(); }
        };
        var destination = Path.Combine(fixture.Directory, "after-close.mp4");
        await model.ExportSelectedAsync(destination);
        Assert.True(closed);
        Assert.True(File.Exists(destination));
        Assert.False(model.HasSelection);
        Assert.False(model.HasPreviewExportStatus);
        await model.SelectForPlaybackAsync(card);
        Assert.True(model.CanExport);
        Assert.False(model.HasPreviewExportStatus);
    });

    [Fact]
    public void FailedLegacyImportStaysVisibleAfterAutomaticRecordingRestore() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, ClipLibrary.IndexFileName), "invalid", TestContext.Current.CancellationToken);
        var recorder = new FakeRecorder { BorderlessAccess = ClipBorderlessAccessResult.Allowed };
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = new ClipsViewModel(new() { StorageDirectory = fixture.Directory, Enabled = true }, recorder,
            Dispatcher.CurrentDispatcher, libraryDirectory: Path.Combine(fixture.Directory, "private"));
        await model.InitializeAsync();
        Assert.Contains("could not be imported", model.Error, StringComparison.Ordinal);
        await model.RestoreEnabledPreferenceAsync();
        Assert.True(model.ClippingEnabled);
        Assert.Equal(0, recorder.PermissionCalls);
        Assert.Equal(0, recorder.PermissionChecks);
        Assert.Contains("could not be imported", model.Error, StringComparison.Ordinal);
        Assert.Equal(fixture.Directory, model.Preferences.LegacyLibraryDirectory);
    });

    [Fact]
    public void PendingLegacySavesStayVisibleAcrossRestartsUntilCompleted() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var token = TestContext.Current.CancellationToken;
        var legacyDirectory = Path.Combine(fixture.Directory, "legacy");
        var privateDirectory = Path.Combine(fixture.Directory, "private");
        var legacy = new ClipLibrary(legacyDirectory);
        var recording = new ClipRecordingSpec(60, 1080, 60, 75);
        var ready = await legacy.ReserveSaveAsync(recording, token);
        await File.WriteAllBytesAsync(ready.MediaPath, FakeRecorder.Bytes, token);
        await legacy.CommitFinalizedAsync(ready.Id, FakeRecorder.Media, token);
        var pending = await legacy.ReserveSaveAsync(recording, token);
        await File.WriteAllBytesAsync(pending.MediaPath, FakeRecorder.Bytes, token);
        var originalIndex = await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, ClipLibrary.IndexFileName), token);
        var settings = new ClipsSettings { StorageDirectory = legacyDirectory };

        for (var start = 0; start < 2; start++)
        {
            using var model = new ClipsViewModel(settings, new FakeRecorder(), Dispatcher.CurrentDispatcher,
                libraryDirectory: privateDirectory);
            var persisted = 0;
            model.PreferencesChanged += (_, _) => persisted++;
            await model.InitializeAsync();
            Assert.Contains("unfinished saves", model.Error, StringComparison.Ordinal);
            Assert.Equal(legacyDirectory, model.Preferences.LegacyLibraryDirectory);
            Assert.Equal(0, persisted);
            Assert.Equal(ready.Id, Assert.Single(model.Clips).Id);
            Assert.Single(Directory.GetFiles(privateDirectory, "*.mp4"));
            Assert.False(File.Exists(Path.Combine(privateDirectory, $"{pending.Id:N}.mp4")));
            Assert.Equal(originalIndex, await File.ReadAllBytesAsync(Path.Combine(legacyDirectory, ClipLibrary.IndexFileName), token));
            Assert.Equal(FakeRecorder.Bytes, await File.ReadAllBytesAsync(pending.MediaPath, token));
            settings = model.Preferences;
        }

        await legacy.CommitFinalizedAsync(pending.Id, FakeRecorder.Media, token);
        using var completed = new ClipsViewModel(settings, new FakeRecorder(), Dispatcher.CurrentDispatcher,
            libraryDirectory: privateDirectory);
        var finalPersisted = 0;
        completed.PreferencesChanged += (_, _) => finalPersisted++;
        await completed.InitializeAsync();
        Assert.False(completed.HasError);
        Assert.Equal("", completed.Preferences.LegacyLibraryDirectory);
        Assert.Equal(1, finalPersisted);
        Assert.Equal(2, completed.Clips.Count);
        Assert.Single(completed.Clips, clip => clip.Id == ready.Id);
        Assert.Single(completed.Clips, clip => clip.Id == pending.Id);
        Assert.Equal(2, Directory.GetFiles(privateDirectory, "*.mp4").Length);
        Assert.Equal(2, (await legacy.GetPageAsync(0, token)).Clips.Count);
        Assert.Equal(FakeRecorder.Bytes, await File.ReadAllBytesAsync(ready.MediaPath, token));
        Assert.Equal(FakeRecorder.Bytes, await File.ReadAllBytesAsync(pending.MediaPath, token));
    });

    [Fact]
    public void PreviewWithoutLibraryDoesNotOpenOrCreateLegacyStorage() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = new ClipsViewModel(new() { StorageDirectory = fixture.Directory }, new FakeRecorder(),
            Dispatcher.CurrentDispatcher, libraryDirectory: "");
        await model.InitializeAsync();
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Empty(model.Clips);
        Assert.False(model.CanToggle);
    });

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
        Assert.Contains("Toggle clipping", model.ShortcutStatus, StringComparison.Ordinal);
        Assert.Contains(replacement.ToString(), model.ShortcutStatus, StringComparison.Ordinal);
        Assert.Contains("Recording settings", model.ShortcutStatus, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(ClipBorderlessAccessResult.Allowed)]
    [InlineData(ClipBorderlessAccessResult.Denied)]
    [InlineData(ClipBorderlessAccessResult.Unavailable)]
    public void DesktopRecordingDoesNotDependOnWgcBorderPermission(ClipBorderlessAccessResult access) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder { BorderlessAccess = access };
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.ToggleAsync();
        Assert.Equal(0, recorder.PermissionCalls);
        Assert.Equal(0, recorder.PermissionChecks);
        Assert.Equal(new[] { "enable" }, recorder.EnableOrder);
        Assert.True(model.ClippingEnabled);
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        await model.SaveClipAsync();
        Assert.Single(model.Clips);
        await model.ToggleAsync();
        Assert.Equal(0, recorder.PermissionCalls);
    });

    [Fact]
    public void RestoringEnabledPreferenceDoesNotRequestBorderlessPermission() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = new ClipsViewModel(new() { StorageDirectory = fixture.Directory, Enabled = true }, recorder, Dispatcher.CurrentDispatcher, libraryDirectory: fixture.Directory);
        await model.InitializeAsync();
        await model.RestoreEnabledPreferenceAsync();
        Assert.True(model.ClippingEnabled);
        Assert.Equal(0, recorder.PermissionCalls);
        Assert.Equal(0, recorder.PermissionChecks);
        Assert.Equal(new[] { "enable" }, recorder.EnableOrder);
    });

    [Fact]
    public void SuspendedRuntimeCannotEnableRecording() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Disabled, false, true, false, "Clipping is off"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        model.SetRuntimeActive(false);
        await model.ToggleAsync();
        Assert.Equal(0, recorder.ToggleCalls);
        Assert.False(model.ClippingEnabled);
    });

    [Fact]
    public void StartupShortcutStatusIdentifiesTheFailedActionAndRecovery()
    {
        var chord = new OverlayHotkeyChord(OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt, System.Windows.Input.Key.F9);
        var failed = AppController.ClipShortcutStatus("Save a clip", true, chord, "another application is using this shortcut");
        Assert.Contains("Save a clip", failed, StringComparison.Ordinal);
        Assert.Contains(chord.ToString(), failed, StringComparison.Ordinal);
        Assert.Contains("Recording settings", failed, StringComparison.Ordinal);
        Assert.Equal("Save a clip shortcut is off.", AppController.ClipShortcutStatus("Save a clip", false, chord, null));
        Assert.Equal($"Save a clip: {chord}.", AppController.ClipShortcutStatus("Save a clip", true, chord, null));
    }

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
    public void TwoQuickSaveRequestsCommitBothAndThirdReportsLimit() => OnDispatcher(async () =>
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
            Assert.True(model.HasQueuedSave);
            Assert.Contains("end when saving starts", model.SaveQueueStatus, StringComparison.Ordinal);
            Assert.False(model.CanRequestSave);
            await model.SaveClipAsync();
            Assert.Contains("Two clip saves are already pending", model.Notice, StringComparison.Ordinal);
            Assert.Equal(1, recorder.SaveCalls);
            Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
        }
        finally { gate.TrySetResult(); await saving; }
        Assert.Equal(2, recorder.SaveCalls);
        Assert.Equal(2, model.Clips.Count);
        Assert.False(model.HasQueuedSave);
        Assert.Empty(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
        Assert.False(model.IsBusy);
    });

    [Fact]
    public void PreparingSavesWaitForPacketsWithoutPrematureReservations() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Preparing, true, true, false, "Preparing game capture"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        await model.SaveClipAsync();
        Assert.True(model.HasQueuedSave);
        Assert.False(model.CanRequestSave);
        Assert.Contains("end when saving starts", model.SaveQueueStatus, StringComparison.Ordinal);
        Assert.Equal(0, recorder.SaveCalls);
        Assert.Empty(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) =>
        {
            if (!model.IsBusy && model.Clips.Count == 2 && !model.HasQueuedSave) finished.TrySetResult();
        };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, recorder.SaveCalls);
        Assert.Contains("60.0 s", model.Notice, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(ClipRecorderState.Disabled)]
    [InlineData(ClipRecorderState.WaitingForGame)]
    [InlineData(ClipRecorderState.Paused)]
    [InlineData(ClipRecorderState.Reconnecting)]
    [InlineData(ClipRecorderState.Error)]
    [InlineData(ClipRecorderState.Unavailable)]
    [InlineData(ClipRecorderState.Stopping)]
    public void InterruptedRecordingCancelsQueuedMomentInsteadOfSavingLater(ClipRecorderState interrupted) => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Preparing, true, true, false, "Preparing game capture"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        recorder.Set(new(interrupted, true, true, false, "Capture interrupted"));
        Assert.False(model.HasQueuedSave);
        Assert.True(model.HasSaveQueueStatus);
        Assert.Contains("Queued saves cancelled", model.SaveQueueStatus, StringComparison.Ordinal);
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        Assert.Equal(0, recorder.SaveCalls);
        Assert.Empty(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
    });

    [Fact]
    public void RuntimeSuspendCancelsQueuedMoment() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Preparing, true, true, false, "Preparing game capture"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        model.SetRuntimeActive(false);
        Assert.False(model.HasQueuedSave);
        Assert.Contains("Queued saves cancelled", model.SaveQueueStatus, StringComparison.Ordinal);
        model.SetRuntimeActive(true);
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        Assert.Equal(0, recorder.SaveCalls);
    });

    [Fact]
    public void FailedActiveSaveCancelsSecondIntentWithoutRetrying() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new FakeRecorder { SaveGate = gate, FailSave = true };
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        var first = model.SaveClipAsync();
        try
        {
            await recorder.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await model.SaveClipAsync();
            Assert.True(model.HasQueuedSave);
        }
        finally { gate.TrySetResult(); await first; }
        Assert.Equal(1, recorder.SaveCalls);
        Assert.False(model.HasQueuedSave);
        Assert.Contains("previous clip could not be saved", model.SaveQueueStatus, StringComparison.Ordinal);
        Assert.True(model.HasError);
        Assert.Empty(model.Clips);
        Assert.Single(await new ClipLibrary(fixture.Directory).ListPendingAsync(TestContext.Current.CancellationToken));
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
    public void ChangingExportFolderPreservesLibraryAndPendingReminder() => OnDispatcher(async () =>
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
            Assert.Equal(1, model.NewClipCount);
        }
        finally { gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, model.NewClipCount);
        Assert.Single(model.Clips);
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
    public void ChoosingExportFolderDoesNotCreateOrChangeThePrivateLibrary() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        using var model = fixture.Model(new FakeRecorder());
        await model.InitializeAsync();
        var directory = Path.Combine(fixture.Directory, new string('a', 260 - fixture.Directory.Length - 38));
        Assert.Equal(260, Path.Combine(directory, Guid.Empty.ToString("N") + ".mp4").Length);
        var commits = 0;
        model.PreferencesChanged += (_, _) => commits++;

        await model.SetStorageDirectoryAsync(directory);

        Assert.Empty(model.Error);
        Assert.Equal(directory, model.StorageDirectory);
        Assert.Equal(directory, model.Preferences.StorageDirectory);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(1, commits);
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
            recorder, Dispatcher.CurrentDispatcher, libraryDirectory: directory);
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
        Assert.Equal(silent
            ? "Clip saved · 60.0 s. Game audio was unavailable. Select it below to watch or export."
            : "Clip saved · 60.0 s. Select it below to watch or export.", model.Notice);
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
    public void SavedCardAppearsBeforeCompletionAndKeepsTheCurrentPlaybackSelection() => OnDispatcher(async () =>
    {
        using var fixture = new Fixture();
        await new ClipLibrary(fixture.Directory).ReserveSaveAsync(new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        var recorder = new FakeRecorder();
        recorder.Set(new(ClipRecorderState.Buffering, true, true, true, "Recording game clips"));
        using var model = fixture.Model(recorder);
        await model.InitializeAsync();
        await model.SaveClipAsync();
        var selected = Assert.Single(model.Clips);
        await model.SelectForPlaybackAsync(selected);
        var insertedWhilePublishing = false;
        model.Clips.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add &&
                change.NewStartingIndex == 0 && change.NewItems?[0] is ClipCardItem added && added.Id != selected.Id)
            {
                insertedWhilePublishing |= model.IsBusy;
                Assert.StartsWith("1 unfinished", model.PendingText, StringComparison.Ordinal);
            }
        };
        await model.SaveClipAsync();
        Assert.True(insertedWhilePublishing);
        Assert.Equal(2, model.Clips.Count);
        Assert.Same(selected, model.SelectedClip);
        Assert.Contains(selected, model.Clips);
        Assert.True(selected.IsSelected);
        model.ClosePlayback();
        Assert.False(selected.IsSelected);
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
        Assert.Contains("Details copied", model.Notice, StringComparison.Ordinal);
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
        public ClipsViewModel Model(IClipRecorder recorder) => new(new() { StorageDirectory = Directory }, recorder, Dispatcher.CurrentDispatcher, libraryDirectory: Directory);
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
        public int PermissionCalls { get; private set; }
        public int PermissionChecks { get; private set; }
        public bool LastShowCaptureBorder { get; private set; }
        public ClipRecordingSpec? LastRecording { get; private set; }
        public ClipBorderlessAccessResult BorderlessAccess { get; init; } = ClipBorderlessAccessResult.Unavailable;
        public TaskCompletionSource? PermissionGate { get; init; }
        public TaskCompletionSource PermissionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> EnableOrder { get; } = [];
        public int SaveCalls { get; private set; }
        public bool FailSave { get; init; }
        public Exception? SaveFailure { get; init; }
        public ClipRecorderSnapshot? SaveFault { get; init; }
        public TaskCompletionSource? SaveGate { get; init; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SilentSave { get; init; }
        public void Set(ClipRecorderSnapshot snapshot) { Snapshot = snapshot; StateChanged?.Invoke(this, EventArgs.Empty); }
        public async Task<ClipBorderlessAccessResult> RequestBorderlessAccessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); PermissionCalls++; EnableOrder.Add("permission");
            PermissionEntered.TrySetResult();
            if (PermissionGate is not null) await PermissionGate.Task.WaitAsync(cancellationToken);
            return BorderlessAccess;
        }
        public Task<ClipBorderlessAccessResult> CheckBorderlessAccessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); PermissionChecks++; EnableOrder.Add("check");
            return Task.FromResult(BorderlessAccess);
        }
        public Task SetEnabledAsync(bool enabled, ClipRecordingSpec recording, CancellationToken cancellationToken, bool showCaptureBorder = false)
        {
            cancellationToken.ThrowIfCancellationRequested(); ToggleCalls++;
            LastShowCaptureBorder = showCaptureBorder;
            LastRecording = recording;
            EnableOrder.Add(enabled ? "enable" : "disable");
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
