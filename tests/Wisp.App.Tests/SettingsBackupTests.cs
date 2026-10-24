using System.IO;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SettingsBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "settings-backup-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    [Fact]
    public void ASaveKeepsABackupAndACorruptFileLoadsFromIt()
    {
        var service = new SettingsService(SettingsPath);
        service.Save(new AppSettings { UdpPort = 6123, SidebarCollapsed = true });
        Assert.True(File.Exists(SettingsPath + ".bak"));

        File.WriteAllText(SettingsPath, "{ \"UdpPort\": ");
        var restored = service.Load();

        Assert.Equal(6123, restored.UdpPort);
        Assert.True(restored.SidebarCollapsed);
        Assert.True(service.RestoredFromBackup);
        Assert.Equal("failure", service.LoadOutcome);
        Assert.Equal("invalid-data", service.LoadFailure?.Reason);
    }

    [Fact]
    public void WithoutABackupACorruptFileStillFallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "not json");
        var service = new SettingsService(SettingsPath);
        var settings = service.Load();
        Assert.False(service.RestoredFromBackup);
        Assert.Equal("failure", service.LoadOutcome);
        Assert.Equal(new AppSettings().UdpPort, settings.UdpPort);
    }

    [Fact]
    public void AGoodLoadWritesTheMissingBackupAndAMissingFileIsNotRestored()
    {
        var service = new SettingsService(SettingsPath);
        service.Save(new AppSettings { UdpPort = 6124 });
        File.Delete(SettingsPath + ".bak");
        Assert.Equal(6124, service.Load().UdpPort);
        Assert.True(File.Exists(SettingsPath + ".bak"));
        Assert.False(service.RestoredFromBackup);

        File.Delete(SettingsPath);
        var fresh = service.Load();
        Assert.Equal("none", service.LoadOutcome);
        Assert.False(service.RestoredFromBackup);
        Assert.NotEqual(6124, fresh.UdpPort);
    }
}
