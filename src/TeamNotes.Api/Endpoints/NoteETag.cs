using Microsoft.Extensions.Primitives;

namespace TeamNotes.Api.Endpoints;

/// <summary>
/// Strong, version-based ETags: a note at version 3 has ETag <c>"3"</c>. Writes must send exactly one such value in
/// If-Match. Weak tags, the <c>*</c> wildcard and lists are rejected on purpose: the client must name the version it
/// edited, otherwise the lost-update protection is meaningless.
/// </summary>
public static class NoteETag
{
    public static string Format(int version) => $"\"{version}\"";

    public enum ParseOutcome
    {
        Missing,
        Malformed,
        Valid,
    }

    public static ParseOutcome TryParseIfMatch(StringValues headerValues, out int version)
    {
        version = 0;
        if (StringValues.IsNullOrEmpty(headerValues))
        {
            return ParseOutcome.Missing;
        }

        if (headerValues.Count != 1)
        {
            return ParseOutcome.Malformed;
        }

        var value = headerValues[0]!.Trim();
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
        {
            return ParseOutcome.Malformed;
        }

        var digits = value[1..^1];
        // Canonical form only, exactly as Format produces it: ASCII digits, no sign, spaces or leading zero, positive,
        // within int range. ETags compare as opaque strings, so "01" is not the same tag as "1".
        if (!digits.All(char.IsAsciiDigit) || digits[0] == '0' || !int.TryParse(digits, out version))
        {
            version = 0;
            return ParseOutcome.Malformed;
        }

        return ParseOutcome.Valid;
    }
}
