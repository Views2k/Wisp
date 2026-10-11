using System.IO;

namespace Wisp.App.Supplementary;

internal static class SupplementaryPaths
{
    // A redirected Windows profile is trusted as the storage location. Links inside Wisp's own subtree are not.
    internal static void CheckDirectory(string path, string? ownedRoot)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot ?? Path.GetPathRoot(path)!));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Supplementary storage must stay within its owned directory.");
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var attributes = directory.Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Supplementary storage cannot follow an owned directory link.");
            if (directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new IOException("Supplementary storage has no matching owned directory.");
    }
}
