using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Wisp.App.Clips;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HealthContextRecorderTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RingIsBoundedAndPublishedSnapshotsStayImmutable()
    {
        long now = 1;
        var recorder = new HealthContextRecorder(() => now);
        recorder.RecordSample(Sample());
        var first = recorder.Snapshot();
        for (var index = 1; index <= 100; index++)
        {
            now += Stopwatch.Frequency * 2;
            recorder.RecordBreadcrumb(HealthEventCode.SettingsSaveFailed, index);
            recorder.RecordSample(Sample(index * 2));
        }
        var recent = recorder.Snapshot();
        Assert.Single(first.Samples); Assert.Empty(first.Breadcrumbs);
        Assert.Equal(HealthContextRecorder.SampleCapacity, recent.Samples.Length);
        Assert.Equal(HealthContextRecorder.BreadcrumbCapacity, recent.Breadcrumbs.Length);
        Assert.Equal(5, recent.Breadcrumbs[0].ErrorCode);
        Assert.Equal(100, recent.Breadcrumbs[^1].ErrorCode);
        Assert.True(HealthContextRecorder.IsValidSnapshot(recent));
        Assert.Same(recent, recorder.Snapshot());
        // Leave ample room for the bounded crash stack within the 128 KiB report cap.
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(recent).Length < 96 * 1024);
    }

    [Fact]
    public void SamplesPublishAtMostOncePerTwoSecondsAndDoNotInventFreshness()
    {
        long now = 1;
        var recorder = new HealthContextRecorder(() => now);
        recorder.RecordSample(Sample());
        var first = recorder.Snapshot();
        now += Stopwatch.Frequency;
        recorder.RecordBreadcrumb(HealthEventCode.ClipExportStarted);
        recorder.RecordSample(Sample(1));
        Assert.Same(first, recorder.Snapshot());
        Assert.Equal(Epoch, first.CapturedAtUtc);
        now += Stopwatch.Frequency;
        recorder.RecordSample(Sample(2));
        Assert.Equal(2, recorder.Snapshot().Samples.Length);
        Assert.Single(recorder.Snapshot().Breadcrumbs);
    }

    [Fact]
    public void ReportCaptureIncludesImmediateFailureWithoutMutatingOrRefreshingCachedSamples()
    {
        var recorder = new HealthContextRecorder();
        recorder.RecordSample(Sample());
        var cached = recorder.Snapshot();
        recorder.RecordRendererFailure(unchecked((int)0x887A0005));
        var report = recorder.CaptureForReport();
        Assert.Same(cached, recorder.Snapshot());
        Assert.Empty(cached.Breadcrumbs);
        Assert.Equal(cached.Samples, report.Samples);
        Assert.Equal(Epoch, Assert.Single(report.Samples).TimestampUtc);
        var failure = Assert.Single(report.Breadcrumbs);
        Assert.Equal(HealthEventCode.RendererFailed, failure.Code);
        Assert.Equal(unchecked((int)0x887A0005), failure.ErrorCode);
        Assert.True(HealthContextRecorder.IsValidSnapshot(report));
    }

    [Fact]
    public void ReportCaptureWorksBeforeTheFirstHealthSample()
    {
        var recorder = new HealthContextRecorder();
        recorder.RecordBreadcrumb(HealthEventCode.ApplicationStarted);
        var report = recorder.CaptureForReport();
        Assert.Empty(report.Samples);
        Assert.Equal(HealthEventCode.ApplicationStarted, Assert.Single(report.Breadcrumbs).Code);
        Assert.True(HealthContextRecorder.IsValidSnapshot(report));
    }

    [Fact]
    public void RendererCountersDescribeSubmissionAndActualQueueAgeNotDisplayedFrames()
    {
        long now = 1;
        var recorder = new HealthContextRecorder(() => now);
        recorder.RecordSample(Sample());
        recorder.RecordPresent(HealthPresentResult.Submitted, 1, 1 + Stopwatch.Frequency / 4);
        recorder.RecordPresent(HealthPresentResult.Busy, 1, 1 + Stopwatch.Frequency);
        recorder.RecordPresent(HealthPresentResult.Occluded, 1, 1 + Stopwatch.Frequency);
        recorder.RecordQueueDrop(3);
        recorder.RecordRendererFailure(unchecked((int)0x887A0005));
        now += Stopwatch.Frequency * 2;
        recorder.RecordSample(Sample(2));
        var renderer = recorder.Snapshot().Samples[^1].Renderer;
        Assert.Equal(3, renderer.PresentCalls); Assert.Equal(1, renderer.Submissions);
        Assert.Equal(1, renderer.BusyResults); Assert.Equal(1, renderer.OccludedResults);
        Assert.Equal(1, renderer.Failures); Assert.Equal(3, renderer.QueueDropped);
        Assert.Equal(unchecked((int)0x887A0005), renderer.LastErrorCode);
        Assert.Equal(0.5, renderer.RendererSubmissionsPerSecond);
        Assert.Equal(250, renderer.RendererQueuedToSubmitAgeMs!.Value, 3);
        Assert.Equal(renderer.RendererQueuedToSubmitAgeMs, renderer.MaximumQueuedToSubmitAgeMs);
        Assert.Contains(recorder.Snapshot().Breadcrumbs, item => item.Code == HealthEventCode.RendererFailed && item.ErrorCode == renderer.LastErrorCode);
    }

    [Fact]
    public void FailedPresentCountsAnAttemptWithoutInventingABusyResultOrDuplicatingFailure()
    {
        var recorder = new HealthContextRecorder();
        var hresult = unchecked((int)0x887A0005);
        recorder.RecordRendererFailure(hresult);
        recorder.RecordPresent(HealthPresentResult.Failed, 1, 1 + Stopwatch.Frequency);
        recorder.RecordSample(Sample());
        var snapshot = recorder.Snapshot();
        var renderer = Assert.Single(snapshot.Samples).Renderer;
        Assert.Equal(1, renderer.PresentCalls);
        Assert.Equal(0, renderer.Submissions);
        Assert.Equal(0, renderer.BusyResults);
        Assert.Equal(0, renderer.OccludedResults);
        Assert.Equal(1, renderer.Failures);
        Assert.Equal(hresult, renderer.LastErrorCode);
        Assert.Null(renderer.RendererQueuedToSubmitAgeMs);
        Assert.Null(renderer.MaximumQueuedToSubmitAgeMs);
        Assert.Equal(hresult, Assert.Single(snapshot.Breadcrumbs).ErrorCode);
    }

    [Fact]
    public void AutomaticProjectionDropsIdentifiersAndInvalidScalarValues()
    {
        var recorder = new HealthContextRecorder();
        recorder.RecordSample(Sample() with
        {
            SessionId = "PRIVATE_SESSION",
            NativeStatus = "PRIVATE_STATUS",
            GameplayVisibility = "PRIVATE_VISIBILITY",
            IncomingHz = double.NaN,
            PacketAgeMilliseconds = double.PositiveInfinity,
            AcceptedPackets = -1,
            WispCpuPercent = 150,
            ManagedHeapBytes = -1
        });
        var snapshot = recorder.Snapshot();
        var sample = Assert.Single(snapshot.Samples);
        Assert.Equal(0, sample.IncomingDatagramsPerSecond); Assert.Null(sample.PacketAgeMs);
        Assert.Equal(0, sample.AcceptedPackets); Assert.Equal(0, sample.ManagedHeapBytes); Assert.Equal(100, sample.CpuPercent);
        Assert.Equal(NativeAssistProviderStatus.Unavailable, sample.NativeStatus);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.True(HealthContextRecorder.IsValidSnapshot(snapshot));
    }

    [Fact]
    public void ReloadValidationRejectsOversizedUnknownOrNonfiniteContext()
    {
        var recorder = new HealthContextRecorder(); recorder.RecordSample(Sample());
        var valid = recorder.Snapshot();
        Assert.False(HealthContextRecorder.IsValidSnapshot(null));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Samples = default }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { DroppedBreadcrumbs = -1 }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Samples = Enumerable.Repeat(valid.Samples[0], 61).ToImmutableArray() }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Samples = [valid.Samples[0] with { CompositionCallbacksPerSecond = double.NaN }] }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Samples = [valid.Samples[0] with { NativeStatus = (NativeAssistProviderStatus)999 }] }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Breadcrumbs = [new(Epoch, (HealthEventCode)999, 0, null, null)] }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Breadcrumbs = [new(Epoch, HealthEventCode.PlayerChanged, 0, null, (HealthPlayerState)999)] }));
        Assert.False(HealthContextRecorder.IsValidSnapshot(valid with { Breadcrumbs = [new(Epoch, HealthEventCode.RecorderChanged, 0, (ClipRecorderState)999, null)] }));
    }

    [Fact]
    public async Task ConcurrentBreadcrumbWritersRemainBoundedAndStructurallyConsistent()
    {
        var recorder = new HealthContextRecorder();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
                recorder.RecordBreadcrumb(HealthEventCode.PlayerChanged, writer, playerState: (HealthPlayerState)writer);
        }, TestContext.Current.CancellationToken)));
        recorder.RecordSample(Sample());
        var snapshot = recorder.Snapshot();
        Assert.InRange(snapshot.Breadcrumbs.Length, 1, HealthContextRecorder.BreadcrumbCapacity);
        Assert.All(snapshot.Breadcrumbs, item => Assert.Equal(item.ErrorCode, (int)item.PlayerState!.Value));
        Assert.True(HealthContextRecorder.IsValidSnapshot(snapshot));
    }

    [Fact]
    public void HotRendererAndTransitionHooksAllocateNoManagedMemoryAfterWarmup()
    {
        var recorder = new HealthContextRecorder();
        for (var index = 0; index < 100; index++)
        {
            recorder.RecordPresent(HealthPresentResult.Submitted, 1, 2);
            recorder.RecordBreadcrumb(HealthEventCode.PlayerChanged, playerState: HealthPlayerState.Playing);
        }
        const int count = 100000;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++)
        {
            recorder.RecordPresent(HealthPresentResult.Submitted, 1, 2);
            recorder.RecordBreadcrumb(HealthEventCode.PlayerChanged, playerState: HealthPlayerState.Playing);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"{count} renderer+transition hook pairs: {elapsed.TotalMilliseconds:F3} ms; {bytes} allocated bytes. CPU microcheck only, not gameplay performance.");
        Assert.Equal(0, bytes);
    }

    private static DebugHealthSample Sample(int seconds = 0) => new()
    { TimestampUtc = Epoch.AddSeconds(seconds), NativeStatus = nameof(NativeAssistProviderStatus.Ready), IncomingHz = 60, CompositionHz = 120 };
}
