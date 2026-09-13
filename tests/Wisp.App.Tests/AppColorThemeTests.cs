using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;

namespace Wisp.App.Tests;

public sealed class AppColorThemeTests
{
    private static readonly IReadOnlyDictionary<string, string> NeutralTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#090C11",
            ["PanelBrush"] = "#0E131B",
            ["SidebarBrush"] = "#0E131B",
            ["CardBrush"] = "#141B25",
            ["RaisedBrush"] = "#1B2431",
            ["StrokeBrush"] = "#293646",
            ["TextBrush"] = "#F5F8FC",
            ["MutedBrush"] = "#91A0B3",
            ["FaintBrush"] = "#8191A5",
            ["InputBrush"] = "#0B1017",
            ["HoverBrush"] = "#202A39",
            ["SliderTrackBrush"] = "#303D4E",
            ["ToggleTrackBrush"] = "#334052",
            ["ScrollThumbBrush"] = "#46566A"
        };

    private static readonly string[] SolidBrushKeys =
        NeutralTokens.Keys.Concat(new[] { "AccentBrush", "AccentBlueBrush", "AccentTextBrush" }).ToArray();

    private static readonly string[] OrbitBrushKeys =
        { "OrbitCardBrush", "OrbitBorderBrush", "OrbitButtonBrush", "OrbitSelectedBrush" };

    private static readonly string[] ModernBrushKeys =
        { "OrbitSurfaceBorderBrush", "OrbitControlBrush", "OrbitSelectionBrush", "OrbitFocusBrush" };

    private static readonly string[] ThemeBrushKeys = SolidBrushKeys.Concat(OrbitBrushKeys).Concat(ModernBrushKeys).ToArray();

    private static readonly string[] StyleResourceKeys =
        { "OrbitGlowOpacity", "OrbitSurfaceOpacity", "OrbitStrokeThickness", "OrbitCornerRadius", "OrbitButtonRadius", "OrbitCardPadding" };

    private static readonly string[] ThemeResourceKeys = ThemeBrushKeys.Concat(StyleResourceKeys).Append("AppParticleColor").ToArray();

    private static readonly string[] BackgroundBrushKeys =
    {
        "WindowBrush",
        "PanelBrush",
        "SidebarBrush",
        "CardBrush",
        "RaisedBrush",
        "StrokeBrush",
        "InputBrush",
        "HoverBrush",
        "SliderTrackBrush",
        "ToggleTrackBrush",
        "ScrollThumbBrush"
    };

    private static readonly string[] ReadableSurfaceKeys =
    {
        "WindowBrush",
        "PanelBrush",
        "SidebarBrush",
        "CardBrush",
        "RaisedBrush",
        "InputBrush",
        "HoverBrush"
    };

    private static readonly string[] PersistentSurfaceKeys =
    {
        "WindowBrush",
        "PanelBrush",
        "SidebarBrush",
        "CardBrush",
        "InputBrush"
    };

    [Fact]
    public void CatalogContainsExactlyTheFifteenApprovedAccentThemes()
    {
        var expected = new[]
        {
            new AppColorTheme("Aqua",   "#63D8D4"),
            new AppColorTheme("Mint",   "#83DFBC"),
            new AppColorTheme("Teal",   "#55C5B7"),
            new AppColorTheme("Green",  "#8BD780"),
            new AppColorTheme("Blue",   "#7BB8F8"),
            new AppColorTheme("Indigo", "#A1ACFF"),
            new AppColorTheme("Purple", "#B69BF2"),
            new AppColorTheme("Plum",   "#D79AD2"),
            new AppColorTheme("Pink",   "#F2A1D4"),
            new AppColorTheme("Rose",   "#ED9CAC"),
            new AppColorTheme("Red",    "#F58B8B"),
            new AppColorTheme("Orange", "#F5AD7B"),
            new AppColorTheme("Amber",  "#E5C166"),
            new AppColorTheme("Sand",   "#CFBE99"),
            new AppColorTheme("Slate",  "#A8B9C9")
        };

        Assert.Equal(expected, AppColorThemes.All);
        Assert.Equal(15, AppColorThemes.All.Count);
        Assert.Equal(15, AppColorThemes.All.Select(theme => theme.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(15, AppColorThemes.All.Select(theme => theme.Accent).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(AppColorThemes.DefaultName, AppColorThemes.All[0].Name);
    }

    [Fact]
    public void CatalogCannotBeModifiedThroughItsCollectionInterface()
    {
        var list = Assert.IsAssignableFrom<IList<AppColorTheme>>(AppColorThemes.All);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = list[1]);
    }

    [Fact]
    public void BackgroundCatalogPreservesExistingPalettesAndAddsTheWizardPalette()
    {
        var expected = new[]
        {
            new AppBackgroundTheme("Neutral", "#090C11", "#0E131B", "#141B25", "#1B2431", "#293646", "#0B1017", "#202A39", "#303D4E", "#334052", "#46566A"),
            new AppBackgroundTheme("Slate",   "#0C1013", "#11161A", "#171E24", "#1D262E", "#2B3944", "#0E1317", "#202A33", "#31414C", "#354650", "#485B67"),
            new AppBackgroundTheme("Navy",    "#080D16", "#0C1420", "#111B2A", "#172437", "#263850", "#0A111B", "#1A293E", "#293C55", "#2D425C", "#405774"),
            new AppBackgroundTheme("Blue",    "#080E18", "#0C1625", "#111E31", "#172840", "#263C5C", "#0A121F", "#1A2D47", "#294260", "#2D4868", "#405D7D"),
            new AppBackgroundTheme("Indigo",  "#0D0D18", "#141425", "#1B1A31", "#24223F", "#373451", "#10101F", "#282546", "#3D3859", "#443E61", "#5A5376"),
            new AppBackgroundTheme("Purple",  "#120C17", "#1A1222", "#23182D", "#2E1F3A", "#442E51", "#160F1D", "#33233F", "#4B3658", "#523B60", "#684F75"),
            new AppBackgroundTheme("Plum",    "#150C14", "#20131F", "#2A1929", "#362036", "#4C304A", "#1A0F19", "#3A243A", "#533A52", "#5B4059", "#724F70"),
            new AppBackgroundTheme("Rose",    "#170C11", "#23131B", "#2D1923", "#3A202C", "#512F40", "#1B0F15", "#3F2430", "#573948", "#60404F", "#784F60"),
            new AppBackgroundTheme("Red",     "#180C0D", "#241314", "#2F191B", "#3C2023", "#542F34", "#1C0F10", "#412428", "#59383D", "#623E43", "#7A4E54"),
            new AppBackgroundTheme("Orange",  "#170E09", "#23160F", "#2E1D15", "#3A261C", "#50382B", "#1B110C", "#3F2A20", "#564133", "#5E4738", "#765A48"),
            new AppBackgroundTheme("Amber",   "#141006", "#20190B", "#2A2210", "#352B16", "#493D25", "#181309", "#392F19", "#50442E", "#574B33", "#6E6043"),
            new AppBackgroundTheme("Forest",  "#09130C", "#0F1D15", "#15271D", "#1B3124", "#294735", "#0C170F", "#1E3528", "#304C3B", "#355343", "#486958"),
            new AppBackgroundTheme("Green",   "#08140B", "#0D1F12", "#13291A", "#183423", "#264A34", "#0A180D", "#1B3926", "#2D503A", "#325741", "#456D56"),
            new AppBackgroundTheme("Teal",    "#071414", "#0B1F1F", "#102A2A", "#153535", "#244B4B", "#091818", "#183A3A", "#2B5151", "#305959", "#436F6F"),
            new AppBackgroundTheme("Cyan",    "#071318", "#0B1D25", "#102832", "#15323F", "#234857", "#09171D", "#183747", "#2A4E60", "#2F5569", "#426B80"),
            new AppBackgroundTheme("Wisp",    "#090D12", "#0D1218", "#111822", "#1A2832", "#35404F", "#0A0F15", "#161F29", "#303D4E", "#334052", "#46566A")
        };

        Assert.Equal(expected, AppBackgroundThemes.All);
        Assert.Equal(16, AppBackgroundThemes.All.Count);
        Assert.Equal(16, AppBackgroundThemes.All.Select(theme => theme.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, AppBackgroundThemes.All.Distinct().Count());
        Assert.Equal(AppBackgroundThemes.DefaultName, AppBackgroundThemes.All[0].Name);
    }

    [Fact]
    public void BackgroundCatalogCannotBeModifiedThroughItsCollectionInterface()
    {
        var list = Assert.IsAssignableFrom<IList<AppBackgroundTheme>>(AppBackgroundThemes.All);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = list[1]);
    }

    [Theory]
    [InlineData(null, "Neutral")]
    [InlineData("", "Neutral")]
    [InlineData("   ", "Neutral")]
    [InlineData("unknown", "Neutral")]
    [InlineData("slate", "Slate")]
    [InlineData(" FoReSt ", "Forest")]
    public void BackgroundNamesNormalizeToExistingCatalogEntries(string? name, string expected)
    {
        Assert.Equal(expected, AppBackgroundThemes.NormalizeName(name));
        Assert.Same(AppBackgroundThemes.All.Single(theme => theme.Name == expected), AppBackgroundThemes.Resolve(name));
    }

    [Fact]
    public void EveryBackgroundPaletteResolvesWithoutCaseOrWhitespaceSensitivity()
    {
        foreach (var theme in AppBackgroundThemes.All)
        {
            Assert.Same(theme, AppBackgroundThemes.Resolve(" " + theme.Name.ToLowerInvariant() + " "));
        }
    }

    [Theory]
    [InlineData(null, "Aqua")]
    [InlineData("", "Aqua")]
    [InlineData("   ", "Aqua")]
    [InlineData("unknown", "Aqua")]
    [InlineData("blue", "Blue")]
    [InlineData(" PLuM ", "Plum")]
    public void ThemeNamesNormalizeToExistingCatalogEntries(string? name, string expected)
    {
        Assert.Equal(expected, AppColorThemes.NormalizeName(name));
        Assert.Same(AppColorThemes.All.Single(theme => theme.Name == expected), AppColorThemes.Resolve(name));
    }

    [Fact]
    public void EveryPaletteResolvesWithoutCaseOrWhitespaceSensitivity()
    {
        foreach (var theme in AppColorThemes.All)
        {
            Assert.Same(theme, AppColorThemes.Resolve(" " + theme.Name.ToLowerInvariant() + " "));
        }
    }

    [Fact]
    public void EveryThemeKeepsExactNeutralTokensAndChangesOnlyAccentBrushes() => OnSta(() =>
    {
        foreach (var theme in AppColorThemes.All)
        {
            var resources = new ResourceDictionary();
            AppThemeResources.Apply(resources, theme);

            Assert.Equal(ThemeResourceKeys.Length, resources.Count);
            foreach (var (key, expected) in NeutralTokens)
            {
                Assert.Equal(Parse(expected), Brush(resources, key).Color);
            }

            var accent = Parse(theme.Accent);
            Assert.Equal(accent, Brush(resources, "AccentBrush").Color);
            Assert.Equal(accent, Brush(resources, "AccentBlueBrush").Color);
            Assert.Equal(accent, Assert.IsType<Color>(resources["AppParticleColor"]));
            foreach (var key in SolidBrushKeys)
            {
                Assert.True(Brush(resources, key).IsFrozen, key);
                Assert.Equal(byte.MaxValue, Brush(resources, key).Color.A);
            }
        }
    });

    [Fact]
    public void DefaultBackgroundIsTheExactExistingShellAndLegacyApplyRemainsCompatible() => OnSta(() =>
    {
        var accent = AppColorThemes.Resolve("Purple");
        var legacyResources = new ResourceDictionary();
        var explicitResources = new ResourceDictionary();

        AppThemeResources.Apply(legacyResources, accent);
        AppThemeResources.Apply(explicitResources, accent, AppBackgroundThemes.Resolve(null));

        Assert.Equal("Neutral", AppBackgroundThemes.DefaultName);
        Assert.Equal(ThemeResourceKeys.Length, legacyResources.Count);
        Assert.Equal(ThemeResourceKeys.Length, explicitResources.Count);
        foreach (var key in SolidBrushKeys)
        {
            Assert.Equal(Brush(legacyResources, key).Color, Brush(explicitResources, key).Color);
        }

        foreach (var key in OrbitBrushKeys)
        {
            Assert.Equal(Gradient(legacyResources, key).GradientStops.Select(stop => (stop.Color, stop.Offset)),
                Gradient(explicitResources, key).GradientStops.Select(stop => (stop.Color, stop.Offset)));
        }

        foreach (var (key, expected) in NeutralTokens)
        {
            Assert.Equal(Parse(expected), Brush(explicitResources, key).Color);
        }
    });

    [Fact]
    public void ApplicationStyleResourcesAreStableAndDoNotChangeSharedHudColors() => OnSta(() =>
    {
        var sharedStroke = new SolidColorBrush(Colors.Red);
        var sharedText = new SolidColorBrush(Colors.White);
        var shared = new ResourceDictionary { ["StrokeBrush"] = sharedStroke, ["TextBrush"] = sharedText };
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(shared);
        var style = new AppStyleSettings
        {
            GlowStrength = 35,
            SurfaceOpacity = 70,
            BorderThickness = 2,
            CornerRadius = 12,
            CardPadding = 30,
            BorderColor = "#80665544",
            TextColor = "#FFF0EEDD",
            MutedTextColor = "#FFCCBBAA"
        };
        var accent = AppColorThemes.Resolve("Plum");
        var background = AppBackgroundThemes.Resolve("Wisp");
        AppThemeResources.Apply(resources, accent, background, null, null, style);
        Assert.Equal(0.35, Assert.IsType<double>(resources["OrbitGlowOpacity"]));
        Assert.Equal(0.70, Assert.IsType<double>(resources["OrbitSurfaceOpacity"]));
        Assert.Equal(new Thickness(2), Assert.IsType<Thickness>(resources["OrbitStrokeThickness"]));
        Assert.Equal(new CornerRadius(12), Assert.IsType<CornerRadius>(resources["OrbitCornerRadius"]));
        Assert.Equal(new CornerRadius(12), Assert.IsType<CornerRadius>(resources["OrbitButtonRadius"]));
        Assert.Equal(new Thickness(30), Assert.IsType<Thickness>(resources["OrbitCardPadding"]));
        Assert.Equal(Parse(style.BorderColor!), Brush(resources, "StrokeBrush").Color);
        Assert.All(Gradient(resources, "OrbitBorderBrush").GradientStops, stop => Assert.Equal(Parse(style.BorderColor!), stop.Color));
        Assert.Equal(Parse(style.TextColor!), Brush(resources, "TextBrush").Color);
        Assert.Equal(Parse(style.MutedTextColor!), Brush(resources, "MutedBrush").Color);
        Assert.Same(sharedStroke, shared["StrokeBrush"]);
        Assert.Same(sharedText, shared["TextBrush"]);
        Assert.Equal(Colors.Red, sharedStroke.Color);
        Assert.Equal(Colors.White, sharedText.Color);
        Assert.All(ThemeBrushKeys, key => Assert.True(((System.Windows.Freezable)resources[key]).IsFrozen));

        var original = ThemeResourceKeys.ToDictionary(key => key, key => resources[key]);
        AppThemeResources.Apply(resources, accent, background, null, null, style.Clone());
        Assert.All(ThemeResourceKeys, key => Assert.Same(original[key], resources[key]));

        AppThemeResources.Apply(resources, accent, background);
        Assert.Equal(1, Assert.IsType<double>(resources["OrbitGlowOpacity"]));
        Assert.Equal(1, Assert.IsType<double>(resources["OrbitSurfaceOpacity"]));
        Assert.Equal(new Thickness(1), Assert.IsType<Thickness>(resources["OrbitStrokeThickness"]));
        Assert.Equal(new CornerRadius(28), Assert.IsType<CornerRadius>(resources["OrbitCornerRadius"]));
        Assert.Equal(new CornerRadius(26), Assert.IsType<CornerRadius>(resources["OrbitButtonRadius"]));
        Assert.Equal(new Thickness(24), Assert.IsType<Thickness>(resources["OrbitCardPadding"]));
        Assert.Equal(Parse(background.Stroke), Brush(resources, "StrokeBrush").Color);
        Assert.Equal(Parse("#F5F8FC"), Brush(resources, "TextBrush").Color);
        Assert.Equal(Parse("#91A0B3"), Brush(resources, "MutedBrush").Color);
    });

    [Fact]
    public void ModernSurfaceRolesKeepLegacyTokensAndHonorExplicitBorderCustomization() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        var accent = AppColorThemes.Resolve("Purple");
        var background = AppBackgroundThemes.Resolve("Neutral");
        AppThemeResources.Apply(resources, accent, background);
        Assert.Equal(Colors.Transparent, Brush(resources, "OrbitSurfaceBorderBrush").Color);
        Assert.Equal(Brush(resources, "RaisedBrush").Color, Brush(resources, "OrbitControlBrush").Color);
        Assert.All(ModernBrushKeys, key => Assert.True(Brush(resources, key).IsFrozen));
        var focus = Brush(resources, "OrbitFocusBrush").Color;
        Assert.True(Contrast(focus, Brush(resources, "OrbitControlBrush").Color) >= 3);
        Assert.True(Contrast(focus, Brush(resources, "OrbitSelectionBrush").Color) >= 3);
        var oldColors = SolidBrushKeys.ToDictionary(key => key, key => Brush(resources, key).Color);
        var oldGradients = OrbitBrushKeys.ToDictionary(key => key,
            key => Gradient(resources, key).GradientStops.Select(stop => (stop.Color, stop.Offset)).ToArray());

        AppThemeResources.Apply(resources, accent, background, null, null, new AppStyleSettings { BorderThickness = 2 });
        Assert.Equal(Brush(resources, "StrokeBrush").Color, Brush(resources, "OrbitSurfaceBorderBrush").Color);
        Assert.All(SolidBrushKeys, key => Assert.Equal(oldColors[key], Brush(resources, key).Color));
        Assert.All(OrbitBrushKeys, key => Assert.Equal(oldGradients[key],
            Gradient(resources, key).GradientStops.Select(stop => (stop.Color, stop.Offset))));

        var style = new AppStyleSettings { BorderColor = "#8044AACC", BorderThickness = 0 };
        AppThemeResources.Apply(resources, accent, background, null, null, style);
        Assert.Equal(Parse(style.BorderColor!), Brush(resources, "OrbitSurfaceBorderBrush").Color);
        Assert.Equal(new Thickness(0), Assert.IsType<Thickness>(resources["OrbitStrokeThickness"]));
        Assert.Equal(focus, Brush(resources, "OrbitFocusBrush").Color);
    });

    [Fact]
    public void EveryBackgroundMapsAllSemanticSurfaceTokens() => OnSta(() =>
    {
        var accent = AppColorThemes.Resolve("Aqua");
        foreach (var background in AppBackgroundThemes.All)
        {
            var resources = new ResourceDictionary();
            AppThemeResources.Apply(resources, accent, background);

            Assert.Equal(ThemeResourceKeys.Length, resources.Count);
            foreach (var (key, expected) in SurfaceTokens(background))
            {
                Assert.Equal(Parse(expected), Brush(resources, key).Color);
            }

            Assert.Equal(Parse("#F5F8FC"), Brush(resources, "TextBrush").Color);
            Assert.Equal(Parse("#91A0B3"), Brush(resources, "MutedBrush").Color);
            Assert.Equal(Parse("#8191A5"), Brush(resources, "FaintBrush").Color);
            Assert.Equal(Parse(accent.Accent), Brush(resources, "AccentBrush").Color);
            Assert.Equal(Parse(accent.Accent), Brush(resources, "AccentBlueBrush").Color);
        }
    });

    [Fact]
    public void NeutralResourcesAreInvariantAcrossAllThemes() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        AppThemeResources.Apply(resources, AppColorThemes.All[0]);
        var expectedNeutrals = NeutralTokens.Keys.ToDictionary(key => key, key => Brush(resources, key).Color);

        foreach (var theme in AppColorThemes.All.Skip(1))
        {
            AppThemeResources.Apply(resources, theme);
            foreach (var (key, expected) in expectedNeutrals)
            {
                Assert.Equal(expected, Brush(resources, key).Color);
            }
        }
    });

    [Fact]
    public void ApplyingThemesOnlyOverridesTheTargetDictionaryAndPreservesSharedBrushes() => OnSta(() =>
    {
        var originalBackground = new SolidColorBrush(Parse("#010203"));
        var originalAccent = new SolidColorBrush(Parse("#040506"));
        var originalAccentText = new SolidColorBrush(Colors.Magenta);
        var warning = new SolidColorBrush(Parse("#FFBE67"));
        var nativeHudBrush = new SolidColorBrush(Colors.White);
        var sharedResources = new ResourceDictionary
        {
            ["WindowBrush"] = originalBackground,
            ["AccentBrush"] = originalAccent,
            ["AccentTextBrush"] = originalAccentText,
            ["WarningBrush"] = warning
        };
        var windowResources = new ResourceDictionary
        {
            ["AccentBrush"] = originalAccent,
            ["NativeHudBrush"] = nativeHudBrush
        };
        windowResources.MergedDictionaries.Add(sharedResources);
        var otherWindowResources = new ResourceDictionary();
        otherWindowResources.MergedDictionaries.Add(sharedResources);

        foreach (var theme in AppColorThemes.All)
        {
            AppThemeResources.Apply(windowResources, theme);

            Assert.Equal(Parse("#090C11"), Brush(windowResources, "WindowBrush").Color);
            Assert.Equal(Parse(theme.Accent), Brush(windowResources, "AccentBrush").Color);
            Assert.Same(originalBackground, sharedResources["WindowBrush"]);
            Assert.Same(originalAccent, sharedResources["AccentBrush"]);
            Assert.Same(originalAccentText, sharedResources["AccentTextBrush"]);
            Assert.Same(originalAccentText, otherWindowResources["AccentTextBrush"]);
            Assert.NotSame(originalAccentText, windowResources["AccentTextBrush"]);
            Assert.Equal(Colors.Magenta, originalAccentText.Color);
            Assert.False(originalAccentText.IsFrozen);
            Assert.Same(originalBackground, otherWindowResources["WindowBrush"]);
            Assert.Same(originalAccent, otherWindowResources["AccentBrush"]);
            Assert.Same(warning, windowResources["WarningBrush"]);
            Assert.Same(nativeHudBrush, windowResources["NativeHudBrush"]);
            Assert.Equal(Parse("#010203"), originalBackground.Color);
            Assert.Equal(Parse("#040506"), originalAccent.Color);
            Assert.Equal(Parse("#FFBE67"), warning.Color);
            Assert.Equal(Colors.White, nativeHudBrush.Color);
            Assert.False(originalBackground.IsFrozen);
            Assert.False(originalAccent.IsFrozen);
            Assert.False(warning.IsFrozen);
            Assert.False(nativeHudBrush.IsFrozen);
        }
    });

    [Fact]
    public void ChangingThemePreservesNeutralsAndReplacesOnlyLocalAccents() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        AppThemeResources.Apply(resources, AppColorThemes.Resolve("Aqua"));
        var originalBrushes = SolidBrushKeys.ToDictionary(key => key, key => Brush(resources, key));
        var originalColors = originalBrushes.ToDictionary(pair => pair.Key, pair => pair.Value.Color);

        AppThemeResources.Apply(resources, AppColorThemes.Resolve("Plum"));

        foreach (var key in NeutralTokens.Keys)
        {
            Assert.Same(originalBrushes[key], resources[key]);
            Assert.Equal(originalColors[key], originalBrushes[key].Color);
            Assert.True(originalBrushes[key].IsFrozen);
            Assert.True(Brush(resources, key).IsFrozen);
        }

        foreach (var key in new[] { "AccentBrush", "AccentBlueBrush" })
        {
            Assert.NotSame(originalBrushes[key], resources[key]);
            Assert.Equal(originalColors[key], originalBrushes[key].Color);
            Assert.True(originalBrushes[key].IsFrozen);
            Assert.True(Brush(resources, key).IsFrozen);
        }
    });

    [Theory]
    [InlineData("#FF000080", null, "#FFFFFFFF")]
    [InlineData("#FFF1E65C", null, "#FF000000")]
    [InlineData("#FF777777", null, "#FF000000")]
    [InlineData("#59FFFFFF", null, "#FFFFFFFF")]
    [InlineData("#59FFFFFF", "#D1565656", "#FF000000")]
    public void AccentTextRemainsReadableWithCustomDarkBrightAndTranslucentColors(
        string customAccent,
        string? customBackground,
        string expectedForeground) => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        AppThemeResources.Apply(
            resources,
            AppColorThemes.All[0],
            AppBackgroundThemes.All[0],
            customAccent,
            customBackground);

        var accent = Brush(resources, "AccentBrush").Color;
        var foreground = Brush(resources, "AccentTextBrush");
        Assert.Equal(Parse(customAccent), accent);
        Assert.Equal(accent, Brush(resources, "AccentBlueBrush").Color);
        Assert.Equal(Parse(expectedForeground), foreground.Color);
        Assert.True(foreground.IsFrozen);
        foreach (var surface in new[] { "CardBrush", "RaisedBrush" })
        {
            var visibleAccent = Composite(accent, Brush(resources, surface).Color);
            var ratio = Contrast(foreground.Color, visibleAccent);
            Assert.True(ratio >= 4.5, $"Accent text on {surface} has {ratio:F2}:1 contrast.");
        }
    });

    [Fact]
    public void PrimaryButtonKeepsTheSolidAccentUsedForItsMidGrayTextContrast() => OnSta(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "src", "Wisp.App", "OrbitWindowResources.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var primary = document.Descendants(presentation + "Style")
            .Single(element => element.Attribute(xaml + "Key")?.Value == "PrimaryButtonStyle");
        var resourceMarkup = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Style TargetType=\"TextBlock\"/>" +
            "<Style TargetType=\"Button\"/>" + primary + "</ResourceDictionary>";
        var resources = Assert.IsType<ResourceDictionary>(XamlReader.Parse(resourceMarkup));
        AppThemeResources.Apply(resources, AppColorThemes.All[0], AppBackgroundThemes.All[0], "#FF777777", null);
        var button = new System.Windows.Controls.Button
        {
            Content = "Record run",
            Resources = resources,
            Style = Assert.IsType<Style>(resources["PrimaryButtonStyle"])
        };
        button.Measure(new Size(180, 52));
        button.Arrange(new Rect(0, 0, 180, 52));
        button.UpdateLayout();
        var border = Assert.IsType<System.Windows.Controls.Border>(button.Template.FindName("ButtonBorder", button));
        var fill = Assert.IsType<SolidColorBrush>(border.Background);
        var text = Assert.IsType<SolidColorBrush>(button.Foreground);
        Assert.Equal(Parse("#FF777777"), fill.Color);
        Assert.Equal(1, fill.Opacity);
        Assert.Equal(1, border.Opacity);
        Assert.Equal(Colors.Black, text.Color);
        Assert.True(Contrast(text.Color, fill.Color) >= 4.5);
    });

    [Fact]
    public void ChangingTheBackgroundUpdatesTranslucentAccentTextWithoutChangingTheAccent() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        var accent = AppColorThemes.All[0];
        var background = AppBackgroundThemes.All[0];
        AppThemeResources.Apply(resources, accent, background, "#59FFFFFF", "#D1000000");
        var originalBrushes = SolidBrushKeys.ToDictionary(key => key, key => Brush(resources, key));

        AppThemeResources.Apply(resources, accent, background, "#59FFFFFF", "#D1000000");
        foreach (var key in SolidBrushKeys)
        {
            Assert.Same(originalBrushes[key], resources[key]);
        }

        AppThemeResources.Apply(resources, accent, background, "#59FFFFFF", "#D1565656");

        Assert.Same(originalBrushes["AccentBrush"], resources["AccentBrush"]);
        Assert.Same(originalBrushes["AccentBlueBrush"], resources["AccentBlueBrush"]);
        Assert.NotSame(originalBrushes["AccentTextBrush"], resources["AccentTextBrush"]);
        Assert.Equal(Colors.White, originalBrushes["AccentTextBrush"].Color);
        Assert.Equal(Colors.Black, Brush(resources, "AccentTextBrush").Color);
        Assert.True(Brush(resources, "AccentTextBrush").IsFrozen);
        Assert.Equal((byte)0xD1, Brush(resources, "CardBrush").Color.A);
        Assert.Equal((byte)0xD1, Brush(resources, "RaisedBrush").Color.A);
    });

    [Fact]
    public void AccentAndBackgroundSelectionsAreIndependent() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        var navy = AppBackgroundThemes.Resolve("Navy");
        AppThemeResources.Apply(resources, AppColorThemes.Resolve("Aqua"), navy);
        var navyBackgrounds = BackgroundBrushKeys.ToDictionary(key => key, key => Brush(resources, key));
        var fixedTextBrushes = new[] { "TextBrush", "MutedBrush", "FaintBrush" }
            .ToDictionary(key => key, key => Brush(resources, key));
        var aquaAccents = new[] { "AccentBrush", "AccentBlueBrush" }
            .ToDictionary(key => key, key => Brush(resources, key));

        AppThemeResources.Apply(resources, AppColorThemes.Resolve("Plum"), navy);

        foreach (var key in BackgroundBrushKeys)
        {
            Assert.Same(navyBackgrounds[key], resources[key]);
        }
        foreach (var key in fixedTextBrushes.Keys)
        {
            Assert.Same(fixedTextBrushes[key], resources[key]);
        }
        foreach (var key in aquaAccents.Keys)
        {
            Assert.NotSame(aquaAccents[key], resources[key]);
            Assert.Equal(Parse(AppColorThemes.Resolve("Plum").Accent), Brush(resources, key).Color);
        }

        var plumAccents = new[] { "AccentBrush", "AccentBlueBrush" }
            .ToDictionary(key => key, key => Brush(resources, key));
        AppThemeResources.Apply(resources, AppColorThemes.Resolve("Plum"), AppBackgroundThemes.Resolve("Rose"));

        foreach (var key in plumAccents.Keys)
        {
            Assert.Same(plumAccents[key], resources[key]);
        }
        foreach (var key in fixedTextBrushes.Keys)
        {
            Assert.Same(fixedTextBrushes[key], resources[key]);
        }
        foreach (var (key, expected) in SurfaceTokens(AppBackgroundThemes.Resolve("Rose")))
        {
            Assert.Equal(Parse(expected), Brush(resources, key).Color);
        }
    });

    [Fact]
    public void EveryAccentMaintainsReadableContrastOnNeutralSurfaces() => OnSta(() =>
    {
        foreach (var theme in AppColorThemes.All)
        {
            var resources = new ResourceDictionary();
            AppThemeResources.Apply(resources, theme);
            foreach (var surface in new[] { "WindowBrush", "PanelBrush", "SidebarBrush", "CardBrush", "RaisedBrush", "InputBrush", "HoverBrush" })
            {
                var ratio = Contrast(Brush(resources, "AccentBrush").Color, Brush(resources, surface).Color);
                Assert.True(ratio >= 4.5, $"{theme.Name}: accent on {surface} has {ratio:F2}:1 contrast.");
            }
        }
    });

    [Fact]
    public void EveryAccentAndBackgroundCombinationMaintainsReadableContrastAndFrozenBrushes() => OnSta(() =>
    {
        foreach (var accent in AppColorThemes.All)
        {
            foreach (var background in AppBackgroundThemes.All)
            {
                var resources = new ResourceDictionary();
                AppThemeResources.Apply(resources, accent, background);

                AssertContrastAtLeast(resources, "AccentTextBrush", "AccentBrush", 4.5, accent, background);

                foreach (var surface in ReadableSurfaceKeys)
                {
                    AssertContrastAtLeast(resources, "TextBrush", surface, 4.5, accent, background);
                    AssertContrastAtLeast(resources, "MutedBrush", surface, 4.5, accent, background);
                    AssertContrastAtLeast(resources, "AccentBrush", surface, 4.5, accent, background);
                }

                foreach (var surface in PersistentSurfaceKeys)
                {
                    AssertContrastAtLeast(resources, "FaintBrush", surface, 4.5, accent, background);
                }

                foreach (var key in SolidBrushKeys)
                {
                    Assert.True(Brush(resources, key).IsFrozen, $"{accent.Name}/{background.Name}: {key} is not frozen.");
                    Assert.Equal(byte.MaxValue, Brush(resources, key).Color.A);
                }

                foreach (var key in OrbitBrushKeys)
                {
                    var gradient = Gradient(resources, key);
                    Assert.True(gradient.IsFrozen, key);
                    Assert.Equal(3, gradient.GradientStops.Count);
                    Assert.All(gradient.GradientStops, stop => Assert.Equal(byte.MaxValue, stop.Color.A));
                }
            }
        }
    });

    [Fact]
    public void ApplyingBackgroundCreatesLocalFrozenBrushesWithoutMutatingMergedResources() => OnSta(() =>
    {
        var inheritedWindow = new SolidColorBrush(Parse(AppBackgroundThemes.Resolve("Slate").Window));
        inheritedWindow.Freeze();
        var warning = new SolidColorBrush(Parse("#FFBE67"));
        var sharedResources = new ResourceDictionary
        {
            ["WindowBrush"] = inheritedWindow,
            ["WarningBrush"] = warning
        };
        var windowResources = new ResourceDictionary();
        windowResources.MergedDictionaries.Add(sharedResources);

        AppThemeResources.Apply(windowResources, AppColorThemes.Resolve("Orange"), AppBackgroundThemes.Resolve("Slate"));

        Assert.NotSame(inheritedWindow, windowResources["WindowBrush"]);
        Assert.Same(inheritedWindow, sharedResources["WindowBrush"]);
        Assert.Same(warning, sharedResources["WarningBrush"]);
        Assert.Same(warning, windowResources["WarningBrush"]);
        Assert.Equal(Parse("#FFBE67"), warning.Color);
        Assert.False(warning.IsFrozen);
        foreach (var key in SolidBrushKeys)
        {
            Assert.True(Brush(windowResources, key).IsFrozen, key);
        }
    });

    [Theory]
    [InlineData("#5963D8D4", "#D10B1830")]
    [InlineData("#FFF2A1D4", "#FF361922")]
    public void OrbitGradientsPreserveCustomSurfaceAlphaAndProvideDepth(string accent, string background) => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        AppThemeResources.Apply(resources, AppColorThemes.All[0], AppBackgroundThemes.All[0], accent, background);

        Assert.Equal(ThemeResourceKeys.OrderBy(key => key), resources.Keys.Cast<string>().OrderBy(key => key));
        Assert.Equal(Parse(accent), Brush(resources, "AccentBrush").Color);
        Assert.Equal(Parse(background), Brush(resources, "WindowBrush").Color);
        foreach (var key in OrbitBrushKeys)
        {
            var gradient = Gradient(resources, key);
            Assert.True(gradient.IsFrozen, key);
            Assert.Equal(new Point(0, 0), gradient.StartPoint);
            Assert.Equal(BrushMappingMode.RelativeToBoundingBox, gradient.MappingMode);
            Assert.All(gradient.GradientStops, stop => Assert.Equal(Parse(background).A, stop.Color.A));
        }

        var card = Gradient(resources, "OrbitCardBrush");
        Assert.Equal(Brush(resources, "CardBrush").Color, card.GradientStops[1].Color);
        Assert.True(Luminance(card.GradientStops[2].Color) < Luminance(card.GradientStops[1].Color));
        var button = Gradient(resources, "OrbitButtonBrush");
        Assert.Equal(Brush(resources, "RaisedBrush").Color, button.GradientStops[1].Color);
        Assert.True(Luminance(button.GradientStops[0].Color) > Luminance(button.GradientStops[1].Color));
        Assert.True(Luminance(button.GradientStops[2].Color) < Luminance(button.GradientStops[1].Color));
        Assert.NotEqual(Brush(resources, "StrokeBrush").Color,
            Gradient(resources, "OrbitBorderBrush").GradientStops[1].Color);
    });

    [Theory]
    [InlineData("Aqua", "Neutral")]
    [InlineData("Pink", "Navy")]
    [InlineData("Amber", "Forest")]
    public void OrbitEdgeLightingStaysDistinctFromSurfacesAndDarkensTowardTheBottom(
        string accent, string background) => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        AppThemeResources.Apply(resources, AppColorThemes.Resolve(accent), AppBackgroundThemes.Resolve(background));
        var card = Gradient(resources, "OrbitCardBrush").GradientStops;
        var border = Gradient(resources, "OrbitBorderBrush").GradientStops;
        Assert.True(Contrast(border[0].Color, card[0].Color) >= 3,
            "The upper outline should remain distinct from the lit card surface.");
        Assert.True(Contrast(border[1].Color, card[1].Color) >= 2,
            "The outline should retain accent lighting beyond its first stop.");
        Assert.True(Luminance(border[0].Color) > Luminance(border[1].Color));
        Assert.True(Luminance(border[1].Color) > Luminance(border[2].Color));
        var window = Brush(resources, "WindowBrush").Color;
        Assert.True(ColorDistance(card[2].Color, window) < ColorDistance(card[2].Color, card[1].Color));
        Assert.True(ColorDistance(border[2].Color, window) <
            ColorDistance(border[2].Color, Brush(resources, "StrokeBrush").Color));
        var button = Gradient(resources, "OrbitButtonBrush").GradientStops;
        Assert.True(ColorDistance(button[2].Color, window) < ColorDistance(button[2].Color, button[1].Color));
    });

    [Fact]
    public void OrbitLightingUsesTheResolvedAccentAlphaWithoutChangingTheAccent() => OnSta(() =>
    {
        var transparent = new ResourceDictionary();
        var translucent = new ResourceDictionary();
        var opaque = new ResourceDictionary();
        foreach (var (resources, accent) in new[]
        {
            (transparent, "#0063D8D4"), (translucent, "#5963D8D4"), (opaque, "#FF63D8D4")
        })
        {
            AppThemeResources.Apply(resources, AppColorThemes.Resolve("Rose"),
                AppBackgroundThemes.All[0], accent, "#D10B1830");
            Assert.Equal(Parse(accent), Brush(resources, "AccentBrush").Color);
        }

        foreach (var (gradientKey, surfaceKey) in new[]
        {
            ("OrbitCardBrush", "CardBrush"), ("OrbitBorderBrush", "StrokeBrush"),
            ("OrbitSelectedBrush", "PanelBrush")
        })
        {
            var surface = Brush(transparent, surfaceKey).Color;
            Assert.Equal(surface, Gradient(transparent, gradientKey).GradientStops[0].Color);
            var partial = ColorDistance(surface, Gradient(translucent, gradientKey).GradientStops[0].Color);
            var full = ColorDistance(surface, Gradient(opaque, gradientKey).GradientStops[0].Color);
            Assert.True(partial > 0 && partial < full, gradientKey);
        }
        Assert.Equal(Brush(transparent, "StrokeBrush").Color,
            Gradient(transparent, "OrbitBorderBrush").GradientStops[1].Color);
        Assert.True(ColorDistance(Brush(translucent, "StrokeBrush").Color,
            Gradient(translucent, "OrbitBorderBrush").GradientStops[1].Color) <
            ColorDistance(Brush(opaque, "StrokeBrush").Color,
                Gradient(opaque, "OrbitBorderBrush").GradientStops[1].Color));
        Assert.Equal(Gradient(transparent, "OrbitButtonBrush").GradientStops.Select(stop => stop.Color),
            Gradient(opaque, "OrbitButtonBrush").GradientStops.Select(stop => stop.Color));
    });

    [Fact]
    public void OrbitGradientsReuseLocalResourcesAndUpdateWithCustomColors() => OnSta(() =>
    {
        var resources = new ResourceDictionary();
        var accent = AppColorThemes.All[0];
        var background = AppBackgroundThemes.All[0];
        AppThemeResources.Apply(resources, accent, background, "#5963D8D4", "#D10B1830");
        var original = ThemeBrushKeys.ToDictionary(key => key, key => resources[key]);
        var originalStops = OrbitBrushKeys.ToDictionary(key => key,
            key => Gradient(resources, key).GradientStops.Select(stop => stop.Color).ToArray());

        AppThemeResources.Apply(resources, accent, background, "#5963D8D4", "#D10B1830");
        foreach (var key in ThemeBrushKeys)
        {
            Assert.Same(original[key], resources[key]);
        }

        AppThemeResources.Apply(resources, accent, background, "#59F2A1D4", "#D10B1830");
        Assert.Same(original["OrbitButtonBrush"], resources["OrbitButtonBrush"]);
        foreach (var key in OrbitBrushKeys.Where(key => key != "OrbitButtonBrush"))
        {
            Assert.NotSame(original[key], resources[key]);
            Assert.False(originalStops[key].SequenceEqual(Gradient(resources, key).GradientStops.Select(stop => stop.Color)));
        }
        foreach (var key in OrbitBrushKeys)
        {
            var oldGradient = Assert.IsType<LinearGradientBrush>(original[key]);
            Assert.True(oldGradient.IsFrozen);
            Assert.Equal(originalStops[key], oldGradient.GradientStops.Select(stop => stop.Color));
        }

        var beforeBackground = OrbitBrushKeys.ToDictionary(key => key, key => Gradient(resources, key));
        AppThemeResources.Apply(resources, accent, background, "#59F2A1D4", "#E130182B");
        foreach (var key in OrbitBrushKeys)
        {
            Assert.NotSame(beforeBackground[key], resources[key]);
            Assert.True(Gradient(resources, key).IsFrozen);
            Assert.All(Gradient(resources, key).GradientStops, stop => Assert.Equal((byte)0xE1, stop.Color.A));
        }
    });

    [Fact]
    public void OrbitGradientsOverrideInheritedResourcesWithoutMutatingSharedBrushes() => OnSta(() =>
    {
        var shared = new ResourceDictionary();
        AppThemeResources.Apply(shared, AppColorThemes.All[0]);
        var original = OrbitBrushKeys.ToDictionary(key => key, key => Gradient(shared, key));
        var window = new ResourceDictionary();
        window.MergedDictionaries.Add(shared);
        var otherWindow = new ResourceDictionary();
        otherWindow.MergedDictionaries.Add(shared);

        AppThemeResources.Apply(window, AppColorThemes.All[0]);
        foreach (var key in OrbitBrushKeys)
        {
            Assert.NotSame(original[key], window[key]);
            Assert.Same(original[key], shared[key]);
            Assert.Same(original[key], otherWindow[key]);
            Assert.True(Gradient(window, key).IsFrozen);
        }
    });

    [Fact]
    public void NullArgumentsAreRejectedWithoutAddingResources() => OnSta(() =>
    {
        var resources = new ResourceDictionary();

        Assert.Throws<ArgumentNullException>(() => AppThemeResources.Apply(null!, AppColorThemes.All[0]));
        Assert.Throws<ArgumentNullException>(() => AppThemeResources.Apply(resources, null!));
        Assert.Throws<ArgumentNullException>(() => AppThemeResources.Apply(null!, AppColorThemes.All[0], AppBackgroundThemes.All[0]));
        Assert.Throws<ArgumentNullException>(() => AppThemeResources.Apply(resources, null!, AppBackgroundThemes.All[0]));
        Assert.Throws<ArgumentNullException>(() => AppThemeResources.Apply(resources, AppColorThemes.All[0], null!));
        Assert.Empty(resources);
    });

    private static IReadOnlyDictionary<string, string> SurfaceTokens(AppBackgroundTheme background) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = background.Window,
            ["PanelBrush"] = background.Panel,
            ["SidebarBrush"] = background.Panel,
            ["CardBrush"] = background.Card,
            ["RaisedBrush"] = background.Raised,
            ["StrokeBrush"] = background.Stroke,
            ["InputBrush"] = background.Input,
            ["HoverBrush"] = background.Hover,
            ["SliderTrackBrush"] = background.SliderTrack,
            ["ToggleTrackBrush"] = background.ToggleTrack,
            ["ScrollThumbBrush"] = background.ScrollThumb
        };

    private static void AssertContrastAtLeast(
        ResourceDictionary resources,
        string foregroundKey,
        string backgroundKey,
        double minimum,
        AppColorTheme accent,
        AppBackgroundTheme background)
    {
        var ratio = Contrast(Brush(resources, foregroundKey).Color, Brush(resources, backgroundKey).Color);
        Assert.True(
            ratio >= minimum,
            $"{accent.Name}/{background.Name}: {foregroundKey} on {backgroundKey} has {ratio:F2}:1 contrast.");
    }

    private static LinearGradientBrush Gradient(ResourceDictionary resources, string key) =>
        Assert.IsType<LinearGradientBrush>(resources[key]);

    private static double ColorDistance(Color first, Color second) =>
        Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);

    private static SolidColorBrush Brush(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]);

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Color first, Color second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(Color color) =>
        0.2126 * LinearChannel(color.R) + 0.7152 * LinearChannel(color.G) + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Theme resource STA check timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
