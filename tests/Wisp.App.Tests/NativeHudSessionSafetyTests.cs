using System.Collections.Concurrent;
using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudSessionSafetyTests
{
    private const int CarA = 314;
    private const int CarB = 3766;
    private const int FutureCar = 2_000_000_123;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(6);

    [Theory]
    [InlineData("pause")]
    [InlineData("car-switch")]
    [InlineData("layout-toggle")]
    [InlineData("race-off")]
    public async Task OldReadCannotRepublishAfterReturningToTheSameCar(string transition)
    {
        using var oldRead = new ReadGate();
        using var nextRead = new ReadGate();
        var memory = new SessionMemory(CarA, oldRead, nextRead);
        await using var service = new NativeHudProcessService(new SessionFactory(memory));
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await oldRead.WaitUntilEnteredAsync();

            switch (transition)
            {
                case "pause":
                    service.UpdateTelemetry(null, nativeLayoutActive: false);
                    break;
                case "car-switch":
                    service.UpdateTelemetry(State(CarB, timestamp: 2), nativeLayoutActive: true);
                    break;
                case "layout-toggle":
                    service.UpdateTelemetry(State(CarA, timestamp: 2), nativeLayoutActive: false);
                    break;
                case "race-off":
                    service.UpdateTelemetry(State(CarA, timestamp: 2) with { IsRaceOn = false }, nativeLayoutActive: true);
                    break;
            }

            memory.ConfigureVehicle(CarA, redlineRpm: 7_500);
            service.UpdateTelemetry(State(CarA, timestamp: 3), nativeLayoutActive: true);
            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);

            oldRead.Release();
            await nextRead.WaitUntilEnteredAsync();
            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
            Assert.False(service.SnapshotFor(CarB).HasAvailableCapabilities);

            nextRead.Release();
            var resumed = await WaitForSnapshotAsync(service, CarA, snapshot => snapshot.HasAvailableCapabilities);
            Assert.True(resumed.Available);
            Assert.Equal(7_500, resumed.ExactRedline.Rpm, 2);
            Assert.True(resumed.Assists.Available);
        }
        finally
        {
            oldRead.Release();
            nextRead.Release();
        }
    }

    [Fact]
    public async Task StaleReadFailureCannotReplaceTheNewCarsUnavailableState()
    {
        using var oldRead = new ReadGate();
        using var nextRead = new ReadGate();
        var memory = new SessionMemory(CarA, oldRead, nextRead);
        await using var service = new NativeHudProcessService(new SessionFactory(memory));
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await oldRead.WaitUntilEnteredAsync();

            memory.ConfigureVehicle(CarB);
            service.UpdateTelemetry(State(CarB, timestamp: 2), nativeLayoutActive: true);
            oldRead.Release();
            await nextRead.WaitUntilEnteredAsync();

            var pending = service.SnapshotFor(CarB);
            Assert.False(pending.HasAvailableCapabilities);
            Assert.Equal(NativeAssistProviderStatus.Unavailable, pending.Status);
            nextRead.Release();
            var switched = await WaitForSnapshotAsync(service, CarB, snapshot => snapshot.HasAvailableCapabilities);
            Assert.Equal(CarB, switched.CarOrdinal);
            Assert.True(switched.Assists.Available);
        }
        finally
        {
            oldRead.Release();
            nextRead.Release();
        }
    }

    [Fact]
    public async Task ForwardSameCarPacketsDoNotStarveAnInFlightValidRead()
    {
        using var oldRead = new ReadGate();
        using var nextRead = new ReadGate();
        var memory = new SessionMemory(CarA, oldRead, nextRead);
        var factory = new SessionFactory(memory);
        await using var service = new NativeHudProcessService(factory);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await oldRead.WaitUntilEnteredAsync();
            for (uint timestamp = 2; timestamp <= 128; timestamp++)
            {
                service.UpdateTelemetry(State(CarA, timestamp), nativeLayoutActive: true);
            }

            oldRead.Release();
            await nextRead.WaitUntilEnteredAsync();
            var published = service.SnapshotFor(CarA);
            Assert.True(published.HasAvailableCapabilities);
            Assert.True(published.Available);
            Assert.Equal(6_500, published.ExactRedline.Rpm, 2);
            Assert.Equal(1, factory.OpenCount);
            nextRead.Release();
        }
        finally
        {
            oldRead.Release();
            nextRead.Release();
        }
    }

    [Fact]
    public async Task StaleAttachFailureCannotReplaceTheNewSessionStatus()
    {
        using var oldOpen = new ReadGate();
        using var nextOpen = new ReadGate();
        using var feedCancellation = new CancellationTokenSource();
        var memory = new SessionMemory(CarB);
        var factory = new SessionFactory(memory, oldOpen, nextOpen, rejectFirstOpen: true);
        await using var service = new NativeHudProcessService(factory);
        var feed = Task.CompletedTask;
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await oldOpen.WaitUntilEnteredAsync();
            service.UpdateTelemetry(State(CarB, timestamp: 2), nativeLayoutActive: true);
            feed = FeedSameCarAsync(service, State(CarB, timestamp: 2), feedCancellation.Token);
            oldOpen.Release();
            await nextOpen.WaitUntilEnteredAsync();

            var pending = service.SnapshotFor(CarB);
            Assert.False(pending.HasAvailableCapabilities);
            Assert.Equal(NativeAssistProviderStatus.Unavailable, pending.Status);
            nextOpen.Release();
            var ready = await WaitForSnapshotAsync(service, CarB, snapshot => snapshot.HasAvailableCapabilities);
            Assert.Equal(CarB, ready.CarOrdinal);
            Assert.True(ready.Assists.Available);
        }
        finally
        {
            oldOpen.Release();
            nextOpen.Release();
            feedCancellation.Cancel();
            await feed;
        }
    }

    [Fact]
    public async Task DisposalClearsPublishedStateRejectsLateReadsAndAcceptsFurtherTelemetry()
    {
        using var pendingRead = new ReadGate();
        var memory = new SessionMemory(CarA, nextRead: pendingRead);
        var factory = new SessionFactory(memory);
        await using var service = new NativeHudProcessService(factory);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            await WaitForSnapshotAsync(service, CarA, snapshot => snapshot.HasAvailableCapabilities);
            service.UpdateTelemetry(State(CarA, timestamp: 2), nativeLayoutActive: true);
            await pendingRead.WaitUntilEnteredAsync();

            var disposal = service.DisposeAsync().AsTask();
            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
            service.UpdateTelemetry(null, nativeLayoutActive: false);
            service.UpdateTelemetry(State(CarB), nativeLayoutActive: true);
            pendingRead.Release();
            await disposal.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            for (uint timestamp = 3; timestamp < 32; timestamp++)
            {
                service.UpdateTelemetry(State(CarA, timestamp), nativeLayoutActive: true);
                service.UpdateTelemetry(null, nativeLayoutActive: false);
            }

            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
            Assert.False(service.SnapshotFor(CarB).HasAvailableCapabilities);
            Assert.Equal(1, factory.OpenCount);
        }
        finally
        {
            pendingRead.Release();
        }
    }

    [Fact]
    public async Task CompatibilityGenerationChangeHidesPublishedAndInFlightOldPackSnapshots()
    {
        using var oldPackRead = new ReadGate();
        using var newPackRead = new ReadGate();
        var memory = new SessionMemory(CarA, nextRead: oldPackRead, thirdRead: newPackRead);
        var factory = new SessionFactory(memory);
        await using var service = new NativeHudProcessService(factory);
        try
        {
            service.UpdateTelemetry(State(CarA), nativeLayoutActive: true);
            var original = await WaitForSnapshotAsync(service, CarA, snapshot => snapshot.HasAvailableCapabilities);
            service.UpdateTelemetry(State(CarA, timestamp: 2), nativeLayoutActive: true);
            await oldPackRead.WaitUntilEnteredAsync();

            factory.AdvanceCompatibilityGeneration();
            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
            service.UpdateTelemetry(State(CarA, timestamp: 3), nativeLayoutActive: true);
            oldPackRead.Release();
            await newPackRead.WaitUntilEnteredAsync();

            Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
            Assert.Equal(2, factory.OpenCount);
            newPackRead.Release();
            var current = await WaitForSnapshotAsync(service, CarA, snapshot => snapshot.HasAvailableCapabilities);
            Assert.True(current.Generation > original.Generation);
            Assert.True(current.Available);
            Assert.True(current.Assists.Available);
        }
        finally
        {
            oldPackRead.Release();
            newPackRead.Release();
        }
    }

    [Fact]
    public async Task InitialNullRaceOffAndInactiveLayoutSamplesAreHarmless()
    {
        var memory = new SessionMemory(FutureCar);
        var factory = new SessionFactory(memory);
        await using var service = new NativeHudProcessService(factory);

        service.UpdateTelemetry(null, nativeLayoutActive: false);
        service.UpdateTelemetry(null, nativeLayoutActive: true);
        service.UpdateTelemetry(State(FutureCar) with { IsRaceOn = false }, nativeLayoutActive: true);
        service.UpdateTelemetry(State(FutureCar), nativeLayoutActive: false);
        Assert.False(service.SnapshotFor(FutureCar).HasAvailableCapabilities);
        Assert.Equal(0, factory.OpenCount);

        service.UpdateTelemetry(State(FutureCar, timestamp: 2), nativeLayoutActive: true);
        var ready = await WaitForSnapshotAsync(service, FutureCar, snapshot => snapshot.HasAvailableCapabilities);
        Assert.Equal(FutureCar, ready.CarOrdinal);
        Assert.True(ready.Assists.Available);
    }

    [Theory]
    [InlineData(12, 4_000f, 8_000f, true)]
    [InlineData(0, 4_000f, 8_000f, true)]
    public async Task UnknownHighCarIdsUseTelemetryPowertrainAndAvailableCapabilities(
        int cylinders,
        float rpm,
        float maximumRpm,
        bool tachometerAvailable)
    {
        var memory = new SessionMemory(FutureCar);
        memory.ConfigureVehicle(FutureCar, rpm, maximumRpm, maximumRpm > 0 ? 6_500 : 0);
        await using var service = new NativeHudProcessService(new SessionFactory(memory));
        var state = State(FutureCar) with
        {
            NumCylinders = cylinders,
            EngineRpm = rpm,
            EngineMaximumRpm = maximumRpm
        };

        Assert.Equal(cylinders == 0, state.IsElectric);
        service.UpdateTelemetry(state, nativeLayoutActive: true);
        var snapshot = await WaitForSnapshotAsync(service, FutureCar, result => result.HasAvailableCapabilities);
        Assert.Equal(FutureCar, snapshot.CarOrdinal);
        Assert.Equal(tachometerAvailable, snapshot.Available);
        Assert.True(snapshot.Assists.Available);
        Assert.True(snapshot.Assists.IsTCROn);
        Assert.False(service.SnapshotFor(CarA).HasAvailableCapabilities);
    }

    [Fact]
    public async Task UnknownElectricMetadataCannotBypassTheVerifiedMaximumRpmGuard()
    {
        var memory = new SessionMemory(FutureCar);
        memory.ConfigureVehicle(FutureCar, rpm: 0, maximumRpm: 0, redlineRpm: 0);
        await using var service = new NativeHudProcessService(new SessionFactory(memory));
        var state = State(FutureCar) with { NumCylinders = 0, EngineRpm = 0, EngineMaximumRpm = 0 };

        Assert.True(state.IsElectric);
        service.UpdateTelemetry(state, nativeLayoutActive: true);
        var snapshot = await WaitForSnapshotAsync(
            service, FutureCar, result => result.Status == NativeAssistProviderStatus.InvalidProvider);
        Assert.False(snapshot.HasAvailableCapabilities);
        Assert.False(snapshot.Available);
        Assert.False(snapshot.Assists.Available);
    }

    [Fact]
    public async Task InvalidAssistThresholdDoesNotDiscardAValidTachometer()
    {
        var memory = new SessionMemory(FutureCar);
        memory.SetThreshold(0.2f);
        await using var service = new NativeHudProcessService(new SessionFactory(memory));

        service.UpdateTelemetry(State(FutureCar), nativeLayoutActive: true);
        var snapshot = await WaitForSnapshotAsync(service, FutureCar, result => result.HasAvailableCapabilities);
        Assert.True(snapshot.Available);
        Assert.False(snapshot.Assists.Available);
        Assert.Equal(6_500, snapshot.ExactRedline.Rpm, 2);
    }

    private static VehicleState State(int carOrdinal, uint timestamp = 1) => new()
    {
        IsRaceOn = true,
        CarOrdinal = carOrdinal,
        GameTimestampMilliseconds = timestamp,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        Drivetrain = DrivetrainType.RearWheelDrive,
        NumCylinders = 8,
        EngineRpm = 4_000,
        EngineMaximumRpm = 8_000,
        GroundSpeedMetersPerSecond = 0,
        WheelRotationRadiansPerSecond = new WheelValues(0, 0, 0, 0),
        TireSlipRatio = new WheelValues(0, 0, 0, 0),
        TireSlipAngle = new WheelValues(0, 0, 0, 0),
        NormalizedSuspensionTravel = new WheelValues(0, 0, 0, 0),
        LateralAccelerationMetersPerSecondSquared = 0,
        LongitudinalAccelerationMetersPerSecondSquared = 0,
        Gear = TransmissionGear.First,
        Steering = 0,
        Accelerator = 0,
        Brake = 0
    };

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

            await Task.Delay(10);
        }

        throw new TimeoutException("The fake native session did not reach the expected snapshot state.");
    }

    private static async Task FeedSameCarAsync(
        NativeHudProcessService service,
        VehicleState state,
        CancellationToken cancellationToken)
    {
        var timestamp = state.GameTimestampMilliseconds;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                service.UpdateTelemetry(state with { GameTimestampMilliseconds = ++timestamp }, nativeLayoutActive: true);
                await Task.Delay(16, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
                throw new TimeoutException("The test did not release its fake native-read gate.");
            }
        }

        public void Release() => _released.Set();
        public void Dispose() => _released.Dispose();
    }

    private sealed class SessionFactory(
        SessionMemory memory,
        ReadGate? oldOpen = null,
        ReadGate? nextOpen = null,
        bool rejectFirstOpen = false) : INativeHudProcessMemoryFactory
    {
        private int _openCount;
        private long _compatibilityGeneration;

        public int OpenCount => Volatile.Read(ref _openCount);
        public long CompatibilityGeneration => Interlocked.Read(ref _compatibilityGeneration);

        public void AdvanceCompatibilityGeneration() => Interlocked.Increment(ref _compatibilityGeneration);

        public bool TryOpen(out INativeHudProcessMemory? opened, out NativeAssistProviderStatus status)
        {
            var attempt = Interlocked.Increment(ref _openCount);
            if (attempt == 1)
            {
                oldOpen?.Enter();
            }
            else if (attempt == 2)
            {
                nextOpen?.Enter();
            }

            if (attempt == 1 && rejectFirstOpen)
            {
                opened = null;
                status = NativeAssistProviderStatus.UnsupportedBuild;
                return false;
            }

            memory.Reopen();
            opened = memory;
            status = NativeAssistProviderStatus.Ready;
            return true;
        }
    }

    private sealed class SessionMemory : INativeHudProcessMemory
    {
        private const ulong Module = 0x0000000140000000;
        private const ulong SourceList = 0x0000000200000000;
        private const ulong Source = 0x0000000300000000;
        private const ulong Provider = 0x0000000400000000;
        private const ulong FirstWheel = 0x0000000500000000;

        private readonly NativeHudCompatibilityPack _pack = NativeHudBuildContract.BuiltIn;
        private readonly ConcurrentDictionary<ulong, byte> _bytes = new();
        private readonly ConcurrentDictionary<ulong, uint> _uint32 = new();
        private readonly ConcurrentDictionary<ulong, ulong> _uint64 = new();
        private readonly ConcurrentDictionary<ulong, float> _singles = new();
        private readonly ReadGate? _oldRead;
        private readonly ReadGate? _nextRead;
        private readonly ReadGate? _thirdRead;
        private int _readAttempt;
        private int _disposed = 1;

        public SessionMemory(int carOrdinal, ReadGate? oldRead = null, ReadGate? nextRead = null, ReadGate? thirdRead = null)
        {
            _oldRead = oldRead;
            _nextRead = nextRead;
            _thirdRead = thirdRead;
            var fields = _pack.Fields;
            SetThreshold(0.1f);
            _uint64[Module + _pack.SourceVectorRva] = SourceList;
            _uint64[Module + _pack.SourceVectorRva + 8] = SourceList + 8;
            _uint64[Module + _pack.SourceVectorRva + 16] = SourceList + 8;
            _uint64[SourceList] = Source;
            _uint64[Source + fields.SourceProvider] = Provider;
            _uint64[Provider] = Module + _pack.LeadVtableRva;
            foreach (var slot in _pack.RequiredVtableSlots)
            {
                _uint64[Module + _pack.LeadVtableRva + slot.Key] = Module + slot.Value;
            }

            _bytes[Provider + fields.LocalPlayerFlag] = 1;
            _bytes[Provider + fields.LocalPlayerProviderFlag] = 1;
            _bytes[Provider + fields.StmAvailable] = 1;
            _bytes[Provider + fields.TcrAvailable] = 1;
            _bytes[Provider + fields.AbsAvailable] = 1;
            _bytes[Provider + fields.LcAvailable] = 1;
            _uint32[Provider + fields.StmState] = 0;
            _uint32[Provider + fields.AbsState] = 1;
            _uint32[Provider + fields.LcMode] = 2;
            _singles[Provider + fields.LcPrimary] = 0;
            _singles[Provider + fields.LcSecondary] = 0;
            _singles[Provider + fields.TcrSecondary] = 0;
            _singles[Provider + fields.TcrPrimary] = 0.2f;
            _singles[Provider + fields.TcrTertiary] = 0;

            var pointerFields = new[] { fields.FirstWheelPointer, fields.SecondWheelPointer, fields.ThirdWheelPointer };
            for (var index = 0; index < pointerFields.Length; index++)
            {
                var wheel = FirstWheel + ((ulong)index * 0x1000);
                _uint64[Provider + pointerFields[index]] = wheel;
                _uint32[wheel + fields.WheelId] = (uint)index;
            }

            for (ulong index = 0; index < 4; index++)
            {
                _singles[Provider + fields.TcrWheelValues + (index * 4)] = 0;
            }

            ConfigureVehicle(carOrdinal);
        }

        public ulong ModuleBase => Module;

        public void ConfigureVehicle(int carOrdinal, float rpm = 4_000, float maximumRpm = 8_000, float redlineRpm = 6_500)
        {
            var fields = _pack.Fields;
            _uint32[Source + fields.SourceCarOrdinal] = (uint)carOrdinal;
            _singles[Provider + fields.ProviderRpm] = ToAngularVelocity(rpm);
            _singles[Provider + fields.ProviderSimRedlineAngularVelocity] = ToAngularVelocity(redlineRpm);
            _singles[Provider + fields.ProviderTachometerMaximumAngularVelocity] = ToAngularVelocity(maximumRpm);
        }

        public void SetThreshold(float value) => _singles[Module + _pack.ThresholdRva] = value;
        public void Reopen() => Volatile.Write(ref _disposed, 0);
        public void Dispose() => Volatile.Write(ref _disposed, 1);

        public bool TryReadByte(ulong address, out byte value) => Read(_bytes, address, out value);
        public bool TryReadUInt32(ulong address, out uint value) => Read(_uint32, address, out value);
        public bool TryReadUInt64(ulong address, out ulong value) => Read(_uint64, address, out value);

        public bool TryReadSingle(ulong address, out float value)
        {
            var available = Read(_singles, address, out value);
            if (address == Module + _pack.ThresholdRva)
            {
                var attempt = Interlocked.Increment(ref _readAttempt);
                if (attempt == 1)
                {
                    _oldRead?.Enter();
                }
                else if (attempt == 2)
                {
                    _nextRead?.Enter();
                }
                else if (attempt == 3)
                {
                    _thirdRead?.Enter();
                }
            }

            return available;
        }

        private bool Read<T>(ConcurrentDictionary<ulong, T> values, ulong address, out T value) where T : struct
        {
            value = default;
            return Volatile.Read(ref _disposed) == 0 && values.TryGetValue(address, out value);
        }

        private static float ToAngularVelocity(float rpm) => rpm * 2 * MathF.PI / 60;
    }
}
