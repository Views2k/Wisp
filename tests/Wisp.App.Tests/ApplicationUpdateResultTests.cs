using System.Text;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateResultTests
{
    [Fact]
    public void AtomicallyConsumesInstalledResultAndReportsValidatedVersion()
    {
        using var layout = new ResultLayout(
            """
            {"schemaVersion":1,"state":"installed","sourceVersion":"1.0.0","targetVersion":"1.2.3","errorCode":"","message":"The update was installed successfully.","recordedAtUtc":"2026-08-30T12:00:00Z"}
            """);

        var consumed = ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result);

        Assert.True(consumed);
        Assert.Equal("Wisp 1.2.3 was installed successfully.", result.Status);
        Assert.Equal("Check again", result.Action);
        Assert.False(File.Exists(layout.ResultPath));
        Assert.Empty(Directory.EnumerateFiles(layout.ResultDirectory, "*.consumed"));
    }

    [Fact]
    public void FailureResultUsesFixedUiCopyInsteadOfRecordMessage()
    {
        using var layout = new ResultLayout(
            """
            {"schemaVersion":1,"state":"failed","sourceVersion":"1.0.0","targetVersion":"1.2.3","errorCode":"UPDATE_INSTALLER_EXIT_FAILED","message":"untrusted text","recoveryState":"restarted","recordedAtUtc":"2026-08-30T12:00:00Z"}
            """);

        var consumed = ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result);

        Assert.True(consumed);
        Assert.DoesNotContain("untrusted", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "The previous update did not complete. Wisp restarted the verified installation.",
            result.Status);
        Assert.False(File.Exists(layout.ResultPath));
    }

    [Theory]
    [InlineData("application-still-running", "Wisp remained open")]
    [InlineData("deferred", "may still be closing")]
    [InlineData("no-verified-installation", "could not be verified")]
    [InlineData("restart-failed", "could not be restarted")]
    public void FailureResultReportsTheRecordedRecoveryOutcome(string recoveryState, string expectedText)
    {
        using var layout = new ResultLayout(
            $$"""
            {"schemaVersion":1,"state":"failed","sourceVersion":"1.0.0","targetVersion":"1.2.3","errorCode":"UPDATE_FAILURE","message":"ignored","recoveryState":"{{recoveryState}}","recordedAtUtc":"2026-08-30T12:00:00Z"}
            """);

        var consumed = ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result);

        Assert.True(consumed);
        Assert.Contains(expectedText, result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidResultIsConsumedWithoutBeingDisplayed()
    {
        using var layout = new ResultLayout("{\"schemaVersion\":99,\"state\":\"installed\"}");

        var consumed = ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out _);

        Assert.False(consumed);
        Assert.False(File.Exists(layout.ResultPath));
    }

    private sealed class ResultLayout : IDisposable
    {
        internal ResultLayout(string json)
        {
            Root = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
            ResultDirectory = Path.Combine(Root, "Wisp");
            ResultPath = Path.Combine(ResultDirectory, "update-result.json");
            Directory.CreateDirectory(ResultDirectory);
            File.WriteAllText(ResultPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal string Root { get; }
        internal string ResultDirectory { get; }
        internal string ResultPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
