namespace Wisp.App;

public sealed record BoostGaugeTheme(string Name, string Low, string Mid, string High)
{
    public override string ToString() => Name;
}

public static class BoostGaugeThemes
{
    public const string DefaultName = "Aqua";

    public static IReadOnlyList<BoostGaugeTheme> All { get; } = Array.AsReadOnly(new[]
    {
        new BoostGaugeTheme("Aqua",   "#67E9F1", "#3B9CF4", "#805CFA"),
        new BoostGaugeTheme("Mint",   "#7FE7C2", "#49BFD8", "#667EF4"),
        new BoostGaugeTheme("Teal",   "#55D7CA", "#318FDC", "#6B5DEB"),
        new BoostGaugeTheme("Green",  "#8ADD91", "#45C8B0", "#478CE8"),
        new BoostGaugeTheme("Blue",   "#75C8FF", "#3D7DFA", "#795AF0"),
        new BoostGaugeTheme("Indigo", "#78A2FF", "#5967F5", "#9A5CF1"),
        new BoostGaugeTheme("Purple", "#8F8BFF", "#8C5CEB", "#D05AE8"),
        new BoostGaugeTheme("Plum",   "#B97BE6", "#9D56D8", "#EA5DCA"),
        new BoostGaugeTheme("Pink",   "#F08FCF", "#CB69E4", "#755FFF"),
        new BoostGaugeTheme("Rose",   "#F18FAE", "#D96BCD", "#845FF1"),
        new BoostGaugeTheme("Red",    "#F58D93", "#CE64CF", "#795DF0"),
        new BoostGaugeTheme("Orange", "#F5AE78", "#E36EBC", "#775FF2"),
        new BoostGaugeTheme("Amber",  "#E5C26A", "#D27CC5", "#755FF0"),
        new BoostGaugeTheme("Sand",   "#D6C6A5", "#AF8BCF", "#6F76ED"),
        new BoostGaugeTheme("Slate",  "#A6BED0", "#718FD2", "#745FE8"),
        new BoostGaugeTheme("Stock",  "#ECEFF4", "#ECEFF4", "#ECEFF4")
    });

    public static BoostGaugeTheme Resolve(string? name)
    {
        var candidate = name?.Trim();
        return All.FirstOrDefault(theme =>
            string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    public static string NormalizeName(string? name) => Resolve(name).Name;
}
