using System.ComponentModel;
using System.Security.Cryptography;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Wisp.App.Tests",
        Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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
