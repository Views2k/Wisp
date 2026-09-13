using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ParticleColorSettingsTests
{
    [Fact]
    public void ExistingSettingsFollowAccentWithoutReplacingAnySavedColors()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"CustomAccentColor":"#FFEE9944","CustomBackgroundColor":"#FF15151D","AnimatedBackground":false,"CpuRenderingEnabled":true}""")!;
        settings.MigrateSettings();
        Assert.Null(settings.CustomParticleColor);
        Assert.Equal(ColorCustomization.ResolveAccent(settings), ColorCustomization.ResolveParticle(settings));
        settings.CustomAccentColor = "#FF4499EE";
        Assert.Equal(Color.FromRgb(68, 153, 238), ColorCustomization.ResolveParticle(settings));
        Assert.Equal("#FF15151D", settings.CustomBackgroundColor);
        Assert.False(settings.AnimatedBackground);
        Assert.True(settings.CpuRenderingEnabled);
    }

    [Theory]
    [InlineData("004499EE", "#004499EE")]
    [InlineData("aabbcc", "#FFAABBCC")]
    [InlineData("bad-color", null)]
    public void CustomColorNormalizesAndPersistsIncludingFullyHiddenParticles(string value, string? expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(Path.Combine(directory, "settings.json"));
            service.Save(new AppSettings { CustomParticleColor = value });
            var restored = service.Load();
            Assert.Equal(expected, restored.CustomParticleColor);
            restored.CustomParticleColor = null;
            service.Save(restored);
            Assert.Null(service.Load().CustomParticleColor);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ParticleColorChangesDoNotChangeAnySurfaceBrushAndResetRestoresAccentTracking()
    {
        var resources = new ResourceDictionary();
        var theme = AppColorThemes.Resolve(null);
        var background = AppBackgroundThemes.Resolve(null);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D");
        var window = resources["WindowBrush"];
        var card = resources["CardBrush"];
        var panel = resources["PanelBrush"];
        Assert.Equal(Color.FromRgb(238, 153, 68), resources["AppParticleColor"]);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D", customParticleColor: "#804499EE");
        Assert.Equal(Color.FromArgb(128, 68, 153, 238), resources["AppParticleColor"]);
        Assert.Same(window, resources["WindowBrush"]);
        Assert.Same(card, resources["CardBrush"]);
        Assert.Same(panel, resources["PanelBrush"]);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D", customParticleColor: null);
        Assert.Equal(Color.FromRgb(238, 153, 68), resources["AppParticleColor"]);
        Assert.Same(window, resources["WindowBrush"]);
    }
}
