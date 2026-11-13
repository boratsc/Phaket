using System.Numerics;

namespace Phaket.Core.Omrl;

public sealed class NumericVersion : IComparable<NumericVersion>
{
    private readonly BigInteger[] segments;
    private NumericVersion(BigInteger[] segments) => this.segments = segments;

    public static bool TryParse(string? text, out NumericVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        if (parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))) return false;
        version = new NumericVersion(parts.Select(BigInteger.Parse).ToArray());
        return true;
    }

    public int CompareTo(NumericVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(segments.Length, other.segments.Length); i++)
        {
            var left = i < segments.Length ? segments[i] : BigInteger.Zero;
            var right = i < other.segments.Length ? other.segments[i] : BigInteger.Zero;
            var comparison = left.CompareTo(right);
            if (comparison != 0) return comparison;
        }
        return 0;
    }
}

public sealed class VersionRange
{
    private readonly (string Operator, NumericVersion Version)[] clauses;
    private VersionRange((string, NumericVersion)[] clauses) => this.clauses = clauses;

    public static bool TryParse(IEnumerable<string>? ranges, out VersionRange? range)
    {
        range = null;
        if (ranges is null) return false;
        var clauses = new List<(string, NumericVersion)>();
        var count = 0;
        foreach (var text in ranges)
        {
            count++;
            if (text == "*") continue;
            if (string.IsNullOrEmpty(text)) return false;
            var op = text.StartsWith(">=", StringComparison.Ordinal) || text.StartsWith("<=", StringComparison.Ordinal)
                ? text[..2] : text[0] is '>' or '<' or '=' ? text[..1] : "=";
            var offset = text[0] is '>' or '<' or '=' ? op.Length : 0;
            if (!NumericVersion.TryParse(text[offset..], out var version)) return false;
            clauses.Add((op, version!));
        }
        if (count == 0) return false;
        range = new VersionRange(clauses.ToArray());
        return true;
    }

    public bool Contains(NumericVersion version) => clauses.All(c => c.Operator switch
    {
        ">=" => version.CompareTo(c.Version) >= 0,
        ">" => version.CompareTo(c.Version) > 0,
        "<=" => version.CompareTo(c.Version) <= 0,
        "<" => version.CompareTo(c.Version) < 0,
        "=" => version.CompareTo(c.Version) == 0,
        _ => false
    });
}
