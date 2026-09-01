namespace Wisp.Update;

internal static class VersionResourceText
{
    private static readonly char[] PaddingCharacters = ['\0', ' '];

    internal static string? Normalize(string? value) => value?.TrimEnd(PaddingCharacters);
}
