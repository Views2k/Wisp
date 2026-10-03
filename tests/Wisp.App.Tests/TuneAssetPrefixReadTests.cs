using System.IO;
using Wisp.App;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneAssetPrefixReadTests
{
    [Theory]
    [InlineData(16221184U)]
    [InlineData(16222208U)]
    [InlineData(1048576U)]
    [InlineData(17270784U)]
    [InlineData(67108864U)]
    public void MutableLengthReadsBoundedCurrentStreamAndStillRejectsUnverifiedTransform(uint length)
    {
        using var memory = new AssetMemory(length);

        var error = Assert.Throws<InvalidDataException>(() =>
            TuneAssetCapture.ReadDecoded(memory, TestContext.Current.CancellationToken, NativeTuneLayout.Steam));

        Assert.Equal("The game's tuning metadata could not be verified.", error.Message);
        Assert.Equal((int)length, memory.BulkBytesRead);
        Assert.Equal((ulong)length, memory.HighestBulkEnd);
        Assert.False(memory.ReadPastStream);
        Assert.InRange(memory.TotalBytesRead, (long)length + 1, TuneAssetCapture.MaximumReadBytes);
        Assert.True(memory.SawRepeatedHeaderRead);
    }

    [Fact]
    public void ShorterStreamIsRejectedBeforeAnyAssetBytesAreRead()
    {
        using var memory = new AssetMemory(TuneAssetCapture.MinimumLength - 1024);

        Assert.Throws<TuneAssetStreamValidationException>(() =>
            TuneAssetCapture.ReadDecoded(memory, TestContext.Current.CancellationToken, NativeTuneLayout.Steam));

        Assert.Equal(0, memory.BulkBytesRead);
    }

    [Fact]
    public void ChunkVectorMustStillCoverTheFullMutableLength()
    {
        using var memory = new AssetMemory(16222208, chunkCount: 123);

        Assert.Throws<InvalidDataException>(() =>
            TuneAssetCapture.ReadDecoded(memory, TestContext.Current.CancellationToken, NativeTuneLayout.Steam));

        Assert.Equal(0, memory.BulkBytesRead);
    }

    private sealed class AssetMemory : INativeHudProcessMemory
    {
        private const uint ChunkSize = 131072;
        private const ulong BulkStart = 0x2000000;
        private readonly Dictionary<ulong, byte[]> _headers = [];
        private readonly HashSet<ulong> _readHeaders = [];
        private readonly ulong _bulkCapacity;
        private readonly uint _length;
        internal int BulkBytesRead { get; private set; }
        internal ulong HighestBulkEnd { get; private set; }
        internal bool ReadPastStream { get; private set; }
        internal long TotalBytesRead { get; private set; }
        internal bool SawRepeatedHeaderRead { get; private set; }
        public ulong ModuleBase => 0x140000000;

        internal AssetMemory(uint length, int? chunkCount = null)
        {
            _length = length;
            var count = chunkCount ?? checked((int)((length + (ulong)ChunkSize - 1) / ChunkSize));
            _bulkCapacity = (ulong)count * ChunkSize;
            Put(ModuleBase + 0xA8AF088, 0x100000);
            Put(0x100160, 0x110000);
            Put(0x110000, ModuleBase + 0x6C7A570);
            Put(0x110010, 0x120000);
            Put(0x120048, 0x4B771290);
            Put(0x120008, 1);
            Put(0x120010, 0x130000);
            Put(0x130008, 0x140000);
            Put(0x140008, 0x150000);
            Put(0x150000, 0x160000);
            Put(0x160040, 0x170000);
            Put(0x170000, ModuleBase + 0x6C79E70);
            Put(0x170008, 0x180000);
            Put(0x180000, 0x190000);
            Put(0x190000, ModuleBase + 0x6C7B3D8);
            Put(0x190030, 0x1A0000);
            Put(0x1A0000, ModuleBase + 0x6C7BC10);
            Put(0x1A0030, 0x1B0000);
            Put(0x1B0000, ModuleBase + 0x6C7B9B0);
            Put(0x1B0030, ChunkSize);
            Put(0x1B0034, length);
            Put(0x1B0040, length);
            Put(0x1B0048, 0x1C0000);
            Put(0x1B0050, 0x1C0000 + (ulong)count * 8);
            Put(0x1B0060, 0);
            for (var i = 0; i < count; i++)
                Put(0x1C0000 + (ulong)i * 8, BulkStart + (ulong)i * ChunkSize);
            _headers[ModuleBase + 0x8F75670] = new byte[1024];
            _headers[ModuleBase + 0x8F75A70] = new byte[256];
        }

        private void Put(ulong address, ulong value) => _headers[address] = BitConverter.GetBytes(value);
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            TotalBytesRead += destination.Length;
            if (_headers.TryGetValue(address, out var bytes) && bytes.Length >= destination.Length)
            {
                SawRepeatedHeaderRead |= !_readHeaders.Add(address);
                bytes.AsSpan(0, destination.Length).CopyTo(destination);
                return true;
            }
            if (address < BulkStart || address - BulkStart + (ulong)destination.Length > _bulkCapacity) return false;
            var end = address - BulkStart + (ulong)destination.Length;
            BulkBytesRead += destination.Length;
            HighestBulkEnd = Math.Max(HighestBulkEnd, end);
            ReadPastStream |= end > _length;
            destination.Fill(0xA5);
            return true;
        }
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public void Dispose() { }
    }
}
