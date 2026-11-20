using System.Buffers.Binary;
using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App.Drift;

internal readonly record struct DriftZoneScanRegion(ulong Base, ulong Size);

// The game memory the curve check reads: its committed, private, writable regions.
internal interface IDriftZoneScanMemory
{
    IReadOnlyList<DriftZoneScanRegion> Regions();

    // Fills destination from address; bytes the layer cannot or will not read stay zero.
    void ReadChunk(ulong address, Span<byte> destination);

    // An exact read of a short range; false when any byte could not be read.
    bool Read(ulong address, Span<byte> destination);
}

internal enum DriftZoneCurveVerdict
{
    Verified,
    NotFound,
    Unstable,
    Incomplete
}

internal sealed record DriftZoneCurveScanResult(DriftZoneCurveVerdict Verdict, int Entries, long BytesRead, TimeSpan Duration);

internal sealed record DriftZoneCurveScanOptions
{
    internal int ChunkBytes { get; init; } = 4 * 1024 * 1024;
    internal long MaximumBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    internal TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(90);
    internal TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(1);

    // The read yields between chunks for this multiple of the time each chunk took.
    internal double PaceFactor { get; init; } = 3;
    internal Action<TimeSpan> Wait { get; init; } = static delay => Thread.Sleep(delay);
}

// Confirms that the running game holds the known Drift Zone scoring curve. The game keeps one scoring
// entry per drift zone, each with the minimum, maximum and saturation angles and the two multipliers
// close together. The check finds the saturation angle, requires the other four values beside it in one
// consistent arrangement, and reads every entry a second time. It never writes to the game.
internal static class DriftZoneCurveScan
{
    internal const int MinimumEntries = 8;
    private const int Reach = 0x80;
    private const int WindowBytes = Reach * 2 + 8;
    private const int MaximumCandidates = 4096;

    private readonly record struct Entry(ulong Address, int Minimum, int Maximum, int Low, int High);

    internal static DriftZoneCurveScanResult Run(
        IDriftZoneScanMemory memory,
        DriftZoneScoringProfile curve,
        DriftZoneCurveScanOptions options,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();
        var pattern = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(pattern, (float)curve.SaturationAngleDegrees);
        var companions = new[]
        {
            BitConverter.SingleToUInt32Bits((float)curve.MinimumAngleDegrees),
            BitConverter.SingleToUInt32Bits((float)curve.MaximumAngleDegrees),
            BitConverter.SingleToUInt32Bits((float)curve.MinimumAngleMultiplier),
            BitConverter.SingleToUInt32Bits((float)curve.MaximumAngleMultiplier)
        };
        var chunk = new byte[Math.Max(4096, options.ChunkBytes)];
        var window = new byte[WindowBytes];
        var candidates = new List<Entry>();
        long bytes = 0;
        var complete = true;
        var work = new Stopwatch();

        foreach (var region in memory.Regions())
        {
            for (ulong offset = 0; offset < region.Size && complete; offset += (ulong)chunk.Length)
            {
                if (cancellation.IsCancellationRequested || bytes >= options.MaximumBytes || clock.Elapsed >= options.MaximumDuration)
                {
                    complete = false;
                    break;
                }

                work.Restart();
                var span = chunk.AsSpan(0, (int)Math.Min((ulong)chunk.Length, region.Size - offset));
                span.Clear();
                var address = region.Base + offset;
                memory.ReadChunk(address, span);
                bytes += span.Length;
                Collect(memory, address, span, pattern, companions, window, candidates);
                if (options.PaceFactor > 0)
                {
                    options.Wait(TimeSpan.FromTicks((long)(work.Elapsed.Ticks * options.PaceFactor)));
                }
            }

            if (!complete)
            {
                break;
            }
        }

        if (!complete)
        {
            return new DriftZoneCurveScanResult(DriftZoneCurveVerdict.Incomplete, 0, bytes, clock.Elapsed);
        }

        // Every entry of the real structure has the same arrangement; stray matches do not.
        var entries = candidates
            .GroupBy(static entry => (entry.Minimum, entry.Maximum, entry.Low, entry.High))
            .OrderByDescending(static group => group.Count())
            .Select(static group => group.ToList())
            .FirstOrDefault();
        if (entries is null || entries.Count < MinimumEntries)
        {
            return new DriftZoneCurveScanResult(DriftZoneCurveVerdict.NotFound, entries?.Count ?? 0, bytes, clock.Elapsed);
        }

        options.Wait(options.SettleDelay);
        foreach (var entry in entries)
        {
            if (cancellation.IsCancellationRequested)
            {
                return new DriftZoneCurveScanResult(DriftZoneCurveVerdict.Incomplete, 0, bytes, clock.Elapsed);
            }

            if (!ReadWindow(memory, entry.Address, window) ||
                !pattern.AsSpan().SequenceEqual(window.AsSpan(Reach, 4)) ||
                Arrangement(entry.Address, window, companions) != entry)
            {
                return new DriftZoneCurveScanResult(DriftZoneCurveVerdict.Unstable, entries.Count, bytes, clock.Elapsed);
            }
        }

        return new DriftZoneCurveScanResult(DriftZoneCurveVerdict.Verified, entries.Count, bytes, clock.Elapsed);
    }

