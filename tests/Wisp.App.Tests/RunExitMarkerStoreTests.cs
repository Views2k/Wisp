using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.App.CrashDiagnostics;
using Xunit;
using NativeFile = Wisp.App.Clips.ClipBufferStore.NativeFile;

namespace Wisp.App.Tests;

public sealed class RunExitMarkerStoreTests
{
    [Fact]
    public void HeldHandleRenameKeepsNoReplacementDefaultAndRequiresExplicitFixedTargetReplacement()
    {
        using var directory = new TemporaryDirectory();
        var markerPath = Path.Combine(directory.Path, "running.json");
        File.WriteAllText(markerPath, "original");
        File.WriteAllText(Path.Combine(directory.Path, "temporary.tmp"), "replacement");
        using (var parent = NativeFile.OpenDirectoryTree(directory.Path, create: false))
        using (var file = NativeFile.Open(parent, "temporary.tmp", NativeFile.ReadWriteDelete, 0, 1, false))
        {
            Assert.Throws<IOException>(() => NativeFile.RenameNew(file, parent, "running.json"));
            Assert.Equal("original", File.ReadAllText(markerPath));
            Assert.Throws<IOException>(() => NativeFile.RenameNew(file, parent, "../outside.json", replaceExisting: true));
            NativeFile.RenameNew(file, parent, "running.json", replaceExisting: true);
        }
        Assert.Equal("replacement", File.ReadAllText(markerPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, "temporary.tmp")));
    }

    [Fact]
    public void CleanShutdownIsNotRecoveredButUnfinishedShutdownIs()
    {
        using var directory = new TemporaryDirectory();
        var first = Marker(0);
        using (var run = RunExitMarkerStore.TryBegin(directory.Path, first))
        { Assert.NotNull(run); Assert.Null(run.Previous); Assert.True(run.MarkClean()); }
        var second = Marker(1);
        using (var run = RunExitMarkerStore.TryBegin(directory.Path, second))
        { Assert.NotNull(run); Assert.Null(run.Previous); }
        using var third = RunExitMarkerStore.TryBegin(directory.Path, Marker(2));
        Assert.NotNull(third); Assert.Equal(second.RunId, third.Previous!.RunId);
        Assert.True(third.MarkClean());
    }

    [Fact]
    public void LiveLeasePreventsAnotherProcessOrWindowsSessionFromReplacingTheMarker()
    {
        using var directory = new TemporaryDirectory();
        var first = Marker(0);
        using var owner = RunExitMarkerStore.TryBegin(directory.Path, first);
        Assert.NotNull(owner);
        Assert.Null(RunExitMarkerStore.TryBegin(directory.Path, Marker(1) with { ProcessId = 43 }));
        Assert.Equal(first.RunId, Read(directory).RunId);
        Assert.True(owner.MarkClean());
    }

    [Fact]
    public void StaleOwnerCannotCleanANewerMarker()
    {
        using var directory = new TemporaryDirectory();
        using var owner = RunExitMarkerStore.TryBegin(directory.Path, Marker(0));
        Assert.NotNull(owner);
        var newer = Marker(1);
        File.WriteAllBytes(Path.Combine(directory.Path, "running.json"), JsonSerializer.SerializeToUtf8Bytes(newer));
        Assert.False(owner.MarkClean());
        Assert.Equal(newer.RunId, Read(directory).RunId); Assert.False(Read(directory).CleanExit);
    }

    [Fact]
    public void FailedMarkerCommitReleasesItsLeaseAndDoesNotTouchTheConflictingDirectory()
    {
        using var directory = new TemporaryDirectory();
        var conflict = Path.Combine(directory.Path, "running.json");
        Directory.CreateDirectory(conflict);
        Assert.Null(RunExitMarkerStore.TryBegin(directory.Path, Marker(0)));
        Assert.True(Directory.Exists(conflict));
        Directory.Delete(conflict);
        using var next = RunExitMarkerStore.TryBegin(directory.Path, Marker(1));
        Assert.NotNull(next); Assert.Null(next.Previous);
    }

    [Fact]
    public void OversizedOrInvalidMarkerCannotSupplyPreviousBuildOrEventIdentity()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "running.json"), new string('x', 8193));
        using (var owner = RunExitMarkerStore.TryBegin(directory.Path, Marker(0)))
        { Assert.NotNull(owner); Assert.Null(owner.Previous); }
        var invalid = Marker(0) with { ProcessCreationFileTime = 1 };
        File.WriteAllBytes(Path.Combine(directory.Path, "running.json"), JsonSerializer.SerializeToUtf8Bytes(invalid));
        using var next = RunExitMarkerStore.TryBegin(directory.Path, Marker(1));
        Assert.NotNull(next); Assert.Null(next.Previous);
    }

    [Fact]
    public void ReplacingAHardLinkedMarkerPreservesTheOtherFilesContents()
    {
        using var directory = new TemporaryDirectory();
        var original = Path.Combine(directory.Path, "unrelated.txt");
        File.WriteAllText(original, "PRIVATE_UNRELATED_CONTENT");
        Assert.True(CreateHardLink(Path.Combine(directory.Path, "running.json"), original, IntPtr.Zero));
        using var owner = RunExitMarkerStore.TryBegin(directory.Path, Marker(0));
        Assert.NotNull(owner); Assert.Null(owner.Previous);
        Assert.Equal("PRIVATE_UNRELATED_CONTENT", File.ReadAllText(original));
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(Read(directory)), StringComparison.Ordinal);
    }

    private static RunExitMarker Marker(int minutes)
    {
        var started = WindowsFaultEventReaderTests.Started.AddMinutes(minutes);
        return WindowsFaultEventReaderTests.Marker() with { StartedAtUtc = started, ProcessCreationFileTime = started.UtcDateTime.ToFileTimeUtc() };
    }
    private static RunExitMarker Read(TemporaryDirectory directory) =>
        JsonSerializer.Deserialize<RunExitMarker>(File.ReadAllBytes(Path.Combine(directory.Path, "running.json")))!;
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WispRunMarkerTests", Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
