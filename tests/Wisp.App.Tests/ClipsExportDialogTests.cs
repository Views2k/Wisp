using System.IO;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ClipsExportDialogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wisp-clip-export-dialog-" + Guid.NewGuid().ToString("N"));
    private static readonly ClipEntry Clip = new(Guid.Parse("baea6f2c-8d2a-4e2f-a0bc-3c1f73f7c900"),
        new DateTimeOffset(2026, 10, 1, 16, 23, 45, TimeSpan.FromHours(2)), new(60, 1080, 60, 75),
        new(128, 1920, 1080, 60, 0, 600_000_000, false));

    [Fact]
    public void SaveAsUsesSelectedFolderAndStableMp4NameWithoutOverwrite()
    {
        Directory.CreateDirectory(_directory);
        var dialog = ClipsPage.CreateExportDialog(_directory, Clip);
        Assert.Equal(_directory, dialog.InitialDirectory);
        Assert.Equal("Wisp-20261001-142345-baea6f2c8d2a4e2fa0bc3c1f73f7c900.mp4", dialog.FileName);
        Assert.Equal("MP4 video (*.mp4)|*.mp4", dialog.Filter);
        Assert.Equal("mp4", dialog.DefaultExt);
        Assert.True(dialog.AddExtension);
        Assert.True(dialog.CheckPathExists);
        Assert.False(dialog.OverwritePrompt);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void MissingExportFolderFallsBackToVideosWithoutCreatingIt()
    {
        var dialog = ClipsPage.CreateExportDialog(_directory, Clip);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), dialog.InitialDirectory);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("clip.mkv")]
    [InlineData("clip.mp4.txt")]
    public void WrongExtensionIsRejectedBeforeExport(string filename)
    {
        Assert.Equal("Use .mp4 for the exported video.", ClipsPage.ValidateExportDestination(Path.Combine(_directory, filename)));
    }

    [Fact]
    public void ExistingDestinationIsPreservedAndNewMp4DestinationIsAllowed()
    {
        Directory.CreateDirectory(_directory);
        var occupied = Path.Combine(_directory, "keep.mp4");
        File.WriteAllText(occupied, "existing export");
        Assert.NotNull(ClipsPage.ValidateExportDestination(occupied));
        Assert.Equal("existing export", File.ReadAllText(occupied));
        var available = Path.Combine(_directory, "chosen-name.MP4");
        Assert.Null(ClipsPage.ValidateExportDestination(available));
        Assert.False(File.Exists(available));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
