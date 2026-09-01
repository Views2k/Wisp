namespace Wisp.Updater;

internal static class UpdatePathSafety
{
    internal static string RequireAbsolutePath(string? path, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw InvalidPath(errorCode);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath(errorCode, exception);
        }
    }

    internal static bool IsContainedBy(string candidatePath, string rootPath, bool allowRoot = false)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return allowRoot || !relativePath.Equals(".", StringComparison.Ordinal);
    }

    internal static bool PathsEqual(string firstPath, string secondPath) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(secondPath)),
            StringComparison.OrdinalIgnoreCase);

    internal static void RequireNoReparsePoints(string rootPath, string candidatePath, string errorCode)
    {
        if (!IsContainedBy(candidatePath, rootPath))
        {
            throw InvalidPath(errorCode);
        }

        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        var currentPath = rootPath;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw InvalidPath(errorCode, exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidPath(errorCode);
            }
        }
    }

    private static UpdateFailureException InvalidPath(string errorCode, Exception? innerException = null) =>
        new(
            UpdaterExitCode.InvalidRequest,
            errorCode,
            "The update request contains an unsafe or invalid path.",
            innerException);
}
