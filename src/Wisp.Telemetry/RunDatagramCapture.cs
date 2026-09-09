using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Wisp.Telemetry;

public readonly record struct RunDatagram(ReadOnlyMemory<byte> Bytes, long Timestamp, long Sequence);

// The receiver only copies into preallocated slots. Parsing and storage belong to its single reader.
public sealed class RunDatagramCapture
{
    private readonly Channel<byte[]> _available;
    private readonly Channel<(byte[] Buffer, int Length, long Timestamp, long Sequence)> _pending;
    private long _sequence;
    private long _dropped;
    private int _completed;

    internal RunDatagramCapture(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 8192);
        _available = Channel.CreateBounded<byte[]>(capacity);
        _pending = Channel.CreateBounded<(byte[], int, long, long)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        for (var index = 0; index < capacity; index++)
            _available.Writer.TryWrite(new byte[Fh6PacketLayout.PacketLength]);
    }

    public long DroppedDatagrams => Interlocked.Read(ref _dropped);
    public long ObservedDatagrams => Interlocked.Read(ref _sequence);
    public string CompletionReason { get; private set; } = "Recording stopped";

    internal void Capture(ReadOnlySpan<byte> bytes, long timestamp)
    {
        if (Volatile.Read(ref _completed) != 0) return;
        var sequence = Interlocked.Increment(ref _sequence);
        if (!_available.Reader.TryRead(out var buffer))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        var length = bytes.Length == Fh6PacketLayout.PacketLength ? bytes.Length : 0;
        bytes[..length].CopyTo(buffer);
        if (!_pending.Writer.TryWrite((buffer, length, timestamp, sequence)))
        {
            _available.Writer.TryWrite(buffer);
            Interlocked.Increment(ref _dropped);
        }
    }

    internal void Complete(string reason)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        CompletionReason = reason;
        _pending.Writer.TryComplete();
    }

    public async IAsyncEnumerable<RunDatagram> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in _pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                yield return new RunDatagram(packet.Buffer.AsMemory(0, packet.Length), packet.Timestamp, packet.Sequence);
            }
            finally
            {
                _available.Writer.TryWrite(packet.Buffer);
            }
        }
    }
}
