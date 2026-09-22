using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeShiftCaptureEvidenceTests
{
    private const ulong Provider = 0x0000020100000000;
    private const ulong Wheel = 0x0000020200000000;

    [Fact]
    public void NullCaptureHubDoesNotReadOrAllocate()
    {
        var memory = Fixture();
        var snapshot = Snapshot();
        Assert.Null(ShiftCaptureHub.Current);
        NativeShiftCaptureEvidence.RecordIfArmed(memory, Provider, snapshot);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) NativeShiftCaptureEvidence.RecordIfArmed(memory, Provider, snapshot);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public void StableBoundedEvidencePreservesBytesAndLinksConfiguredFingerprintWithoutAddresses()
    {
        var memory = Fixture();
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.Equal("stable", evidence.Status);
        Assert.True(evidence.DoubleReadConsistent);
        Assert.Equal(2177, evidence.CarOrdinal);
        Assert.Equal(new string('A', 64), evidence.ConfigurationFingerprint);
        Assert.Equal(1234, evidence.ConfigurationObservedQpc);
        Assert.NotNull(evidence.AuxiliaryFingerprint);
        Assert.Equal(64, evidence.AuxiliaryFingerprint.Length);
        Assert.Equal(10, evidence.Fields.Length);
        Assert.All(evidence.Fields, field => Assert.Equal(field.RawLittleEndianHex, field.RepeatedRawLittleEndianHex));
        var mass = Assert.Single(evidence.Fields, field => field.RelativePath == "provider-0x568");
        Assert.Equal(1400d, mass.FirstFiniteValue);
        Assert.Equal(Convert.ToHexString(BitConverter.GetBytes(1400f)), mass.RawLittleEndianHex);
        Assert.Contains("unverified", mass.Semantics);
        Assert.Equal(24, memory.Reads.Count);
        Assert.Equal(108, memory.Reads.Sum(read => read.Size));
        Assert.All(memory.Reads, read => Assert.InRange(read.Size, 4, 8));
        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Provider.ToString(), serialized);
        Assert.DoesNotContain(Wheel.ToString(), serialized);
        Assert.DoesNotContain(Convert.ToHexString(BitConverter.GetBytes(Wheel)), serialized);
        Assert.Contains("does not establish live acceleration", evidence.EstimatorHypothesis!.Limitation);
        Assert.False(evidence.EstimatorHypothesis.AppliedToCueTargets);
        Assert.Equal(evidence.ConfigurationFingerprint, evidence.EstimatorHypothesis.ConfigurationFingerprint);
        Assert.Contains("clutch engagement", evidence.UnobservedSignals);
    }

    [Fact]
    public void ChangedScalarIsExplicitlyUnstableAndCannotReceiveFingerprint()
    {
        var memory = Fixture();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0x23C && count == 2) memory.Single(address, .8f);
        };
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.Equal("unstable-data", evidence.Status);
        Assert.False(evidence.DoubleReadConsistent);
        Assert.Null(evidence.AuxiliaryFingerprint);
        Assert.Null(evidence.EstimatorHypothesis);
        var changed = Assert.Single(evidence.Fields, field => field.RawLittleEndianHex != field.RepeatedRawLittleEndianHex);
        Assert.Equal("provider+0x23C", changed.RelativePath);
    }

    [Fact]
    public void ReadFailureReportsRelativeFieldAndPassOnly()
    {
        var memory = Fixture();
        memory.FailAddress = Provider + 0x170;
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.Equal("read-failed", evidence.Status);
        Assert.Equal("provider+0x170", evidence.FailedRelativeField);
        Assert.Equal(1, evidence.FailedPass);
        Assert.Null(evidence.AuxiliaryFingerprint);
        Assert.Empty(evidence.Fields);
    }

    [Fact]
    public void SelectedWheelReferenceChangeIsNotReportedAsStableRadius()
    {
        var memory = Fixture();
        memory.BeforeRead = (address, count) =>
        {
            if (address == Provider + 0xBA0 && count == 2) memory.UInt64(address, Wheel + 0x1000);
        };
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.Equal("unstable-wheel-reference", evidence.Status);
        Assert.Null(evidence.AuxiliaryFingerprint);
    }

    [Fact]
    public void NonfiniteScalarKeepsRawBitsButDoesNotSerializeNonfiniteNumber()
    {
        var memory = Fixture();
        memory.Single(Provider + 0x12C, float.NaN);
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.Equal("nonfinite-scalar", evidence.Status);
        var field = Assert.Single(evidence.Fields, item => item.RelativePath == "provider+0x12C");
        Assert.Null(field.FirstFiniteValue);
        Assert.Equal(Convert.ToHexString(BitConverter.GetBytes(float.NaN)), field.RawLittleEndianHex);
        Assert.DoesNotContain("NaN", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void FailedConfiguredSnapshotCausesNoAuxiliaryReads()
    {
        var memory = Fixture();
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider,
            new NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus.ReadFailure, 2177));
        Assert.Equal("unavailable-snapshot", evidence.Status);
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public void DiagnosticEstimatorUsesSelectedRadiusAndDoesNotImplyPhysicalAccelerationOrCueChanges()
    {
        var memory = Fixture();
        memory.Single(Provider - 0x568, 100);
        memory.Single(Provider + 0x12C, 0);
        memory.Single(Provider + 0x170, 0);
        memory.Single(Provider + 0x23C, 2);
        memory.Single(Provider + 0x2BA0, 0);
        memory.Single(Provider + 0x4120, 0);
        memory.Single(Wheel + 0x5AC, .5f);
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        var derived = Assert.IsType<NativeShiftEstimatorEvidence>(evidence.EstimatorHypothesis);
        Assert.Equal("derived-estimator-hypothesis", derived.Status);
        Assert.Equal("0x3197DD0", derived.SourceFunctionRva);
        Assert.Equal("fixed-ratio-inertia-factor-v1", derived.FormulaVersion);
        Assert.Equal(100d / 388, derived.Gears[0].Factor, 12);
        Assert.Equal(100d / 228, derived.Gears[1].Factor, 12);
        Assert.False(derived.AppliedToCueTargets);
        Assert.Equal(108, memory.Reads.Sum(read => read.Size));
    }

    [Fact]
    public void Recorded2974ConfigurationReproducesPreviouslyAuditedEstimatorFactors()
    {
        // Existing September 19 bounded capture and independently reduced estimator arithmetic.
        var memory = Fixture();
        memory.Single(Provider - 0x568, 13.329413414001465f);
        memory.Single(Provider + 0x12C, 0.000075999996624887f);
        memory.Single(Provider + 0x170, 0.000357999990228564f);
        memory.Single(Provider + 0x23C, 0.003496000077575445f);
        memory.Single(Provider + 0x2BA0, 0.018919777125120163f);
        memory.Single(Provider + 0x4120, 0.021246645599603653f);
        memory.Single(Provider + 0x2ADC, 0.3502500057220459f);
        memory.Single(Provider + 0x405C, 0.3642500042915344f);
        memory.Single(Wheel + 0x5AC, 0.3642500042915344f);
        double[] ratios = [2.799999713897705, 2.1000003814697266, 1.550000548362732,
            1.2500008344650269, 1.0500010251998901, 0.9000011682510376, 0.7800003290176392];
        var snapshot = new NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus.Ready,
            2974, observedTimestamp: 1234, fingerprint: new string('B', 64), forwardRatios: ratios,
            finalDrive: 3.800006151199341);
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, snapshot);
        var derived = Assert.IsType<NativeShiftEstimatorEvidence>(evidence.EstimatorHypothesis);
        double[] expected = [.7846185308020452, .8499097780295558, .8934223795333851,
            .912980507461133, .9240581782075353, .9312430357492677, .9362604915011232];
        Assert.Equal(expected.Length, derived.Gears.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], derived.Gears[i].Factor, 12);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void InvalidInertiaCannotProduceDiagnosticFactors(float inertia)
    {
        var memory = Fixture();
        memory.Single(Provider + 0x23C, inertia);
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, Snapshot());
        Assert.True(evidence.EstimatorHypothesis is null || evidence.EstimatorHypothesis.Gears.Length == 0);
        Assert.DoesNotContain("NaN", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void InvalidOrMissingGearingLeavesRawAuxiliaryEvidenceAvailableWithoutFactors()
    {
        var memory = Fixture();
        var snapshot = new NativeShiftPerformanceSnapshot(NativeShiftPerformanceStatus.Ready,
            2177, observedTimestamp: 1234, fingerprint: new string('A', 64), forwardRatios: [2, 3], finalDrive: 0);
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(memory, Provider, snapshot);
        Assert.Equal("stable", evidence.Status);
        Assert.Equal("unavailable-inputs", evidence.EstimatorHypothesis!.Status);
        Assert.Empty(evidence.EstimatorHypothesis.Gears);
        Assert.Equal(10, evidence.Fields.Length);
    }

    [Fact]
    public void AssociationBracketReportsElapsedGapAndExplicitNonAtomicLimitation()
    {
        var evidence = NativeShiftCaptureEvidence.ReadForCapture(Fixture(), Provider, Snapshot());
        var controlled = evidence with { ConfigurationObservedQpc = 100, ReadStartedQpc = 100 + evidence.QpcFrequency };
        Assert.Equal(1000d, controlled.ConfigurationAgeAtReadStartMilliseconds);
        Assert.Contains("atomic", controlled.Association);
        Assert.True(evidence.DoubleReadConsistent);
        Assert.Null((controlled with { ReadStartedQpc = 99 }).ConfigurationAgeAtReadStartMilliseconds);
    }

    private static NativeShiftPerformanceSnapshot Snapshot() => new(NativeShiftPerformanceStatus.Ready,
        2177, observedTimestamp: 1234, fingerprint: new string('A', 64), forwardRatios: [3, 2], finalDrive: 2);

    private static Memory Fixture()
    {
        var memory = new Memory();
        memory.Single(Provider - 0x568, 1400);
        memory.Single(Provider + 0x12C, .2f);
        memory.Single(Provider + 0x170, .3f);
        memory.Single(Provider + 0x23C, .4f);
        memory.Single(Provider + 0x2BA0, .7f);
        memory.Single(Provider + 0x4120, .8f);
        memory.Single(Provider + 0x2ADC, .31f);
        memory.Single(Provider + 0x405C, .33f);
        memory.UInt64(Provider + 0xBA0, Wheel);
        memory.UInt32(Wheel + 0x5A0, 2);
        memory.Single(Wheel + 0x5AC, .33f);
        return memory;
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, int> _counts = [];
        internal readonly List<(ulong Address, int Size)> Reads = [];
        internal Action<ulong, int>? BeforeRead;
        internal ulong? FailAddress;
        internal void Single(ulong address, float value) => UInt32(address, BitConverter.SingleToUInt32Bits(value));
        internal void UInt32(ulong address, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Write(address, bytes); }
        internal void UInt64(ulong address, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Write(address, bytes); }
        private void Write(ulong address, ReadOnlySpan<byte> bytes) { for (var i = 0; i < bytes.Length; i++) _bytes[address + (ulong)i] = bytes[i]; }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            var count = _counts.GetValueOrDefault(address) + 1;
            _counts[address] = count;
            BeforeRead?.Invoke(address, count);
            if (address == FailAddress) return false;
            for (var i = 0; i < destination.Length; i++) if (!_bytes.TryGetValue(address + (ulong)i, out destination[i])) return false;
            return true;
        }
        public bool TryReadByte(ulong address, out byte value) { Span<byte> bytes = stackalloc byte[1]; var ok = TryReadBytes(address, bytes); value = bytes[0]; return ok; }
        public bool TryReadUInt32(ulong address, out uint value) { Span<byte> bytes = stackalloc byte[4]; var ok = TryReadBytes(address, bytes); value = BinaryPrimitives.ReadUInt32LittleEndian(bytes); return ok; }
        public bool TryReadUInt64(ulong address, out ulong value) { Span<byte> bytes = stackalloc byte[8]; var ok = TryReadBytes(address, bytes); value = BinaryPrimitives.ReadUInt64LittleEndian(bytes); return ok; }
        public bool TryReadSingle(ulong address, out float value) { var ok = TryReadUInt32(address, out var bits); value = BitConverter.UInt32BitsToSingle(bits); return ok; }
    }
}
