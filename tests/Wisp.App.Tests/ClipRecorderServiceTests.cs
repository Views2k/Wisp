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
        Assert.Equal(ClipRecorderState.WaitingForGame, service.Snapshot.State);
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
    public async Task ResizeWaitsForNewObservationEpochWithoutRetryingUnchangedTarget()
    {
        using var fixture = new Fixture();
        var factory = new SessionFactory();
        await using var service = fixture.Service(factory);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 1), service.TargetObservationGeneration);
        var first = await factory.NextAsync();
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        first.Emit("buffering", "none");
        first.Emit("waiting", "window_resized");
        await first.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.True(service.Snapshot.Enabled);
        Assert.Equal(ClipRecorderState.WaitingForGame, service.Snapshot.State);
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
    public async Task GenericCrashRequiresExplicitReenableEvenWhenTargetChanges()
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
        Assert.Equal(ClipRecorderState.Error, service.Snapshot.State);
        Assert.False(service.Snapshot.Enabled);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        Assert.Equal(1, factory.Count);
        await service.SetEnabledAsync(true, Recording, TestToken);
        service.ObserveTarget(new(Target, 2), service.TargetObservationGeneration);
        var second = await factory.NextAsync();
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Equal(2, factory.Count);
    }

    [Theory]
    [InlineData("capture_failed", "Game capture failed.")]
    [InlineData("encoder_failed", "Video encoding failed.")]
    [InlineData("audio_failed", "Game audio recording failed.")]
    [InlineData("audio_capture_failed", "Game audio recording failed.")]
    [InlineData("protocol_error", "The recorder connection failed.")]
    [InlineData("helper_start_failed", "The recorder could not start.")]
    [InlineData("helper_timeout", "The recorder did not respond in time.")]
    [InlineData("helper_exited", "The recorder closed unexpectedly.")]
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
        Assert.Equal(ClipRecorderState.WaitingForGame, service.Snapshot.State);
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

    [Fact]
    public async Task OffOnBetweenVisibilityTicksStillClearsTheStableWindow()
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

            // No controller tick occurs during either transition.
            await service.SetEnabledAsync(false, Recording, TestToken);
            await service.SetEnabledAsync(true, Recording, TestToken);
            Assert.NotNull(focus.CaptureObservation);
            prepare.Invoke(controller, null);
            Assert.True(focus.CaptureRequested);
            Assert.Null(focus.CaptureObservation);
            observe.Invoke(controller, null);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);

            var replacement = candidate with { CreationFileTime = 789 };
            Assert.Null(tracker.Observe(replacement));
            observe.Invoke(controller, null);
            await ReconcileAsync(service);
            Assert.Equal(1, factory.Count);
            Assert.NotNull(tracker.Observe(replacement));
            observe.Invoke(controller, null);
            var second = await factory.NextAsync();
            await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
            Assert.Equal(replacement.CreationFileTime, second.StartedTarget!.CreationFileTime);
        }
        finally { recorderField.SetValue(controller, originalRecorder); }
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
        internal ClipRecorderService Service(SessionFactory factory) => new(() => Directory, factory.Create, true);
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
        { cancellationToken.ThrowIfCancellationRequested(); Emit("waiting", "waiting_for_game"); return Task.CompletedTask; }
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
