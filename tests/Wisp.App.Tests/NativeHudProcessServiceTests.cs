using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudProcessServiceTests
{
    private const ulong Module = 0x0000000140000000;
    private const ulong SourceList = 0x0000000200000000;
    private const ulong Source = 0x0000000300000000;
    private const ulong Provider = 0x0000000400000000;

    [Fact]
    public void FreshElectricGearSurvivesOneOptionalGaugeReadMiss()
    {
        var observed = Stopwatch.Frequency;
        var previous = ReadySnapshot(observed);
        var current = ReadySnapshot(0) with
        {
            ElectricGearState = NativeElectricGearState.Unavailable
        };

        var result = NativeHudProcessService.RetainFreshElectricGearState(
            current,
            previous,
            observed + TimestampMilliseconds(50));

        Assert.Equal(previous.ElectricGearState, result.ElectricGearState);
        Assert.Equal(observed, result.NativeGaugeObservedTimestamp);
    }

    [Theory]
    [InlineData(76)]
    [InlineData(500)]
    public void ElectricGearCarryForwardExpires(int ageMilliseconds)
    {
        var observed = Stopwatch.Frequency;
        var current = ReadySnapshot(0) with
        {
            ElectricGearState = NativeElectricGearState.Unavailable
        };

        var result = NativeHudProcessService.RetainFreshElectricGearState(
            current,
            ReadySnapshot(observed),
            observed + TimestampMilliseconds(ageMilliseconds));

        Assert.False(result.ElectricGearState.Available);
        Assert.Equal(0, result.NativeGaugeObservedTimestamp);
    }

    [Fact]
    public void ElectricGearCarryForwardNeverCrossesCars()
    {
        var observed = Stopwatch.Frequency;
        var current = ReadySnapshot(0) with
        {
            CarOrdinal = 3766,
            ElectricGearState = NativeElectricGearState.Unavailable
        };

        var result = NativeHudProcessService.RetainFreshElectricGearState(
            current,
            ReadySnapshot(observed),
            observed + TimestampMilliseconds(20));

        Assert.False(result.ElectricGearState.Available);
    }

    [Fact]
    public void CurrentNativeGaugeObservationIsNeverReplaced()
    {
        var previousObserved = Stopwatch.Frequency;
        var currentObserved = previousObserved + TimestampMilliseconds(20);
        var current = ReadySnapshot(currentObserved) with
        {
            ElectricGearState = NativeElectricGearState.Unavailable
        };

        var result = NativeHudProcessService.RetainFreshElectricGearState(
            current,
            ReadySnapshot(previousObserved),
            currentObserved);

        Assert.Same(current, result);
        Assert.False(result.ElectricGearState.Available);
    }

    [Fact]
    public async Task CarSwitchAndTelemetryReconnectNeverPublishStaleState()
    {
        var memory = ValidMemory(314, Provider, tcrOn: true);
        var factory = new FakeFactory(memory);
        await using var service = new NativeHudProcessService(factory);

        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        var first = await WaitForAsync(() => service.SnapshotFor(314), result => result.Available);
        Assert.True(first.Assists.IsTCROn);
        Assert.Equal(6_500, first.ExactRedline.Rpm, 2);
        Assert.Equal(8_000, first.TachometerMaximumRpm, 2);

        const ulong replacement = 0x0000000410000000;
        ConfigureProvider(memory, replacement, tcrOn: false);
        memory.SetUInt64(Source + 0x7740, replacement);
        memory.SetUInt32(Source + 0x740C, 3766);
        service.UpdateTelemetry(State(3766), nativeLayoutActive: true);

        Assert.False(service.SnapshotFor(3766).Available);
        Assert.False(service.SnapshotFor(314).Available);
        var switched = await WaitForAsync(() => service.SnapshotFor(3766), result => result.Available);
        Assert.False(switched.Assists.IsTCROn);
        Assert.Equal(3766, switched.CarOrdinal);

        service.UpdateTelemetry(null, nativeLayoutActive: false);
        Assert.False(service.SnapshotFor(3766).Available);
        await WaitForAsync(() => memory.DisposeCount, count => count > 0);

        service.UpdateTelemetry(State(3766), nativeLayoutActive: true);
        var reconnected = await WaitForAsync(() => service.SnapshotFor(3766), result => result.Available);
        Assert.False(reconnected.Assists.IsTCROn);
        Assert.True(factory.OpenCount >= 2);
    }

    [Fact]
    public async Task UnsupportedBuildStatusFailsClosed()
    {
        await using var service = new NativeHudProcessService(new RejectingFactory());
        service.UpdateTelemetry(State(314), nativeLayoutActive: true);

        var result = await WaitForAsync(
            () => service.SnapshotFor(314),
            snapshot => snapshot.Status == NativeAssistProviderStatus.UnsupportedBuild);

        Assert.False(result.Available);
    }

    [Fact]
    public async Task SameCarTuneChangeRefreshesExactStateOnTheExistingWorkerAndHandle()
    {
        var memory = ValidMemory(314, Provider, tcrOn: true);
        var factory = new FakeFactory(memory);
        await using var service = new NativeHudProcessService(factory);

        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        var stock = await WaitForAsync(
            () => service.SnapshotFor(314),
            snapshot => snapshot.Available && Math.Abs(snapshot.ExactRedline.Rpm - 6_500) < 0.1);
        Assert.True(stock.ExactRedline.IsExact);

        memory.SetSingle(Provider + 0x0248, 7_500 * 2 * MathF.PI / 60);
        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        var upgraded = await WaitForAsync(
            () => service.SnapshotFor(314),
            snapshot => snapshot.Available && Math.Abs(snapshot.ExactRedline.Rpm - 7_500) < 0.1);

        Assert.Equal(8_000, upgraded.TachometerMaximumRpm, 2);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task TelemetryMismatchClearsExactStateAndRecoversWithoutAnotherProcessHandle()
    {
        var memory = ValidMemory(314, Provider, tcrOn: true);
        var factory = new FakeFactory(memory);
        await using var service = new NativeHudProcessService(factory);

        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        await WaitForAsync(() => service.SnapshotFor(314), snapshot => snapshot.Available);

        memory.SetSingle(Provider + 0x024C, 9_000 * 2 * MathF.PI / 60);
        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        var failed = await WaitForAsync(
            () => service.SnapshotFor(314),
            snapshot => snapshot.Status == NativeAssistProviderStatus.TelemetryMismatch);
        Assert.False(failed.Available);
        Assert.False(failed.ExactRedline.IsExact);

        memory.SetSingle(Provider + 0x024C, 8_000 * 2 * MathF.PI / 60);
        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        var recovered = await WaitForAsync(() => service.SnapshotFor(314), snapshot => snapshot.Available);
        Assert.True(recovered.ExactRedline.IsExact);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task PacketBurstCannotExceedTheSixtyHertzFullAuditCadence()
    {
        var memory = ValidMemory(314, Provider, tcrOn: true);
        var factory = new FakeFactory(memory);
        var visibility = new CountingVisibilityResolver();
        await using var service = new NativeHudProcessService(factory, _ => visibility);

        service.UpdateTelemetry(State(314), nativeLayoutActive: true);
        await WaitForAsync(() => visibility.ReadCount, count => count >= 1);

        for (var timestamp = 2u; timestamp <= 2_000; timestamp++)
        {
            service.UpdateTelemetry(
                State(314) with { GameTimestampMilliseconds = timestamp },
                nativeLayoutActive: true);
            service.RequestNativeGaugeSample();
        }

        await WaitForAsync(() => visibility.ReadCount, count => count >= 3);
        Assert.True(
            visibility.MinimumGapTicks >= (long)Math.Ceiling(Stopwatch.Frequency / 60d),
            $"Full native reads were only {visibility.MinimumGapTicks} ticks apart.");
        Assert.Equal(1, factory.OpenCount);
    }

    private static VehicleState State(int carOrdinal) => new()
    {
        IsRaceOn = true,
        CarOrdinal = carOrdinal,
        EngineRpm = 4_000,
        EngineMaximumRpm = 8_000,
        GameTimestampMilliseconds = 1,
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        Drivetrain = DrivetrainType.RearWheelDrive,
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

    private static NativeHudSnapshot ReadySnapshot(long observedTimestamp) =>
        NativeHudSnapshot.Unavailable(
            NativeAssistProviderStatus.Ready,
            generation: 1,
            carOrdinal: 314) with
        {
            Available = true,
            NativeGaugeObservedTimestamp = observedTimestamp,
            ElectricGearState = new NativeElectricGearState(
                true,
                1,
                2,
                0,
                -1,
                true)
        };

    private static long TimestampMilliseconds(int milliseconds) =>
        (long)Math.Round(Stopwatch.Frequency * milliseconds / 1_000d);

    private static FakeProcessMemory ValidMemory(int carOrdinal, ulong provider, bool tcrOn)
    {
        var memory = new FakeProcessMemory(Module);
        memory.SetSingle(Module + NativeHudBuildContract.ThresholdRva, 0.1f);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva, SourceList);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 8, SourceList + 8);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 16, SourceList + 8);
        memory.SetUInt64(SourceList, Source);
        memory.SetUInt64(Source + 0x7740, provider);
        memory.SetUInt32(Source + 0x740C, (uint)carOrdinal);
        ConfigureProvider(memory, provider, tcrOn);
        return memory;
    }

    private static void ConfigureProvider(FakeProcessMemory memory, ulong provider, bool tcrOn)
    {
        var vtable = Module + NativeHudBuildContract.LeadVtableRva;
        memory.SetUInt64(provider, vtable);
        foreach (var slot in NativeHudBuildContract.RequiredVtableSlots)
        {
            memory.SetUInt64(vtable + slot.Key, Module + slot.Value);
        }

        memory.SetByte(provider + 0x1464, 1);
        memory.SetByte(provider + 0xC330, 1);
        memory.SetSingle(provider + 0x01B0, 4_000 * 2 * MathF.PI / 60);
        memory.SetSingle(provider + 0x0248, 6_500 * 2 * MathF.PI / 60);
        memory.SetSingle(provider + 0x024C, 8_000 * 2 * MathF.PI / 60);
        memory.SetByte(provider + 0x17B4, 1);
        memory.SetByte(provider + 0x17B5, 1);
        memory.SetByte(provider + 0x17B6, 1);
        memory.SetByte(provider + 0x17B7, 1);
        memory.SetUInt32(provider + 0x1430, 0);
        memory.SetUInt32(provider + 0x1434, 1);
        memory.SetSingle(provider + 0x14EC, 0);
        memory.SetUInt32(provider + 0x1F7C, 2);
        memory.SetSingle(provider + 0xC220, 0);
        memory.SetSingle(provider + 0xC224, tcrOn ? 0.2f : 0);
        memory.SetSingle(provider + 0xC228, 0);
        for (var index = 0; index < 3; index++)
        {
            var wheel = 0x0000000500000000UL + ((ulong)index * 0x1000);
            memory.SetUInt64(provider + 0xBA0 + ((ulong)index * 8), wheel);
            memory.SetUInt32(wheel + 0x5A0, (uint)index);
        }

        for (var index = 0; index < 4; index++)
        {
            memory.SetSingle(provider + 0xC2C8 + ((ulong)index * 4), 0);
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var value = read();
            if (predicate(value))
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The native-assist test state did not settle.");
    }

    private sealed class FakeFactory(FakeProcessMemory memory) : INativeHudProcessMemoryFactory
    {
        public int OpenCount { get; private set; }

        public bool TryOpen(
            out INativeHudProcessMemory? opened,
            out NativeAssistProviderStatus status)
        {
            OpenCount++;
            memory.Reopen();
            opened = memory;
            status = NativeAssistProviderStatus.Ready;
            return true;
        }
    }

    private sealed class RejectingFactory : INativeHudProcessMemoryFactory
    {
        public bool TryOpen(
            out INativeHudProcessMemory? memory,
            out NativeAssistProviderStatus status)
        {
            memory = null;
            status = NativeAssistProviderStatus.UnsupportedBuild;
            return false;
        }
    }

    private sealed class CountingVisibilityResolver : INativeGameplayVisibilityResolver
    {
        private int _readCount;
        private long _lastReadTimestamp;
        private long _minimumGapTicks = long.MaxValue;
        public int ReadCount => Volatile.Read(ref _readCount);
        public long MinimumGapTicks => Interlocked.Read(ref _minimumGapTicks);

        public NativeGameplayVisibility Resolve(IReadOnlyProcessMemory memory, ulong moduleBase)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastReadTimestamp, now);
            if (previous > 0)
            {
                UpdateMinimum(ref _minimumGapTicks, now - previous);
            }

            Interlocked.Increment(ref _readCount);
            return NativeGameplayVisibility.Visible;
        }

        private static void UpdateMinimum(ref long location, long value)
        {
            var current = Interlocked.Read(ref location);
            while (value < current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class FakeProcessMemory(ulong moduleBase) : INativeHudProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, uint> _uint32 = [];
        private readonly Dictionary<ulong, ulong> _uint64 = [];
        private readonly Dictionary<ulong, float> _singles = [];
        private volatile bool _disposed;
        private int _disposeCount;

        public ulong ModuleBase { get; } = moduleBase;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryReadByte(ulong address, out byte value) => Read(_bytes, address, out value);
        public bool TryReadUInt32(ulong address, out uint value) => Read(_uint32, address, out value);
        public bool TryReadUInt64(ulong address, out ulong value) => Read(_uint64, address, out value);
        public bool TryReadSingle(ulong address, out float value) => Read(_singles, address, out value);

        public void SetByte(ulong address, byte value) => _bytes[address] = value;
        public void SetUInt32(ulong address, uint value) => _uint32[address] = value;
        public void SetUInt64(ulong address, ulong value) => _uint64[address] = value;
        public void SetSingle(ulong address, float value) => _singles[address] = value;
        public void Reopen() => _disposed = false;

        public void Dispose()
        {
            _disposed = true;
            Interlocked.Increment(ref _disposeCount);
        }

        private bool Read<T>(Dictionary<ulong, T> values, ulong address, out T value)
        {
            if (_disposed)
            {
                value = default!;
                return false;
            }

            return values.TryGetValue(address, out value!);
        }
    }
}
