namespace Wisp.App.Drift;

// The attached game's private writable memory, read through the read-only handle the HUD already holds.
internal sealed class DriftZoneGameMemory(NativeHudProcessMemory memory) : IDriftZoneScanMemory
{
    private const uint Committed = 0x1000;
    private const uint PrivateMemory = 0x20000;
    private const uint ReadWrite = 0x04;
    private const int Piece = 64 * 1024;

    public IReadOnlyList<DriftZoneScanRegion> Regions()
    {
        var regions = new List<DriftZoneScanRegion>();
        ulong address = 0x10000;
        while (address < NativeHudProcessMemory.UserAddressLimit && memory.TryQueryRegion(address, out var region))
        {
            if (region.State == Committed && region.Type == PrivateMemory && region.Protect == ReadWrite)
            {
                regions.Add(new DriftZoneScanRegion(region.Base, region.Size));
            }

            var next = region.Base + region.Size;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return regions;
    }

    public void ReadChunk(ulong address, Span<byte> destination)
    {
        if (memory.TryReadBytes(address, destination))
        {
            return;
        }

        // A region can change while it is read; keep the pieces that are still readable.
        for (var offset = 0; offset < destination.Length; offset += Piece)
        {
            var part = destination.Slice(offset, Math.Min(Piece, destination.Length - offset));
            if (!memory.TryReadBytes(address + (ulong)offset, part))
            {
                part.Clear();
            }
        }
    }

    public bool Read(ulong address, Span<byte> destination) => memory.TryReadBytes(address, destination);
}
