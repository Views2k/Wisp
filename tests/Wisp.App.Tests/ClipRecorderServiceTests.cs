using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Wisp.App.Clips;
using Wisp.App.DebugLogging;
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
    public async Task RecoveryDetailsSurviveCleanupRetryAndNewRecordingBuffering()
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
        var history = fixture.DiagnosticHistory.Snapshot();
        Assert.Equal(2, history.Length);
        Assert.Contains("Native detail: not available", history[0].Details, StringComparison.Ordinal);
        Assert.Equal(DiagnosticComponent.Recorder, history[^1].Component);
        Assert.Contains("Reason: encoder_failed", history[^1].Details, StringComparison.Ordinal);
        Assert.Contains("Stage: video_submit", history[^1].Details, StringComparison.Ordinal);
        Assert.Contains("HRESULT: 0x80004005", history[^1].Details, StringComparison.Ordinal);
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
        Assert.StartsWith("Recent recording resets", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Reset 1: helper_exited", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Reason: encoder_failed", service.FailureReport, StringComparison.Ordinal);
        Assert.Contains("Stage: video_submit", service.FailureReport, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Directory, service.FailureReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetHistoryKeepsOnlyLatestEightWithLateSanitizedDiagnosticsInOrder()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        for (var number = 1; number <= 10; number++)
        {
            await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            session.Emit("buffering", "none");
            var fields = RecorderFailureDiagnosticTests.Fields();
            fields["reason"] = "scheduler_late";
            fields["stage"] = "video_schedule";
            fields["submittedFrames"] = number;
            fields["path"] = @"C:\PRIVATE_DO_NOT_COPY\private.mp4";
            fields["message"] = "PRIVATE_DO_NOT_COPY";
            session.DiagnosticAfterDisposal = RecorderFailureDiagnostic.Parse(JsonSerializer.SerializeToUtf8Bytes(fields));
            session.Emit("reconnecting", "scheduler_late");
            session.Emit("error", "cancelled"); // A stale callback cannot create a second reset.
            var retry = await delays.NextAsync();
            await ReconcileAsync(service);
            Assert.True(session.Disposed.Task.IsCompleted);
            retry.Resume.TrySetResult();
            session = await factory.NextAsync();
        }
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        var report = service.FailureReport;
        var resets = report.Split('\n').Where(line => line.StartsWith("Reset ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Enumerable.Range(3, 8).Select(number => $"Reset {number}: scheduler_late"), resets.Select(line => line.TrimEnd('\r')));
        Assert.DoesNotContain("Frames submitted: 1;", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Frames submitted: 2;", report, StringComparison.Ordinal);
        for (var number = 3; number <= 10; number++)
            Assert.Contains($"Frames submitted: {number};", report, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", report, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Directory, report, StringComparison.Ordinal);
        Assert.True(report.Length < 16_384);
        Assert.True(service.Snapshot.CanSave);
        var latestDiagnostic = fixture.DiagnosticHistory.Snapshot()[^1];
        Assert.Equal(DiagnosticComponent.Recorder, latestDiagnostic.Component);
        Assert.Contains("Stage: video_schedule", latestDiagnostic.Details, StringComparison.Ordinal);
        Assert.Contains("Frames submitted: 10;", latestDiagnostic.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_DO_NOT_COPY", latestDiagnostic.Details, StringComparison.Ordinal);
        await service.DisposeAsync();
        Assert.Equal(report, service.FailureReport);
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
    [InlineData("hdr_encoder_unsupported", "HDR recording needs a supported NVIDIA 10-bit HEVC encoder. Standard SDR recording is still available on compatible hardware.")]
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
        first.Emit("stopped", "window_closed");
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.True(service.Snapshot.Enabled);
        service.ObserveTarget(new(new(43, 321, 789), 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(2, factory.Count);
        second.Emit("buffering", "none");
        Assert.Contains("Reset 1: window_closed", service.FailureReport, StringComparison.Ordinal);
        Assert.Single(service.FailureReport.Split('\n'), line => line.StartsWith("Reset ", StringComparison.Ordinal));
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
        service.ObserveTarget(new(new(43, 321, 789), 2), service.TargetObservationGeneration);
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
        Assert.False(service.Snapshot.CanEnable);
        Assert.False(service.Snapshot.CanSave);
        await Assert.ThrowsAsync<RecorderClientException>(() => service.SetEnabledAsync(true, Recording, TestToken));
        Assert.Equal(1, factory.Count);
        session.FailCleanup = false;
        await service.DisposeAsync();
    }

    [Fact]
    public async Task NativeCleanupFailureWithConfirmedDisposalRecoversWithoutRestartingWisp()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        first.StopFailureReason = "cleanup_failed";
        first.Emit("error", "cleanup_failed");

        Assert.True((await first.NextDisposalAsync()).Succeeded);
        await ReconcileAsync(service);
        Assert.True(service.Snapshot.Enabled);
        Assert.True(service.Snapshot.CanEnable);
        Assert.False(service.Snapshot.CanSave);
        Assert.Equal(1, factory.Count);
        Assert.Contains("cleanup_failed", service.FailureReport, StringComparison.Ordinal);

        var retry = await delays.NextActiveAsync();
        retry.Resume.TrySetResult();
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.True(first.CleanupConfirmed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, factory.UnconfirmedOverlaps);
        second.Emit("buffering", "none");
        Assert.True(service.Snapshot.CanSave);
    }

    [Theory]
    [InlineData("error", "cleanup_failed")]
    [InlineData("reconnecting", "target_exited")]
    [InlineData("reconnecting", "window_closed")]
    public async Task LateConfirmedCleanupAutomaticallyStartsTheNewGameIdentity(string state, string reason)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.FailCleanup = true;
        try
        {
            first.Emit("buffering", "none");
            first.Emit(state, reason);
            Assert.False((await first.NextDisposalAsync()).Succeeded);
            await ReconcileAsync(service);
            Assert.False(service.Snapshot.CanEnable);
            Assert.False(service.Snapshot.CanSave);
            Assert.False(first.CleanupConfirmed.Task.IsCompleted);

            var replacement = new RecorderTarget(43, 321, 789);
            service.ObserveTarget(new(replacement, 2), service.TargetObservationGeneration);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);
            first.FailCleanup = false;
            var retry = await delays.NextActiveAsync();
            retry.Resume.TrySetResult();

            var second = await factory.NextAsync();
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            Assert.True(first.DisposalCount >= 2);
            Assert.True(first.CleanupConfirmed.Task.IsCompletedSuccessfully);
            Assert.Equal(replacement, second.StartedTarget);
            Assert.Equal(0, factory.UnconfirmedOverlaps);
            second.Emit("buffering", "none");
            Assert.True(service.Snapshot.Enabled);
            Assert.True(service.Snapshot.CanSave);
        }
        finally { first.FailCleanup = false; }
    }

    [Theory]
    [InlineData("error", "cleanup_failed")]
    [InlineData("reconnecting", "target_exited")]
    [InlineData("reconnecting", "window_closed")]
    public async Task DisablingDuringPendingCleanupStaysDisabledAfterLateConfirmation(string state, string reason)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.FailCleanup = true;
        try
        {
            first.Emit(state, reason);
            Assert.False((await first.NextDisposalAsync()).Succeeded);
            await ReconcileAsync(service);
            await service.SetEnabledAsync(false, Recording, TestToken);
            Assert.False(service.Snapshot.Enabled);
            Assert.False(service.Snapshot.CanEnable);
            Assert.Equal(0, service.TargetObservationGeneration);

            first.FailCleanup = false;
            var retry = await delays.NextActiveAsync();
            retry.Resume.TrySetResult();
            await first.CleanupConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            await ReconcileAsync(service);
            Assert.Equal(ClipRecorderState.Disabled, service.Snapshot.State);
            Assert.False(service.Snapshot.Enabled);
            Assert.True(service.Snapshot.CanEnable);
            Assert.False(service.Snapshot.CanSave);
            Assert.Equal(1, factory.Count);
            Assert.Equal(0, factory.UnconfirmedOverlaps);
        }
        finally { first.FailCleanup = false; }
    }

    [Fact]
    public async Task RepeatedUnconfirmedCleanupBacksOffWithoutCreatingOverlappingHelpers()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.FailCleanup = true;
        try
        {
            first.Emit("error", "cleanup_failed");
            Assert.False((await first.NextDisposalAsync()).Succeeded);
            await ReconcileAsync(service);
            var previousDelay = TimeSpan.Zero;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var retry = await delays.NextActiveAsync();
                Assert.InRange(retry.Delay.TotalSeconds, 1, 30);
                Assert.True(retry.Delay >= previousDelay);
                previousDelay = retry.Delay;
                retry.Resume.TrySetResult();
                Assert.False((await first.NextDisposalAsync()).Succeeded);
                await ReconcileAsync(service);
                Assert.False(first.CleanupConfirmed.Task.IsCompleted);
                Assert.False(service.Snapshot.CanEnable);
                Assert.False(service.Snapshot.CanSave);
                Assert.Equal(1, factory.Count);
                Assert.Equal(0, factory.UnconfirmedOverlaps);
            }
            Assert.Equal(TimeSpan.FromSeconds(30), previousDelay);
        }
        finally { first.FailCleanup = false; }
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
        Assert.Contains($"Reset 1: {reason}", service.FailureReport, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("window_minimized")]
    [InlineData("focus_lost")]
    [InlineData("fullscreen_required")]
    public async Task PausedTargetKeepsSaveableHistoryAndResumesWithoutNewHelper(string reason)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        var delays = new RecoveryClock();
        await using var service = fixture.Service(factory, delays.DelayAsync);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        var oldGeneration = service.TargetObservationGeneration;
        first.Emit("paused", reason);
        await ReconcileAsync(service);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.True(service.Snapshot.Enabled);
        Assert.True(service.Snapshot.CanSave);
        Assert.Contains("kept and can be saved", service.Snapshot.Status, StringComparison.Ordinal);
        Assert.False(first.Disposed.Task.IsCompleted);
        Assert.Equal(0, delays.Count);
        Assert.Equal(1, factory.Count);
        Assert.True(service.TargetObservationGeneration > oldGeneration);
        service.ObserveTarget(new(Target, 1), oldGeneration);
        await ReconcileAsync(service);
        Assert.Equal(0, first.Resumes);
        var target = new ClipSaveTarget(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording, "unused");
        var expected = new FinalizedClipMedia(1024, 1920, 1080, 60, 0, 100_000_000, true);
        first.Saved.SetResult(expected);
        Assert.Equal(expected, await service.SaveAsync(target, TestToken));
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        await ReconcileAsync(service);
        Assert.Equal(1, first.Resumes);
        Assert.Equal(1, factory.Count);
        Assert.False(first.Disposed.Task.IsCompleted);
        Assert.True(service.Snapshot.CanSave);
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task SaveStartedWhilePausedCanResumeAndPauseAgainBeforeSaveCompletes()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        var generation = service.TargetObservationGeneration;
        var saving = service.SaveAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording, "unused"), TestToken);
        session.Emit("saving", "none");
        // Native may repeat its retained paused state after a save/control event.
        session.Emit("paused", "focus_lost");
        Assert.Equal(generation, service.TargetObservationGeneration);
        session.Emit("saving", "none");
        session.ResumeEmitsBuffering = false; // Native suppresses this during mux.
        service.ObserveTarget(new(Target, 2), generation);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Resumes);
        Assert.False(saving.IsCompleted);
        Assert.Equal(ClipRecorderState.Saving, service.Snapshot.State);
        Assert.Equal(generation, service.TargetObservationGeneration);
        service.ObserveTarget(null, generation);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Pauses);
        Assert.False(saving.IsCompleted);
        Assert.False(session.Disposed.Task.IsCompleted);
        var expected = new FinalizedClipMedia(1024, 1920, 1080, 60, 0, 100_000_000, true);
        session.Saved.SetResult(expected);
        Assert.Equal(expected, await saving);
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        Assert.Equal(generation, service.TargetObservationGeneration);
        Assert.Equal(1, factory.Count);
        Assert.True(service.Snapshot.CanSave);
        Assert.Empty(service.FailureReport);
    }

    [Theory]
    [InlineData("focus_lost")]
    [InlineData("window_minimized")]
    [InlineData("fullscreen_required")]
    public async Task RefusedResumeRequiresFreshGenerationEvenWhenExternalObservationIsUnchanged(string reason)
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        var observation = new RecorderTargetObservation(Target, 1);
        service.ObserveTarget(observation, service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        var rejectedGeneration = service.TargetObservationGeneration;
        session.ResumeFailureReason = reason;
        service.ObserveTarget(observation, rejectedGeneration);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Resumes);
        Assert.True(service.TargetObservationGeneration > rejectedGeneration);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.True(service.Snapshot.CanSave);
        Assert.False(session.Disposed.Task.IsCompleted);
        var freshGeneration = service.TargetObservationGeneration;
        session.Emit("paused", reason);
        service.ObserveTarget(observation, rejectedGeneration);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Resumes);
        Assert.Equal(freshGeneration, service.TargetObservationGeneration);
        session.ResumeFailureReason = null;
        service.ObserveTarget(observation, freshGeneration);
        await ReconcileAsync(service);
        Assert.Equal(2, session.Resumes);
        Assert.Equal(1, factory.Count);
        Assert.Equal(ClipRecorderState.Buffering, service.Snapshot.State);
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task PausedEventRacingSuccessfulResumeAcknowledgmentRemainsPausedUntilFreshEligibility()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        var generation = service.TargetObservationGeneration;
        session.PauseBeforeResumeReply = true;
        service.ObserveTarget(new(Target, 2), generation);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Resumes);
        Assert.Equal(1, session.Pauses);
        Assert.True(service.TargetObservationGeneration > generation);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.True(service.Snapshot.CanSave);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Resumes);
        Assert.Equal(1, session.Pauses);
        Assert.Equal(1, factory.Count);
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task LosingFocusDuringSaveDoesNotCancelTheWrite()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("buffering", "none");
        var target = new ClipSaveTarget(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording, "unused");
        var saving = service.SaveAsync(target, TestToken);
        service.ObserveTarget(null, service.TargetObservationGeneration);
        await ReconcileAsync(service);
        Assert.Equal(1, session.Pauses);
        Assert.False(saving.IsCompleted);
        Assert.False(session.Disposed.Task.IsCompleted);
        var expected = new FinalizedClipMedia(1024, 1920, 1080, 60, 0, 100_000_000, true);
        session.Saved.SetResult(expected);
        Assert.Equal(expected, await saving);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.True(service.Snapshot.CanSave);
        Assert.Empty(service.FailureReport);
    }

    [Fact]
    public async Task PauseBeforeFirstFrameIsNotSaveableAndDoesNotRestart()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var session = await factory.NextAsync();
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        Assert.Equal(ClipRecorderState.Paused, service.Snapshot.State);
        Assert.False(service.Snapshot.CanSave);
        session.Emit("paused", "focus_lost");
        await ReconcileAsync(service);
        Assert.Equal(0, session.Resumes);
        Assert.Equal(1, factory.Count);
        Assert.False(session.Disposed.Task.IsCompleted);
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
        Assert.Empty(service.FailureReport);
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
        internal sealed record Retry(TimeSpan Delay, TaskCompletionSource Resume, CancellationToken Cancellation);
        private readonly Channel<Retry> _delays = Channel.CreateUnbounded<Retry>();
        internal int Count { get; private set; }
        internal Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            Count++;
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Writer.TryWrite(new(delay, resume, token));
            return resume.Task.WaitAsync(token);
        }
        internal Task<Retry> NextAsync() => _delays.Reader.ReadAsync(TestToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        internal async Task<Retry> NextActiveAsync()
        {
            while (true)
            {
                var retry = await NextAsync();
                if (!retry.Cancellation.IsCancellationRequested) return retry;
            }
        }
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
        internal ComponentDiagnosticHistory DiagnosticHistory { get; } = new();
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispRecorderServiceTests", Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal ClipRecorderService Service(SessionFactory factory,
            Func<TimeSpan, CancellationToken, Task>? recoveryDelay = null) => new(() => Directory, factory.Create, true,
                recoveryDelay: recoveryDelay ?? ((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token)),
                diagnosticHistory: DiagnosticHistory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
    private sealed class SessionFactory
    {
        private readonly Channel<FakeSession> _created = Channel.CreateUnbounded<FakeSession>();
        private int _count;
        private int _unconfirmedOverlaps;
        private FakeSession? _previous;
        internal int Count => Volatile.Read(ref _count);
        internal int UnconfirmedOverlaps => Volatile.Read(ref _unconfirmedOverlaps);
        internal bool FailCleanup { get; init; }
        internal IRecorderSession Create()
        {
            var session = new FakeSession { FailCleanup = FailCleanup };
            var previous = Interlocked.Exchange(ref _previous, session);
            if (previous is not null && !previous.CleanupConfirmed.Task.IsCompletedSuccessfully)
                Interlocked.Increment(ref _unconfirmedOverlaps);
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
        internal readonly TaskCompletionSource CleanupConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<(int Attempt, bool Succeeded)> _disposals = Channel.CreateUnbounded<(int, bool)>();
        private int _disposalCount;
        internal int DisposalCount => Volatile.Read(ref _disposalCount);
        internal readonly TaskCompletionSource<FinalizedClipMedia> Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailCleanup { get; set; }
        internal RecorderFailureDiagnostic? DiagnosticAfterDisposal { get; set; }
        public RecorderFailureDiagnostic? FailureDiagnostic => Disposed.Task.IsCompleted ? DiagnosticAfterDisposal : null;
        internal int Stops { get; private set; }
        internal int Pauses { get; private set; }
        internal int Resumes { get; private set; }
        internal bool ResumeEmitsBuffering { get; set; } = true;
        internal bool PauseBeforeResumeReply { get; set; }
        internal string? ResumeFailureReason { get; set; }
        internal string? StopFailureReason { get; set; }
        private bool _bufferReady;
        internal RecorderTarget? StartedTarget { get; private set; }
        public event EventHandler<RecorderStateUpdate>? StateChanged;
        internal void Emit(string state, string reason)
        {
            if (state == "buffering") _bufferReady = true;
            StateChanged?.Invoke(this, new(state, reason, BufferReady: state == "paused" && _bufferReady));
        }
        public Task OpenAsync(ClipRecordingSpec recording, string storage, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); BorderlessAllowedAtOpen = BorderlessAllowed; Emit("waiting", "waiting_for_game"); return Task.CompletedTask; }
        public Task StartAsync(RecorderTarget target, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); StartedTarget = target; Started.TrySetResult(); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Pauses++; Emit("paused", "focus_lost"); return Task.CompletedTask; }
        public Task ResumeAsync(RecorderTarget target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(StartedTarget, target); Resumes++;
            if (ResumeFailureReason is { } reason)
            {
                Emit("paused", reason);
                return Task.FromException(new RecorderClientException(reason) { NativeRequestRejected = true });
            }
            if (PauseBeforeResumeReply) Emit("paused", "focus_lost");
            else if (ResumeEmitsBuffering) Emit("buffering", "none");
            return Task.CompletedTask;
        }
        public Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken) => Saved.Task.WaitAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Stops++;
            return StopFailureReason is { } reason ? Task.FromException(new RecorderClientException(reason)) : Task.CompletedTask;
        }
        internal Task<(int Attempt, bool Succeeded)> NextDisposalAsync() =>
            _disposals.Reader.ReadAsync(TestToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        public ValueTask DisposeAsync()
        {
            var attempt = Interlocked.Increment(ref _disposalCount);
            var succeeded = !FailCleanup;
            Disposed.TrySetResult();
            if (succeeded) CleanupConfirmed.TrySetResult();
            _disposals.Writer.TryWrite((attempt, succeeded));
            return succeeded ? ValueTask.CompletedTask : ValueTask.FromException(new RecorderClientException("helper_shutdown_failed"));
        }
    }
}
