using Wisp.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudMemoryResolverTests
{
    private const ulong Module = 0x0000000140000000;
    private const ulong SourceList = 0x0000000200000000;
    private const ulong Source = 0x0000000300000000;
    private const ulong Provider = 0x0000000400000000;

    [Fact]
    public void BuildContractRequiresExactVersionLengthAndHash()
    {
        Assert.True(NativeHudBuildContract.Matches(
            NativeHudBuildContract.SupportedVersion,
            NativeHudBuildContract.SupportedExecutableLength,
            NativeHudBuildContract.SupportedSha256.ToLowerInvariant()));
        Assert.False(NativeHudBuildContract.Matches(
            "6.430.771.1",
            NativeHudBuildContract.SupportedExecutableLength,
            NativeHudBuildContract.SupportedSha256));
        Assert.False(NativeHudBuildContract.Matches(
            NativeHudBuildContract.SupportedVersion,
            NativeHudBuildContract.SupportedExecutableLength + 1,
            NativeHudBuildContract.SupportedSha256));
        Assert.False(NativeHudBuildContract.Matches(
            NativeHudBuildContract.SupportedVersion,
            NativeHudBuildContract.SupportedExecutableLength,
            new string('0', 64)));
    }

    [Fact]
    public void UpdatedBuildIsAcceptedAndPreviousAddressMapIsRejected()
    {
        Assert.True(NativeHudBuildContract.Matches(
            "6.430.771.0", 183_853_016,
            "B62B5EC1933B2D11A6B80941AE0D2B38C4A5AAEFDD880E487453D178081D7B44"));
        Assert.False(NativeHudBuildContract.Matches(
            "6.420.696.0", 183_790_552,
            "8B2F8B6AACE53B89DDCFE45CF3F8C199E9A1817B715C4C2C1B512B6BB7A1EEF0"));
        Assert.Equal(0x0A8D9A60UL, NativeHudBuildContract.SourceVectorRva);
        Assert.Equal(0x063C9984UL, NativeHudBuildContract.ThresholdRva);
        Assert.Equal(0x0678A940UL, NativeHudBuildContract.LeadVtableRva);
        Assert.Equal(9, NativeHudBuildContract.RequiredVtableSlots.Count);
    }

    [Fact]
    public void ExactValidatedProviderProducesNativeStates()
    {
        var memory = ValidMemory();
        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 7);

        Assert.True(result.Available);
        Assert.Equal(NativeAssistProviderStatus.Ready, result.Status);
        Assert.Equal(314, result.CarOrdinal);
        Assert.Equal(7UL, result.Generation);
        Assert.True(result.Assists.IsABSOn);
        Assert.True(result.Assists.IsTCROn);
        Assert.True(result.Assists.IsSTMOn);
        Assert.True(result.Assists.IsLCOn);
        Assert.Equal(60, result.Assists.ABSAngle);
        Assert.Equal(20, result.Assists.TCRAngle);
        Assert.Equal(-20, result.Assists.STMAngle);
        Assert.Equal(-60, result.Assists.LCAngle);
        Assert.Equal(6_500, result.ExactRedline.Rpm, 2);
        Assert.Equal(8_000, result.TachometerMaximumRpm, 2);
    }

    [Fact]
    public void CarSwitchRereadsReplacedProviderEvenWhenSourceIsUnchanged()
    {
        var memory = ValidMemory();
        var resolver = new NativeHudMemoryResolver();
        Assert.True(resolver.Resolve(memory, Module, 314, 4_000, 8_000, 1).Assists.IsTCROn);

        const ulong replacement = 0x0000000410000000;
        ConfigureProvider(memory, replacement, absOn: false, tcrOn: false, stmOn: false, lcOn: false);
        memory.SetUInt64(Source + 0x7740, replacement);
        memory.SetUInt32(Source + 0x740C, 3766);
        var result = resolver.Resolve(memory, Module, 3766, 4_000, 8_000, 2);

        Assert.True(result.Available);
        Assert.False(result.Assists.IsTCROn);
        Assert.False(result.Assists.IsLCOn);
        Assert.Equal(3766, result.CarOrdinal);
        Assert.Equal(2UL, result.Generation);
    }

    [Fact]
    public void TuneChangeRefreshesExactRedlineWithoutCarOrProviderChange()
    {
        var memory = ValidMemory();
        var resolver = new NativeHudMemoryResolver();
        var stock = resolver.Resolve(memory, Module, 314, 4_000, 8_000, 1);
        Assert.Equal(6_500, stock.ExactRedline.Rpm, 2);

        memory.SetSingle(Provider + 0x0248, 7_500 * 2 * MathF.PI / 60);
        var upgraded = resolver.Resolve(memory, Module, 314, 4_000, 8_000, 2);

        Assert.True(upgraded.Available);
        Assert.Equal(7_500, upgraded.ExactRedline.Rpm, 2);
        Assert.Equal(8_000, upgraded.TachometerMaximumRpm, 2);
    }

    [Fact]
    public void InvalidRedlineProviderValueFailsClosed()
    {
        var memory = ValidMemory();
        memory.SetSingle(Provider + 0x0248, float.NaN);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.False(result.Available);
        Assert.Equal(NativeAssistProviderStatus.InvalidProvider, result.Status);
        Assert.Equal(ExactRedlineStatus.InvalidProvider, result.ExactRedline.Status);
        Assert.Equal(0, result.TachometerMaximumRpm);
        Assert.True(result.Assists.Available);
        Assert.True(result.HasAvailableCapabilities);
    }

    [Fact]
    public void RedlineAboveNativeTachometerMaximumFailsClosed()
    {
        var memory = ValidMemory();
        memory.SetSingle(Provider + 0x0248, 8_500 * 2 * MathF.PI / 60);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.False(result.Available);
        Assert.Equal(NativeAssistProviderStatus.InvalidProvider, result.Status);
        Assert.False(result.ExactRedline.IsExact);
    }

    [Fact]
    public void NativeMaximumMustMatchDataOutEngineMaximum()
    {
        var memory = ValidMemory();
        memory.SetSingle(Provider + 0x024C, 9_000 * 2 * MathF.PI / 60);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.False(result.Available);
        Assert.Equal(NativeAssistProviderStatus.TelemetryMismatch, result.Status);
        Assert.Equal(ExactRedlineStatus.TelemetryMismatch, result.ExactRedline.Status);
        Assert.False(result.HasAvailableCapabilities);
    }

    [Fact]
    public void AssistReadFailureDoesNotDiscardExactTachometerState()
    {
        var memory = ValidMemory();
        memory.SetSingle(Provider + 0xC224, float.PositiveInfinity);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.True(result.Available);
        Assert.True(result.ExactRedline.IsExact);
        Assert.Equal(6_500, result.ExactRedline.Rpm, 2);
        Assert.False(result.Assists.Available);
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, result.Assists.Status);
    }

    [Fact]
    public void ContractMismatchFailsClosed()
    {
        var memory = ValidMemory();
        memory.SetUInt64(
            Module + NativeHudBuildContract.LeadVtableRva + 0x02A8,
            Module + 0xDEADBEEF);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.False(result.Available);
        Assert.Equal(NativeAssistProviderStatus.PlayerNotUnique, result.Status);
    }

    [Fact]
    public void NonUniqueLocalPlayerFailsClosed()
    {
        var memory = ValidMemory();
        const ulong source2 = 0x0000000310000000;
        const ulong provider2 = 0x0000000420000000;
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 8, SourceList + 16);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 16, SourceList + 16);
        memory.SetUInt64(SourceList + 8, source2);
        ConfigureSource(memory, source2, provider2, 999);
        ConfigureProvider(memory, provider2, absOn: true, tcrOn: true, stmOn: true, lcOn: true);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1, forceSourceAudit: true);

        Assert.False(result.Available);
        Assert.Equal(NativeAssistProviderStatus.PlayerNotUnique, result.Status);
    }

    [Fact]
    public void ZeroLocalPlayerAndTelemetryMismatchFailClosed()
    {
        var noPlayer = ValidMemory();
        noPlayer.SetByte(Provider + 0x1464, 0);
        Assert.False(new NativeHudMemoryResolver().Resolve(
            noPlayer, Module, 314, 4_000, 8_000, 1).Available);

        var wrongCar = ValidMemory();
        Assert.False(new NativeHudMemoryResolver().Resolve(
            wrongCar, Module, 3766, 4_000, 8_000, 1).Available);

        var wrongRpm = ValidMemory();
        Assert.False(new NativeHudMemoryResolver().Resolve(
            wrongRpm, Module, 314, 7_000, 8_000, 1).Available);
    }

    [Fact]
    public void InvalidVectorAndThresholdFailClosed()
    {
        var badVector = ValidMemory();
        badVector.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 8, SourceList + 3);
        Assert.Equal(
            NativeAssistProviderStatus.InvalidSourceVector,
            new NativeHudMemoryResolver().Resolve(
                badVector, Module, 314, 4_000, 8_000, 1).Status);

        var badThreshold = ValidMemory();
        badThreshold.SetSingle(Module + NativeHudBuildContract.ThresholdRva, 0.10001f);
        var partial = new NativeHudMemoryResolver().Resolve(
            badThreshold, Module, 314, 4_000, 8_000, 1);
        Assert.True(partial.Available);
        Assert.True(partial.ExactRedline.IsExact);
        Assert.False(partial.Assists.Available);
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, partial.Assists.Status);
    }

    [Theory]
    [InlineData("ABS")]
    [InlineData("TCR")]
    [InlineData("STM")]
    [InlineData("LC")]
    public void EachPhysicalStateSourceMapsToOnlyItsObservedAssist(string activeAssist)
    {
        var memory = ValidMemory();
        ConfigureProvider(
            memory,
            Provider,
            absOn: activeAssist == "ABS",
            tcrOn: activeAssist == "TCR",
            stmOn: activeAssist == "STM",
            lcOn: activeAssist == "LC");

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.True(result.Available);
        Assert.Equal(activeAssist == "ABS", result.Assists.IsABSOn);
        Assert.Equal(activeAssist == "TCR", result.Assists.IsTCROn);
        Assert.Equal(activeAssist == "STM", result.Assists.IsSTMOn);
        Assert.Equal(activeAssist == "LC", result.Assists.IsLCOn);
    }

    [Theory]
    [InlineData(0x17B6, "ABS")]
    [InlineData(0x17B5, "TCR")]
    [InlineData(0x17B4, "STM")]
    [InlineData(0x17B7, "LC")]
    public void EachPhysicalAvailabilityByteMapsToOnlyItsObservedAssist(int offset, string availableAssist)
    {
        var memory = ValidMemory();
        memory.SetByte(Provider + 0x17B4, 0);
        memory.SetByte(Provider + 0x17B5, 0);
        memory.SetByte(Provider + 0x17B6, 0);
        memory.SetByte(Provider + 0x17B7, 0);
        memory.SetByte(Provider + (ulong)offset, 1);

        var result = new NativeHudMemoryResolver().Resolve(
            memory, Module, 314, 4_000, 8_000, 1);

        Assert.Equal(availableAssist == "ABS", result.Assists.IsABSAvailable);
        Assert.Equal(availableAssist == "TCR", result.Assists.IsTCRAvailable);
        Assert.Equal(availableAssist == "STM", result.Assists.IsSTMAvailable);
        Assert.Equal(availableAssist == "LC", result.Assists.IsLCAvailable);
    }

    [Fact]
    public void MissingRedlineDoesNotDiscardIndependentlyVerifiedAssists()
    {
        var memory = ValidMemory();
        memory.RemoveSingle(Provider + 0x0248);
        var result = new NativeHudMemoryResolver().Resolve(memory, Module, 314, 4_000, 8_000, 1);
        Assert.False(result.Available);
        Assert.False(result.ExactRedline.IsExact);
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, result.Status);
        Assert.True(result.Assists.Available);
        Assert.True(result.Assists.IsABSOn);
    }

    [Fact]
    public void RelocatedBuildUsesEveryPackAddressAndFieldInsteadOfLegacyConstants()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var json = JsonNode.Parse(stream)!.AsObject();
        json["id"] = "fh6-future-layout-test";
        json["gameVersion"] = "6.999.1.0";
        json["executableSha256"] = new string('F', 64);
        foreach (var name in new[] { "sourceVectorRva", "thresholdRva", "leadVtableRva" })
        {
            json[name] = json[name]!.GetValue<ulong>() + FakeMemory.ModuleDelta;
        }

        foreach (var field in json["fields"]!.AsObject().ToArray())
        {
            json["fields"]![field.Key] = field.Value!.GetValue<ulong>() + FakeMemory.FieldDelta;
        }

        foreach (var slot in json["requiredVtableSlots"]!.AsArray())
        {
            slot!["targetRva"] = slot["targetRva"]!.GetValue<ulong>() + FakeMemory.ModuleDelta;
        }

        var pack = NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        var original = ValidMemory();
        original.SetUInt32(Source + 0x740C, 2_000_000_123);
        var relocated = original.Relocate();
        var actual = new NativeHudMemoryResolver(pack).Resolve(relocated, Module, 2_000_000_123, 4_000, 8_000, 1);

        Assert.True(actual.Available);
        Assert.True(actual.Assists.Available);
        Assert.True(actual.Assists.IsABSOn && actual.Assists.IsTCROn && actual.Assists.IsSTMOn && actual.Assists.IsLCOn);
        Assert.Equal(6_500, actual.ExactRedline.Rpm, 2);
        Assert.Equal(8_000, actual.TachometerMaximumRpm, 2);
        Assert.False(new NativeHudMemoryResolver().Resolve(relocated, Module, 2_000_000_123, 4_000, 8_000, 2).HasAvailableCapabilities);
    }

    [Theory]
    [InlineData(0UL, 314)]
    [InlineData(ulong.MaxValue, 314)]
    [InlineData(0x00007FFFFFFF0000UL, 314)]
    [InlineData(Module, 0)]
    [InlineData(Module, -1)]
    public void InvalidModuleSpansAndCarIdsAreRejectedBeforeAnyMemoryRead(ulong moduleBase, int carOrdinal)
    {
        var result = new NativeHudMemoryResolver().Resolve(new MustNotReadMemory(), moduleBase, carOrdinal, 4_000, 8_000, 1);
        Assert.False(result.HasAvailableCapabilities);
        Assert.Equal(NativeAssistProviderStatus.InvalidProvider, result.Status);
    }

    private sealed class MustNotReadMemory : IReadOnlyProcessMemory
    {
        public bool TryReadByte(ulong address, out byte value) => throw new InvalidOperationException("Unexpected read");
        public bool TryReadUInt32(ulong address, out uint value) => throw new InvalidOperationException("Unexpected read");
        public bool TryReadUInt64(ulong address, out ulong value) => throw new InvalidOperationException("Unexpected read");
        public bool TryReadSingle(ulong address, out float value) => throw new InvalidOperationException("Unexpected read");
    }

    private static FakeMemory ValidMemory()
    {
        var memory = new FakeMemory();
        memory.SetSingle(Module + NativeHudBuildContract.ThresholdRva, 0.1f);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva, SourceList);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 8, SourceList + 8);
        memory.SetUInt64(Module + NativeHudBuildContract.SourceVectorRva + 16, SourceList + 8);
        memory.SetUInt64(SourceList, Source);
        ConfigureSource(memory, Source, Provider, 314);
        ConfigureProvider(memory, Provider, absOn: true, tcrOn: true, stmOn: true, lcOn: true);
        return memory;
    }

    private static void ConfigureSource(FakeMemory memory, ulong source, ulong provider, uint carOrdinal)
    {
        memory.SetUInt64(source + 0x7740, provider);
        memory.SetUInt32(source + 0x740C, carOrdinal);
    }

    private static void ConfigureProvider(
        FakeMemory memory,
        ulong provider,
        bool absOn,
        bool tcrOn,
        bool stmOn,
        bool lcOn)
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
        memory.SetUInt32(provider + 0x1430, stmOn ? 1u : 0u);
        memory.SetUInt32(provider + 0x1434, absOn ? 1u : 0u);
        memory.SetSingle(provider + 0x14EC, lcOn ? 0 : 0.2f);
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

    private sealed class FakeMemory : IReadOnlyProcessMemory
    {
        internal const ulong ModuleDelta = 0x1000;
        internal const ulong FieldDelta = 0x200;
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, uint> _uint32 = [];
        private readonly Dictionary<ulong, ulong> _uint64 = [];
        private readonly Dictionary<ulong, float> _singles = [];

        public bool TryReadByte(ulong address, out byte value) => _bytes.TryGetValue(address, out value);
        public bool TryReadUInt32(ulong address, out uint value) => _uint32.TryGetValue(address, out value);
        public bool TryReadUInt64(ulong address, out ulong value) => _uint64.TryGetValue(address, out value);
        public bool TryReadSingle(ulong address, out float value) => _singles.TryGetValue(address, out value);

        public void SetByte(ulong address, byte value) => _bytes[address] = value;
        public void SetUInt32(ulong address, uint value) => _uint32[address] = value;
        public void SetUInt64(ulong address, ulong value) => _uint64[address] = value;
        public void SetSingle(ulong address, float value) => _singles[address] = value;
        public void RemoveSingle(ulong address) => _singles.Remove(address);

        internal FakeMemory Relocate()
        {
            var result = new FakeMemory();
            foreach (var entry in _bytes) result.SetByte(RelocatedAddress(entry.Key), entry.Value);
            foreach (var entry in _uint32) result.SetUInt32(RelocatedAddress(entry.Key), entry.Value);
            foreach (var entry in _singles) result.SetSingle(RelocatedAddress(entry.Key), entry.Value);
            foreach (var entry in _uint64)
            {
                var value = entry.Value >= Module && entry.Value < Module + NativeHudBuildContract.BuiltIn.ImageSize
                    ? entry.Value + ModuleDelta : entry.Value;
                result.SetUInt64(RelocatedAddress(entry.Key), value);
            }

            return result;
        }

        private static ulong RelocatedAddress(ulong address) =>
            address >= Module && address < Module + NativeHudBuildContract.BuiltIn.ImageSize
                ? address + ModuleDelta
                : address >= Source && address != Provider ? address + FieldDelta : address;
    }
}
