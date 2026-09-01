using System.Text.Json;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class UpdateApplyRequestTests
{
    [Fact]
    public void ApplyContractUsesStableExplicitWireNames()
    {
        var request = new UpdateApplyRequest(
            "C:\\staging\\Wisp-Setup-1.2.3.exe",
            "1.2.3",
            "1.0.0",
            123,
            "C:\\app\\Wisp.exe",
            new string('a', 64),
            456,
            UpdateApplyContract.CreateReadyEventName(new string('b', 64)));

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"stagedInstallerPath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"parentProcessId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"appExecutablePath\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedSha256\"", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedSizeBytes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"readyEventName\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadySignalNamesRequireAnUnpredictableLowercaseToken()
    {
        var token = new string('c', UpdateApplyContract.ReadyTokenHexLength);
        var eventName = UpdateApplyContract.CreateReadyEventName(token);

        Assert.True(UpdateApplyContract.IsValidReadyEventName(eventName));
        Assert.False(UpdateApplyContract.IsValidReadyEventName(@"Local\Wisp.Update.Ready.shared"));
        Assert.False(UpdateApplyContract.IsValidReadyEventName(
            UpdateApplyContract.ReadyEventPrefix + token.ToUpperInvariant()));
    }
}
