using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGameplayVisibilityServiceTests
{
    private const int CarA = 314;
    private const int CarB = 3766;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    [Theory]
    [InlineData(NativeGameplayVisibility.Visible)]
    [InlineData(NativeGameplayVisibility.Hidden)]
    public async Task VisibilityPublishesWithoutAnyGaugeSourceOrAssistCapability(NativeGameplayVisibility visibility)
    {
        var memory = new NoGaugeMemory();
        var factory = new FakeFactory(memory);
        var resolver = new ScriptedResolver(new ReadStep(visibility));
        NativeHudCompatibilityPack? attachedPack = null;
        await using var service = new NativeHudProcessService(factory, pack =>
        {
            attachedPack = pack;
            return resolver;
        });
        var startedAt = Stopwatch.GetTimestamp();

        service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
        var snapshot = await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == visibility);

        Assert.True(snapshot.HasAvailableCapabilities);
        Assert.False(snapshot.Available);
        Assert.False(snapshot.Assists.Available);
        Assert.Equal(NativeAssistProviderStatus.InvalidSourceVector, snapshot.Status);
        Assert.InRange(snapshot.VisibilityObservedTimestamp, Math.Max(1L, startedAt), Stopwatch.GetTimestamp());
        Assert.Equal(CarA, snapshot.CarOrdinal);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, memory.DisposeCount);
        Assert.Same(memory.CompatibilityPack, attachedPack);
        Assert.Same(memory, resolver.LastMemory);
        Assert.Equal(memory.ModuleBase, resolver.LastModuleBase);
        AssertUnknown(service.SnapshotFor(CarB));
    }

    [Fact]
    public async Task HiddenHudContinuesReadingAndRecoversOnTheSameHandle()
    {
        using var visibleRead = new ReadGate();
        var memory = new NoGaugeMemory();
        var factory = new FakeFactory(memory);
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Hidden),
            new ReadStep(NativeGameplayVisibility.Visible, visibleRead));
        await using var service = new NativeHudProcessService(factory, _ => resolver);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            var hidden = await WaitForSnapshotAsync(
                service,
                CarA,
                value => value.GameplayVisibility == NativeGameplayVisibility.Hidden);
            await visibleRead.WaitUntilEnteredAsync();
            service.UpdateTelemetry(State(CarA, 2), nativeLayoutActive: true);
            visibleRead.Release();
            var visible = await WaitForSnapshotAsync(
                service,
                CarA,
                value => value.Generation > hidden.Generation &&
                         value.GameplayVisibility == NativeGameplayVisibility.Visible);

            Assert.True(visible.VisibilityObservedTimestamp >= hidden.VisibilityObservedTimestamp);
            Assert.Equal(1, factory.OpenCount);
            Assert.Equal(0, memory.DisposeCount);
            Assert.True(memory.SourceVectorReadCount >= 2);
            Assert.False(visible.Available);
            Assert.False(visible.Assists.Available);
        }
        finally
        {
            visibleRead.Release();
        }
    }

    [Fact]
    public async Task RepeatedFrozenTelemetryRefreshesNativeSceneWithoutAReaderRestart()
    {
        using var hiddenRead = new ReadGate();
        using var restoredRead = new ReadGate();
        var memory = new NoGaugeMemory();
        var factory = new FakeFactory(memory);
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible),
            new ReadStep(NativeGameplayVisibility.Hidden, hiddenRead),
            new ReadStep(NativeGameplayVisibility.Visible, restoredRead));
        await using var service = new NativeHudProcessService(factory, _ => resolver);
        var frozenTelemetry = State(CarA) with { ReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };

        try
        {
            service.UpdateTelemetry(frozenTelemetry, nativeLayoutActive: true);
            var visible = await WaitForSnapshotAsync(service, CarA,
                value => value.GameplayVisibility == NativeGameplayVisibility.Visible);

            Assert.True(AppController.ShouldPreserveHudVisuals(
                true, TelemetryConnectionState.Lost, false, true, true));
            service.UpdateTelemetry(frozenTelemetry, nativeLayoutActive: true);
            await hiddenRead.WaitUntilEnteredAsync();
            hiddenRead.Release();
            var hidden = await WaitForSnapshotAsync(
                service,
                CarA,
                value => value.Generation > visible.Generation &&
                         value.GameplayVisibility == NativeGameplayVisibility.Hidden);
            Assert.False(OverlayVisibilityPolicy.ShouldShow(
                true, false, true, false, true, false, true, false, hidden.GameplayVisibility, true));

            service.UpdateTelemetry(frozenTelemetry, nativeLayoutActive: true);
            await restoredRead.WaitUntilEnteredAsync();
            restoredRead.Release();
            var restored = await WaitForSnapshotAsync(
                service,
                CarA,
                value => value.Generation > hidden.Generation &&
                         value.GameplayVisibility == NativeGameplayVisibility.Visible);
            Assert.True(OverlayVisibilityPolicy.ShouldShow(
                true, false, true, false, true, false, true, false, restored.GameplayVisibility, true));
            Assert.Equal(1, factory.OpenCount);
            Assert.Equal(0, memory.DisposeCount);
            Assert.True(memory.SourceVectorReadCount >= 3);
            Assert.False(restored.Available);
            Assert.False(restored.Assists.Available);
        }
        finally
        {
            hiddenRead.Release();
            restoredRead.Release();
        }
    }

    [Theory]
    [InlineData(NativeGameplayVisibility.Unknown)]
    [InlineData((NativeGameplayVisibility)99)]
    public async Task UnknownOrInvalidReaderResultHasNoObservationTimestamp(NativeGameplayVisibility visibility)
    {
        var resolver = new ScriptedResolver(new ReadStep(visibility));
        await using var service = new NativeHudProcessService(new FakeFactory(new NoGaugeMemory()), _ => resolver);

        service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
        var snapshot = await WaitForSnapshotAsync(service, CarA, value => value.Generation > 0);

        AssertUnknown(snapshot);
        Assert.False(snapshot.HasAvailableCapabilities);
    }

    [Fact]
    public async Task FailedVisibilityReadClearsThePreviousKnownState()
    {
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible),
            new ReadStep(NativeGameplayVisibility.Unknown));
        await using var service = new NativeHudProcessService(new FakeFactory(new NoGaugeMemory()), _ => resolver);

        service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
        var visible = await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == NativeGameplayVisibility.Visible);
        service.UpdateTelemetry(State(CarA, 2), nativeLayoutActive: true);
        var unknown = await WaitForSnapshotAsync(service, CarA, value => value.Generation > visible.Generation);

        AssertUnknown(unknown);
        Assert.False(unknown.HasAvailableCapabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldVisibilityCannotRepublishAfterPauseOrCarSwitchBackToTheSameCar(bool switchCar)
    {
        using var oldRead = new ReadGate();
        using var nextRead = new ReadGate();
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible, oldRead),
            new ReadStep(NativeGameplayVisibility.Hidden, nextRead));
        await using var service = new NativeHudProcessService(new FakeFactory(new NoGaugeMemory()), _ => resolver);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await oldRead.WaitUntilEnteredAsync();
            service.UpdateTelemetry(switchCar ? State(CarB, 2) : null, nativeLayoutActive: switchCar);
            service.UpdateTelemetry(State(CarA, 3), nativeLayoutActive: true);
            AssertUnknown(service.SnapshotFor(CarA));

            oldRead.Release();
            await nextRead.WaitUntilEnteredAsync();
            AssertUnknown(service.SnapshotFor(CarA));
            AssertUnknown(service.SnapshotFor(CarB));
            nextRead.Release();

            var current = await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == NativeGameplayVisibility.Hidden);
            Assert.True(current.VisibilityObservedTimestamp > 0);
        }
        finally
        {
            oldRead.Release();
            nextRead.Release();
        }
    }

    [Fact]
    public async Task ForwardSameCarTelemetryDoesNotStarveVisibilityPublication()
    {
        using var firstRead = new ReadGate();
        using var nextRead = new ReadGate();
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible, firstRead),
            new ReadStep(NativeGameplayVisibility.Hidden, nextRead));
        await using var service = new NativeHudProcessService(new FakeFactory(new NoGaugeMemory()), _ => resolver);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await firstRead.WaitUntilEnteredAsync();
            for (uint timestamp = 2; timestamp <= 20; timestamp++)
            {
                service.UpdateTelemetry(State(CarA, timestamp), nativeLayoutActive: true);
            }

            firstRead.Release();
            await nextRead.WaitUntilEnteredAsync();
            var published = service.SnapshotFor(CarA);
            Assert.Equal(NativeGameplayVisibility.Visible, published.GameplayVisibility);
            Assert.True(published.VisibilityObservedTimestamp > 0);
            nextRead.Release();
        }
        finally
        {
            firstRead.Release();
            nextRead.Release();
        }
    }

    [Fact]
    public async Task CompatibilityChangeRejectsPublishedAndInFlightVisibilityAndRecreatesTheResolver()
    {
        using var oldRead = new ReadGate();
        using var newRead = new ReadGate();
        var factory = new FakeFactory(new NoGaugeMemory());
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible),
            new ReadStep(NativeGameplayVisibility.Visible, oldRead),
            new ReadStep(NativeGameplayVisibility.Hidden, newRead));
        var resolverCreations = 0;
        await using var service = new NativeHudProcessService(factory, _ =>
        {
            Interlocked.Increment(ref resolverCreations);
            return resolver;
        });
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            var original = await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == NativeGameplayVisibility.Visible);
            service.UpdateTelemetry(State(CarA, 2), nativeLayoutActive: true);
            await oldRead.WaitUntilEnteredAsync();

            factory.AdvanceCompatibilityGeneration();
            AssertUnknown(service.SnapshotFor(CarA));
            service.UpdateTelemetry(State(CarA, 3), nativeLayoutActive: true);
            oldRead.Release();
            await newRead.WaitUntilEnteredAsync();
            AssertUnknown(service.SnapshotFor(CarA));
            Assert.Equal(2, factory.OpenCount);
            Assert.Equal(2, Volatile.Read(ref resolverCreations));
            newRead.Release();

            var current = await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == NativeGameplayVisibility.Hidden);
            Assert.True(current.Generation > original.Generation);
        }
        finally
        {
            oldRead.Release();
            newRead.Release();
        }
    }

    [Fact]
    public async Task DisposalClearsVisibilityAndRejectsThePendingRead()
    {
        using var pendingRead = new ReadGate();
        var resolver = new ScriptedResolver(
            new ReadStep(NativeGameplayVisibility.Visible),
            new ReadStep(NativeGameplayVisibility.Visible, pendingRead));
        await using var service = new NativeHudProcessService(new FakeFactory(new NoGaugeMemory()), _ => resolver);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await WaitForSnapshotAsync(service, CarA, value => value.GameplayVisibility == NativeGameplayVisibility.Visible);
            service.UpdateTelemetry(State(CarA, 2), nativeLayoutActive: true);
            await pendingRead.WaitUntilEnteredAsync();

            var disposal = service.DisposeAsync().AsTask();
            AssertUnknown(service.SnapshotFor(CarA));
            service.UpdateTelemetry(State(CarB, 3), nativeLayoutActive: true);
            pendingRead.Release();
            await disposal.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            AssertUnknown(service.SnapshotFor(CarA));
            AssertUnknown(service.SnapshotFor(CarB));
        }
        finally
        {
            pendingRead.Release();
        }
    }

    private static void AssertUnknown(NativeHudSnapshot snapshot)
    {
        Assert.Equal(NativeGameplayVisibility.Unknown, snapshot.GameplayVisibility);
        Assert.Equal(0L, snapshot.VisibilityObservedTimestamp);
    }

    private static async Task<NativeHudSnapshot> WaitForSnapshotAsync(
        NativeHudProcessService service,
        int carOrdinal,
        Func<NativeHudSnapshot, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TestTimeout)
        {
            var snapshot = service.SnapshotFor(carOrdinal);
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("The fake gameplay visibility reader did not publish the expected state.");
    }

    private static VehicleState State(int carOrdinal, uint timestamp = 1) => new()
    {
        IsRaceOn = true,
        CarOrdinal = carOrdinal,
        EngineRpm = 4_000,
        EngineMaximumRpm = 8_000,
        GameTimestampMilliseconds = timestamp,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        Drivetrain = DrivetrainType.RearWheelDrive,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = default,
        TireSlipRatio = default,
        TireSlipAngle = default,
        NormalizedSuspensionTravel = default,
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

    private sealed record ReadStep(NativeGameplayVisibility Visibility, ReadGate? Gate = null);

    private sealed class ScriptedResolver(params ReadStep[] steps) : INativeGameplayVisibilityResolver
    {
        private int _reads;
        public IReadOnlyProcessMemory? LastMemory { get; private set; }
        public ulong LastModuleBase { get; private set; }

        public NativeGameplayVisibility Resolve(IReadOnlyProcessMemory memory, ulong moduleBase)
        {
            LastMemory = memory;
            LastModuleBase = moduleBase;
            var index = Interlocked.Increment(ref _reads) - 1;
            var step = steps[Math.Min(index, steps.Length - 1)];
            step.Gate?.Enter();
            return step.Visibility;
        }
    }

    private sealed class ReadGate : IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TestTimeout);

        public void Enter()
        {
            _entered.TrySetResult(true);
            if (!_released.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release its fake visibility-read gate.");
            }
        }

        public void Release() => _released.Set();
        public void Dispose() => _released.Dispose();
    }

    private sealed class FakeFactory(NoGaugeMemory memory) : INativeHudProcessMemoryFactory
    {
        private int _opens;
        private long _compatibilityGeneration;
        public int OpenCount => Volatile.Read(ref _opens);
        public long CompatibilityGeneration => Interlocked.Read(ref _compatibilityGeneration);

        public void AdvanceCompatibilityGeneration() => Interlocked.Increment(ref _compatibilityGeneration);

        public bool TryOpen(out INativeHudProcessMemory? opened, out NativeAssistProviderStatus status)
        {
            Interlocked.Increment(ref _opens);
            opened = memory;
            status = NativeAssistProviderStatus.Ready;
            return true;
        }
    }

    private sealed class NoGaugeMemory : INativeHudProcessMemory
    {
        private int _disposes;
        private int _sourceVectorReads;
        public ulong ModuleBase => 0x140000000;
        public NativeHudCompatibilityPack CompatibilityPack => NativeHudBuildContract.BuiltIn;
        public int DisposeCount => Volatile.Read(ref _disposes);
        public int SourceVectorReadCount => Volatile.Read(ref _sourceVectorReads);

        public bool TryReadByte(ulong address, out byte value)
        {
            value = 0;
            return false;
        }

        public bool TryReadUInt32(ulong address, out uint value)
        {
            value = 0;
            return false;
        }

        public bool TryReadUInt64(ulong address, out ulong value)
        {
            if (address == ModuleBase + CompatibilityPack.SourceVectorRva)
            {
                Interlocked.Increment(ref _sourceVectorReads);
            }

            value = 0;
            return false;
        }

        public bool TryReadSingle(ulong address, out float value)
        {
            value = 0;
            return false;
        }

        public void Dispose() => Interlocked.Increment(ref _disposes);
    }
}
