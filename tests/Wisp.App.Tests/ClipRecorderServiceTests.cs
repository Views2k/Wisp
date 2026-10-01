using System.IO;
using System.Reflection;
using System.Threading.Channels;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipRecorderServiceTests
{
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    private static readonly RecorderTarget Target = new(42, 123, 456);
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, ClipBorderlessAccessResult.Allowed, true)]
    [InlineData(true, ClipBorderlessAccessResult.Allowed, false)]
    [InlineData(false, ClipBorderlessAccessResult.Denied, false)]
    [InlineData(false, ClipBorderlessAccessResult.Unavailable, false)]
    public async Task CaptureBorderPreferenceAndCheckedGrantAreAppliedBeforeSessionOpen(
        bool showCaptureBorder, ClipBorderlessAccessResult access, bool expectedAllowed)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var checks = 0; var requests = 0;
        await using var service = new ClipRecorderService(() => fixture.Directory, factory.Create, true,
            checkBorderless: _ => { checks++; return Task.FromResult(access); },
            requestBorderless: _ => { requests++; return Task.FromResult(ClipBorderlessAccessResult.Allowed); });
        Assert.Equal(access, await service.CheckBorderlessAccessAsync(TestToken));
        await service.SetEnabledAsync(true, Recording, TestToken, showCaptureBorder);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(expectedAllowed, session.BorderlessAllowedAtOpen);
        Assert.Equal(expectedAllowed, session.BorderlessAllowed);
        Assert.Equal(1, checks);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ShowingCaptureBorderOverridesAnAlreadyGrantedExplicitRequest()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = new ClipRecorderService(() => fixture.Directory, factory.Create, true,
            requestBorderless: _ => Task.FromResult(ClipBorderlessAccessResult.Allowed));
        Assert.Equal(ClipBorderlessAccessResult.Allowed, await service.RequestBorderlessAccessAsync(TestToken));
        await service.SetEnabledAsync(true, Recording, TestToken, showCaptureBorder: true);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.False(session.BorderlessAllowedAtOpen);
        Assert.False(session.BorderlessAllowed);
    }

    [Fact]
    public async Task FailureReportSurvivesCleanupAndRetryUntilNewRecordingActuallyBuffers()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.DiagnosticAfterDisposal = RecorderFailureDiagnostic.Parse(RecorderFailureDiagnosticTests.Line());
        first.Emit("error", "helper_exited");
        await ReconcileAsync(service);
        Assert.True(first.Disposed.Task.IsCompleted);
        Assert.Contains("Reason: encoder_failed", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Stage: video_submit", service.FailureReport, StringComparison.Ordinal);
        var report = service.FailureReport;

        await service.SetEnabledAsync(false, Recording, TestToken);
        Assert.Equal(report, service.FailureReport);
        await service.SetEnabledAsync(true, Recording, TestToken);
        Assert.Equal(report, service.FailureReport);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(report, service.FailureReport);
        second.Emit("buffering", "none");
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task FailureReportWithoutNativeDetailRemainsAvailableAfterServiceDisposal()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("error", "encoder_failed");
        await service.DisposeAsync();
        Assert.Contains("Reason: encoder_failed", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Native detail: not available", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("60s, 1080p, 60fps, quality 75", service.FailureReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongPersistedStorageCannotEnableOrLaunchEvenAfterStorageValidation()
    {
        var factory = new SessionFactory();
        var directory = @"C:\" + new string('a', 220);
        await using var service = new ClipRecorderService(() => directory, factory.Create, helperAvailable: true,
            validateStorage: _ => Task.FromResult(directory));
        var error = await Assert.ThrowsAsync<IOException>(() => service.SetEnabledAsync(true, Recording, TestToken));
        Assert.Equal(ClipsSettings.RecordingPathTooLongMessage, error.Message);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        await ReconcileAsync(service);
        Assert.Equal(ClipRecorderState.Disabled, service.Snapshot.State);
        Assert.False(service.Snapshot.Enabled);
        Assert.False(service.Snapshot.CanSave);
        Assert.Equal(0, factory.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableOrCancellationDuringStorageValidationCannotStartHelper(bool cancel)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var validation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        await using var service = new ClipRecorderService(() => fixture.Directory, factory.Create, helperAvailable: true,
            validateStorage: _ => validation.Task);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var pendingEnable = service.SetEnabledAsync(true, Recording, cancellation.Token);
        Assert.False(pendingEnable.IsCompleted);
        if (cancel) await cancellation.CancelAsync();
        else await service.SetEnabledAsync(false, Recording, TestToken);
        validation.SetResult(fixture.Directory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingEnable);
        Assert.False(service.Snapshot.Enabled);
        Assert.False(service.Snapshot.CanSave);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task DisabledOrArmedWithoutGameNeverCreatesAHelper()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        Assert.Equal(ClipRecorderState.Disabled, service.Snapshot.State);
        Assert.Equal(0, factory.Count);
        await service.SetEnabledAsync(true, Recording, TestToken);
        Assert.True(service.Snapshot.Enabled);
        Assert.Equal(ClipRecorderState.WaitingForGame, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);
        Assert.Equal(0, factory.Count);
        await service.SetEnabledAsync(false, Recording, TestToken);
        Assert.False(service.Snapshot.Enabled);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task StartAckAndWaitingMessageDoNotInventEncodedFrames()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(ClipRecorderState.Preparing, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);
        session.Emit("buffering", "none");
        Assert.Equal(ClipRecorderState.Buffering, service.Snapshot.State);
        Assert.True(service.Snapshot.CanSave);
        for (var index = 0; index < 100; index++) service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        Assert.Equal(1, factory.Count);
        Assert.Equal(0, session.Stops);
        await service.SetEnabledAsync(false, Recording, TestToken);
        Assert.Equal(1, factory.Count);
        Assert.True(session.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ResizeAcceptsNewObservationWithoutWaitingForTheBackoff()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        first.Emit("reconnecting", "window_resized");
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.True(service.Snapshot.Enabled);
        Assert.Equal(ClipRecorderState.Reconnecting, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);
        for (var index = 0; index < 100; index++) service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        Assert.Equal(1, factory.Count);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        second.Emit("buffering", "none");
        Assert.Equal(ClipRecorderState.Buffering, service.Snapshot.State);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public async Task HelperExitKeepsIntentAndNewTargetCancelsTheOldRetry()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("error", "helper_exited");
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(ClipRecorderState.Reconnecting, service.Snapshot.State);
        Assert.True(service.Snapshot.Enabled);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(2, factory.Count);
    }

    [Theory]
    [InlineData("encoder_failed", "Video encoding failed.")]
    [InlineData("protocol_error", "The recorder connection failed.")]
    [InlineData("helper_start_failed", "The recorder could not start.")]
    [InlineData("storage_failed", "The clip folder could not be written.")]
    [InlineData("unsupported_gpu", "A compatible hardware video encoder is unavailable.")]
    public async Task RecorderFailureKeepsItsSpecificStatusAfterLateCancellation(string reason, string expected)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);

        session.Emit("error", reason);
        session.Emit("error", "cancelled");
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);

        Assert.Equal(ClipRecorderState.Error, service.Snapshot.State);
        Assert.False(service.Snapshot.Enabled);
        Assert.False(service.Snapshot.CanSave);
        Assert.StartsWith(expected, service.Snapshot.Status, StringComparison.Ordinal);
        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public async Task ActualGameExitKeepsIntentAndStartsNewIdentityOnce()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        service.ObserveTarget(null, service.TargetObservationGeneration);
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.True(service.Snapshot.Enabled);
        service.ObserveTarget(new(new(43, 321, 789), 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(2, factory.Count);
    }

    [Fact]
    public async Task SaveWaitsForFinalizationAndReportsSilentVideoHonestly()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "audio_unavailable");
        var id = Guid.NewGuid();
        var save = service.SaveAsync(new(id, DateTimeOffset.UtcNow, Recording, Path.Combine(fixture.Directory, $"{id:N}.mp4")), TestToken);
        Assert.Equal(ClipRecorderState.Saving, service.Snapshot.State);
        Assert.False(save.IsCompleted);
        session.Emit("buffering", "audio_unavailable");
        Assert.Equal(ClipRecorderState.Saving, service.Snapshot.State);
        session.Saved.SetResult(new(1024, 1920, 1080, 60, 1, 10_000_001, false));
        var result = await save;
        Assert.False(result.HasAudio);
        Assert.Equal(ClipRecorderState.Buffering, service.Snapshot.State);
        Assert.Contains("audio is unavailable", service.Snapshot.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OldSessionSnapshotCannotEnableSaveForNewSession()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        var revisionField = typeof(ClipRecorderService).GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldRevision = Assert.IsType<long>(revisionField.GetValue(service));
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        var currentRevision = Assert.IsType<long>(revisionField.GetValue(service));
        var publish = typeof(ClipRecorderService).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stale = new ClipRecorderSnapshot(ClipRecorderState.Buffering, true, true, true, "Old session");
        publish.Invoke(service, [stale, oldRevision, second]);
        publish.Invoke(service, [stale, currentRevision, first]);
        Assert.Equal(ClipRecorderState.Preparing, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);
        second.Emit("buffering", "none");
        Assert.True(service.Snapshot.CanSave);
    }

    [Fact]
    public async Task UnconfirmedShutdownCannotLaunchAnotherHelper()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory { FailCleanup = true };
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        await service.SetEnabledAsync(false, Recording, TestToken);
        Assert.Equal(ClipRecorderState.Error, service.Snapshot.State);
        Assert.False(service.Snapshot.CanEnable);
        await Assert.ThrowsAsync<RecorderClientException>(() => service.SetEnabledAsync(true, Recording, TestToken));
        Assert.Equal(1, factory.Count);
        session.FailCleanup = false;
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ReenableRequiresFreshObservationAndRejectsLatePreviousGeneration()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        var previousGeneration = service.TargetObservationGeneration;
        service.ObserveTarget(new(Target, 1), previousGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");

        await service.SetEnabledAsync(false, Recording, TestToken);
        Assert.Equal(0, service.TargetObservationGeneration);
        service.ObserveTarget(new(Target, 1), previousGeneration);
        await service.SetEnabledAsync(true, Recording, TestToken);
        var currentGeneration = service.TargetObservationGeneration;
        Assert.True(currentGeneration > previousGeneration);
        service.ObserveTarget(new(Target, 1), previousGeneration);
        await ReconcileAsync(service);
        Assert.Equal(1, factory.Count);
        Assert.Equal(ClipRecorderState.WaitingForGame, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);

        var replacement = new RecorderTarget(43, 321, 789);
        service.ObserveTarget(new(replacement, 2), currentGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(replacement, second.StartedTarget);
        Assert.Equal(2, factory.Count);
    }

    [Theory]
    [InlineData("off_on")]
    [InlineData("focus_lost")]
    [InlineData("fullscreen_required")]
    [InlineData("window_minimized")]
    [InlineData("window_closed")]
    public async Task NewDemandBetweenVisibilityTicksStillClearsTheStableWindow(string transition)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await using var controller = new AppController(new AppSettings(), _ => { }, new NoStartupRegistration());
        var recorderField = typeof(AppController).GetField("_clipRecorder", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalRecorder = recorderField.GetValue(controller);
        var focus = Assert.IsType<ForzaFocusService>(typeof(AppController).GetField("_forzaFocusService",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller));
        var tracker = Assert.IsType<ForzaCaptureObservationTracker>(typeof(ForzaFocusService).GetField("_captureObservation",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(focus));
        var prepare = typeof(AppController).GetMethod("PrepareClipTargetObservation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var observe = typeof(AppController).GetMethod("ObserveClipTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var candidate = new ForzaCaptureCandidate(Target.ProcessId, Target.Window, Target.CreationFileTime, 1920, 1080);
        recorderField.SetValue(controller, service);
        try
        {
            await service.SetEnabledAsync(true, Recording, TestToken);
            prepare.Invoke(controller, null);
            tracker.Observe(candidate);
            Assert.NotNull(tracker.Observe(candidate));
            observe.Invoke(controller, null);
            var first = await factory.NextAsync();
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);

            var previousGeneration = service.TargetObservationGeneration;
            var previousObservation = focus.CaptureObservation;
            // No controller tick sees the interruption, including a brief Alt-Tab.
            if (transition == "off_on")
            {
                await service.SetEnabledAsync(false, Recording, TestToken);
                await service.SetEnabledAsync(true, Recording, TestToken);
            }
            else
            {
                first.Emit(transition == "window_closed" ? "reconnecting" : "paused", transition);
                await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
                await ReconcileAsync(service);
                Assert.Equal(transition == "window_closed" ? ClipRecorderState.WaitingForGame : ClipRecorderState.Paused, service.Snapshot.State);
                Assert.True(service.Snapshot.Enabled);
                Assert.False(service.Snapshot.CanSave);
                Assert.Equal(ClipRecorderService.ReasonText(transition), service.Snapshot.Status);
            }
            Assert.True(service.TargetObservationGeneration > previousGeneration);
            service.ObserveTarget(previousObservation, previousGeneration);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);
            Assert.NotNull(focus.CaptureObservation);
            prepare.Invoke(controller, null);
            Assert.True(focus.CaptureRequested);
            Assert.Null(focus.CaptureObservation);
            observe.Invoke(controller, null);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);

            var replacement = transition == "off_on" ? candidate with { CreationFileTime = 789 } : candidate;
            Assert.Null(tracker.Observe(replacement));
            observe.Invoke(controller, null);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);
            Assert.NotNull(tracker.Observe(replacement));
            observe.Invoke(controller, null);
            var second = await factory.NextAsync();
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            Assert.Equal(replacement.CreationFileTime, second.StartedTarget!.CreationFileTime);
            second.Emit("buffering", "none");
            Assert.True(service.Snapshot.CanSave);
        }
        finally { recorderField.SetValue(controller, originalRecorder); }
    }

    [Theory]
    [InlineData("capture_stale")]
    [InlineData("capture_reconnecting")]
    [InlineData("encoder_reconnecting")]
    [InlineData("audio_reconnecting")]
    [InlineData("scheduler_late")]
    [InlineData("window_resized")]
    [InlineData("helper_exited")]
    [InlineData("helper_timeout")]
    public async Task RecoverableInterruptionCleansOldSessionBeforeAutomaticReacquisition(string reason)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        var generation = service.TargetObservationGeneration;
        service.ObserveTarget(new(Target, 1), generation);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        first.Emit("reconnecting", reason);
        var retry = await delays.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Delay);
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(ClipRecorderState.Reconnecting, service.Snapshot.State);
        Assert.True(service.Snapshot.Enabled);
        Assert.False(service.Snapshot.CanSave);
        Assert.Equal(generation, service.TargetObservationGeneration);
        Assert.Contains("buffer restarts", service.Snapshot.Status, StringComparison.Ordinal);
        first.Emit("error", "cancelled");
        await ReconcileAsync(service);
        Assert.Equal(1, factory.Count);
        retry.Resume.TrySetResult();
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(Target, second.StartedTarget);
        Assert.True(first.Disposed.Task.IsCompleted);
        second.Emit("buffering", "none");
        Assert.True(service.Snapshot.CanSave);
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task RepeatedBriefRecoveriesBackOffAndDisableCancelsPendingRetry()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            session.Emit("buffering", "none");
            session.Emit("reconnecting", "capture_stale");
            var retry = await delays.NextAsync();
            Assert.Equal(TimeSpan.FromSeconds(seconds), retry.Delay);
            await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            retry.Resume.TrySetResult();
            session = await factory.NextAsync();
        }
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("reconnecting", "capture_stale");
        var last = await delays.NextAsync();
        var count = factory.Count;
        await service.SetEnabledAsync(false, Recording, TestToken);
        last.Resume.TrySetResult();
        await ReconcileAsync(service);
        Assert.Equal(count, factory.Count);
        Assert.False(service.Snapshot.Enabled);
    }

    [Fact]
    public async Task MinimizedTargetPausesWithoutRetryingUntilRestoredObservation()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("paused", "window_minimized");
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        await ReconcileAsync(service);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.True(service.Snapshot.Enabled);
        Assert.Equal(0, delays.Count);
        Assert.Equal(1, factory.Count);
        service.ObserveTarget(null, service.TargetObservationGeneration);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var restored = await factory.NextAsync();
        await restored.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        restored.Emit("buffering", "none");
        Assert.True(service.Snapshot.CanSave);
    }

    [Fact]
    public async Task RepeatedFrameNoticeKeepsTheSamePlayableSessionAndHistory()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        session.Emit("buffering", "capture_stale");
        await ReconcileAsync(service);
        Assert.Equal(ClipRecorderState.Buffering, service.Snapshot.State);
        Assert.True(service.Snapshot.CanSave);
        Assert.Contains("last frame", service.Snapshot.Status, StringComparison.Ordinal);
        Assert.Equal(0, delays.Count);
        Assert.Equal(1, factory.Count);
        Assert.Equal(0, session.Stops);
        session.Emit("buffering", "none");
        Assert.Equal("Recording game clips.", service.Snapshot.Status);
    }

    [Fact]
    public async Task SavePublicationErrorHasCopyableStorageEvidenceAndKeepsRecording()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        var save = service.SaveAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording, "unused.mp4"), TestToken);
        session.Saved.SetException(new RecorderClientException("clip_storage_full")
        { StorageStage = "copy_media", StorageHResult = unchecked((int)0x80070070) });
        await Assert.ThrowsAsync<RecorderClientException>(() => save);
        Assert.True(service.Snapshot.CanSave);
        Assert.True(service.Snapshot.Enabled);
        Assert.Contains("Reason: clip_storage_full", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Storage stage: copy_media", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Storage HRESULT: 0x80070070", service.FailureReport, StringComparison.Ordinal);
        var storageReport = service.FailureReport;
        session.DiagnosticAfterDisposal = RecorderFailureDiagnostic.Parse(RecorderFailureDiagnosticTests.Line());
        session.Emit("error", "encoder_failed");
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        await ReconcileAsync(service);
        Assert.Equal(storageReport, service.FailureReport);
    }

    private sealed class RecoveryClock
    {
        internal sealed record Retry(TimeSpan Delay, TaskCompletionSource Resume);
        private readonly Channel<Retry> _delays = Channel.CreateUnbounded<Retry>();
        internal int Count { get; private set; }
        internal Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            Count++;
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Writer.TryWrite(new(delay, resume));
            return resume.Task.WaitAsync(token);
        }
        internal Task<Retry> NextAsync() => _delays.Reader.ReadAsync(TestToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestToken);
    }

    private static async Task ReconcileAsync(ClipRecorderService service)
    {
        var lifecycle = Assert.IsType<SemaphoreSlim>(typeof(ClipRecorderService).GetField("_lifecycle",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
        await lifecycle.WaitAsync(TestToken);
        try
        {
            var reconcile = typeof(ClipRecorderService).GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Assert.IsAssignableFrom<Task>(reconcile.Invoke(service, null));
        }
        finally { lifecycle.Release(); }
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispRecorderServiceTests", Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal ClipRecorderService Service(SessionFactory factory,
            Func<TimeSpan, CancellationToken, Task>? recoveryDelay = null) => new(() => Directory, factory.Create, true,
                recoveryDelay: recoveryDelay ?? ((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token)));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class SessionFactory
    {
        private readonly Channel<FakeSession> _created = Channel.CreateUnbounded<FakeSession>();
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal bool FailCleanup { get; init; }
        internal IRecorderSession Create()
        {
            var session = new FakeSession { FailCleanup = FailCleanup };
            Interlocked.Increment(ref _count); _created.Writer.TryWrite(session); return session;
        }
        internal async Task<FakeSession> NextAsync() => await _created.Reader.ReadAsync(TestToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestToken);
    }
    private sealed class FakeSession : IRecorderSession
    {
        public bool BorderlessAllowed { get; set; }
        internal bool BorderlessAllowedAtOpen { get; private set; }
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<FinalizedClipMedia> Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailCleanup { get; set; }
        internal RecorderFailureDiagnostic? DiagnosticAfterDisposal { get; set; }
        public RecorderFailureDiagnostic? FailureDiagnostic => Disposed.Task.IsCompleted ? DiagnosticAfterDisposal : null;
        internal int Stops { get; private set; }
        internal RecorderTarget? StartedTarget { get; private set; }
        public event EventHandler<RecorderStateUpdate>? StateChanged;
        internal void Emit(string state, string reason) => StateChanged?.Invoke(this, new(state, reason));
        public Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); BorderlessAllowedAtOpen = BorderlessAllowed; Emit("waiting", "waiting_for_game"); return Task.CompletedTask; }
        public Task StartAsync(RecorderTarget target, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); StartedTarget = target; Started.TrySetResult(); return Task.CompletedTask; }
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) => Saved.Task.WaitAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Stops++; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return FailCleanup ? ValueTask.FromException(new RecorderClientException("helper_shutdown_failed")) : ValueTask.CompletedTask;
        }
    }
}
