using Wisp.Core;

namespace Wisp.App;

// Metadata belongs to an owner; captions belong to individual windows. A failed
// caption on one window must not hide another matching window from that owner.
internal sealed class ForzaWindowOwnerCache(Func<int, string?> readName, Func<int, string?> readPath)
{
    private readonly Dictionary<int, string?> _names = [];
    private readonly Dictionary<int, string?> _paths = [];
    private readonly HashSet<int> _matched = [];

    internal bool Matches(int processId, string caption)
    {
        if (processId <= 0) return false;
        if (!_names.TryGetValue(processId, out var name))
        {
            name = readName(processId);
            _names[processId] = name;
        }
        if (!ForzaProcessIdentityPolicy.Matches(name, caption, null)) return false;
        _matched.Add(processId);
        return true;
    }

    internal string? GetMatchedExecutablePath(int processId)
    {
        if (!_matched.Contains(processId)) return null;
        if (!_paths.TryGetValue(processId, out var path))
        {
            path = readPath(processId);
            _paths[processId] = path;
        }
        return path;
    }
}
