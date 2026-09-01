using System.IO;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationUpdateStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Wisp.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PruneRetainsPendingAttemptAndIgnoresUnexpectedEntries()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var retained = CreateAttempt("1.2.3", now.AddDays(-10), 40);
        var expired = CreateAttempt("1.2.3", now.AddDays(-30), 20);
        var excessOld = CreateAttempt("1.2.3", now.AddDays(-3), 30);
        var excessNew = CreateAttempt("1.2.3", now.AddDays(-2), 30);

        var unexpectedVersion = Path.Combine(_root, "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unexpectedVersion);
        File.WriteAllText(Path.Combine(unexpectedVersion, "keep.txt"), "keep");

        var unexpectedAttempt = Path.Combine(_root, "1.2.3", "not-an-attempt");
        Directory.CreateDirectory(unexpectedAttempt);
        File.WriteAllText(Path.Combine(unexpectedAttempt, "keep.txt"), "keep");

        var unexpectedContents = Path.Combine(_root, "1.2.3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unexpectedContents);
        File.WriteAllText(Path.Combine(unexpectedContents, "notes.txt"), "keep");
        SetLastWrite(unexpectedContents, now.AddDays(-40));

        ApplicationUpdateStaging.PruneRoot(
            _root,
            retained,
            now,
            TimeSpan.FromDays(14),
            maximumAttempts: 2,
            maximumBytes: 100);

        Assert.True(Directory.Exists(retained));
        Assert.False(Directory.Exists(expired));
        Assert.False(Directory.Exists(excessOld));
        Assert.True(Directory.Exists(excessNew));
        Assert.True(Directory.Exists(unexpectedVersion));
        Assert.True(Directory.Exists(unexpectedAttempt));
        Assert.True(Directory.Exists(unexpectedContents));
        Assert.True(File.Exists(Path.Combine(unexpectedContents, "notes.txt")));
    }

    [Fact]
    public void PruneEnforcesTotalByteCapOldestFirst()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var oldest = CreateAttempt("2.0.0", now.AddHours(-3), 60);
        var middle = CreateAttempt("2.0.0", now.AddHours(-2), 60);
        var newest = CreateAttempt("2.0.0", now.AddHours(-1), 60);

        ApplicationUpdateStaging.PruneRoot(
            _root,
            retainedAttemptDirectory: null,
            now,
            TimeSpan.FromDays(14),
            maximumAttempts: 10,
            maximumBytes: 100);

        Assert.False(Directory.Exists(oldest));
        Assert.False(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public void PruneRemovesExpectedPartialAttemptButNotUnknownFile()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var safeAttempt = CreateAttemptDirectory("3.4.5");
        var partialName = $".Wisp-Setup-3.4.5.exe.{Guid.NewGuid():N}.partial";
        File.WriteAllBytes(Path.Combine(safeAttempt, partialName), [1, 2, 3]);
        SetLastWrite(safeAttempt, now.AddDays(-1));

        var unknownAttempt = CreateAttemptDirectory("3.4.5");
        File.WriteAllText(Path.Combine(unknownAttempt, "do-not-delete.txt"), "keep");
        SetLastWrite(unknownAttempt, now.AddDays(-1));

        ApplicationUpdateStaging.PruneRoot(
            _root,
            retainedAttemptDirectory: null,
            now,
            TimeSpan.Zero,
            maximumAttempts: 1,
            maximumBytes: 0);

        Assert.False(Directory.Exists(safeAttempt));
        Assert.True(Directory.Exists(unknownAttempt));
        Assert.True(File.Exists(Path.Combine(unknownAttempt, "do-not-delete.txt")));
    }

    [Fact]
    public void InvalidRetainedPathFailsClosed()
    {
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var attempt = CreateAttempt("4.0.0", now.AddDays(-30), 10);
        var invalidRetainedPath = Path.Combine(_root, "..", "outside");

        ApplicationUpdateStaging.PruneRoot(
            _root,
            invalidRetainedPath,
            now,
            TimeSpan.Zero,
            maximumAttempts: 1,
            maximumBytes: 0);

        Assert.True(Directory.Exists(attempt));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateAttempt(string version, DateTimeOffset lastWrite, int bytes)
    {
        var attempt = CreateAttemptDirectory(version);
        var installer = Path.Combine(attempt, $"Wisp-Setup-{version}.exe");
        File.WriteAllBytes(installer, new byte[bytes]);
        SetLastWrite(attempt, lastWrite);
        return attempt;
    }

    private string CreateAttemptDirectory(string version)
    {
        var attempt = Path.Combine(_root, version, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        return attempt;
    }

    private static void SetLastWrite(string attempt, DateTimeOffset value)
    {
        foreach (var file in Directory.GetFiles(attempt))
        {
            File.SetLastWriteTimeUtc(file, value.UtcDateTime);
        }
        Directory.SetLastWriteTimeUtc(attempt, value.UtcDateTime);
    }
}
