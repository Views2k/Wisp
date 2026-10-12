using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.App.CrashDiagnostics;
using Wisp.App.DebugLogging;
using Wisp.App.Supplementary;
using Xunit;
using NativeFile = Wisp.App.Clips.ClipBufferStore.NativeFile;

namespace Wisp.App.Tests;

public sealed class RunExitMarkerStoreTests
{
    [Fact]
    public void ExceptionBeforeMarkerCreationKeepsTheSameOriginatingRun()
    {
        using var directory = new TemporaryDirectory();
        var marker = Marker(0);
        CrashReport? saved = null;
        var monitor = new CrashExceptionMonitor(new EarlyCrashSink(report => saved = report), runId: () => marker.RunId);
        monitor.UiException(new IOException());
        Assert.Equal(marker.RunId, saved!.RunId);
        using var owner = RunExitMarkerStore.TryBegin(directory.Path, marker);
        Assert.NotNull(owner);
        Assert.Equal(saved.RunId, owner.RunId);
        Assert.NotNull(saved.Sanitize());
    }

    private sealed class EarlyCrashSink(Action<CrashReport> save) : ICrashReportSink
    {
        public bool TrySave(CrashReport report) { save(report); return true; }
    }

    [Fact]
    public void ShutdownIntentSurvivesCheckpointsAndRestartWithoutClaimingCompletedShutdown()
    {
        using var directory = new TemporaryDirectory();
        var marker = Marker(0);
        using (var owner = RunExitMarkerStore.TryBegin(directory.Path, marker))
        {
            Assert.False(owner!.MarkShutdownStarted(ShutdownIntent.None));
            Assert.True(owner.MarkShutdownStarted(ShutdownIntent.Update));
            Assert.True(owner.MarkShutdownStarted(ShutdownIntent.Requested));
            Assert.True(owner.Checkpoint(new(marker.StartedAtUtc.AddSeconds(10),
                [new() { TimestampUtc = marker.StartedAtUtc.AddSeconds(8) }], [])));
        }
        using var next = RunExitMarkerStore.TryBegin(directory.Path, Marker(1));
        Assert.Equal(ShutdownIntent.Update, next!.Previous!.ShutdownIntent);
        Assert.False(next.Previous.CleanExit);
        Assert.Single(next.Previous.Context!.Samples);
        File.WriteAllBytes(Path.Combine(directory.Path, "running.json"), JsonSerializer.SerializeToUtf8Bytes(Marker(2)));
        Assert.False(next.MarkShutdownStarted(ShutdownIntent.Requested));
    }

    [Fact]
    public void CheckpointSurvivesRestartWithBoundedSamplesAndOriginalAges()
    {
        using var directory = new TemporaryDirectory();
        var first = Marker(0);
        var captured = first.StartedAtUtc.AddSeconds(10);
        var snapshot = new HealthContextSnapshot(captured,
            [new() { TimestampUtc = captured.AddSeconds(-8) }, new() { TimestampUtc = captured.AddSeconds(-6) },
             new() { TimestampUtc = captured.AddSeconds(-4), CpuPercent = 25 }, new() { TimestampUtc = captured.AddSeconds(-2), CpuPercent = 30, GameVerification = NativeGameVerification.Steam }], []);
        using (var owner = RunExitMarkerStore.TryBegin(directory.Path, first))
        {
            Assert.NotNull(owner);
            Assert.True(owner.Checkpoint(snapshot));
            Assert.False(owner.Checkpoint(snapshot));
        }
        using var next = RunExitMarkerStore.TryBegin(directory.Path, Marker(2));
        var previous = Assert.IsType<RunExitMarker>(next!.Previous);
        Assert.Equal(3, previous.Context!.Samples.Length);
        Assert.Equal(NativeGameVerification.Steam, previous.Context.Samples[^1].GameVerification);
        Assert.Equal(captured, previous.Context.CapturedAtUtc);
        var report = previous.ToReport(next.StartedAtUtc) with
        { RecoveredExit = new(first.StartedAtUtc, next.StartedAtUtc, WindowsFaultLookupStatus.NoMatchingEvent, null) };
        var incident = SupplementaryIncidentCapture.Crash(report);
        Assert.True(SupplementaryIncidentSchema.Valid(incident));
        Assert.Equal(116000, incident.Context!.Value[0].AgeMs);
        Assert.Equal(112000, incident.Context.Value[^1].AgeMs);
        Assert.Equal(30, incident.Context.Value[^1].CpuPercent);
    }

    [Fact]
    public void CheckpointCannotUndoCleanShutdownOrReplaceNewerOwner()
    {
        using var directory = new TemporaryDirectory();
        var marker = Marker(0);
        var snapshot = new HealthContextSnapshot(marker.StartedAtUtc.AddSeconds(10),
            [new() { TimestampUtc = marker.StartedAtUtc.AddSeconds(8) }], []);
        using (var owner = RunExitMarkerStore.TryBegin(directory.Path, marker))
        {
            Assert.True(owner!.Checkpoint(snapshot));
            Assert.True(owner.MarkClean());
            Assert.False(owner.Checkpoint(snapshot with { CapturedAtUtc = snapshot.CapturedAtUtc.AddSeconds(10) }));
            Assert.True(Read(directory).CleanExit);
        }
        using var next = RunExitMarkerStore.TryBegin(directory.Path, Marker(1));
        Assert.Null(next!.Previous);
        File.WriteAllBytes(Path.Combine(directory.Path, "running.json"), JsonSerializer.SerializeToUtf8Bytes(Marker(2)));
        Assert.False(next.Checkpoint(new(next.StartedAtUtc.AddSeconds(10),
            [new() { TimestampUtc = next.StartedAtUtc.AddSeconds(8) }], [])));
    }

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
