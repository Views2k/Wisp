using System.Buffers.Binary;
using Wisp.App.Drift;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DriftZoneCurveScanTests
{
    private static readonly DriftZoneScoringProfile Curve = new(10, 110, (double)59.4f, 30, 40);
    private static readonly DriftZoneCurveScanOptions Fast = new() { PaceFactor = 0, SettleDelay = TimeSpan.Zero, Wait = static _ => { } };

    [Fact]
    public void TwentyEntriesInTheGameArrangementAreVerified()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 24 * 0xB00);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + (ulong)i * 0xB00 + 0x8D4);

        var result = DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.Verified, result.Verdict);
        Assert.Equal(20, result.Entries);
        Assert.Equal(24 * 0xB00, result.BytesRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    public void FewerEntriesThanTheMinimumAreNotAccepted(int count)
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 24 * 0xB00);
        for (var i = 0; i < count; i++) memory.WriteEntry(pool + (ulong)i * 0xB00 + 0x8D4);

        var result = DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.NotFound, result.Verdict);
        Assert.Equal(count, result.Entries);
    }

    [Fact]
    public void ADifferentSaturationAngleIsNotFound()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 24 * 0xB00);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + (ulong)i * 0xB00 + 0x8D4, saturation: 65f);

        Assert.Equal(DriftZoneCurveVerdict.NotFound, DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None).Verdict);
    }

    [Fact]
    public void ADifferentMultiplierIsNotFound()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 24 * 0xB00);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + (ulong)i * 0xB00 + 0x8D4, high: 45f, extra: false);

        Assert.Equal(DriftZoneCurveVerdict.NotFound, DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None).Verdict);
    }

    [Fact]
    public void StrayCopiesOfTheAngleDoNotCountAsEntries()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 0x10000);
        for (var i = 0; i < 40; i++) memory.WriteSingle(pool + 0x100 + (ulong)i * 0x200, 59.4f);
        // Seven complete entries in one arrangement and three in another are still too few of either.
        for (var i = 0; i < 7; i++) memory.WriteEntry(pool + 0x8000 + (ulong)i * 0x400);
        for (var i = 0; i < 3; i++) memory.WriteShuffledEntry(pool + 0xC000 + (ulong)i * 0x400);

        var result = DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.NotFound, result.Verdict);
        Assert.Equal(7, result.Entries);
    }

    [Fact]
    public void EntriesThatChangeBeforeTheSecondReadAreUnstable()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 24 * 0xB00);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + (ulong)i * 0xB00 + 0x8D4);
        var options = Fast with { Wait = _ => memory.WriteSingle(pool + 3 * 0xB00 + 0x8D4, 12f) };

        var result = DriftZoneCurveScan.Run(memory, Curve, options, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.Unstable, result.Verdict);
    }

    [Fact]
    public void EntriesAcrossChunkBordersAndAtBlockEdgesAreFound()
    {
        var memory = new FakeMemory();
        var first = memory.Add(0x1E0_0000_0000, 0x3000);
        var second = memory.Add(0x1F0_0000_0000, 0x3000);
        // One at the very start of a block, one at its very end, and four whose fields straddle a 4 KiB chunk border.
        memory.WriteEntry(first + 8);
        memory.WriteEntry(first + 0x3000 - 0x24);
        memory.WriteEntry(first + 0xFFC);
        memory.WriteEntry(first + 0x2004);
        memory.WriteEntry(second + 0x1000);
        memory.WriteEntry(second + 0x1FF8);
        for (var i = 0; i < 4; i++) memory.WriteEntry(second + 0x2100 + (ulong)i * 0x400);
        var options = Fast with { ChunkBytes = 4096 };

        var result = DriftZoneCurveScan.Run(memory, Curve, options, CancellationToken.None);

        Assert.Equal(10, result.Entries);
        Assert.Equal(DriftZoneCurveVerdict.Verified, result.Verdict);
    }

    [Fact]
    public void PagesTheMemoryLayerLeavesOutAreNotReadAnotherWay()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 0x20000);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + 0x10000 + (ulong)i * 0x400);
        memory.LeaveOut(pool, 0x10000);

        var result = DriftZoneCurveScan.Run(memory, Curve, Fast, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.Verified, result.Verdict);
        Assert.Equal(0, memory.LeftOutBytesRead);
    }

    [Fact]
    public void ALimitOrACancellationLeavesTheResultIncomplete()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 0x40000);
        for (var i = 0; i < 20; i++) memory.WriteEntry(pool + 0x30000 + (ulong)i * 0x400);
        var small = Fast with { ChunkBytes = 4096, MaximumBytes = 0x8000 };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Equal(DriftZoneCurveVerdict.Incomplete, DriftZoneCurveScan.Run(memory, Curve, small, CancellationToken.None).Verdict);
        Assert.Equal(DriftZoneCurveVerdict.Incomplete, DriftZoneCurveScan.Run(memory, Curve, Fast, cancelled.Token).Verdict);
        Assert.Equal(DriftZoneCurveVerdict.Incomplete, DriftZoneCurveScan.Run(memory, Curve, Fast with { MaximumDuration = TimeSpan.Zero }, CancellationToken.None).Verdict);
    }

    [Fact]
    public void TheReadPausesBetweenChunksAndBeforeTheSecondRead()
    {
        var memory = new FakeMemory();
        var pool = memory.Add(0x1E0_0000_0000, 0x4000);
        for (var i = 0; i < 8; i++) memory.WriteEntry(pool + 0x100 + (ulong)i * 0x400);
        var waits = new List<TimeSpan>();
        var options = new DriftZoneCurveScanOptions { ChunkBytes = 4096, SettleDelay = TimeSpan.FromSeconds(1), Wait = waits.Add };

        var result = DriftZoneCurveScan.Run(memory, Curve, options, CancellationToken.None);

        Assert.Equal(DriftZoneCurveVerdict.Verified, result.Verdict);
        Assert.Equal(5, waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), waits[^1]);
    }

    private sealed class FakeMemory : IDriftZoneScanMemory
    {
        private readonly List<(ulong Base, byte[] Data)> _blocks = [];
        private readonly List<(ulong Start, ulong End)> _leftOut = [];

        internal long LeftOutBytesRead { get; private set; }

        internal ulong Add(ulong address, int size)
        {
            _blocks.Add((address, new byte[size]));
            return address;
        }

        internal void LeaveOut(ulong start, ulong length) => _leftOut.Add((start, start + length));

        internal void WriteSingle(ulong address, float value) => BinaryPrimitives.WriteSingleLittleEndian(At(address, 4), value);

        // The arrangement both game builds use: minimum, maximum, saturation, four other values, then the multipliers.
        internal void WriteEntry(ulong saturationAddress, float saturation = 59.4f, float high = 40f, bool extra = true)
        {
            float[] values = extra
                ? [10f, 110f, saturation, 54f, 10f, 50f, 0.1f, 30f, high, 45f, 50f]
                : [10f, 110f, saturation, 54f, 11f, 50f, 0.1f, 30f, high, 45f, 50f];
            for (var i = 0; i < values.Length; i++) WriteSingle(saturationAddress - 8 + (ulong)i * 4, values[i]);
        }

        internal void WriteShuffledEntry(ulong saturationAddress)
        {
            float[] values = [30f, 40f, 59.4f, 10f, 110f];
            for (var i = 0; i < values.Length; i++) WriteSingle(saturationAddress - 8 + (ulong)i * 4, values[i]);
        }

        public IReadOnlyList<DriftZoneScanRegion> Regions() => _blocks.Select(block => new DriftZoneScanRegion(block.Base, (ulong)block.Data.Length)).ToList();

        public void ReadChunk(ulong address, Span<byte> destination)
        {
            if (!Read(address, destination)) return;
            foreach (var (start, end) in _leftOut)
            {
                var from = Math.Max(start, address);
                var to = Math.Min(end, address + (ulong)destination.Length);
                if (to > from) destination.Slice((int)(from - address), (int)(to - from)).Clear();
            }
        }

        public bool Read(ulong address, Span<byte> destination)
        {
            foreach (var (blockBase, data) in _blocks)
            {
                if (address < blockBase || address + (ulong)destination.Length > blockBase + (ulong)data.Length) continue;
                data.AsSpan((int)(address - blockBase), destination.Length).CopyTo(destination);
                foreach (var (start, end) in _leftOut)
                {
                    if (address < end && address + (ulong)destination.Length > start && destination.Length <= 0x200) LeftOutBytesRead += destination.Length;
                }

                return true;
            }

            return false;
        }

        private Span<byte> At(ulong address, int length)
        {
            foreach (var (blockBase, data) in _blocks)
            {
                if (address >= blockBase && address + (ulong)length <= blockBase + (ulong)data.Length) return data.AsSpan((int)(address - blockBase), length);
            }

            throw new ArgumentOutOfRangeException(nameof(address));
        }
    }
}
