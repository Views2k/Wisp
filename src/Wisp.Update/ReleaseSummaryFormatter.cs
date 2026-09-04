using System.Globalization;
using System.Text;

namespace Wisp.Update;

internal static class ReleaseSummaryFormatter
{
    internal const int MaximumCharacters = 480;
    private const int MaximumLines = 6;
    private const int MaximumSourceCharacters = 8192;

    internal static string Format(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var source = body.AsSpan(0, Math.Min(body.Length, MaximumSourceCharacters));
        var plain = StripMarkup(source);
        var result = new StringBuilder(Math.Min(plain.Length, MaximumCharacters));
        var lines = 0;
        var truncated = body.Length > MaximumSourceCharacters;

        foreach (var rawLine in plain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (lines == MaximumLines)
            {
                truncated = true;
                break;
            }

            var separatorLength = result.Length == 0 ? 0 : Environment.NewLine.Length;
            var available = MaximumCharacters - result.Length - separatorLength;
            if (available <= 0)
            {
                truncated = true;
                break;
            }

            if (separatorLength != 0)
            {
                result.AppendLine();
            }

            if (line.Length > available)
            {
                result.Append(line.AsSpan(0, available));
                truncated = true;
                break;
            }

            result.Append(line);
            lines++;
        }

        if (truncated && result.Length != 0)
        {
            if (result.Length == MaximumCharacters)
            {
                result.Length--;
            }

            result.Append('…');
        }

        return result.ToString();
    }

    private static string StripMarkup(ReadOnlySpan<char> source)
    {
        var result = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (source[index..].StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                source[index..].StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                while (index + 1 < source.Length && !char.IsWhiteSpace(source[index + 1]))
                {
                    index++;
                }

                continue;
            }

            if (character == '<')
            {
                var closing = source[(index + 1)..].IndexOf('>');
                if (closing >= 0)
                {
                    index += closing + 1;
                    continue;
                }
            }

            var labelStart = character == '['
                ? index + 1
                : character == '!' && index + 1 < source.Length && source[index + 1] == '['
                    ? index + 2
                    : -1;
            if (labelStart >= 0)
            {
                var labelEndOffset = source[labelStart..].IndexOf(']');
                if (labelEndOffset >= 0)
                {
                    var labelEnd = labelStart + labelEndOffset;
                    var destinationStart = labelEnd + 1;
                    if (destinationStart < source.Length && source[destinationStart] == '(')
                    {
                        var destinationEndOffset = source[(destinationStart + 1)..].IndexOf(')');
                        if (destinationEndOffset >= 0)
                        {
                            result.Append(source[labelStart..labelEnd]);
                            index = destinationStart + destinationEndOffset + 1;
                            continue;
                        }
                    }
                }
            }

            if (character is '`' or '*' or '~')
            {
                continue;
            }

            if (character is '\r' or '\n' or '\t' ||
                (!char.IsControl(character) && char.GetUnicodeCategory(character) != UnicodeCategory.Format))
            {
                result.Append(character);
            }
            else
            {
                result.Append(' ');
            }
        }

        return result.ToString();
    }

    private static string NormalizeLine(string value)
    {
        var line = value.Trim();
        while (line.Length != 0 && line[0] is '#' or '>')
        {
            line = line[1..].TrimStart();
        }

        if (line.Length >= 2 && (line[0] is '-' or '+') && char.IsWhiteSpace(line[1]))
        {
            line = line[2..].TrimStart();
        }

        var normalized = new StringBuilder(line.Length);
        var previousWasSpace = false;
        foreach (var character in line)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    normalized.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                normalized.Append(character);
                previousWasSpace = false;
            }
        }

        return normalized.ToString().Trim();
    }
}
