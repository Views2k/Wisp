using System.Buffers.Binary;
using Wisp.Telemetry;

namespace Wisp.App;

// Diagnostic projection only. Every original byte remains in rawBase64; none of
// these additional channels changes the live parser, cue or vehicle state.
internal static class ShiftCapturePacketEvidence
{
    internal static object? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Fh6PacketLayout.PacketLength) return null;
        return new
        {
            schema = "fh-324-common-layout-projection-v1",
            engineIdleRpm = Scalar(bytes, 12),
            verticalAcceleration = Scalar(bytes, 24),
            angularVelocity = Triple(bytes, 44),
            orientationYawPitchRoll = Triple(bytes, 56),
            wheelOnRumbleStrip = new[] {
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(116, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(120, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(124, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(128, 4)) },
            wheelInPuddleDepth = Four(bytes, 132),
            surfaceRumble = Four(bytes, 148),
            combinedTireSlip = Four(bytes, 180),
            suspensionTravelMeters = Four(bytes, 196),
            carClass = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(216, 4)),
            performanceIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(220, 4)),
            positionInGame = Triple(bytes, 244),
            fuel = Scalar(bytes, 288),
            distance = Scalar(bytes, 292),
            dashByte317 = bytes[317],
            dashByte318 = bytes[318],
            meaning = "Common Forza layout projection. Bytes 317/318 are candidate clutch/handbrake input fields, not measured clutch engagement. Additional channel semantics require FH6 validation; raw packet is authoritative."
        };
    }

    private static double? Scalar(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));
        return float.IsFinite(value) ? value : null;
    }

    private static double?[] Triple(ReadOnlySpan<byte> bytes, int offset) =>
        [Scalar(bytes, offset), Scalar(bytes, offset + 4), Scalar(bytes, offset + 8)];

    private static double?[] Four(ReadOnlySpan<byte> bytes, int offset) =>
        [Scalar(bytes, offset), Scalar(bytes, offset + 4), Scalar(bytes, offset + 8), Scalar(bytes, offset + 12)];
}
