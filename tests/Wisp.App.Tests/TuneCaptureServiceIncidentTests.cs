using System.IO;
using Wisp.App.Supplementary;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneCaptureServiceIncidentTests
{
    [Fact]
    public void TuneReadFailureNamesTheReaderMethodThatFailedWithoutItsMessage()
    {
        var read = new NativeTuneRead(new Memory(), CancellationToken.None, maximumBytes: 4);
        _ = read.Bytes(0x10000, 4);
        var error = Assert.Throws<InvalidDataException>(() => read.Bytes(0x10000, 1));

        var incident = TuneCaptureService.TuneIncident(TuneCaptureStage.ReadTune, "tune-read-failed", error);

        var exception = Assert.Single(incident.Exceptions!.Value);
        Assert.Equal("InvalidDataException", exception.Type);
        Assert.Contains(SupplementaryIncidentSchema.MethodId("Wisp.App.Tunes.NativeTuneRead.Bytes"), exception.Methods);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.DoesNotContain("exceeded its bounds", System.Text.Json.JsonSerializer.Serialize(incident), StringComparison.Ordinal);

        // Different checks are different problems: the failing method is part of the signature.
        var elsewhere = TuneCaptureService.TuneIncident(TuneCaptureStage.ReadTune, "tune-read-failed", new InvalidDataException("other"));
        Assert.NotEqual(incident.Signature, elsewhere.Signature);
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public bool TryReadBytes(ulong address, Span<byte> destination) { destination.Fill(1); return true; }
    }
}
