using System.IO;
using System.Security;
using Wisp.Update;

namespace Wisp.App;

internal static class ApplicationUpdateStaging
{
    private const int MaximumRetainedAttempts = 3;
    private const long MaximumRetainedBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan MaximumAttemptAge = TimeSpan.FromDays(14);

    internal static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wisp",
        "Updates");

    internal static string CreateAttemptDirectory(SemanticVersion version)
    {
        // Keep direct callers bounded even when no update check preceded them.
        TryPrune(retainedAttemptDirectory: null);

        var root = Path.GetFullPath(RootDirectory);
        Directory.CreateDirectory(root);
        RequireRegularDirectory(root);

        var versionDirectory = Path.Combine(root, version.ToString());
        Directory.CreateDirectory(versionDirectory);
        RequireRegularDirectory(versionDirectory);

        var attemptDirectory = Path.Combine(versionDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attemptDirectory);
        RequireRegularDirectory(attemptDirectory);
        return attemptDirectory;
    }

    internal static void TryPrune(string? retainedAttemptDirectory)
    {
        try
        {
            PruneRoot(
                RootDirectory,
                retainedAttemptDirectory,
                DateTimeOffset.UtcNow,
                MaximumAttemptAge,
                MaximumRetainedAttempts,
                MaximumRetainedBytes);
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            // Staging cleanup is best effort and must never prevent an update check.
        }
    }

    internal static void PruneRoot(
        string rootDirectory,
        string? retainedAttemptDirectory,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge,
        int maximumAttempts,
        long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (maximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || !IsRegularDirectory(root))
        {
            return;
        }

        string? retained = null;
        if (!string.IsNullOrWhiteSpace(retainedAttemptDirectory))
        {
            if (!TryResolveAttemptPath(root, retainedAttemptDirectory, out retained, out _))
            {
                // An invalid preserve path is ambiguous. Leave the entire root untouched.
                return;
            }
        }

        var attempts = DiscoverAttempts(root);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoffUtc = nowUtc.UtcDateTime - maximumAge;
        foreach (var attempt in attempts)
        {
            if (!SamePath(attempt.Path, retained) && attempt.LastWriteUtc < cutoffUtc)
            {
                planned.Add(attempt.Path);
            }
        }

        var survivors = attempts
            .Where(attempt => !planned.Contains(attempt.Path))
            .OrderBy(attempt => attempt.LastWriteUtc)
            .ThenBy(attempt => attempt.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retainedBytes = SaturatingTotal(survivors);
        var retainedCount = survivors.Count;
        foreach (var attempt in survivors)
        {
            if (retainedCount <= maximumAttempts && retainedBytes <= maximumBytes)
            {
                break;
            }
            if (SamePath(attempt.Path, retained))
            {
                continue;
            }

            planned.Add(attempt.Path);
            retainedCount--;
            retainedBytes = Math.Max(0, retainedBytes - attempt.SizeBytes);
        }

        foreach (var attempt in attempts
                     .Where(attempt => planned.Contains(attempt.Path))
                     .OrderBy(attempt => attempt.LastWriteUtc))
        {
            TryDeleteManagedAttempt(root, attempt.Path);
        }
    }

    internal static void TryDeleteAttemptDirectory(string? attemptDirectory)
    {
        if (string.IsNullOrWhiteSpace(attemptDirectory))
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root) || !IsRegularDirectory(root))
            {
                return;
            }
            TryDeleteManagedAttempt(root, attemptDirectory);
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            // A later bounded cleanup pass can retry an interrupted download directory.
        }
    }

    internal static string StageUpdater(string attemptDirectory)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Wisp.Updater.exe");
        var destination = Path.Combine(RequireAttemptDirectory(attemptDirectory), "Wisp.Updater.exe");
        RequireRegularFile(source, "The installed update helper is unavailable.");
        if (File.Exists(destination))
        {
            RequireRegularFile(destination, "The staged update helper is invalid.");
            File.Delete(destination);
        }
        File.Copy(source, destination, overwrite: false);
        RequireRegularFile(destination, "The staged update helper is unavailable.");
        return destination;
    }

    internal static string RequireAttemptDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!TryResolveAttemptPath(root, path, out var fullPath, out _))
        {
            throw new InvalidOperationException("The update attempt is outside Wisp's staging directory.");
        }

        RequireRegularDirectory(root);
        RequireRegularDirectory(Path.GetDirectoryName(fullPath)!);
        RequireRegularDirectory(fullPath);
        return fullPath;
    }

    private static List<ManagedAttempt> DiscoverAttempts(string root)
    {
        var attempts = new List<ManagedAttempt>();
        foreach (var versionEntry in GetEntries(root))
        {
            if (!IsRegularDirectory(versionEntry) ||
                !SemanticVersion.TryParse(Path.GetFileName(versionEntry), out var version))
            {
                continue;
            }

            foreach (var attemptEntry in GetEntries(versionEntry))
            {
                if (TryInspectAttempt(root, attemptEntry, version, out var attempt))
                {
                    attempts.Add(attempt);
                }
            }
        }
        return attempts;
    }

    private static bool TryInspectAttempt(
        string root,
        string path,
        SemanticVersion expectedVersion,
        out ManagedAttempt attempt)
    {
        attempt = default;
        try
        {
            if (!TryResolveAttemptPath(root, path, out var fullPath, out var version) ||
                version != expectedVersion ||
                !IsRegularDirectory(fullPath))
            {
                return false;
            }

            var files = new List<string>();
            long size = 0;
            var lastWriteUtc = Directory.GetLastWriteTimeUtc(fullPath);
            foreach (var entry in GetEntries(fullPath))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !IsExpectedAttemptFile(Path.GetFileName(entry), version))
                {
                    return false;
                }

                var file = new FileInfo(entry);
                size = SaturatingAdd(size, file.Length);
                if (file.LastWriteTimeUtc > lastWriteUtc)
                {
                    lastWriteUtc = file.LastWriteTimeUtc;
                }
                files.Add(file.FullName);
            }

            attempt = new ManagedAttempt(fullPath, files, size, lastWriteUtc);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static void TryDeleteManagedAttempt(string root, string path)
    {
        try
        {
            if (!TryResolveAttemptPath(root, path, out var fullPath, out var version) ||
                !TryInspectAttempt(root, fullPath, version, out var inspected))
            {
                return;
            }

            // Re-enumerate immediately before mutation. Any new or changed entry
            // makes the attempt ineligible rather than broadening deletion scope.
            var currentEntries = GetEntries(fullPath);
            if (currentEntries.Length != inspected.Files.Count ||
                currentEntries.Any(entry => !inspected.Files.Contains(
                    Path.GetFullPath(entry), StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }

            foreach (var file in inspected.Files)
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return;
                }
            }
            foreach (var file in inspected.Files)
            {
                File.Delete(file);
            }

            if (GetEntries(fullPath).Length == 0)
            {
                Directory.Delete(fullPath, recursive: false);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            // Never widen deletion or block the app when a directory changes mid-pass.
        }
    }

    private static bool TryResolveAttemptPath(
        string root,
        string path,
        out string fullPath,
        out SemanticVersion version)
    {
        fullPath = string.Empty;
        version = default;
        try
        {
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var relative = Path.GetRelativePath(root, candidate);
            var components = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathFullyQualified(relative) ||
                components.Length != 2 ||
                components.Any(component => component is "." or "..") ||
                !SemanticVersion.TryParse(components[0], out version) ||
                !IsGuidDirectoryName(components[1]))
            {
                return false;
            }

            var expected = Path.GetFullPath(Path.Combine(root, components[0], components[1]));
            if (!SamePath(candidate, expected))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static bool IsExpectedAttemptFile(string name, SemanticVersion version)
    {
        var installerName = $"Wisp-Setup-{version}.exe";
        return string.Equals(name, installerName, StringComparison.Ordinal) ||
               string.Equals(name, "Wisp.Updater.exe", StringComparison.Ordinal) ||
               string.Equals(name, "apply-request.json", StringComparison.Ordinal) ||
               IsGuidWrapped(name, $".{installerName}.", ".partial") ||
               IsGuidWrapped(name, ".apply-request.", ".tmp");
    }

    private static bool IsGuidWrapped(string value, string prefix, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var identifier = value.AsSpan(prefix.Length, value.Length - prefix.Length - suffix.Length);
        return identifier.Length == 32 && Guid.TryParseExact(identifier, "N", out _);
    }

    private static bool IsGuidDirectoryName(string value) =>
        value.Length == 32 && Guid.TryParseExact(value, "N", out _);

    private static string[] GetEntries(string path)
        => Directory.GetFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly);

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                   FileAttributes.Directory;
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            return false;
        }
    }

    private static long SaturatingTotal(IEnumerable<ManagedAttempt> attempts)
    {
        long total = 0;
        foreach (var attempt in attempts)
        {
            total = SaturatingAdd(total, attempt.SizeBytes);
        }
        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException;

    private static void RequireRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        {
            throw new IOException("The Wisp update staging directory is not a regular directory.");
        }
    }

    private static void RequireRegularFile(string path, string message)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(message);
        }
    }

    private readonly record struct ManagedAttempt(
        string Path,
        IReadOnlyList<string> Files,
        long SizeBytes,
        DateTime LastWriteUtc);
}
