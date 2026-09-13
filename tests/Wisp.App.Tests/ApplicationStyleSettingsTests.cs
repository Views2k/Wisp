using System.Text.Json;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ApplicationStyleSettingsTests
{
    [Fact]
    public void InvalidStyleValuesNormalizeWithoutTouchingOtherPreferences()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 9,
            UdpPort = 5601,
            CpuRenderingEnabled = true,
            OverlayOpacity = 0.6,
            ResizableDashboardDisplay = true,
            ApplicationStyle = new AppStyleSettings
            {
                GlowStrength = -1,
                SurfaceOpacity = 130,
                BorderThickness = double.NaN,
                CornerRadius = double.PositiveInfinity,
                CardPadding = 0,
                BorderColor = "invalid",
                TextColor = " abcdef ",
                MutedTextColor = "#80445566"
            }
        };

        settings.MigrateSettings();

        Assert.Equal(0, settings.ApplicationStyle.GlowStrength);
        Assert.Equal(100, settings.ApplicationStyle.SurfaceOpacity);
        Assert.Equal(1, settings.ApplicationStyle.BorderThickness);
        Assert.Equal(28, settings.ApplicationStyle.CornerRadius);
        Assert.Equal(12, settings.ApplicationStyle.CardPadding);
        Assert.Null(settings.ApplicationStyle.BorderColor);
        Assert.Equal("#FFABCDEF", settings.ApplicationStyle.TextColor);
        Assert.Equal("#80445566", settings.ApplicationStyle.MutedTextColor);
        Assert.Equal(5601, settings.UdpPort);
        Assert.True(settings.CpuRenderingEnabled);
        Assert.True(settings.ResizableDashboardDisplay);
        Assert.Equal(0.6, settings.OverlayOpacity);
    }

    [Fact]
    public void MissingOrNullStyleKeepsExistingUsersOnTheOriginalPaletteAndDefaults()
    {
        foreach (var json in new[]
                 {
                     """{"SettingsRevision":9,"ColorTheme":"Plum","OverlayOpacity":0.7}""",
                     """{"SettingsRevision":9,"ColorTheme":"Plum","OverlayOpacity":0.7,"ApplicationStyle":null}"""
                 })
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
            settings.MigrateSettings();
            var style = settings.ApplicationStyle;
            Assert.Equal(100, style.GlowStrength);
            Assert.Equal(100, style.SurfaceOpacity);
            Assert.Equal(1, style.BorderThickness);
            Assert.Equal(28, style.CornerRadius);
            Assert.Equal(24, style.CardPadding);
            Assert.Null(style.BorderColor);
            Assert.Null(style.TextColor);
            Assert.Null(style.MutedTextColor);
            Assert.Equal("Neutral", settings.BackgroundTheme);
            Assert.Equal("Plum", settings.ColorTheme);
            Assert.Equal(0.7, settings.OverlayOpacity);
            Assert.False(settings.ResizableDashboardDisplay);
        }
    }

    [Fact]
    public void StylesCloneIndependentlyAndClampEveryUpperAndLowerBoundary()
    {
        var original = new AppStyleSettings();
        var copy = original.Clone();
        copy.GlowStrength = double.NaN;
        copy.SurfaceOpacity = double.NegativeInfinity;
        copy.BorderThickness = 50;
        copy.CornerRadius = 100;
        copy.CardPadding = 100;
        copy.Normalize();
        Assert.NotSame(original, copy);
        Assert.Equal(100, copy.GlowStrength);
        Assert.Equal(100, copy.SurfaceOpacity);
        Assert.Equal(3, copy.BorderThickness);
        Assert.Equal(48, copy.CornerRadius);
        Assert.Equal(36, copy.CardPadding);
        Assert.Equal(1, original.BorderThickness);
        Assert.Equal(28, original.CornerRadius);
        Assert.Equal(24, original.CardPadding);
        copy.BorderThickness = -1;
        copy.CornerRadius = -1;
        copy.CardPadding = double.NaN;
        copy.Normalize();
        Assert.Equal(0, copy.BorderThickness);
        Assert.Equal(0, copy.CornerRadius);
        Assert.Equal(24, copy.CardPadding);
    }

    [Fact]
    public async Task ControllerStyleChangesOwnTheirCopyAndPreserveIndependentPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", "application-style-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new AppSettings
            {
                SettingsRevision = 9,
                ColorTheme = "Plum",
                CustomHudBorderColor = "#80334455",
                OverlayOpacity = 0.65,
                RecordingCountdownSeconds = 5,
                CpuRenderingEnabled = true
            };
            var service = new SettingsService(path);
            await using (var controller = new AppController(settings, service))
            {
                var selected = new AppStyleSettings { BorderThickness = 2.5, TextColor = "abcdef" };
                controller.SetApplicationStyle(selected);
                Assert.NotSame(selected, settings.ApplicationStyle);
                Assert.Equal("#FFABCDEF", settings.ApplicationStyle.TextColor);
                Assert.Equal("abcdef", selected.TextColor);
                selected.BorderThickness = 0;
                Assert.Equal(2.5, settings.ApplicationStyle.BorderThickness);
                var savedStyle = settings.ApplicationStyle;
                controller.SetApplicationStyle(savedStyle.Clone());
                Assert.Same(savedStyle, settings.ApplicationStyle);

                controller.SetResizableDashboardDisplay(true);
                controller.SetApplicationStyle(new AppStyleSettings());
                Assert.True(settings.ResizableDashboardDisplay);
                controller.SetApplicationStyle(savedStyle);
                service.Save(settings);
            }

            var restored = service.Load();
            Assert.Equal(2.5, restored.ApplicationStyle.BorderThickness);
            Assert.Equal("#FFABCDEF", restored.ApplicationStyle.TextColor);
            Assert.True(restored.ResizableDashboardDisplay);
            Assert.Equal("Plum", restored.ColorTheme);
            Assert.Equal("#80334455", restored.CustomHudBorderColor);
            Assert.Equal(0.65, restored.OverlayOpacity);
            Assert.Equal(5, restored.RecordingCountdownSeconds);
            Assert.True(restored.CpuRenderingEnabled);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
