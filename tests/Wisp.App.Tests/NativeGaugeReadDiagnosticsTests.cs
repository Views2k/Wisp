using System.Reflection;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGaugeReadDiagnosticsTests
{
    private const ulong Module = 0x140000000;
    private const ulong Hud = 0x260000010;
    private const ulong TypeVector = 0x270000000;
    private const ulong Instances = 0x280000000;
    private const ulong Outer = 0x290000000;
    private const ulong Child = 0x2A0000000;
    private const ulong ReplacementChild = 0x2A8000000;
    private const ulong Source = 0x2B0000000;
    private static NativeGaugeLayout Layout => NativeHudBuildContract.BuiltIn.NativeGauge!;

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(1u, false, false)]
    [InlineData(2u, false, true)]
    [InlineData(0u, true, false)]
    [InlineData(3u, true, true)]
    [InlineData(4u, true, false)]
    public void OptInPreservesResultsAndEveryReadInOrder(uint mode, bool electric, bool structural)
    {
        var offMemory = Memory.FromExistingFixture(mode, electric);
        var onMemory = Memory.FromExistingFixture(mode, electric);
        var off = new NativeGaugeDirectResolver();
        var on = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };

        for (var index = 0; index < 3; index++)
        {
            var expected = off.Read(offMemory, Module, Source, electric, structural);
            var actual = on.Read(onMemory, Module, Source, electric, structural);
            Equivalent(expected, actual);
            Assert.Equal(offMemory.Calls, onMemory.Calls);
            Assert.True(actual.IsAvailable);
            Assert.Equal(default, off.LastDiagnostics);
            Assert.True(on.LastDiagnostics.Enabled);
            Assert.True(on.LastDiagnostics.HasValidatedSample);
            Assert.Equal(NativeGaugeReadFailure.None, on.LastDiagnostics.Failure);
            Assert.Equal(index == 0 ? NativeGaugeCacheOutcome.Miss : NativeGaugeCacheOutcome.Hit,
                on.LastDiagnostics.CacheOutcome);
            Assert.Equal(mode, on.LastDiagnostics.Mode);
            Assert.Equal(actual.HasNeedlePair, on.LastDiagnostics.HasNeedlePair);
            Assert.True(on.LastDiagnostics.ElapsedStopwatchTicks >= 0);
            Assert.Equal((double)actual.NeedleAngleDegrees, on.LastDiagnostics.Angle);
            Assert.Equal((double)actual.NeedleBlurAmount, on.LastDiagnostics.Blur);
        }
    }

    [Theory]
    [InlineData("registry", NativeGaugeReadStage.Registry, NativeGaugeReadFailure.ValidationFailed)]
    [InlineData("hud", NativeGaugeReadStage.HudSignature, NativeGaugeReadFailure.ValidationFailed)]
    [InlineData("vector", NativeGaugeReadStage.TypeVector, NativeGaugeReadFailure.ValidationFailed)]
    [InlineData("type", NativeGaugeReadStage.TypeEntry, NativeGaugeReadFailure.NotFound)]
    [InlineData("duplicate-type", NativeGaugeReadStage.TypeEntry, NativeGaugeReadFailure.Ambiguous)]
    [InlineData("instance", NativeGaugeReadStage.Instance, NativeGaugeReadFailure.ValidationFailed)]
    [InlineData("source", NativeGaugeReadStage.Source, NativeGaugeReadFailure.WrongSource)]
    [InlineData("child", NativeGaugeReadStage.Child, NativeGaugeReadFailure.MissingChild)]
    [InlineData("vtable", NativeGaugeReadStage.ChildVtable, NativeGaugeReadFailure.ValidationFailed)]
    [InlineData("block", NativeGaugeReadStage.GaugeBlock, NativeGaugeReadFailure.ReadFailed)]
    [InlineData("mode", NativeGaugeReadStage.Mode, NativeGaugeReadFailure.UnexpectedMode)]
    [InlineData("angle", NativeGaugeReadStage.Angle, NativeGaugeReadFailure.InvalidAngle)]
    [InlineData("blur", NativeGaugeReadStage.Blur, NativeGaugeReadFailure.InvalidBlur)]
    [InlineData("maximum", NativeGaugeReadStage.TachometerMaximum, NativeGaugeReadFailure.InvalidMaximum)]
    public void FailedGuardsKeepTheSameReadsAndPublishOnlyTheFailure(
        string mutation, NativeGaugeReadStage stage, NativeGaugeReadFailure failure)
    {
        var offMemory = Memory.FromExistingFixture();
        var onMemory = Memory.FromExistingFixture();
        Mutate(offMemory, mutation);
        Mutate(onMemory, mutation);
        var off = new NativeGaugeDirectResolver();
        var on = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };

        Equivalent(off.Read(offMemory, Module, Source, false), on.Read(onMemory, Module, Source, false));
        Assert.Equal(offMemory.Calls, onMemory.Calls);
        Assert.Equal(stage, on.LastDiagnostics.Stage);
        Assert.Equal(failure, on.LastDiagnostics.Failure);
        Assert.False(on.LastDiagnostics.HasValidatedSample);
        Assert.False(on.LastDiagnostics.HasNeedlePair);
        Assert.Equal(0u, on.LastDiagnostics.Mode);
        Assert.True(double.IsNaN(on.LastDiagnostics.Angle));
        Assert.True(double.IsNaN(on.LastDiagnostics.Blur));
    }

    [Fact]
    public void ChangedOwnershipAfterTheGaugeCopyIsRejectedWithoutPublishingItsValues()
    {
        var memory = Memory.FromExistingFixture();
        memory.CopyChild();
        memory.BeforeBlock = () => memory.SetUInt64(Outer + Layout.OuterChildOffset, ReplacementChild);
        var reader = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };

        Assert.False(reader.Read(memory, Module, Source, false).IsAvailable);
        Assert.Equal(NativeGaugeReadStage.OwnershipRecheck, reader.LastDiagnostics.Stage);
        Assert.Equal(NativeGaugeReadFailure.OwnershipChanged, reader.LastDiagnostics.Failure);
        Assert.False(reader.LastDiagnostics.HasValidatedSample);
        Assert.True(double.IsNaN(reader.LastDiagnostics.Angle));
    }

    [Fact]
    public void RejectedCacheCanResolveTheReplacementAndStillReportsRecovery()
    {
        var offMemory = Memory.FromExistingFixture();
        var onMemory = Memory.FromExistingFixture();
        var off = new NativeGaugeDirectResolver();
        var on = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };
        Equivalent(off.Read(offMemory, Module, Source, false), on.Read(onMemory, Module, Source, false));
        foreach (var memory in new[] { offMemory, onMemory })
        {
            memory.CopyChild();
            memory.SetUInt64(Child, 0);
            memory.SetUInt64(Outer + Layout.OuterChildOffset, ReplacementChild);
        }

        Equivalent(off.Read(offMemory, Module, Source, false, false),
            on.Read(onMemory, Module, Source, false, false));
        Assert.Equal(offMemory.Calls, onMemory.Calls);
        Assert.Equal(NativeGaugeCacheOutcome.RejectedThenResolved, on.LastDiagnostics.CacheOutcome);
        Assert.Equal(NativeGaugeReadFailure.None, on.LastDiagnostics.Failure);
        Assert.True(on.LastDiagnostics.HasValidatedSample);
    }

    [Fact]
    public void RejectedCacheFailureDoesNotRetainPreviouslyValidatedValues()
    {
        var memory = Memory.FromExistingFixture();
        var reader = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };
        Assert.True(reader.Read(memory, Module, Source, false).IsAvailable);
        memory.SetSingle(Child + Layout.ChildAngleOffset, float.NaN);

        Assert.False(reader.Read(memory, Module, Source, false, false).IsAvailable);
        Assert.Equal(NativeGaugeCacheOutcome.RejectedThenUnavailable, reader.LastDiagnostics.CacheOutcome);
        Assert.Equal(NativeGaugeReadFailure.InvalidAngle, reader.LastDiagnostics.Failure);
        Assert.True(double.IsNaN(reader.LastDiagnostics.Angle));
        reader.DiagnosticsEnabled = false;
        Assert.Equal(default, reader.LastDiagnostics);
    }

    [Fact]
    public void ExistingArgumentExceptionStillPropagatesAndReportsNoSample()
    {
        var reader = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };
        Assert.Throws<ArgumentNullException>(() => reader.Read(null!, Module, Source, false));
        Assert.Equal(NativeGaugeReadFailure.ReadThrew, reader.LastDiagnostics.Failure);
        Assert.False(reader.LastDiagnostics.HasValidatedSample);
    }

    [Fact]
    public void EnabledCachedDiagnosticsDoNotAllocate()
    {
        var memory = Memory.FromExistingFixture();
        memory.RecordCalls = false;
        var reader = new NativeGaugeDirectResolver { DiagnosticsEnabled = true };
        for (var index = 0; index < 64; index++) reader.Read(memory, Module, Source, false, false);
        var started = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++) reader.Read(memory, Module, Source, false, false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - started;
        Assert.Equal(0, allocated);
        Assert.Equal(NativeGaugeCacheOutcome.Hit, reader.LastDiagnostics.CacheOutcome);
    }

    private static void Mutate(Memory memory, string name)
    {
        var target = TypeVector + 8 * Layout.HudTypeVectorEntryStride;
        switch (name)
        {
            case "registry": memory.SetUInt64(Module + Layout.RegistryGlobalRva, 0); break;
            case "hud": memory.SetByte(Module + Layout.HudSubobjectSlotZeroTargetRva, 0x90); break;
            case "vector": memory.SetUInt64(Hud + Layout.HudSubobjectOffset + Layout.HudTypeVectorOffset + Layout.HudTypeVectorEndOffset, TypeVector - 8); break;
            case "type": memory.SetUInt64(target + Layout.HudTypeTokenOffset, 0); break;
            case "duplicate-type": memory.SetUInt64(TypeVector + Layout.HudTypeTokenOffset, Module + Layout.HudTypeTokenRva); break;
            case "instance": memory.SetUInt64(target + Layout.HudTypeInstancesEndOffset, Instances); break;
            case "source": memory.SetUInt64(Outer + Layout.OuterSourceOffset, Source + 0x1000); break;
            case "child": memory.SetUInt64(Outer + Layout.OuterChildOffset, 0); break;
            case "vtable": memory.SetUInt64(Child, 0); break;
            case "block": memory.RejectGaugeBlock = true; break;
            case "mode": memory.SetUInt32(Child + Layout.ChildModeOffset, 4); break;
            case "angle": memory.SetSingle(Child + Layout.ChildAngleOffset, float.NaN); break;
            case "blur": memory.SetSingle(Child + Layout.ChildBlurOffset, 1); break;
            case "maximum": memory.SetSingle(Child + Layout.ChildMaximumTachometerOffset, 0); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static void Equivalent(NativeGaugeDirectResult expected, NativeGaugeDirectResult actual)
    {
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.IsElectric, actual.IsElectric);
        Assert.Equal(expected.HasNeedlePair, actual.HasNeedlePair);
        Assert.Equal(expected.NeedleAngleDegrees, actual.NeedleAngleDegrees);
        Assert.Equal(expected.NeedleBlurAmount, actual.NeedleBlurAmount);
        Assert.Equal(expected.TachometerMaximum, actual.TachometerMaximum);
        Assert.Equal(expected.PowerFillAmount, actual.PowerFillAmount);
        Assert.Equal(expected.RegenFillAmount, actual.RegenFillAmount);
        Assert.Equal(expected.RegenPowerRatio, actual.RegenPowerRatio);
        Assert.Equal(expected.ElectricMaximumSpeed, actual.ElectricMaximumSpeed);
        Assert.Equal(expected.HasHeadlightState, actual.HasHeadlightState);
        Assert.Equal(expected.AreHeadlightsOn, actual.AreHeadlightsOn);
        Assert.Equal(expected.ElectricGearState, actual.ElectricGearState);
        Assert.Equal(expected.DisplayedSpeedState, actual.DisplayedSpeedState);
        Assert.Equal(expected.ObservedTimestamp > 0, actual.ObservedTimestamp > 0);
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes;
        public List<(char Kind, ulong Address, int Size)> Calls { get; } = [];
        public bool RecordCalls { get; set; } = true;
        public bool RejectGaugeBlock { get; set; }
        public Action? BeforeBlock { get; set; }

        private Memory(Dictionary<ulong, byte> bytes) => _bytes = new(bytes);

        public static Memory FromExistingFixture(uint mode = 1, bool electric = false)
        {
            // Reuse the established reader fixture, without changing its test visibility.
            var fixture = typeof(NativeGaugeDirectResolverTests).GetMethod("ValidMemory", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [mode, electric, 13UL])!;
            var bytes = (Dictionary<ulong, byte>)fixture.GetType().GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture)!;
            return new Memory(bytes);
        }

        public void CopyChild()
        {
            for (ulong offset = 0; offset < 0x1000; offset++)
                if (_bytes.TryGetValue(Child + offset, out var value)) _bytes[ReplacementChild + offset] = value;
        }

        public void SetByte(ulong address, byte value) => _bytes[address] = value;
        public void SetUInt64(ulong address, ulong value)
        {
            for (var index = 0; index < 8; index++) _bytes[address + (ulong)index] = (byte)(value >> (index * 8));
        }
        public void SetUInt32(ulong address, uint value)
        {
            for (var index = 0; index < 4; index++) _bytes[address + (ulong)index] = (byte)(value >> (index * 8));
        }
        public void SetSingle(ulong address, float value) => SetUInt32(address, BitConverter.SingleToUInt32Bits(value));
        private void Record(char kind, ulong address, int size)
        {
            if (RecordCalls) Calls.Add((kind, address, size));
        }
        public bool TryReadByte(ulong address, out byte value)
        {
            Record('b', address, 1);
            return _bytes.TryGetValue(address, out value);
        }
        public bool TryReadUInt32(ulong address, out uint value)
        {
            Record('i', address, 4);
            var read = ReadInteger(address, 4, out var raw);
            value = (uint)raw;
            return read;
        }
        public bool TryReadUInt64(ulong address, out ulong value)
        {
            Record('l', address, 8);
            return ReadInteger(address, 8, out value);
        }
        public bool TryReadSingle(ulong address, out float value)
        {
            Record('f', address, 4);
            var read = ReadInteger(address, 4, out var raw);
            value = BitConverter.UInt32BitsToSingle((uint)raw);
            return read;
        }
        private bool ReadInteger(ulong address, int size, out ulong value)
        {
            value = 0;
            for (var index = 0; index < size; index++)
            {
                if (!_bytes.TryGetValue(address + (ulong)index, out var item)) return false;
                value |= (ulong)item << (index * 8);
            }
            return true;
        }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            Record('B', address, destination.Length);
            if (address >= Child && address < Child + 0x1000)
            {
                BeforeBlock?.Invoke();
                if (RejectGaugeBlock) return false;
            }
            for (var index = 0; index < destination.Length; index++)
                _bytes.TryGetValue(address + (ulong)index, out destination[index]);
            return true;
        }
    }
}
