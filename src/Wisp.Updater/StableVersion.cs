using System.Globalization;
using Wisp.Update;

namespace Wisp.Updater;

internal readonly record struct StableVersion(int Major, int Minor, int Patch) : IComparable<StableVersion>
{
    internal static bool TryParse(string? value, out StableVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3
            || !TryParsePart(parts[0], out var major)
            || !TryParsePart(parts[1], out var minor)
            || !TryParsePart(parts[2], out var patch))
        {
            return false;
        }

        version = new StableVersion(major, minor, patch);
        return true;
    }

    internal bool MatchesVersionResource(string? value)
    {
        var normalized = VersionResourceText.Normalize(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var parts = normalized.Split('.', StringSplitOptions.None);
        if (parts.Length is not (3 or 4)
            || !TryParseResourcePart(parts[0], out var major)
            || !TryParseResourcePart(parts[1], out var minor)
            || !TryParseResourcePart(parts[2], out var patch)
            || (parts.Length == 4 && (!TryParseResourcePart(parts[3], out var revision) || revision != 0)))
        {
            return false;
        }

        return major == Major && minor == Minor && patch == Patch;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public int CompareTo(StableVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0 ? minorComparison : Patch.CompareTo(other.Patch);
    }

    private static bool TryParsePart(string part, out int value)
    {
        value = 0;
        return part.Length > 0
            && (part.Length == 1 || part[0] != '0')
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }

    private static bool TryParseResourcePart(string part, out int value)
    {
        value = 0;
        return part.Length > 0
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }
}
