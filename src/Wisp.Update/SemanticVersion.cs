using System.Globalization;
using System.Text.RegularExpressions;

namespace Wisp.Update;

public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex VersionPattern = new(
        "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex TagPattern = new(
        "\\Av(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public SemanticVersion(int major, int minor, int patch)
    {
        ValidateComponent(major, nameof(major));
        ValidateComponent(minor, nameof(minor));
        ValidateComponent(patch, nameof(patch));
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException("The version must use strict X.Y.Z semantic-version syntax.");
        }

        return version;
    }

    public static SemanticVersion ParseTag(string value)
    {
        if (!TryParseTag(value, out var version))
        {
            throw new FormatException("The release tag must use strict vX.Y.Z semantic-version syntax.");
        }

        return version;
    }

    public static bool TryParse(string? value, out SemanticVersion version) =>
        TryParseMatch(value, VersionPattern, out version);

    public static bool TryParseTag(string? value, out SemanticVersion version) =>
        TryParseMatch(value, TagPattern, out version);

    public static SemanticVersion FromSystemVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0 || version.Revision is < -1 or > 0)
        {
            throw new ArgumentException("The application version must be X.Y.Z or X.Y.Z.0.", nameof(version));
        }

        return new SemanticVersion(version.Major, version.Minor, version.Build);
    }

    public Version ToSystemVersion() => new(Major, Minor, Patch, 0);

    public int CompareTo(SemanticVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0 ? minorComparison : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public string ToTagString() => $"v{this}";

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParseMatch(string? value, Regex pattern, out SemanticVersion version)
    {
        version = default;
        if (value is null || value.Length is 0 or > 32)
        {
            return false;
        }

        var match = pattern.Match(value);
        if (!match.Success ||
            !TryParseComponent(match.Groups[1].Value, out var major) ||
            !TryParseComponent(match.Groups[2].Value, out var minor) ||
            !TryParseComponent(match.Groups[3].Value, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    private static bool TryParseComponent(string value, out int component) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out component) &&
        component <= ushort.MaxValue;

    private static void ValidateComponent(int component, string parameterName)
    {
        if ((uint)component > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName,
                "Version components must fit the Windows PE version resource range.");
        }
    }
}