    private static void Collect(
        IDriftZoneScanMemory memory,
        ulong address,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> pattern,
        uint[] companions,
        byte[] window,
        List<Entry> candidates)
    {
        var position = 0;
        while (position + 4 <= data.Length && candidates.Count < MaximumCandidates)
        {
            var index = data[position..].IndexOf(pattern);
            if (index < 0)
            {
                return;
            }

            var at = position + index;
            position = at + 1;
            var hit = address + (ulong)at;
            if ((hit & 3) != 0)
            {
                continue;
            }

            if (at >= Reach && at + Reach + 8 <= data.Length)
            {
                data.Slice(at - Reach, WindowBytes).CopyTo(window);
            }
            else if (!ReadWindow(memory, hit, window))
            {
                continue;
            }

            if (Arrangement(hit, window, companions) is { } entry)
            {
                candidates.Add(entry);
            }
        }
    }

    // The nearest float32 copy of each companion value within reach of the saturation angle.
    private static Entry? Arrangement(ulong hit, ReadOnlySpan<byte> window, uint[] companions)
    {
        Span<int> nearest = stackalloc int[4];
        for (var value = 0; value < companions.Length; value++)
        {
            var best = int.MinValue;
            for (var offset = 0; offset + 4 <= window.Length; offset += 4)
            {
                if (offset == Reach || BinaryPrimitives.ReadUInt32LittleEndian(window[offset..]) != companions[value])
                {
                    continue;
                }

                var delta = offset - Reach;
                if (best == int.MinValue || Math.Abs(delta) < Math.Abs(best))
                {
                    best = delta;
                }
            }

            if (best == int.MinValue)
            {
                return null;
            }

            nearest[value] = best;
        }

        return new Entry(hit, nearest[0], nearest[1], nearest[2], nearest[3]);
    }

    // Reads the bytes around a value page by page, so an entry at the edge of a memory block keeps its readable side.
    private static bool ReadWindow(IDriftZoneScanMemory memory, ulong hit, Span<byte> window)
    {
        window.Clear();
        var start = hit - Reach;
        if (memory.Read(start, window))
        {
            return true;
        }

        var any = false;
        for (var offset = 0; offset < window.Length;)
        {
            var at = start + (ulong)offset;
            var length = (int)Math.Min((ulong)(window.Length - offset), 0x1000 - (at & 0xFFF));
            if (memory.Read(at, window.Slice(offset, length)))
            {
                any = true;
            }
            else
            {
                window.Slice(offset, length).Clear();
            }

            offset += length;
        }

        return any;
    }
}
