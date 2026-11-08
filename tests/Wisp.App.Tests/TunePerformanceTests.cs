using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TunePerformanceTests
{
    [Theory]
    [InlineData("verified", true)]
    [InlineData("version", false)]
    [InlineData("hash", false)]
    [InlineData("length", false)]
    [InlineData("image", false)]
    public void Steam483PerformanceRequiresTheVerifiedExecutable(string field, bool expected)
    {
        using var source = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.UpdatedSteam.json")!;
        var document = JsonNode.Parse(source)!.AsObject();
        document["gameVersion"] = field == "version" ? "6.483.971.0" : "6.483.970.0";
        document["executableSha256"] = field == "hash" ? new string('A', 64) : "63791D0B686186C23ADE10A1AE02EFD785045BD818CC89816F472CAD9A69FCB9";
        document["executableLength"] = field == "length" ? 184268249L : 184268248L;
        document["imageSize"] = field == "image" ? 188731392u + 4096 : 188731392u;
        var profile = NativeTunePerformanceProfiles.Resolve(NativeHudCompatibilityPack.FromRuntimeValidation(document));
        if (!expected) { Assert.Null(profile); return; }
        Assert.NotNull(profile);
        Assert.Equal((0x6F1F698UL, 0x6FA4AD0UL, 0x6422F48UL), (profile.RootPageVtable, profile.TunePageVtable, profile.BenchmarkVtable));
        Assert.Equal(new[] { 303, 668, 255, 1884, 346, 637, 1546 }, profile.Guards.Select(g => g.Length));
    }

    [Theory]
    [InlineData("verified", true)]
    [InlineData("version", false)]
    [InlineData("hash", false)]
    [InlineData("length", false)]
    [InlineData("image", false)]
    public void Steam479PerformanceRequiresTheVerifiedExecutable(string field, bool expected)
    {
        using var source = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.UpdatedSteam.json")!;
        var document = JsonNode.Parse(source)!.AsObject();
        document["gameVersion"] = field == "version" ? "6.479.875.0" : "6.479.874.0";
        document["executableSha256"] = field == "hash" ? new string('A', 64) : "28FE0814168D474F162592D5568479554882D82C6CA6575965B25BBBDFFA89F0";
        document["executableLength"] = field == "length" ? 184214489L : 184214488L;
        document["imageSize"] = field == "image" ? 188669952u + 4096 : 188669952u;
        var profile = NativeTunePerformanceProfiles.Resolve(NativeHudCompatibilityPack.FromRuntimeValidation(document));
        if (!expected) { Assert.Null(profile); return; }
        Assert.NotNull(profile);
        Assert.Equal((0x70E8698UL, 0x716D280UL, 0x6E41728UL), (profile.RootPageVtable, profile.TunePageVtable, profile.BenchmarkVtable));
        Assert.Equal(new[] { 303, 668, 255, 1884, 346, 637, 1546 }, profile.Guards.Select(g => g.Length));
    }

    private static TunePerformance Captured => new(16.000036f, 34.689072f, 1.928532f, 2.594379f,
        2.1999986f, 3.7833304f, 103.90273f, .504751f, .38999075f, .75537235f);

    [Fact]
    public void CapturedSiResultsReproduceTheGamePanelAndComparisonUsesTheSameUnits()
    {
        var rows = TunePresentation.PerformanceRows(Captured, Captured with { Braking60MphMetres = 16.304836f }, true).ToArray();
        Assert.Equal(new[] { "52.5 ft", "113.8 ft", "1.93 g", "2.59 g", "2.200 s", "3.783 s", "232.4 mph", "0.50", "0.39", "0.755" }, rows.Select(row => row.Value));
        Assert.Equal("+1.0 ft", rows[0].Difference);
        Assert.True(rows[0].Changed);
        Assert.False(rows[1].Changed);
        var missing = TunePresentation.PerformanceRows(null, Captured, true).ToArray();
        Assert.All(missing, row => { Assert.Equal("—", row.Value); Assert.Empty(row.Difference); });
    }

    [Fact]
    public void MetricUsersSeeDistancesInMetresAndTopSpeedInKilometresPerHour()
    {
        var rows = TunePresentation.PerformanceRows(Captured, null, false, SpeedUnit.KilometersPerHour).ToArray();
        Assert.Equal("16.0 m", rows[0].Value);
        Assert.Equal("34.7 m", rows[1].Value);
        Assert.Equal("374.0 km/h", rows[6].Value);
        // The game's benchmarks keep their names; only the units follow the Wisp setting.
        Assert.Equal("Braking · 60–0 mph", rows[0].Label);
    }

    [Fact]
    public async Task PerformancePersistsWithSavedTuneAndOlderSnapshotsRemainReadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WispPerformanceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TuneStore(directory);
            var snapshot = TuneUiTestData.ValidSnapshot() with { Performance = Captured };
            var saved = await store.SaveAsync(snapshot, "Track", "", TestContext.Current.CancellationToken);
            Assert.Equal(Captured, (await store.LoadAsync(saved.Id, TestContext.Current.CancellationToken)).Snapshot.Performance);
            var old = await store.SaveAsync(snapshot with { Performance = null }, "Road", "", TestContext.Current.CancellationToken);
            Assert.Null((await store.LoadAsync(old.Id, TestContext.Current.CancellationToken)).Snapshot.Performance);
            Assert.True(TuneSnapshotValidator.TryValidate(snapshot, out _));
            Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Performance = Captured with { AeroBalance = float.NaN } }, out _));
            Assert.False(TuneSnapshotValidator.TryValidate(snapshot with { Performance = Captured with { TopSpeedMetresPerSecond = -1 } }, out _));
            Assert.Equal(Captured, JsonSerializer.Deserialize<TuneSnapshot>(JsonSerializer.Serialize(snapshot))!.Performance);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CompletedMatchingBenchmarkIsAcceptedAndFailedMetricStaysMissing()
    {
        var memory = new Memory();
        Assert.Equal(3.5f, memory.Read()!.Acceleration60MphSeconds);
        memory.I32(Memory.Benchmark + 0x914 + 4, 3);
        var result = memory.Read()!;
        Assert.Null(result.Acceleration60MphSeconds);
        Assert.Equal(3.5f, result.Acceleration100MphSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PendingOrDifferentCarPartsAndSettingsDoNotAcquireOldResults(int fault)
    {
        var memory = new Memory();
        switch (fault)
        {
            case 0: memory.I32(Memory.Benchmark + 0xB10, 12); break;
            case 1: memory.Put(Memory.Benchmark + 0xB14, [0]); break;
            case 2: memory.I32(Memory.Benchmark + 0x90C, 2); break;
            case 3: memory.Put(Memory.Descriptor + 0xC + 0x100, [1]); break;
            case 4: memory.Put(Memory.Actor + 0x71F4 + 0xA0, [1]); break;
            case 5: memory.I32(Memory.Descriptor, 2); break;
        }
        Assert.Null(memory.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingResultsOrNonfiniteValuesAreRejected(bool changes)
    {
        var memory = new Memory { ChangeOnRepeat = changes };
        if (!changes) memory.I32(Memory.Benchmark + 0x914, BitConverter.SingleToInt32Bits(float.NaN));
        if (changes) Assert.Throws<TuneChangedException>(() => memory.Read());
        else Assert.Throws<InvalidDataException>(() => memory.Read());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetupKeyIncludesEveryInstalledPartAndNormalizedSetting(bool changePart)
    {
        var memory = new Memory();
        var key = memory.SetupKey();
        var address = changePart ? Memory.Descriptor + 0xC + 0x193 : Memory.Actor + 0x71F4 + 0xB7;
        memory.Put(address, [1]);
        Assert.NotEqual(key, memory.SetupKey());
        memory.Put(address, [0]);
        Assert.Equal(key, memory.SetupKey());
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        internal const ulong Actor = 0x200000, Descriptor = 0x300000, Benchmark = 0x400000;
        private readonly Dictionary<ulong, byte> _bytes = [];
        private int _resultReads;
        internal bool ChangeOnRepeat { get; init; }
        internal Memory()
        {
            I32(Actor + 0x100, 3141); I32(Descriptor, 3141); I32(Benchmark + 0x90C, 3141);
            Put(Actor + 0x2E0, BitConverter.GetBytes(Descriptor));
            Put(Descriptor + 0xC, new byte[0x194]); Put(Benchmark + 0x134, new byte[0x188]);
            Put(Actor + 0x71F4, new byte[0xB8]); Put(Benchmark + 0x2C8, new byte[0xB8]);
            I32(Benchmark + 0xB10, 13); Put(Benchmark + 0xB14, [1]);
            foreach (var offset in new ulong[] { 0x954, 0x944, 0x96C, 0x974, 0x914, 0x924, 0x93C, 0xAD8, 0xAD0, 0xAC8 })
            { I32(Benchmark + offset, BitConverter.SingleToInt32Bits(offset is 0xAD8 or 0xAD0 ? .5f : 3.5f)); I32(Benchmark + offset + 4, 2); }
        }
        internal string SetupKey() => NativeTunePerformance.ReadSetup(new(this, TestContext.Current.CancellationToken), Actor, 0x100)!.Key;
        internal TunePerformance? Read()
        {
            var read = new NativeTuneRead(this, TestContext.Current.CancellationToken);
            return NativeTunePerformance.ReadSetup(read, Actor, 0x100) is { } setup
                ? NativeTunePerformance.ReadResults(read, Benchmark, setup) : null;
        }
        internal void I32(ulong address, int value) => Put(address, BitConverter.GetBytes(value));
        internal void Put(ulong address, byte[] data)
        { for (var i = 0; i < data.Length; i++) _bytes[address + (ulong)i] = data[i]; }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            for (var i = 0; i < destination.Length; i++)
                if (!_bytes.TryGetValue(address + (ulong)i, out destination[i])) return false;
            if (address == Benchmark + 0x914 && ++_resultReads > 1 && ChangeOnRepeat) destination[0] ^= 1;
            return true;
        }
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
    }
}
