namespace Wisp.Core;

public static class ForzaProcessIdentityPolicy
{
    private const string NormalizedGameName = "forzahorizon6";

    public static bool Matches(
        string? processName,
        string? windowTitle,
        string? executablePath,
        IEnumerable<string>? knownGameDirectories = null)
    {
        var normalizedProcessName = Normalize(processName);
        var recognizedWindowHost = IsRecognizedWindowHost(normalizedProcessName);
        if (string.Equals(normalizedProcessName, NormalizedGameName, StringComparison.Ordinal) ||
            (recognizedWindowHost &&
             string.Equals(windowTitle?.Trim(), "Forza Horizon 6", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!recognizedWindowHost ||
            string.IsNullOrWhiteSpace(executablePath) ||
            knownGameDirectories is null)
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return false;
        }

        var normalizedExecutableDirectory = Path.TrimEndingDirectorySeparator(executableDirectory);
        return knownGameDirectories.Any(directory =>
            !string.IsNullOrWhiteSpace(directory) &&
            string.Equals(
                normalizedExecutableDirectory,
                Path.TrimEndingDirectorySeparator(directory),
                StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRecognizedWindowHost(string? processName)
    {
        var normalizedProcessName = Normalize(processName);
        return normalizedProcessName is
            "applicationframehost" or
            "gamehost" or
            "gamelaunchhelper" or
            "xgamehelper";
    }

    private static string Normalize(string? value) => string.Concat(
        (value ?? string.Empty).Where(character => char.IsLetterOrDigit(character)))
        .ToLowerInvariant();
}
