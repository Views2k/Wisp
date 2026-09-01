using System.Text;
using Xunit;

namespace Wisp.Updater.Tests;

public sealed class CommandAndErrorTests
{
    [Fact]
    public void ReadySignalAcknowledgesOnlyTheNamedPerAttemptEvent()
    {
        var eventName = Wisp.Update.UpdateApplyContract.CreateReadyEventName(
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant());
        using var readyEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventName,
            out var createdNew);
        Assert.True(createdNew);

        new WindowsUpdateReadySignal().Signal(eventName);

        Assert.True(readyEvent.WaitOne(TimeSpan.Zero));
    }

    [Fact]
    public void CommandLineRequiresApplySwitchAndSingleRequestPath()
    {
        Assert.Equal("C:\\stage\\apply.json", CommandLine.ParseApplyRequestPath(
            ["--apply", "C:\\stage\\apply.json"]));

        var missingSwitch = Assert.Throws<UpdateFailureException>(
            () => CommandLine.ParseApplyRequestPath(["C:\\stage\\apply.json"]));
        var extraArgument = Assert.Throws<UpdateFailureException>(
            () => CommandLine.ParseApplyRequestPath(["--apply", "C:\\stage\\apply.json", "/ALLUSERS"]));

        Assert.Equal("UPDATE_ARGUMENTS", missingSwitch.ErrorCode);
        Assert.Equal("UPDATE_ARGUMENTS", extraArgument.ErrorCode);
    }

    [Fact]
    public void ResultFileIsAtomicLocalConciseAndHasNoUtf8Bom()
    {
        var root = Path.Combine(Path.GetTempPath(), "Wisp.Updater.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var resultFile = new UpdateResultFile(root);

            resultFile.TryWriteFailure(
                new StableVersion(1, 0, 0),
                new StableVersion(1, 2, 3),
                "UPDATE_INSTALLER_HASH_MISMATCH",
                "The staged installer did not match metadata.",
                UpdateRecoveryState.Restarted);

            Assert.Equal(Path.Combine(root, "Wisp", "update-result.json"), resultFile.ResultPath);
            var bytes = File.ReadAllBytes(resultFile.ResultPath);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            var text = File.ReadAllText(resultFile.ResultPath);
            Assert.Contains("UPDATE_INSTALLER_HASH_MISMATCH", text, StringComparison.Ordinal);
            Assert.Contains("\"recoveryState\":\"restarted\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain(root, text, StringComparison.OrdinalIgnoreCase);

            resultFile.TryWriteSuccess(new StableVersion(1, 0, 0), new StableVersion(1, 2, 3));

            var installedText = File.ReadAllText(resultFile.ResultPath);
            Assert.Contains("\"state\":\"installed\"", installedText, StringComparison.Ordinal);
            Assert.DoesNotContain("UPDATE_INSTALLER_HASH_MISMATCH", installedText, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(resultFile.ResultPath)!,
                "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
