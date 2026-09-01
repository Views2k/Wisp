namespace Wisp.App;

internal static class SetupPresentation
{
    public static bool UseStackedAppearance(double contentWidth) =>
        !double.IsFinite(contentWidth) || contentWidth < 740;
}
