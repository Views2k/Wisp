using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCueMenuConfigurationTests
{
    private const ulong Module = 0x140000000;
    private const ulong SourceList = 0x200000000;
    private const ulong Source = 0x300000000;
    private const ulong Provider = 0x400000000;
    private const ulong SelectedWheel = 0x500000000;
    private const int Car = 3289;
    private static readonly NativeHudCompatibilityPack Pack = NativeHudBuildContract.BuiltIn;

    [Theory]
    [InlineData(NativeGameplayVisibility.Hidden)]
    [InlineData(NativeGameplayVisibility.Unknown)]
    public void VisibilityLossRequiresFreshSameCarTuneBeforeResuming(NativeGameplayVisibility loss)
    {
        long now = 1000;
        var memory = FixtureMemory();
        var resolver = new NativeHudMemoryResolver(Pack, new(() => now, 1000)) { ShiftCueEnabled = true };
        var initial = Visible(resolver, Resolve(resolver, memory));
        Assert.NotNull(initial.ShiftPerformance?.Profile);
        var original = initial.ShiftPerformance!;
        Assert.True(original.RequiresLiveState);
        Assert.NotNull(original.LiveState);
        var originalRatio = (float)original.Profile!.ForwardRatios[1];
        var changedRatio = originalRatio * .95f;
        var staticReads = memory.StaticBlockReads;
        var liveReads = memory.LiveBlockReads;

        // Reproduce the real stale window: unchanged car/provider identity and
        // only one millisecond elapsed, so the normal cache still returns A.
        memory.Single(Provider + 0x28 + 2 * 20, changedRatio);
        now++;
        var beforeVisibility = Resolve(resolver, memory);
        Assert.Same(original.Profile, beforeVisibility.ShiftPerformance?.Profile);
        Assert.Equal(original.Fingerprint, beforeVisibility.ShiftPerformance?.Fingerprint);
        Assert.NotNull(beforeVisibility.ShiftPerformance?.LiveState);
        Assert.Equal(staticReads, memory.StaticBlockReads);
        Assert.Equal(liveReads + 4, memory.LiveBlockReads);
        liveReads = memory.LiveBlockReads;

        var hidden = resolver.ApplyShiftGameplayVisibility(beforeVisibility, loss);
        Assert.Null(hidden.ShiftPerformance);
        Assert.Equal(initial.ExactRedline, hidden.ExactRedline);
        Assert.Null(resolver.RefreshNativeGauge(memory, Module, hidden, 2, false).ShiftPerformance);
        Assert.Null(Resolve(resolver, memory).ShiftPerformance);
        Assert.Equal(staticReads, memory.StaticBlockReads);
        Assert.Equal(liveReads, memory.LiveBlockReads);

        // The service learns visibility only after resolving the vehicle. This
        // first visible result must remain empty until the next normal audit.
        var resumed = Visible(resolver, Resolve(resolver, memory));
        Assert.Null(resumed.ShiftPerformance);
        Assert.Null(resolver.RefreshNativeGauge(memory, Module, resumed, 3, false).ShiftPerformance);
        Assert.Equal(staticReads, memory.StaticBlockReads);
        Assert.Equal(liveReads, memory.LiveBlockReads);
        var refreshed = Visible(resolver, Resolve(resolver, memory));
        Assert.NotNull(refreshed.ShiftPerformance?.Profile);
        Assert.True(refreshed.ShiftPerformance!.RequiresLiveState);
        Assert.NotNull(refreshed.ShiftPerformance.LiveState);
        Assert.NotEqual(original.Fingerprint, refreshed.ShiftPerformance!.Fingerprint);
        Assert.Equal((double)changedRatio, refreshed.ShiftPerformance.Profile!.ForwardRatios[1]);
        Assert.True(memory.StaticBlockReads > staticReads);

        var refreshedReads = memory.StaticBlockReads;
        Assert.Same(refreshed.ShiftPerformance.Profile, Visible(resolver, Resolve(resolver, memory)).ShiftPerformance?.Profile);
        Assert.Equal(refreshedReads, memory.StaticBlockReads);
    }

    [Fact]
    public void FirstVisibleStartUsesFreshProfileWithoutExtraAudit()
    {
        var memory = FixtureMemory();
        var resolver = new NativeHudMemoryResolver(Pack, new(() => 1000, 1000)) { ShiftCueEnabled = true };
        var first = Visible(resolver, Resolve(resolver, memory));
        Assert.NotNull(first.ShiftPerformance?.Profile);
        Assert.True(first.ShiftPerformance!.RequiresLiveState);
        Assert.NotNull(first.ShiftPerformance.LiveState);
        var reads = memory.StaticBlockReads;
        var liveReads = memory.LiveBlockReads;
        var repeated = Visible(resolver, Resolve(resolver, memory));
        Assert.Same(first.ShiftPerformance.Profile, repeated.ShiftPerformance?.Profile);
        Assert.Equal(first.ShiftPerformance.Fingerprint, repeated.ShiftPerformance?.Fingerprint);
        Assert.NotNull(repeated.ShiftPerformance?.LiveState);
        Assert.Equal(reads, memory.StaticBlockReads);
        Assert.Equal(liveReads + 4, memory.LiveBlockReads);
    }

    [Fact]
    public void DisabledCueNeverReadsStaticShiftMetadataAcrossVisibilityChanges()
    {
        var memory = FixtureMemory();
        var resolver = new NativeHudMemoryResolver(Pack, new(() => 1000, 1000));
        Assert.Null(Visible(resolver, Resolve(resolver, memory)).ShiftPerformance);
        resolver.ApplyShiftGameplayVisibility(Resolve(resolver, memory), NativeGameplayVisibility.Hidden);
        Assert.Null(Visible(resolver, Resolve(resolver, memory)).ShiftPerformance);
        Assert.Null(Visible(resolver, Resolve(resolver, memory)).ShiftPerformance);
        Assert.Equal(0, memory.BlockReads);
    }

    [Fact]
    public void FailedPostMenuRefreshCannotRestorePreviousProfile()
    {
        var memory = FixtureMemory();
        var resolver = new NativeHudMemoryResolver(Pack, new(() => 1000, 1000)) { ShiftCueEnabled = true };
        var initial = Visible(resolver, Resolve(resolver, memory));
        Assert.NotNull(initial.ShiftPerformance?.Profile);
        Assert.True(initial.ShiftPerformance!.RequiresLiveState);
        Assert.NotNull(initial.ShiftPerformance.LiveState);
        resolver.ApplyShiftGameplayVisibility(initial, NativeGameplayVisibility.Unknown);
        Assert.Null(Visible(resolver, Resolve(resolver, memory)).ShiftPerformance);
        memory.FailAddress = Provider + 0x25C;
        var failed = Visible(resolver, Resolve(resolver, memory));
        Assert.Null(failed.ShiftPerformance?.Profile);
        Assert.Equal("ReadFailure", failed.ShiftPerformance?.Status);
        Assert.Equal("", failed.ShiftPerformance?.Fingerprint);
    }

    private static NativeHudSnapshot Resolve(NativeHudMemoryResolver resolver, Memory memory) =>
        resolver.Resolve(memory, Module, Car, 4000, 10000, 1);

    private static NativeHudSnapshot Visible(NativeHudMemoryResolver resolver, NativeHudSnapshot snapshot) =>
        resolver.ApplyShiftGameplayVisibility(snapshot, NativeGameplayVisibility.Visible);

    private static Memory FixtureMemory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Wisp.App", "Wisp.App.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "tests", "Wisp.App.Tests", "Fixtures", "ShiftCue", "na-3289.json")))!.AsObject();
        var memory = new Memory();
        memory.Zero(Provider, 0xB6C);
        memory.UInt64(Module + Pack.SourceVectorRva, SourceList);
        memory.UInt64(Module + Pack.SourceVectorRva + 8, SourceList + 8);
        memory.UInt64(Module + Pack.SourceVectorRva + 16, SourceList + 8);
        memory.UInt64(SourceList, Source);
        memory.UInt64(Source + Pack.Fields.SourceProvider, Provider);
        memory.UInt32(Source + Pack.Fields.SourceCarOrdinal, Car);
        memory.UInt64(Provider, Module + Pack.LeadVtableRva);
        memory.Byte(Provider + Pack.Fields.LocalPlayerFlag, 1);
        memory.Byte(Provider + Pack.Fields.LocalPlayerProviderFlag, 1);
        memory.Single(Provider + Pack.Fields.ProviderRpm, (float)(4000 * Math.PI / 30));
        foreach (var slot in Pack.RequiredVtableSlots)
            memory.UInt64(Module + Pack.LeadVtableRva + slot.Key, Module + slot.Value);
        memory.UInt32(Provider + 0x234, 5);
        foreach (var (key, offset) in new[] { ("redline", 0x248), ("maximum", 0x24C),
            ("inverse_step", 0x250), ("step", 0x254), ("configured_peak_torque", 0x654),
            ("configured_peak_power", 0x664), ("ceiling_candidate", 0x648), ("final_drive", 0xB68) })
            memory.Single(Provider + (ulong)offset, fixture[key]!.GetValue<float>());
        var raw = fixture["raw"]!.AsArray();
        memory.UInt32(Provider + 0x258, (uint)raw.Count);
        for (var i = 0; i < raw.Count; i++) memory.Single(Provider + 0x25C + (ulong)i * 4, raw[i]!.GetValue<float>());
        var modifiers = fixture["modifier_bits"]!.AsArray();
        for (var i = 0; i < modifiers.Count; i++) memory.UInt32(Provider + 0xA90 + (ulong)i * 4, modifiers[i]!.GetValue<uint>());
        var gears = fixture["gears"]!.AsArray();
        memory.UInt32(Provider + 0x24, (uint)gears.Count);
        for (var i = 0; i < gears.Count; i++)
            for (var j = 0; j < 5; j++)
                memory.Single(Provider + 0x28 + (ulong)i * 20 + (ulong)j * 4, gears[i]![j]!.GetValue<float>());
        // Explicit synthetic values provide the additional configured inertia
        // contract; the captured curve above remains unchanged.
        memory.Single(Provider - 0x568, 20);
        memory.Single(Provider + 0x2BA0, .125f);
        memory.Single(Provider + 0x4120, .25f);
        memory.Single(Provider + 0x2ADC, .5f);
        memory.Single(Provider + 0x405C, .5f);
        memory.Single(Provider + 0x12C, .125f);
        memory.Single(Provider + 0x170, .0625f);
        memory.Single(Provider + 0x23C, .03125f);
        memory.UInt64(Provider + 0xBA0, SelectedWheel);
        memory.Single(SelectedWheel + 0x5AC, .5f);
        memory.Byte(Provider + 8, 2);
        memory.Byte(Provider + 9, 2);
        memory.Byte(Provider + 10, 1);
        memory.Single(Provider + 0x1B0, (float)(4000 * Math.PI / 30));
        memory.UInt32(Provider + 0x224, 0);
        memory.UInt32(Provider + 0x228, 0);
        memory.Single(Provider + 0x678, 0);
        memory.Single(Provider + 0x2520, 1);
        memory.Byte(Module + 0xA8EA2AC, 0);
        return memory;
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        internal int BlockReads;
        internal int StaticBlockReads;
        internal int LiveBlockReads;
        internal ulong? FailAddress;
        internal void Zero(ulong address, int count) { for (var i = 0; i < count; i++) Byte(address + (ulong)i, 0); }
        internal void Byte(ulong address, byte value) => _bytes[address] = value;
        internal void UInt32(ulong address, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Write(address, bytes); }
        internal void UInt64(ulong address, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Write(address, bytes); }
        internal void Single(ulong address, float value) => UInt32(address, BitConverter.SingleToUInt32Bits(value));
        private void Write(ulong address, ReadOnlySpan<byte> bytes) { for (var i = 0; i < bytes.Length; i++) Byte(address + (ulong)i, bytes[i]); }
        private bool Read(ulong address, Span<byte> bytes)
        {
            if (FailAddress == address) return false;
            for (var i = 0; i < bytes.Length; i++) if (!_bytes.TryGetValue(address + (ulong)i, out bytes[i])) return false;
            return true;
        }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            BlockReads++;
            if (address == Provider + 8 || address == Provider + 0x224) LiveBlockReads++;
            else StaticBlockReads++;
            return Read(address, destination);
        }
        public bool TryReadByte(ulong address, out byte value) => _bytes.TryGetValue(address, out value);
        public bool TryReadUInt32(ulong address, out uint value) { Span<byte> bytes = stackalloc byte[4]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : 0; return ok; }
        public bool TryReadUInt64(ulong address, out ulong value) { Span<byte> bytes = stackalloc byte[8]; var ok = Read(address, bytes); value = ok ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0; return ok; }
        public bool TryReadSingle(ulong address, out float value) { var ok = TryReadUInt32(address, out var bits); value = BitConverter.UInt32BitsToSingle(bits); return ok; }
    }
}
