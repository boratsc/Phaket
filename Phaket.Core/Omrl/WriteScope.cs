namespace Phaket.Core.Omrl;

// Containment rules for archive.extract `include` globs. A pattern is only
// accepted when every path it can match already lies in a writable area of the
// game definition, so an include list can never widen the write boundary.
// Deliberately conservative: single-segment wildcards must match exactly (or
// be covered by a writable `*`/`**`). Generated recipes use writableAreas
// verbatim, so this is enough without a full glob-automata inclusion check.
public static class WriteScope
{
    public static bool IsSafePattern(string pattern) =>
        pattern.Length is > 0 and <= 512
        && !pattern.StartsWith('/')
        && !pattern.Contains('\\')
        && !pattern.Contains(':')
        && !pattern.Any(char.IsControl)
        && !pattern.Split('/').Any(segment => segment is "" or "." or "..");

    public static bool Covers(GameDefinition game, string relativePattern) =>
        game.WritableAreas.Any(area => Covers(area, relativePattern));

    private static bool Covers(string writable, string include) =>
        Covers(Segments(writable), 0, Segments(include), 0);

    private static string[] Segments(string pattern) => pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Covers(string[] writable, int w, string[] include, int i)
    {
        if (i == include.Length)
            return w == writable.Length || w == writable.Length - 1 && writable[w] == "**";
        if (w == writable.Length) return false;
        if (writable[w] == "**")
            return w == writable.Length - 1 || Covers(writable, w + 1, include, i)
                || Covers(writable, w, include, i + 1);
        // A `**` in the include but not in the writable area is broader than allowed.
        if (include[i] == "**" || !SegmentCovers(writable[w], include[i])) return false;
        return Covers(writable, w + 1, include, i + 1);
    }

    private static bool SegmentCovers(string writable, string include) =>
        writable == "*" || writable == include;
}