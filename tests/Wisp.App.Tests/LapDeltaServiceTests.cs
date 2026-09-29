using System.Diagnostics;
using System.Numerics;
using Wisp.App.Laps;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapDeltaServiceTests
{
    private static VehicleState State(int tick) => new()
    {
        IsRaceOn = true,
        GameTimestampMilliseconds = (uint)(tick * 100),
        ReceivedAtUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(tick * 100),
        ReceivedTimestamp = Stopwatch.GetTimestamp(),
        CarOrdinal = 42,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 16,
        Lap = new(new Vector3((float)(150 * Math.Cos(tick % 600 / 600d * Math.Tau)), 0,
            (float)(150 * Math.Sin(tick % 600 / 600d * Math.Tau))), tick % 600 / 10f, 60, tick / 10f, (ushort)(tick / 600), 1),
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        EngineRpm = 3000,
        EngineMaximumRpm = 7000,
        Gear = TransmissionGear.Third,
        Steering = 0,
        Accelerator = 100,
        Brake = 0
    };

    private static async Task WaitFor(Func<bool> ready)
    {
        var timeout = Stopwatch.StartNew();
        while (!ready() && timeout.ElapsedMilliseconds < 3000) await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(ready());
    }

    private static (LapReferenceStore Store, string Path) KeptLaps(Func<string?> gameRun)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wisp-laps-{Guid.NewGuid():N}.json");
        return (new LapReferenceStore(path, gameRun), path);
    }

    private static async Task DriveAsync(LapDeltaService service, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            var state = State(i);
            service.Observe(state);
            if (i % 40 == 0 || i == to) await WaitFor(() => service.Latest.ReceivedTimestamp == state.ReceivedTimestamp);
        }
    }

    private static async Task CloseAsync(LapDeltaService service)
    {
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResetBeforeTheFirstSampleDoesNotBringBackKeptLaps()
    {
        var (store, path) = KeptLaps(() => "7:1");
        var first = new LapDeltaService(store);
        first.Configure(true, LapDeltaReference.SessionBest);
        await DriveAsync(first, 0, 620);
        await WaitFor(() => first.Latest.Status == LapDeltaStatus.Comparing);
        await CloseAsync(first);
        Assert.NotNull(store.Load());

        var restarted = new LapDeltaService(store);
        restarted.Configure(true, LapDeltaReference.SessionBest);
        restarted.ResetReferences();
        await DriveAsync(restarted, 700, 760);
        await CloseAsync(restarted);
        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ResetReferenceLapsClearsKeptLapsWithoutLaterTelemetry()
    {
        var (store, path) = KeptLaps(() => "7:2");
        var service = new LapDeltaService(store);
        service.Configure(true, LapDeltaReference.SessionBest);
        await DriveAsync(service, 0, 620);
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        service.ResetReferences();
        await CloseAsync(service);
        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CompletionWaitsForTheLastReferenceSave()
    {
        using var writes = new ManualResetEventSlim(true);
        var (store, path) = KeptLaps(() => { writes.Wait(TimeSpan.FromSeconds(5)); return "7:3"; });
        var service = new LapDeltaService(store);
        service.Configure(true, LapDeltaReference.SessionBest);
        await DriveAsync(service, 0, 0);
        writes.Reset();
        await DriveAsync(service, 1, 620);
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        service.Dispose();
        var completion = service.Completion;
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(completion.IsCompleted);
        Assert.False(File.Exists(path));
        writes.Set();
        await completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.NotNull(store.Load());
        File.Delete(path);
    }

    [Fact]
    public async Task BackgroundConsumerPublishesFreshReferenceAndFencesSettingsChanges()
    {
        using var service = new LapDeltaService();
        service.Configure(true, LapDeltaReference.SessionBest, mapEnabled: true);
        for (var i = 0; i <= 620; i++)
        {
            var state = State(i);
            service.Observe(state);
            if (i % 40 == 0) await WaitFor(() => service.Latest.ReceivedTimestamp == state.ReceivedTimestamp);
        }
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        Assert.InRange(service.Latest.Seconds!.Value, -.01, .01);
        Assert.True(service.LatestMap!.Outline.Complete);
        service.Configure(true, LapDeltaReference.PreviousLap);
        Assert.Null(service.Latest.Seconds);
        service.Observe(State(621));
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        service.Reset();
        Assert.Null(service.Latest.Seconds);
        Assert.Null(service.LatestMap);
        service.Observe(State(622));
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.WaitingForLap);
        service.Configure(false, LapDeltaReference.PreviousLap);
        service.Observe(State(623));
        Assert.Null(service.Latest.Seconds);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RejectedPacketAndPauseKeepReferenceWhileFencingOldFrames()
    {
        using var service = new LapDeltaService();
        service.Configure(true, LapDeltaReference.SessionBest, true);
        for (var i = 0; i <= 800; i++)
        {
            var state = State(i);
            service.Observe(state);
            if (i % 40 == 0) await WaitFor(() => service.Latest.ReceivedTimestamp == state.ReceivedTimestamp);
        }
        var map = service.LatestMap!.Outline;
        service.Observe(null);
        Assert.Null(service.Latest.Seconds);
        Assert.Null(service.LatestMap);
        service.Observe(State(801));
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        Assert.Same(map, service.LatestMap!.Outline);
        service.Observe(State(802) with { IsRaceOn = false });
        service.Observe(State(803));
        await WaitFor(() => service.Latest.Status == LapDeltaStatus.Comparing);
        Assert.Same(map, service.LatestMap!.Outline);
        service.Configure(true, LapDeltaReference.SessionBest, true, LapTimingMode.TimeAttack);
        Assert.Null(service.Latest.Seconds);
        Assert.Null(service.LatestMap);
        service.Observe(State(804));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Null(service.Latest.Seconds);
        Assert.Null(service.LatestMap);
    }

    [Fact]
    public async Task StaleTelemetryAndQueuedFramesCannotSurviveReset()
    {
        using var service = new LapDeltaService();
        service.Configure(true, LapDeltaReference.SessionBest, mapEnabled: true);
        var stale = State(0) with { ReceivedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency };
        service.Observe(stale);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Same(LapDeltaReading.Waiting, service.Latest);
        Assert.Null(service.LatestMap);
        for (var i = 0; i < 10_000; i++) service.Observe(State(i));
        service.Configure(false, LapDeltaReference.SessionBest);
        service.Reset();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Same(LapDeltaReading.Waiting, service.Latest);
        Assert.Null(service.LatestMap);
        service.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }
}
