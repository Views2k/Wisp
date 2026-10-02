using System.Text.Json;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderLosslessStateTests
{
    [Fact]
    public void BufferDetailsCarryActualDurationAndSizeWithoutHidingCaptureWarnings()
    {
        var session = Guid.NewGuid();
        var fields = Fields(session);
        var state = Assert.IsType<RecorderStateUpdate>(RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.Equal(new RecorderLosslessBuffer(580_000_000, 1_048_576, 2_097_152, true), state.LosslessBuffer);
        var status = ClipRecorderService.BufferingStatus(state);
        Assert.Contains("Lossless history:", status);
        Assert.Contains("Size limit reached", status);
        Assert.Contains("No new game frames", ClipRecorderService.BufferingStatus(state with { Reason = "capture_stale" }));
        Assert.Contains("audio is unavailable", ClipRecorderService.BufferingStatus(state with { Reason = "audio_unavailable" }));
    }

    [Theory]
    [InlineData("duration100ns", 0L)]
    [InlineData("duration100ns", 3_000_000_001L)]
    [InlineData("payloadBytes", 0L)]
    [InlineData("payloadBytes", 2_097_153L)]
    [InlineData("budgetBytes", 1_048_575L)]
    [InlineData("budgetBytes", 12L * 1024 * 1024 * 1024 + 16L * 1024 * 1024 + 1)]
    public void RejectsInvalidOrUnboundedBufferMetrics(string name, long value)
    {
        var session = Guid.NewGuid();
        var fields = Fields(session);
        ((Dictionary<string, object>)fields["losslessBuffer"])[name] = value;
        Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
    }

    [Theory]
    [InlineData("stopped", "stopped")]
    [InlineData("paused", "window_minimized")]
    public void MetricsCannotLeakIntoStoppedOrUnreadyPausedState(string stateName, string reason)
    {
        var session = Guid.NewGuid();
        var fields = Fields(session);
        fields["state"] = stateName;
        fields["reason"] = reason;
        if (stateName == "paused") fields["bufferReady"] = false;
        Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        fields.Remove("losslessBuffer");
        var stopped = Assert.IsType<RecorderStateUpdate>(RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.Null(stopped.LosslessBuffer);
    }

    [Fact]
    public void PausedStateKeepsMetricsWhenRecordedHistoryIsReady()
    {
        var session = Guid.NewGuid();
        var fields = Fields(session);
        fields["state"] = "paused";
        fields["reason"] = "focus_lost";
        fields["bufferReady"] = true;
        var paused = Assert.IsType<RecorderStateUpdate>(RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(fields), session));
        Assert.True(paused.BufferReady);
        Assert.Equal(new RecorderLosslessBuffer(580_000_000, 1_048_576, 2_097_152, true), paused.LosslessBuffer);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"duration100ns\":1,\"payloadBytes\":1,\"budgetBytes\":2}")]
    [InlineData("{\"duration100ns\":1,\"payloadBytes\":1,\"budgetBytes\":2,\"sizeLimited\":false,\"extra\":0}")]
    [InlineData("{\"duration100ns\":1,\"payloadBytes\":1,\"payloadBytes\":2,\"budgetBytes\":2,\"sizeLimited\":false}")]
    [InlineData("{\"duration100ns\":1,\"payloadBytes\":1,\"budgetBytes\":2,\"sizeLimited\":\"false\"}")]
    public void RejectsMalformedNestedMetrics(string nested)
    {
        var session = Guid.NewGuid();
        var json = $"{{\"v\":2,\"session\":\"{session:N}\",\"request\":0,\"type\":\"state\",\"state\":\"buffering\",\"reason\":\"none\",\"losslessBuffer\":{nested}}}";
        Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(json), session));
    }

    [Fact]
    public void StandardBufferingCopyIsUnchangedAndLowSpaceHasActionableReason()
    {
        Assert.Equal("Recording game clips.", ClipRecorderService.BufferingStatus(new("buffering", "none")));
        Assert.Contains("turn off Lossless video", ClipRecorderService.ReasonText("lossless_storage_low"));
    }

    private static Dictionary<string, object> Fields(Guid session) => new()
    {
        ["v"] = RecorderProtocol.Version,
        ["session"] = session.ToString("N"),
        ["request"] = 0,
        ["type"] = "state",
        ["state"] = "buffering",
        ["reason"] = "none",
        ["losslessBuffer"] = new Dictionary<string, object>
        {
            ["duration100ns"] = 580_000_000L,
            ["payloadBytes"] = 1_048_576L,
            ["budgetBytes"] = 2_097_152L,
            ["sizeLimited"] = true
        }
    };
}
