namespace Wisp.App;

public sealed record AppColorTheme(string Name, string Accent)
{
    public override string ToString() => Name;
}

public static class AppColorThemes
{
    public const string DefaultName = "Aqua";

    public static IReadOnlyList<AppColorTheme> All { get; } = Array.AsReadOnly(new[]
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
    });

    public static AppColorTheme Resolve(string? name)
    {
        var candidate = name?.Trim();
        return All.FirstOrDefault(theme =>
            string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    public static string NormalizeName(string? name) => Resolve(name).Name;
}
