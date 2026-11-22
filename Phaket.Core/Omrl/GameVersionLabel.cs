using System.Globalization;
using System.Text.RegularExpressions;

namespace Phaket.Core.Omrl;

// PAK-96: Among Us version labels are dates ("2026.8.18"). This mirrors the
// server rule (PhaketServer BuildCatalog.CanonicalLabel) exactly: three numbers
// separated by dots or dashes, no leading zeros in the stored label, and an
// impossible calendar date is not a label. Everything else stays as written.
public static class GameVersionLabel
{
    private static readonly Regex Date = new(@"^(\d{4})[-.](\d{1,2})[-.](\d{1,2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Canonical "Y.M.D" for a date label; null when the text is not a valid date label.
    public static string? Canonical(string? value)
    {
        if (value is null) return null;
        var match = Date.Match(value.Trim());
        if (!match.Success) return null;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return year is >= 2000 and <= 2100 && month is >= 1 and <= 12 && day >= 1
            && day <= DateTime.DaysInMonth(year, month)
                ? $"{year}.{month}.{day}" : null;
    }

    // Date labels become canonical ("2026-8-18" and "2026.08.18" -> "2026.8.18");
    // labels that are not dates ("18.0.0", "ToU") stay as written; a date-shaped
    // but impossible label ("2026-99-99") is rejected instead of guessed.
    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return Date.IsMatch(trimmed) ? Canonical(trimmed) : trimmed;
    }

    // A version range keeps its operator; only the label part is normalized.
    public static string NormalizeRange(string range)
    {
        var op = range.StartsWith(">=", StringComparison.Ordinal) || range.StartsWith("<=", StringComparison.Ordinal)
            ? range[..2]
            : range.Length > 0 && range[0] is '>' or '<' or '=' ? range[..1] : "";
        return Normalize(range[op.Length..]) is { } label ? op + label : range;
    }

    public static bool Matches(string? left, string? right) =>
        left is not null && right is not null
        && (Normalize(left) ?? left) == (Normalize(right) ?? right);
}