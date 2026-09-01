namespace Wisp.App;

public sealed record AppBackgroundTheme(
    string Name,
    string Window,
    string Panel,
    string Card,
    string Raised,
    string Stroke,
    string Input,
    string Hover,
    string SliderTrack,
    string ToggleTrack,
    string ScrollThumb)
{
    public override string ToString() => Name;
}

public static class AppBackgroundThemes
{
    public const string DefaultName = "Neutral";

    public static IReadOnlyList<AppBackgroundTheme> All { get; } = Array.AsReadOnly(new[]
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
    });

    public static AppBackgroundTheme Resolve(string? name)
    {
        var candidate = name?.Trim();
        return All.FirstOrDefault(theme =>
            string.Equals(theme.Name, candidate, StringComparison.OrdinalIgnoreCase)) ?? All[0];
    }

    public static string NormalizeName(string? name) => Resolve(name).Name;
}
