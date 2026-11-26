using System.Text;
using System.Text.Json;

namespace Phaket.Core.Platforms.Aurelia;

// Aurelia's `--json` contract (v0.3.1): stdout carries one compact `result` line per command
// (`aurelia list` itself is a pretty-printed array); progress/QR events go to stderr as NDJSON.
public static class AureliaJson
{
    // The tracing logger colourises output; `ESC [ digits ; digits letter` covers those codes.
    public static string StripAnsi(string text)
    {
        if (!text.Contains('\u001b')) return text;
        var output = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u001b' || i + 1 >= text.Length || text[i + 1] != '[') { output.Append(text[i]); continue; }
            int j = i + 2;
            while (j < text.Length && (char.IsAsciiDigit(text[j]) || text[j] == ';')) j++;
            if (j >= text.Length || !char.IsAsciiLetter(text[j])) { output.Append(text[i]); continue; }
            i = j;
        }
        return output.ToString();
    }

    // Every complete top-level JSON value in `text`, in order. A value is returned only once its
    // brackets balance, so a chunk cut in half never counts; a malformed value yields null so a
    // caller can tell "broken" from "absent".
    public static IReadOnlyList<JsonElement?> Values(string text)
    {
        var values = new List<JsonElement?>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{' && text[i] != '[') { i++; continue; }
            int end = MatchingEnd(text, i);
            if (end < 0) break;
            try { values.Add(JsonDocument.Parse(text[i..(end + 1)]).RootElement.Clone()); }
            catch (JsonException) { values.Add(null); }
            i = end + 1;
        }
        return values;
    }

    // The last value, a broken one included: "the last thing Aurelia said" is what Heroic reads.
    public static JsonElement? Last(string text) => Values(text).LastOrDefault();

    // The index of the bracket closing the value opened at `start`, or -1 if it never closes.
    private static int MatchingEnd(string text, int start)
    {
        int depth = 0;
        int i = start;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{' || c == '[')
            {
                depth++;
                i++;
            }
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) return i;
                i++;
            }
            else if (c == '"')
            {
                int next = SkipString(text, i);
                if (next < 0) return -1;
                i = next + 1;
            }
            else i++;
        }
        return -1;
    }

    // Index of the closing quote, or -1 when the string never ends.
    private static int SkipString(string text, int start)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        return -1;
    }
}