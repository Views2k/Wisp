using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.App.Supplementary;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Wisp.App.Tests",
        Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset LaunchedAt = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private string ResultPath => Path.Combine(_root, "Wisp", "update-result.json");

    [Fact]
    public void InvalidRequestEntryReleasesHandoffWithoutDeletingTheEntry()
    {
        Directory.CreateDirectory(_root);
        var helperPath = Path.Combine(_root, "Wisp.Updater.exe");
        File.WriteAllText(helperPath, "not executed");
        var requestPath = Path.Combine(_root, "apply-request.json");
        Directory.CreateDirectory(requestPath);
        File.WriteAllText(Path.Combine(requestPath, "keep.txt"), "keep");
        var request = CreateRequest();

        Assert.Throws<IOException>(() => ApplicationUpdateLauncher.StartHelper(helperPath, request));

        AssertEventClosed(request.ReadyEventName);
        Assert.True(File.Exists(Path.Combine(requestPath, "keep.txt")));
        Assert.False(File.Exists(helperPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void FailedHelperStartReleasesHandoffAndRemovesItsRequest()
    {
        Directory.CreateDirectory(_root);
        var helperPath = Path.Combine(_root, "missing-updater.exe");
        var request = CreateRequest();

        Assert.Throws<Win32Exception>(() => ApplicationUpdateLauncher.StartHelper(helperPath, request));

        AssertEventClosed(request.ReadyEventName);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void FailureMessageDoesNotExposeExceptionContent()
    {
        var error = ApplicationUpdateLauncher.DescribeStartFailure(new IOException("private-path-marker"));

        Assert.DoesNotContain("private-path-marker", error);
        Assert.Contains("full installer from wispoverlay.com", error);
        Assert.Contains("Wisp stayed open and no files were installed", error);
        Assert.DoesNotContain("missing a usable update helper", error);
    }

    [Fact]
    public void MissingHelperFailureStopsAutomaticHandoffWhileOtherFailuresRetry()
    {
        var missingHelper = new ApplicationUpdateHelperUnavailableException();
        Assert.True(ApplicationUpdateLauncher.IsHelperUnavailable(missingHelper));
        Assert.False(ApplicationUpdateLauncher.IsHelperUnavailable(new IOException("other")));
        Assert.Contains("missing a usable update helper", ApplicationUpdateLauncher.DescribeStartFailure(missingHelper));

        Assert.Equal(AutomaticUpdateApplyOutcome.Unavailable,
            new ApplicationUpdateStartResult(false, "error", HelperUnavailable: true).Outcome);
        Assert.Equal(AutomaticUpdateApplyOutcome.Unavailable,
            new ApplicationUpdateStartResult(false, "error", HelperRejected: true).Outcome);
        Assert.Equal(AutomaticUpdateApplyOutcome.Retry, new ApplicationUpdateStartResult(false, "error").Outcome);
        Assert.Equal(AutomaticUpdateApplyOutcome.Started, new ApplicationUpdateStartResult(true, string.Empty).Outcome);
        ApplicationUpdateStartResult deferred = (false, "Update ready. Wisp will update and restart after you close Forza.");
        Assert.Equal(AutomaticUpdateApplyOutcome.Retry, deferred.Outcome);
    }

    [Theory]
    [InlineData(2, "UPDATE_INSTALL_REGISTRATION", "update-install-registration", true, 5u)]
    [InlineData(2, "UPDATE_APPLICATION_PATH", "update-application-path", true, null)]
    [InlineData(2, "UPDATE_INSTALLER_PATH", "update-installer-path", true, null)]
    [InlineData(6, "UPDATE_INSTALLED_APP_INVALID", "update-installed-app-invalid", true, null)]
    [InlineData(4, "UPDATE_INSTALLER_HASH_MISMATCH", "update-installer-hash-mismatch", false, null)]
    [InlineData(3, "UPDATE_PARENT_IDENTITY", "update-parent-identity", false, 5u)]
    [InlineData(1, "not-a-catalog-code", "update-unexpected-failure", false, null)]
    public void HelperExitRecordNamesTheFailedCheckAndStopsTheSessionWhenItCannotChange(
        int exitCode, string errorCode, string reason, bool permanent, uint? native)
    {
        // Wisp 2.6.12 saw the helper exit with UPDATE_INSTALL_REGISTRATION before it was ready, every
        // two minutes for a day, each reported as "update-verification-failed" and retried although
        // the registered location cannot change within the session.
        WriteResult(new
        {
            schemaVersion = 1,
            state = "failed",
            sourceVersion = "2.6.12",
            targetVersion = "2.6.13",
            errorCode,
            message = "PRIVATE_SENTINEL",
            recoveryState = "application-still-running",
            nativeErrorCode = native,
            recordedAtUtc = LaunchedAt.AddSeconds(3)
        });

        var exit = ApplicationUpdateLauncher.DescribeHelperExit(exitCode, LaunchedAt, _root, LaunchedAt.AddSeconds(20));

        Assert.Equal(reason, exit.Incident.Reason);
        Assert.Equal("update-install-handoff", exit.Incident.FailureStage);
        Assert.Equal(native, exit.Incident.Code);
        Assert.Equal("application-still-running", exit.Incident.Update?.Recovery);
        Assert.True(SupplementaryIncidentSchema.Valid(exit.Incident));
        Assert.DoesNotContain("PRIVATE_SENTINEL", JsonSerializer.Serialize(exit.Incident));
        Assert.DoesNotContain("PRIVATE_SENTINEL", exit.Status);
        Assert.Equal(permanent, exit.Permanent);
        Assert.Contains("wispoverlay.com", exit.Status);
        Assert.Contains("Wisp stayed open and no files were installed", exit.Status);
        if (reason == "update-install-registration")
        {
            Assert.Contains("registered in Windows", exit.Status);
            Assert.Contains("reinstall", exit.Status);
        }
        // The record was consumed: the next launch does not report the same failure again.
        Assert.False(File.Exists(ResultPath));
        Assert.False(ApplicationUpdateLauncher.TryConsumeResult(_root, out _));
    }

    [Fact]
    public void HelperExitWithoutARecordReportsTheExitCodeAndStopsOnlyForARejectedRequest()
    {
        var rejected = ApplicationUpdateLauncher.DescribeHelperExit(2, LaunchedAt, _root, LaunchedAt.AddSeconds(2));
        Assert.Equal("update-unexpected-failure", rejected.Incident.Reason);
        Assert.Equal("update-install-handoff", rejected.Incident.FailureStage);
        Assert.Equal(2u, rejected.Incident.Code);
        Assert.Null(rejected.Incident.Update);
        Assert.True(SupplementaryIncidentSchema.Valid(rejected.Incident));
        Assert.True(rejected.Permanent);
        Assert.Contains("wispoverlay.com", rejected.Status);

        var crashed = ApplicationUpdateLauncher.DescribeHelperExit(unchecked((int)0xC0000005), LaunchedAt, _root, LaunchedAt.AddSeconds(2));
        Assert.Equal(0xC0000005u, crashed.Incident.Code);
        Assert.False(crashed.Permanent);
        Assert.True(SupplementaryIncidentSchema.Valid(crashed.Incident));

        var unknown = ApplicationUpdateLauncher.DescribeHelperExit(null, LaunchedAt, _root, LaunchedAt);
        Assert.Null(unknown.Incident.Code);
        Assert.False(unknown.Permanent);
    }

    [Fact]
    public void StaleHelperRecordIsLeftForTheNextLaunch()
    {
        WriteResult(new
        {
            schemaVersion = 1,
            state = "failed",
            errorCode = "UPDATE_INSTALL_REGISTRATION",
            recoveryState = "restarted",
            recordedAtUtc = LaunchedAt.AddHours(-2)
        });

        var exit = ApplicationUpdateLauncher.DescribeHelperExit(1, LaunchedAt, _root, LaunchedAt.AddSeconds(2));

        Assert.Equal("update-unexpected-failure", exit.Incident.Reason);
        Assert.Equal(1u, exit.Incident.Code);
        Assert.False(exit.Permanent);
        Assert.True(File.Exists(ResultPath));
        Assert.True(ApplicationUpdateLauncher.TryConsumeResult(_root, out var next));
        Assert.Equal("update-install-registration", next.Incident?.Reason);
        Assert.Equal("update-installed", next.Incident?.FailureStage);
        Assert.Equal("restarted", next.Incident?.Update?.Recovery);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"schemaVersion\":\"1\",\"state\":\"failed\",\"errorCode\":\"UPDATE_INSTALL_REGISTRATION\"}")]
    public void UnreadableHelperRecordDoesNotBreakTheReport(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
        File.WriteAllText(ResultPath, content);

        var exit = ApplicationUpdateLauncher.DescribeHelperExit(3, LaunchedAt, _root, LaunchedAt.AddSeconds(2));

        Assert.Equal("update-unexpected-failure", exit.Incident.Reason);
        Assert.False(exit.Permanent);
        Assert.True(SupplementaryIncidentSchema.Valid(exit.Incident));
        Assert.True(File.Exists(ResultPath));
        Assert.False(ApplicationUpdateLauncher.TryConsumeResult(_root, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteResult(object record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
        File.WriteAllText(ResultPath, JsonSerializer.Serialize(record), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private UpdateApplyRequest CreateRequest() => new(
        Path.Combine(_root, "Wisp-Setup-9.9.9.exe"),
        "9.9.9",
        "1.0.0",
        Environment.ProcessId,
        Path.Combine(_root, "Wisp.exe"),
        new string('0', 64),
        1,
        UpdateApplyContract.CreateReadyEventName(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()));

    private static void AssertEventClosed(string eventName)
    {
        var open = EventWaitHandle.TryOpenExisting(eventName, out var handle);
        handle?.Dispose();
        Assert.False(open);
    }
}
