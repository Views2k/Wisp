using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
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

    private static readonly string[] ThemeBrushKeys =
        NeutralTokens.Keys.Concat(new[] { "AccentBrush", "AccentBlueBrush" }).ToArray();

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
    public void BackgroundCatalogContainsExactlyTheFifteenApprovedPalettes()
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
            new AppBackgroundTheme("Cyan",    "#071318", "#0B1D25", "#102832", "#15323F", "#234857", "#09171D", "#183747", "#2A4E60", "#2F5569", "#426B80")
        };

        Assert.Equal(expected, AppBackgroundThemes.All);
        Assert.Equal(15, AppBackgroundThemes.All.Count);
        Assert.Equal(15, AppBackgroundThemes.All.Select(theme => theme.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(15, AppBackgroundThemes.All.Distinct().Count());
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

            Assert.Equal(ThemeBrushKeys.Length, resources.Count);
            foreach (var (key, expected) in NeutralTokens)
            {
                Assert.Equal(Parse(expected), Brush(resources, key).Color);
            }

            var accent = Parse(theme.Accent);
            Assert.Equal(accent, Brush(resources, "AccentBrush").Color);
            Assert.Equal(accent, Brush(resources, "AccentBlueBrush").Color);
            foreach (var key in ThemeBrushKeys)
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
        Assert.Equal(ThemeBrushKeys.Length, legacyResources.Count);
        Assert.Equal(ThemeBrushKeys.Length, explicitResources.Count);
        foreach (var key in ThemeBrushKeys)
        {
            Assert.Equal(Brush(legacyResources, key).Color, Brush(explicitResources, key).Color);
        }

        foreach (var (key, expected) in NeutralTokens)
        {
            Assert.Equal(Parse(expected), Brush(explicitResources, key).Color);
        }
    });

    [Fact]
    public void EveryBackgroundMapsAllSemanticSurfaceTokens() => OnSta(() =>
    {
        var accent = AppColorThemes.Resolve("Aqua");
        foreach (var background in AppBackgroundThemes.All)
        {
            var resources = new ResourceDictionary();
            AppThemeResources.Apply(resources, accent, background);

            Assert.Equal(ThemeBrushKeys.Length, resources.Count);
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
        var warning = new SolidColorBrush(Parse("#FFBE67"));
        var nativeHudBrush = new SolidColorBrush(Colors.White);
        var sharedResources = new ResourceDictionary
        {
            ["WindowBrush"] = originalBackground,
            ["AccentBrush"] = originalAccent,
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
        var originalBrushes = ThemeBrushKeys.ToDictionary(key => key, key => Brush(resources, key));
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

                foreach (var key in ThemeBrushKeys)
                {
                    Assert.True(Brush(resources, key).IsFrozen, $"{accent.Name}/{background.Name}: {key} is not frozen.");
                    Assert.Equal(byte.MaxValue, Brush(resources, key).Color.A);
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
        foreach (var key in ThemeBrushKeys)
        {
            Assert.True(Brush(windowResources, key).IsFrozen, key);
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

    private static SolidColorBrush Brush(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]);

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

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
