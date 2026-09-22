using System.Buffers.Binary;
using System.Diagnostics;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeShiftLiveStateReaderTests
{
    private const ulong Module = 0x140000000;
    private const ulong Source = 0x300000000;
    private const ulong Provider = 0x400000000;
    private const int Car = 3289;
    private static NativeHudCompatibilityPack Pack => NativeHudBuildContract.BuiltIn;

    [Fact]
    public void ReadsNativeEngineAndGearStateWithConservativeTimestamp()
    {
        var memory = ValidMemory();
        var before = Stopwatch.GetTimestamp();
        Assert.True(Read(memory, out var state, out var diagnostic));
        Assert.NotNull(state);
        Assert.InRange(state.ObservedTimestamp, before, Stopwatch.GetTimestamp());
        Assert.Equal(new NativeShiftLiveReadDiagnostic(NativeShiftLiveReadOutcome.Success, 1, state.ObservedTimestamp), diagnostic);
        Assert.Equal(Car, state.CarOrdinal);
        Assert.Equal(7000, state.EngineRpm, 2);
        Assert.Equal(2, state.CurrentGear);
        Assert.Equal(2, state.RequestedGear);
        Assert.Equal(1, state.PreviousGear);
        Assert.False(state.LimiterActive);
        Assert.False(state.SecondaryLimiterActive);
        Assert.Equal(0, state.SecondaryBoundaryRpm);
        Assert.Equal(1, state.OutputControl);
        Assert.False(state.AlternateLimiterBranch);
    }

    [Fact]
    public void ReadsOnlyKnownBoundedFieldsAndBracketsFlagsWithoutRepeatingRpm()
    {
        var memory = ValidMemory();
        Assert.True(Read(memory, out _));
        Assert.Equal(new (ulong Address, int Count)[]
        {
            (Provider + 8, 3), (Provider + 0x224, 8),
            (Provider + 8, 3), (Provider + 0x224, 8)
        }, memory.BlockReads);
        Assert.Equal(1, memory.ReadCounts[Provider + 0x1B0]);
        Assert.Equal(1, memory.ReadCounts[Provider + 0x678]);
        Assert.Equal(1, memory.ReadCounts[Provider + 0x2520]);
        Assert.Equal(1, memory.ReadCounts[Module + 0xA8EA2AC]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(100_000)]
    public void InactiveSecondaryBoundaryIsNeverUsedAsAnOperativeLimit(float value)
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x678, value);
        Assert.True(Read(memory, out var state));
        Assert.Equal(0, state!.SecondaryBoundaryRpm);
    }

    [Fact]
    public void ActiveSecondaryStateRetainsItsBoundaryWithoutInventingItsGameplayName()
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + 0x224, 1);
        memory.UInt32(Provider + 0x228, 1);
        memory.Single(Provider + 0x678, (float)(6500 * Math.PI / 30));
        memory.Single(Provider + 0x2520, .15f);
        memory.Byte(Module + 0xA8EA2AC, 1);
        Assert.True(Read(memory, out var state));
        Assert.True(state!.LimiterActive);
        Assert.True(state.SecondaryLimiterActive);
        Assert.Equal(6500, state.SecondaryBoundaryRpm, 2);
        Assert.Equal(.15, state.OutputControl, 6);
        Assert.True(state.AlternateLimiterBranch);
    }

    [Fact]
    public void SharedLimiterFlagDoesNotActivateStaleSecondaryBoundary()
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + 0x224, 1);
        memory.Single(Provider + 0x678, 50);
        Assert.True(Read(memory, out var state));
        Assert.True(state!.LimiterActive);
        Assert.False(state.SecondaryLimiterActive);
        Assert.Equal(0, state.SecondaryBoundaryRpm);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10_000)]
    public void InvalidActiveSecondaryBoundaryFailsClosed(float value)
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + 0x228, 1);
        memory.Single(Provider + 0x678, value);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData(0x224)]
    [InlineData(0x228)]
    public void UnknownLimiterFlagValueFailsClosed(int offset)
    {
        var memory = ValidMemory();
        memory.UInt32(Provider + (ulong)offset, 2);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void OutOfRangeGearFailsClosed(int offset)
    {
        var memory = ValidMemory();
        memory.Byte(Provider + (ulong)offset, 255);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void StableCurrentAndRequestedDifferenceIsReportedForTransitionSuppression()
    {
        var memory = ValidMemory();
        memory.Byte(Provider + 9, 3);
        Assert.True(Read(memory, out var state));
        Assert.Equal(2, state!.CurrentGear);
        Assert.Equal(3, state.RequestedGear);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(10_000)]
    public void InvalidEngineAngularSpeedFailsClosed(float value)
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x1B0, value);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void FiniteOutputControlIsPreservedBecauseTheGameClampsItDownstream(float value)
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x2520, value);
        Assert.True(Read(memory, out var state));
        Assert.Equal(value, state!.OutputControl);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonfiniteOutputControlFailsClosed(float value)
    {
        var memory = ValidMemory();
        memory.Single(Provider + 0x2520, value);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData(8, "InitialControlsUnavailable")]
    [InlineData(0x1B0, "EngineSpeedUnavailable")]
    [InlineData(0x224, "InitialControlsUnavailable")]
    [InlineData(0x678, "SecondaryBoundaryUnavailable")]
    [InlineData(0x2520, "OutputControlUnavailable")]
    public void AnyFailedRequiredFieldDiscardsTheWholeObservation(int offset, string outcome)
    {
        var memory = ValidMemory();
        memory.FailAddress = Provider + (ulong)offset;
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(outcome, diagnostic.Outcome.ToString());
        Assert.Equal(1, diagnostic.Attempts);
        Assert.Equal(1, memory.ReadCounts[Provider + (ulong)offset]);
    }

    [Fact]
    public void UnreadableGlobalBranchFlagFailsClosed()
    {
        var memory = ValidMemory();
        memory.FailAddress = Module + 0xA8EA2AC;
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(NativeShiftLiveReadOutcome.BranchFlagUnavailable, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.Attempts);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x224)]
    public void PersistentGearOrLimiterChangesRejectBothMixedObservations(int offset)
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + (ulong)offset && count % 2 == 0)
                memory.Byte(address, offset == 8 ? (byte)(count / 2 + 2) : (byte)(count == 2 ? 1 : 0));
        };
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(NativeShiftLiveReadOutcome.InconsistentControls, diagnostic.Outcome);
        Assert.Equal(2, diagnostic.Attempts);
        Assert.Equal(4, memory.ReadCounts[Provider + (ulong)offset]);
        Assert.Equal(2, memory.ReadCounts[Provider + 0x1B0]);
    }

    [Fact]
    public void RecordedFourthGearLimiterRaceRetriesAllValuesInsteadOfPublishingMissingLiveState()
    {
        // Test7 events 20182/20186/20193 bracket one missing native observation
        // between these recorded fourth-gear samples. The export did not retain
        // the failed read's cause; inject a control-bracket race, not a claimed
        // replay of unrecorded memory bytes.
        var memory = ValidMemory();
        memory.Byte(Provider + 8, 4);
        memory.Byte(Provider + 9, 4);
        memory.Byte(Provider + 10, 11);
        memory.Single(Provider + 0x1B0, (float)(9733.955758530406 * Math.PI / 30));
        memory.Single(Provider + 0x2520, 0);
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0x224 && count == 2)
            {
                memory.UInt32(address, 1);
                memory.Single(Provider + 0x1B0, (float)(9747.150154044497 * Math.PI / 30));
                memory.Single(Provider + 0x2520, 1);
            }
        };

        Assert.True(Read(memory, out var state, out var diagnostic));
        Assert.NotNull(state);
        Assert.Equal(new NativeShiftLiveReadDiagnostic(NativeShiftLiveReadOutcome.Success, 2, state.ObservedTimestamp), diagnostic);
        Assert.Equal(4, state.CurrentGear);
        Assert.Equal(4, state.RequestedGear);
        Assert.Equal(9747.150154044497, state.EngineRpm, 2);
        Assert.True(state.LimiterActive);
        Assert.Equal(1, state.OutputControl);
        Assert.Equal(2, memory.ReadCounts[Provider + 0x1B0]);
        Assert.Equal(4, memory.ReadCounts[Provider + 0x224]);
    }

    [Fact]
    public void RetryReturnsNewGearTransitionInsteadOfHoldingOldGear()
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 8 && count == 2)
                memory.Byte(Provider + 9, 3);
        };
        Assert.True(Read(memory, out var state));
        Assert.Equal(2, state!.CurrentGear);
        Assert.Equal(3, state.RequestedGear);
        Assert.Equal(4, memory.ReadCounts[Provider + 8]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityChangeAfterInconsistentBracketNeverRetriesOldCar(bool providerChanged)
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0x224 && count == 2)
            {
                memory.UInt32(address, 1);
                if (providerChanged) memory.UInt64(Source + 0x7740, Provider + 0x10000);
                else memory.UInt32(Source + 0x740C, Car + 1);
            }
        };
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(NativeShiftLiveReadOutcome.FinalIdentityFailed, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.Attempts);
        Assert.Equal(1, memory.ReadCounts[Provider + 0x1B0]);
        Assert.Equal(2, memory.ReadCounts[Provider + 0x224]);
    }

    [Fact]
    public void SecondAttemptReadFailureDoesNotReturnFirstAttemptValues()
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0x224 && count == 2) memory.UInt32(address, 1);
            if (address == Provider + 0x1B0 && count == 2) memory.FailAddress = address;
        };
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(NativeShiftLiveReadOutcome.EngineSpeedUnavailable, diagnostic.Outcome);
        Assert.Equal(2, diagnostic.Attempts);
        Assert.Equal(2, memory.ReadCounts[Provider + 0x1B0]);
    }

    [Fact]
    public void InvalidChangedBracketFailsWithoutRetry()
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0x224 && count == 2) memory.UInt32(address, 2);
        };
        Assert.False(Read(memory, out var state, out var diagnostic));
        Assert.Null(state);
        Assert.Equal(NativeShiftLiveReadOutcome.InvalidControls, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.Attempts);
        Assert.Equal(1, memory.ReadCounts[Provider + 0x1B0]);
        Assert.Equal(2, memory.ReadCounts[Provider + 0x224]);
    }

    [Fact]
    public void ChangingRpmDoesNotRequireAnImpossibleFrozenSimulation()
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, _) =>
        {
            if (address == Provider + 0x2520)
                memory.Single(Provider + 0x1B0, (float)(7050 * Math.PI / 30));
        };
        Assert.True(Read(memory, out var state));
        Assert.Equal(7000, state!.EngineRpm, 2);
    }

    [Fact]
    public void ChangedSourceIdentityBeforeReadNeverReadsDynamics()
    {
        var memory = ValidMemory();
        memory.UInt32(Source + 0x740C, Car + 1);
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
        Assert.Empty(memory.BlockReads);
    }

    [Fact]
    public void ChangedProviderDuringReadCannotPublishOldCarState()
    {
        var memory = ValidMemory();
        memory.BeforeRead = (address, _) =>
        {
            if (address == Provider + 0x2520)
                memory.UInt64(Source + 0x7740, Provider + 0x10000);
        };
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void ChangedVtableDuringReadFailsTheFinalIdentityCheck()
    {
        var memory = ValidMemory();
        var slot = Pack.RequiredVtableSlots.First();
        memory.BeforeRead = (address, _) =>
        {
            if (address == Provider + 0x2520)
                memory.UInt64(Module + Pack.LeadVtableRva + slot.Key, Module + slot.Value + 1);
        };
        Assert.False(Read(memory, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void UnsupportedBuildDoesNotReadAnyMemory()
    {
        var memory = ValidMemory();
        Assert.False(NativeShiftLiveStateReader.TryRead(memory, Module, Source, Provider, Car,
            NativeHudBuildContract.StoreBuiltIn, out var state));
        Assert.Null(state);
        Assert.Empty(memory.ReadCounts);
    }

    private static bool Read(Memory memory, out ShiftCueLiveState? state) =>
        NativeShiftLiveStateReader.TryRead(memory, Module, Source, Provider, Car, Pack, out state);

    private static bool Read(Memory memory, out ShiftCueLiveState? state, out NativeShiftLiveReadDiagnostic diagnostic) =>
        NativeShiftLiveStateReader.TryRead(memory, Module, Source, Provider, Car, Pack, out state, out diagnostic);

    private static Memory ValidMemory()
    {
        var memory = new Memory();
        memory.UInt64(Source + 0x7740, Provider);
        memory.UInt32(Source + 0x740C, Car);
        memory.UInt64(Provider, Module + Pack.LeadVtableRva);
        foreach (var slot in Pack.RequiredVtableSlots)
            memory.UInt64(Module + Pack.LeadVtableRva + slot.Key, Module + slot.Value);
        memory.Byte(Provider + 8, 2);
        memory.Byte(Provider + 9, 2);
        memory.Byte(Provider + 10, 1);
        memory.UInt32(Provider + 0x224, 0);
        memory.UInt32(Provider + 0x228, 0);
        memory.Single(Provider + 0x1B0, (float)(7000 * Math.PI / 30));
        memory.Single(Provider + 0x678, 0);
        memory.Single(Provider + 0x2520, 1);
        memory.Byte(Module + 0xA8EA2AC, 0);
        return memory;
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        internal ulong? FailAddress;
        internal Action<ulong, int>? BeforeRead;
        internal Dictionary<ulong, int> ReadCounts { get; } = [];
        internal List<(ulong Address, int Count)> BlockReads { get; } = [];
        internal void Byte(ulong address, byte value) => _bytes[address] = value;
        internal void UInt32(ulong address, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Write(address, bytes); }
        internal void UInt64(ulong address, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Write(address, bytes); }
        internal void Single(ulong address, float value) => UInt32(address, BitConverter.SingleToUInt32Bits(value));
        private void Write(ulong address, ReadOnlySpan<byte> bytes) { for (var i = 0; i < bytes.Length; i++) _bytes[address + (ulong)i] = bytes[i]; }
        private bool Read(ulong address, Span<byte> bytes)
        {
            var count = ReadCounts.GetValueOrDefault(address) + 1;
            ReadCounts[address] = count;
            BeforeRead?.Invoke(address, count);
            if (FailAddress == address) return false;
            for (var i = 0; i < bytes.Length; i++) if (!_bytes.TryGetValue(address + (ulong)i, out bytes[i])) return false;
            return true;
        }
        public bool TryReadBytes(ulong address, Span<byte> destination) { BlockReads.Add((address, destination.Length)); return Read(address, destination); }
        public bool TryReadByte(ulong address, out byte value) { Span<byte> bytes = stackalloc byte[1]; var ok = Read(address, bytes); value = ok ? bytes[0] : (byte)0; return ok; }
        public bool TryReadUInt32(ulong address, out uint value) { Span<byte> bytes = stackalloc byte[4]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0; return ok; }
        public bool TryReadUInt64(ulong address, out ulong value) { Span<byte> bytes = stackalloc byte[8]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0; return ok; }
        public bool TryReadSingle(ulong address, out float value) { var ok = TryReadUInt32(address, out var bits); value = BitConverter.UInt32BitsToSingle(bits); return ok; }
    }
}
