using System.IO;
using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class FileLockHoldersTests
{
    [Fact]
    public void OnlySharingAndLockViolationsAskWhoHoldsTheFile()
    {
        Assert.True(FileLockHolders.IsLockFailure(new IOException("in use") { HResult = FileLockHolders.SharingViolation }));
        Assert.True(FileLockHolders.IsLockFailure(new IOException("locked") { HResult = FileLockHolders.LockViolation }));
        Assert.False(FileLockHolders.IsLockFailure(new IOException("other") { HResult = unchecked((int)0x80070002) }));
        Assert.False(FileLockHolders.IsLockFailure(new UnauthorizedAccessException()));
        Assert.False(FileLockHolders.IsLockFailure(null));
    }

    [Theory]
    [InlineData("MsMpEng", "MsMpEng")]
    [InlineData("  backup-agent.exe ", "backup-agent.exe")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    [InlineData("C:\\PRIVATE\\tool", "unknown")]
    [InlineData("naïve", "unknown")]
    public void ProcessNamesAreKeptOnlyWhenTheyAreSimpleAscii(string? name, string expected)
    {
        Assert.Equal(expected, FileLockHolders.SafeName(name));
    }

    [Fact]
    public void TheNoteNamesTheHoldersOrSaysUnknown()
    {
        Assert.Equal("File held by: unknown", FileLockHolders.Note([]));
        Assert.Equal("File held by: MsMpEng, Backup", FileLockHolders.Note(["MsMpEng", "Backup"]));
        Assert.Equal(33, FileLockHolders.SafeName(new string('a', 40)).Length + 1);
    }
}
