using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ParticleColorSettingsTests
{
    [Fact]
    public void ExistingSettingsUseIceForWinterAndTrackAccentForClassicWithoutChangingSavedColors()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SettingsRevision":9,"CustomAccentColor":"#FFEE9944","CustomBackgroundColor":"#FF15151D","AnimatedBackground":false,"CpuRenderingEnabled":true}""")!;
        settings.MigrateSettings();
        Assert.Null(settings.CustomParticleColor);
        Assert.Equal(Color.FromRgb(165, 228, 255), ColorCustomization.ResolveParticle(settings));
        settings.IsWinterModeEnabled = false;
        Assert.Equal(ColorCustomization.ResolveAccent(settings), ColorCustomization.ResolveParticle(settings));
        settings.CustomAccentColor = "#FF4499EE";
        Assert.Equal(Color.FromRgb(68, 153, 238), ColorCustomization.ResolveParticle(settings));
        settings.CustomParticleColor = settings.CustomAccentColor;
        settings.IsWinterModeEnabled = true;
        Assert.Equal(ColorCustomization.DefaultWinterParticleColor, ColorCustomization.ResolveParticle(settings));
        settings.CustomWinterParticleColor = "#9049B16C";
        Assert.Equal(Color.FromArgb(144, 73, 177, 108), ColorCustomization.ResolveParticle(settings));
        settings.IsWinterModeEnabled = false;
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
            Assert.Null(restored.CustomWinterParticleColor);
            restored.CustomParticleColor = "#FFFFE0BD";
            restored.CustomWinterParticleColor = value;
            service.Save(restored);
            restored = service.Load();
            Assert.Equal("#FFFFE0BD", restored.CustomParticleColor);
            Assert.Equal(expected, restored.CustomWinterParticleColor);
            restored.CustomWinterParticleColor = null;
            service.Save(restored);
            restored = service.Load();
            Assert.Null(restored.CustomWinterParticleColor);
            Assert.Equal("#FFFFE0BD", restored.CustomParticleColor);
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
        Assert.Equal(ColorCustomization.DefaultWinterParticleColor, resources["AppWinterParticleColor"]);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D", customParticleColor: "#804499EE");
        Assert.Equal(Color.FromArgb(128, 68, 153, 238), resources["AppParticleColor"]);
        Assert.Equal(ColorCustomization.DefaultWinterParticleColor, resources["AppWinterParticleColor"]);
        Assert.Same(window, resources["WindowBrush"]);
        Assert.Same(card, resources["CardBrush"]);
        Assert.Same(panel, resources["PanelBrush"]);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D",
            customParticleColor: "#804499EE", customWinterParticleColor: "#4055BB22");
        Assert.Equal(Color.FromArgb(128, 68, 153, 238), resources["AppParticleColor"]);
        Assert.Equal(Color.FromArgb(64, 85, 187, 34), resources["AppWinterParticleColor"]);
        AppThemeResources.Apply(resources, theme, background, "#FFEE9944", "#FF15151D", customParticleColor: null);
        Assert.Equal(Color.FromRgb(238, 153, 68), resources["AppParticleColor"]);
        Assert.Equal(ColorCustomization.DefaultWinterParticleColor, resources["AppWinterParticleColor"]);
        Assert.Same(window, resources["WindowBrush"]);
    }
}
