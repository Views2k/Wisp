using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class HudBorderThemeTests
{
    [Fact]
    public void MissingPreferenceLoadsTheDefaultWithoutChangingExistingValues()
    {
        WithSettingsPath(path =>
        {
            File.WriteAllText(path, """
                {
                  "SettingsRevision": 7,
                  "UdpPort": 5601,
                  "ColorTheme": "Plum",
                  "BackgroundTheme": "Navy"
                }
                """);

            var settings = new SettingsService(path).Load();

            Assert.Equal(AppColorThemes.DefaultName, settings.HudBorderTheme);
            Assert.Equal(5601, settings.UdpPort);
            Assert.Equal("Plum", settings.ColorTheme);
            Assert.Equal("Navy", settings.BackgroundTheme);
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
                  "HudBorderTheme": "not-a-theme"
                }
                """);

            var service = new SettingsService(path);
            var settings = service.Load();
            Assert.Equal(AppColorThemes.DefaultName, settings.HudBorderTheme);

            settings.HudBorderTheme = " blue ";
            service.Save(settings);

            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                "Blue",
                saved.RootElement.GetProperty(nameof(AppSettings.HudBorderTheme)).GetString());
        });
    }

    [Fact]
    public void NonDefaultPreferenceRoundTrips()
    {
        WithSettingsPath(path =>
        {
            var service = new SettingsService(path);
            var settings = new AppSettings
            {
                HudBorderTheme = "Orange",
                UdpPort = 5601
            };

            service.Save(settings);
            var loaded = service.Load();

            Assert.Equal("Orange", loaded.HudBorderTheme);
            Assert.Equal(5601, loaded.UdpPort);
        });
    }

    [Fact]
    public async Task NormalizedEquivalentControllerSelectionIsANoOp()
    {
        var settings = new AppSettings { HudBorderTheme = "Slate" };
        await using var controller = new AppController(
            settings,
            new SettingsService(Path.Combine(
                Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var field = typeof(AppController).GetField(
            "_settingsSaveTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var saveTimer = Assert.IsType<DispatcherTimer>(field!.GetValue(controller));

        controller.SetHudBorderTheme(" slate ");

        Assert.Equal("Slate", settings.HudBorderTheme);
        Assert.False(saveTimer.IsEnabled);

        controller.SetHudBorderTheme("Orange");
        Assert.Equal("Orange", settings.HudBorderTheme);
        Assert.True(saveTimer.IsEnabled);
    }

    [Fact]
    public void EveryPaletteCreatesTheExpectedFrozenTranslucentBrush() => OnSta(() =>
    {
        foreach (var theme in AppColorThemes.All)
        {
            var resources = new ResourceDictionary();

            HudBorderThemeResources.Apply(resources, theme);

            var brush = Assert.IsType<SolidColorBrush>(
                resources[HudBorderThemeResources.ResourceKey]);
            var expected = (Color)ColorConverter.ConvertFromString(theme.Accent);
            Assert.True(brush.IsFrozen);
            Assert.Equal(HudBorderThemeResources.BorderAlpha, brush.Color.A);
            Assert.Equal(expected.R, brush.Color.R);
            Assert.Equal(expected.G, brush.Color.G);
            Assert.Equal(expected.B, brush.Color.B);
        }
    });

    [Fact]
    public void ApplyingTheSameBorderThemePreservesTheExistingBrush() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        HudBorderThemeResources.Apply(resources, "Aqua");
        var original = resources[HudBorderThemeResources.ResourceKey];

        HudBorderThemeResources.Apply(resources, " aqua ");
        Assert.Same(original, resources[HudBorderThemeResources.ResourceKey]);

        HudBorderThemeResources.Apply(resources, "Rose");
        Assert.NotSame(original, resources[HudBorderThemeResources.ResourceKey]);
    });

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

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "HUD border resource STA check timed out.");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
