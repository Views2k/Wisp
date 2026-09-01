using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class BackgroundThemePersistenceTests
{
    [Fact]
    public void MissingLegacyPreferenceLoadsTheDefaultWithoutChangingExistingValues()
    {
        WithSettingsPath(path =>
        {
            File.WriteAllText(path, """
                {
                  "SettingsRevision": 7,
                  "UdpPort": 5601,
                  "ColorTheme": "Plum",
                  "AnimatedBackground": false
                }
                """);

            var settings = new SettingsService(path).Load();

            Assert.Equal(AppBackgroundThemes.DefaultName, settings.BackgroundTheme);
            Assert.Equal(5601, settings.UdpPort);
            Assert.Equal("Plum", settings.ColorTheme);
            Assert.False(settings.AnimatedBackground);
        });
    }

    [Fact]
    public void InvalidPreferenceNormalizesDuringLoadAndSave()
    {
        WithSettingsPath(path =>
        {
            File.WriteAllText(path, """
                {
                  "SettingsRevision": 7,
                  "BackgroundTheme": "not-a-background"
                }
                """);

            var service = new SettingsService(path);
            var settings = service.Load();
            Assert.Equal(AppBackgroundThemes.DefaultName, settings.BackgroundTheme);

            settings.BackgroundTheme = " still-not-a-background ";
            service.Save(settings);

            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                AppBackgroundThemes.DefaultName,
                saved.RootElement.GetProperty(nameof(AppSettings.BackgroundTheme)).GetString());
        });
    }

    [Fact]
    public void NonDefaultPreferenceRoundTrips()
    {
        WithSettingsPath(path =>
        {
            var service = new SettingsService(path);
            var expected = AppBackgroundThemes.All.Single(theme => theme.Name == "Slate").Name;
            var settings = new AppSettings
            {
                BackgroundTheme = expected,
                UdpPort = 5601
            };

            service.Save(settings);
            var loaded = service.Load();

            Assert.Equal(expected, loaded.BackgroundTheme);
            Assert.Equal(5601, loaded.UdpPort);
        });
    }

    [Fact]
    public async Task NormalizedEquivalentControllerSelectionIsANoOp()
    {
        var settings = new AppSettings { BackgroundTheme = "Slate" };
        await using var controller = new AppController(
            settings,
            new SettingsService(Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var field = typeof(AppController).GetField("_settingsSaveTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var saveTimer = Assert.IsType<DispatcherTimer>(field!.GetValue(controller));

        controller.SetBackgroundTheme(" slate ");

        Assert.Equal("Slate", settings.BackgroundTheme);
        Assert.False(saveTimer.IsEnabled);

        controller.SetBackgroundTheme(AppBackgroundThemes.DefaultName);
        Assert.True(saveTimer.IsEnabled);
    }

    private static void WithSettingsPath(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            test(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
