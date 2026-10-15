using System.Text;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateResultTests
{
    [Theory]
    [InlineData("UPDATE_INSTALLER_EXIT_FAILED", "restarted", 5u, "update-installer-exit-failed")]
    [InlineData("UPDATE_INSTALLER_HASH_MISMATCH", "application-still-running", null, "update-installer-hash-mismatch")]
    [InlineData("private-path-or-message", "untrusted-state", null, "update-unexpected-failure")]
    public void FailedHelperResultRetainsBoundedEvidence(string code, string recovery, uint? exit, string expected)
    {
        using var layout = new ResultLayout(System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            state = "failed",
            errorCode = code,
            recoveryState = recovery,
            installerExitCode = exit,
            nativeErrorCode = 32,
            message = "sensitive content"
        }));
        Assert.True(ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result));
        var incident = Assert.IsType<Supplementary.SupplementaryIncident>(result.Incident);
        Assert.Equal(expected, incident.Reason);
        Assert.Equal(32u, incident.Code);
        Assert.Equal(exit, incident.Update?.InstallerExitCode);
        Assert.Equal(recovery == "untrusted-state" ? "unknown" : recovery, incident.Update?.Recovery);
        Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(incident));
        Assert.DoesNotContain("sensitive", System.Text.Json.JsonSerializer.Serialize(incident));
        Assert.DoesNotContain("private-path", System.Text.Json.JsonSerializer.Serialize(incident));
        Assert.False(ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out _));
    }

    [Fact]
    public void InvalidOptionalCodesCannotBreakResultConsumption()
    {
        using var layout = new ResultLayout("""{"schemaVersion":1,"state":"failed","nativeErrorCode":"private","installerExitCode":-1}""");
        Assert.True(ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result));
        Assert.Null(result.Incident?.Code);
        Assert.Null(result.Incident?.Update?.InstallerExitCode);
        Assert.True(Supplementary.SupplementaryIncidentSchema.Valid(result.Incident!));
    }

    [Fact]
    public void RecoveryOutcomeDoesNotSplitTheFailureButInstallerExitCodeDoes()
    {
        var one = Supplementary.SupplementaryIncidentSchema.Create("update", "update-installer-exit-failed", "update-installed", update: new("restarted", 5));
        var recoveredDifferently = one with { Update = new("deferred", 5) };
        var differentExit = one with { Update = new("restarted", 2) };
        Assert.Equal(one.Signature, Supplementary.SupplementaryIncidentSchema.Signature(recoveredDifferently));
        Assert.NotEqual(one.Signature, Supplementary.SupplementaryIncidentSchema.Signature(differentExit));
        Assert.False(Supplementary.SupplementaryIncidentSchema.Valid(one with { Category = "recorder" }));
    }

    [Fact]
    public void AtomicallyConsumesInstalledResultAndReportsValidatedVersion()
    {
        using var layout = new ResultLayout(
            """
            {"schemaVersion":1,"state":"installed","sourceVersion":"1.0.0","targetVersion":"1.2.3","errorCode":"","message":"The update was installed successfully.","recordedAtUtc":"2026-08-30T12:00:00Z"}
            """);

        var consumed = ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result);

        Assert.True(consumed);
        Assert.DoesNotContain("successfully", result.Status, StringComparison.Ordinal);
        Assert.Equal("1.2.3", result.TargetVersion);
        Assert.Equal("1.0.0", result.SourceVersion);
        Assert.Equal("Check again", result.Action);
        Assert.Equal("unknown", result.Outcome); // The recorded target is not this running build.
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
        Assert.Equal("failure", result.Outcome);
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
    public void MatchingRunningBuildConfirmsInstalledOutcome()
    {
        using var layout = new ResultLayout($$"""{"schemaVersion":1,"state":"installed","targetVersion":"{{ApplicationVersionInfo.MachineVersion}}"}""");
        Assert.True(ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out var result));
        Assert.Equal("success", result.Outcome);
        Assert.False(ApplicationUpdateLauncher.TryConsumeResult(layout.Root, out _));
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
