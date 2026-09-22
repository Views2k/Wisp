using System.Buffers.Binary;
using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCapturePacketEvidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(232)]
    [InlineData(311)]
    [InlineData(323)]
    [InlineData(325)]
    public void OnlyExactHorizonPacketIsProjected(int length) =>
        Assert.Null(ShiftCapturePacketEvidence.Read(new byte[length]));

    [Fact]
    public void ProjectionRetainsUnverifiedInputsAndFiniteDiagnosticsWithoutChangingPacket()
    {
        var packet = new byte[324];
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), 893.56335f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24, 4), float.NaN);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(56, 4), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(244, 4), -345f);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(220, 4), 943);
        packet[317] = 128;
        packet[318] = 255;
        var original = packet.ToArray();
        var root = JsonSerializer.SerializeToElement(ShiftCapturePacketEvidence.Read(packet));
        Assert.Equal(original, packet);
        Assert.Equal(893.56335f, root.GetProperty("engineIdleRpm").GetSingle());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("verticalAcceleration").ValueKind);
        Assert.Equal(1.5, root.GetProperty("orientationYawPitchRoll")[0].GetDouble());
        Assert.Equal(-345, root.GetProperty("positionInGame")[0].GetDouble());
        Assert.Equal(943, root.GetProperty("performanceIndex").GetInt32());
        Assert.Equal(128, root.GetProperty("dashByte317").GetByte());
        Assert.Equal(255, root.GetProperty("dashByte318").GetByte());
        Assert.Contains("not measured clutch engagement", root.GetProperty("meaning").GetString());
    }
}
