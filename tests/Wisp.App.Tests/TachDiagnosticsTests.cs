using System.Diagnostics;
using System.Text.Json;
using Wisp.App.DebugLogging;
using Wisp.Core;
using Wisp.Telemetry;
using Xunit;

namespace Wisp.App.Tests;

[Collection("Tach diagnostics")]
public sealed class TachDiagnosticsTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public TachDiagnosticsTests(ITestOutputHelper output)
    {
        _output = output;
        Reset();
    }

    public void Dispose() => Reset();

    [Fact]
    public void DisabledRecordCallsCreateNoHistoryOrPerCallAllocations()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var input = Input(TelemetryPacketDiagnosticKind.Accepted, timestamp);
        var needle = Needle(TachDiagnostics.NextControlId(), timestamp);
        var native = NativeRead();
        var state = State(timestamp);
        RecordAll(input, needle, native, state);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < 10_000; index++)
            RecordAll(input, needle, native, state);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(TachDiagnostics.IsEnabled);
        Assert.Null(TachDiagnostics.Snapshot());
        Assert.Null(TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow));
        Assert.Equal(0, allocated);
        _output.WriteLine($"Disabled: 60,000 record calls, {allocated} bytes, {elapsed.TotalMilliseconds:F3} ms.");
    }

    [Fact]
    public void CountsSeparateSourcesActualChangesAndPreviewControlIdentity()
    {
        TachDiagnostics.SetEnabled(true);
        var start = Stopwatch.GetTimestamp();
        var hudId = TachDiagnostics.NextControlId();
        var previewId = TachDiagnostics.NextControlId();
        var needle = Needle(hudId, start);
        TachDiagnostics.RecordNeedle(in needle);
        needle = needle with { AppliedTimestamp = start + Ticks(10), RawRpm = 6_000 };
        TachDiagnostics.RecordNeedle(in needle);
        needle = needle with { AppliedTimestamp = start + Ticks(20), Angle = 320, PlaybackAtNewest = true };
        TachDiagnostics.RecordNeedle(in needle);
        needle = needle with { AppliedTimestamp = start + Ticks(30), Source = "fallback" };
        TachDiagnostics.RecordNeedle(in needle);
        needle = needle with { AppliedTimestamp = start + Ticks(40), Source = "unavailable", NeedleVisible = false };
        TachDiagnostics.RecordNeedle(in needle);
        var preview = Needle(previewId, start) with
        {
            ControlKind = "digital",
            HostKind = "MainWindow",
            HostWindowHandle = 42,
            CarOrdinal = 0,
            IsLive = false,
            Source = "rpm",
            Angle = null,
            AppliedRpm = 4_500,
            AppliedFraction = 0.5
        };
        TachDiagnostics.RecordNeedle(in preview);

        var interval = Interval();
        var hud = Assert.Single(interval.Needles, item => item.ControlId == hudId);
        var previewCounts = Assert.Single(interval.Needles, item => item.ControlId == previewId);
        Assert.Equal(5, hud.Applied);
        Assert.Equal(2, hud.Changed);
        Assert.Equal(3, hud.Native);
        Assert.Equal(1, hud.Fallback);
        Assert.Equal(1, hud.Unavailable);
        Assert.Equal(2, hud.SourceSwitches);
        Assert.Equal(3, hud.AtNewest);
        Assert.Equal(10, hud.MaximumApplyGapMilliseconds, 6);
        Assert.Equal(20, hud.MaximumChangedGapMilliseconds, 6);
        Assert.Equal(1, previewCounts.Applied);
        Assert.Equal(1, previewCounts.Changed);
        Assert.Equal(1, previewCounts.Fallback);
        Assert.Equal("MainWindow", previewCounts.HostKind);
        Assert.Equal(42, previewCounts.HostWindowHandle);
        var snapshot = Snapshot();
        Assert.Equal(5, snapshot.NeedleRecent.Length);
        Assert.All(snapshot.NeedleRecent, sample => Assert.Equal(hudId, sample.ControlId));
    }

    [Fact]
    public void NonfiniteOptionalValuesAreNullAndTheEntireExportIsStrictJsonSafe()
    {
        TachDiagnostics.SetEnabled(true);
        var timestamp = Stopwatch.GetTimestamp();
        var needle = Needle(TachDiagnostics.NextControlId(), timestamp) with
        {
            RawRpm = double.NaN,
            AppliedRpm = double.PositiveInfinity,
            AppliedFraction = double.NegativeInfinity,
            Angle = double.NaN,
            Blur = double.PositiveInfinity,
            PlaybackDelayMilliseconds = double.NaN,
            PlaybackTargetDelayMilliseconds = double.PositiveInfinity
        };
        TachDiagnostics.RecordNeedle(in needle);
        var input = Input(TelemetryPacketDiagnosticKind.Rejected, timestamp) with
        {
            Rpm = float.NaN,
            MaximumRpm = float.PositiveInfinity
        };
        TachDiagnostics.RecordTelemetry(input);
        var native = NativeRead() with { Angle = double.NaN, Blur = double.NegativeInfinity };
        TachDiagnostics.RecordNative(in native, 1, 314, "refresh");

        var snapshot = Snapshot();
        var clean = Assert.Single(snapshot.NeedleRecent);
        Assert.Null(clean.AppliedRpm);
        Assert.Null(clean.AppliedFraction);
        Assert.Null(clean.Angle);
        Assert.Null(clean.Blur);
        Assert.True(double.IsFinite(clean.RawRpm));
        Assert.True(double.IsFinite(clean.PlaybackDelayMilliseconds));
        Assert.True(double.IsFinite(clean.PlaybackTargetDelayMilliseconds));
        var cleanNative = Assert.Single(snapshot.NativeRecent);
        Assert.Null(cleanNative.Angle);
        Assert.Null(cleanNative.Blur);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.Equal(JsonValueKind.Null,
            json.RootElement.GetProperty("NeedleRecent")[0].GetProperty("Angle").ValueKind);
        using var intervalJson = JsonDocument.Parse(JsonSerializer.Serialize(Interval()));
        Assert.Equal(JsonValueKind.Object, intervalJson.RootElement.ValueKind);
    }

    [Fact]
    public void AcceptedTelemetryUiSelectionAndAppliedNeedleRetainTheSameSampleIdentity()
    {
        TachDiagnostics.SetEnabled(true);
        var received = Stopwatch.GetTimestamp();
        var accepted = Input(TelemetryPacketDiagnosticKind.Accepted, Stopwatch.GetTimestamp()) with
        {
            ReceivedTimestamp = received,
            Sequence = 7
        };
        TachDiagnostics.RecordTelemetry(accepted);
        TachDiagnostics.RecordUiInput(State(received));
        var applied = Needle(TachDiagnostics.NextControlId(), Stopwatch.GetTimestamp()) with
        {
            ReceivedTimestamp = received,
            NativeObservedTimestamp = received
        };
        TachDiagnostics.RecordNeedle(in applied);

        var snapshot = Snapshot();
        var input = Assert.Single(snapshot.InputRecent, item => item.Route == "Accepted");
        var ui = Assert.Single(snapshot.InputRecent, item => item.Route == "UiSelected");
        var needle = Assert.Single(snapshot.NeedleRecent);
        Assert.Equal(7, input.Sequence);
        Assert.Equal(input.ReceivedTimestamp, ui.ReceivedTimestamp);
        Assert.Equal(input.ReceivedTimestamp, needle.ReceivedTimestamp);
        Assert.Equal(input.GameTimestampMilliseconds, ui.GameTimestampMilliseconds);
        Assert.Equal(input.GameTimestampMilliseconds, needle.GameTimestampMilliseconds);
        Assert.Equal(input.CarOrdinal, needle.CarOrdinal);
        Assert.Equal(input.Rpm, ui.Rpm);
        Assert.Equal(input.Rpm, needle.RawRpm);
        Assert.True(input.Timestamp <= ui.Timestamp);
        Assert.True(ui.Timestamp <= needle.AppliedTimestamp);
        Assert.Equal(Stopwatch.Frequency, snapshot.StopwatchFrequency);
    }

    [Fact]
    public void InputRingRetainsStartupAndNewestEventsWithExactOverwriteCounts()
    {
        TachDiagnostics.SetEnabled(true);
        var firstTimestamp = Stopwatch.GetTimestamp();
        for (var index = 0; index < 46_000; index++)
        {
            var timestamp = firstTimestamp + Ticks(index < 18_000 ? index : index + 61_000);
            TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Received, timestamp) with
            {
                Sequence = index + 1
            });
        }

        var snapshot = Snapshot();
        Assert.Equal(18_000, snapshot.InputStartup.Length);
        Assert.Equal(1, snapshot.InputStartup[0].Sequence);
        Assert.Equal(18_000, snapshot.InputStartup[^1].Sequence);
        Assert.Equal(45_000, snapshot.InputRecent.Length);
        Assert.Equal(1_001, snapshot.InputRecent[0].Sequence);
        Assert.Equal(46_000, snapshot.InputRecent[^1].Sequence);
        Assert.Equal(1_000, snapshot.InputOverwritten);
        Assert.Equal(46_000, Interval().InputReceived);
    }

    [Fact]
    public void StartupHistoryStopsAfterSixtySecondsEvenWhenItsCapacityIsNotFull()
    {
        TachDiagnostics.SetEnabled(true);
        var timestamp = Stopwatch.GetTimestamp();
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Received, timestamp));
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Received, timestamp + Ticks(60_001)));
        var snapshot = Snapshot();
        Assert.Single(snapshot.InputStartup);
        Assert.Equal(2, snapshot.InputRecent.Length);
        Assert.Equal(0, snapshot.InputOverwritten);
    }

    [Fact]
    public void SnapshotDoesNotResetIntervalCountsAndDisablingRetainsExistingHistory()
    {
        TachDiagnostics.SetEnabled(true);
        var timestamp = Stopwatch.GetTimestamp();
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Received, timestamp));
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Drained, timestamp));
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Accepted, timestamp));
        var needle = Needle(TachDiagnostics.NextControlId(), timestamp);
        TachDiagnostics.RecordNeedle(in needle);
        var first = Snapshot();
        var second = Snapshot();
        Assert.Equal(first.CaptureId, second.CaptureId);
        Assert.Equal(first.InputRecent, second.InputRecent);
        Assert.Equal(first.NeedleRecent, second.NeedleRecent);
        var interval = Interval();
        Assert.Equal(2, interval.InputReceived);
        Assert.Equal(1, interval.InputDrained);
        Assert.Equal(1, interval.InputAccepted);
        Assert.Equal(1, Assert.Single(interval.Needles).Applied);
        Assert.Equal(0, Interval().InputAccepted);

        TachDiagnostics.SetEnabled(false);
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Accepted, timestamp));
        Assert.Equal(3, Snapshot().InputRecent.Length);
        Assert.Null(TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReenablingStartsANewCaptureWithoutTreatingTheDisabledPeriodAsAStall()
    {
        TachDiagnostics.SetEnabled(true);
        var now = Stopwatch.GetTimestamp();
        var id = TachDiagnostics.NextControlId();
        var previousTimestamp = now - Ticks(60_000);
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Accepted, previousTimestamp));
        var needle = Needle(id, previousTimestamp);
        TachDiagnostics.RecordNeedle(in needle);
        var previous = Snapshot();
        TachDiagnostics.SetEnabled(false);
        Assert.Equal(previous.CaptureId, Snapshot().CaptureId);

        TachDiagnostics.SetEnabled(true);
        var restarted = Snapshot();
        Assert.NotEqual(previous.CaptureId, restarted.CaptureId);
        Assert.Empty(restarted.InputRecent);
        Assert.Empty(restarted.NeedleRecent);
        Assert.Equal(0, restarted.InputOverwritten);
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Accepted, now));
        needle = Needle(id, now);
        TachDiagnostics.RecordNeedle(in needle);
        var interval = Interval();
        Assert.Equal(1, interval.InputAccepted);
        Assert.Equal(0, interval.MaximumAcceptedGapMilliseconds);
        Assert.Equal(0, Assert.Single(interval.Needles).MaximumApplyGapMilliseconds);

        TachDiagnostics.SetEnabled(true);
        var unchanged = Snapshot();
        Assert.Equal(restarted.CaptureId, unchanged.CaptureId);
        Assert.Single(unchanged.InputRecent);
        Assert.Single(unchanged.NeedleRecent);
        TachDiagnostics.RecordTelemetry(Input(TelemetryPacketDiagnosticKind.Accepted, now + Ticks(10)));
        Assert.Equal(10, Interval().MaximumAcceptedGapMilliseconds, 6);
    }

    [Fact]
    public void EnabledNeedleRecordingReportsOfflineCostWithoutATimingThreshold()
    {
        TachDiagnostics.SetEnabled(true);
        var samples = new TachNeedleDiagnostic[40_000];
        var id = TachDiagnostics.NextControlId();
        var timestamp = Stopwatch.GetTimestamp();
        for (var index = 0; index < samples.Length; index++)
            samples[index] = Needle(id, timestamp + index) with { Angle = 120 + index % 240 };
        long allocated = -1;
        long elapsedTicks = 0;
        long warmupRecords = 0;
        var gcChanges = new int[3];
        Exception? failure = null;
        using var evidence = new AllocationMeasurementEvidence();
        var thread = new Thread(() =>
        {
            try
            {
                RecordNeedles(samples, 0, 20_000);
                warmupRecords = TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow)!.Needles.Single().Applied;
                var gen0 = GC.CollectionCount(0);
                var gen1 = GC.CollectionCount(1);
                var gen2 = GC.CollectionCount(2);
                var started = Stopwatch.GetTimestamp();
                evidence.Start();
                try
                {
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    RecordNeedles(samples, 20_000, 20_000);
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                }
                finally
                {
                    evidence.Stop();
                }
                elapsedTicks = Stopwatch.GetTimestamp() - started;
                gcChanges[0] = GC.CollectionCount(0) - gen0;
                gcChanges[1] = GC.CollectionCount(1) - gen1;
                gcChanges[2] = GC.CollectionCount(2) - gen2;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        { IsBackground = true };
        using (ExecutionContext.SuppressFlow())
            thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The isolated collector measurement did not finish.");
        _output.WriteLine(evidence.Summary());
        Assert.Null(failure);
        Assert.Equal(20_000, warmupRecords);
        Assert.Equal(20_000, Assert.Single(Interval().Needles).Applied);
        _output.WriteLine($"Enabled on an isolated thread after 20,000 warm-up records: 20,000 precomputed needle records, " +
            $"{allocated} bytes, {elapsedTicks * 1_000d / Stopwatch.Frequency:F3} ms. " +
            $"GC collections (0/1/2): {gcChanges[0]}/{gcChanges[1]}/{gcChanges[2]}. " +
            "Offline uncontended collector cost only; this does not measure live HUD presentation.");
        Assert.Equal(0, allocated);
    }

    private static void RecordNeedles(TachNeedleDiagnostic[] samples, int start, int count)
    {
        for (var index = start; index < start + count; index++)
            TachDiagnostics.RecordNeedle(in samples[index]);
    }

    private static void RecordAll(TelemetryPacketDiagnostic input, TachNeedleDiagnostic needle,
        NativeGaugeReadDiagnostics native, VehicleState state)
    {
        TachDiagnostics.RecordTelemetry(input);
        TachDiagnostics.RecordUiInput(state);
        TachDiagnostics.RecordNative(in native, 1, 314, "refresh");
        TachDiagnostics.RecordWorkerWait(1, 1, false);
        TachDiagnostics.RecordNeedle(in needle);
        TachDiagnostics.RecordNeedleLifecycle(needle.ControlId, "analogue", "OverlayWindow", 0, true, true, true);
    }

    private static TelemetryPacketDiagnostic Input(TelemetryPacketDiagnosticKind kind, long timestamp) =>
        new(kind, timestamp, timestamp, 1, 1_000, 4_500, 9_000, 314, true, PacketParseError.None);

    private static NativeGaugeReadDiagnostics NativeRead() => new(
        true, NativeGaugeReadFailure.None, NativeGaugeReadStage.GaugeBlock, NativeGaugeCacheOutcome.Hit,
        true, 0, true, 300, 0.1, 1);

    private static TachNeedleDiagnostic Needle(int id, long timestamp) => new()
    {
        ControlId = id,
        ControlKind = "analogue",
        HostKind = "OverlayWindow",
        Route = "composition",
        Source = "native",
        IsLoaded = true,
        IsVisible = true,
        IsLive = true,
        NeedleVisible = true,
        CarOrdinal = 314,
        GameTimestampMilliseconds = 1_000,
        ReceivedTimestamp = timestamp,
        NativeObservedTimestamp = timestamp,
        AppliedTimestamp = timestamp,
        RawRpm = 4_500,
        Angle = 300,
        Blur = 0.1,
        PlaybackDelayMilliseconds = 40,
        PlaybackTargetDelayMilliseconds = 40,
        BufferedSamples = 2
    };

    private static VehicleState State(long receivedTimestamp) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = 1_000,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        ReceivedTimestamp = receivedTimestamp,
        CarOrdinal = 314,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 4_500,
        EngineMaximumRpm = 9_000,
        Gear = TransmissionGear.Fourth,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

    private static TachCaptureExport Snapshot() => Assert.IsType<TachCaptureExport>(TachDiagnostics.Snapshot());
    private static TachIntervalDiagnostic Interval() =>
        Assert.IsType<TachIntervalDiagnostic>(TachDiagnostics.CollectInterval(DateTimeOffset.UtcNow));
    private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1_000d);
    private static void Reset()
    {
        TachDiagnostics.SetEnabled(false);
        TachDiagnostics.Clear();
    }
}
