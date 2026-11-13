using System.Text;

namespace Phaket.Core.Platforms;

// Minimal Valve KeyValues (VDF/ACF) text parser — read-only, no execution.
// Supports quoted/unquoted tokens, nested objects, // comments and escapes.
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var pos = 0;
        SkipWhitespace(text, ref pos);
        var root = ParseObject(text, ref pos, expectBrace: false);
        return root;
    }

    private static VdfNode ParseObject(string text, ref int pos, bool expectBrace)
    {
        var node = new VdfNode();
        if (expectBrace)
        {
            Expect(text, ref pos, '{');
        }
        while (true)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length)
            {
                if (expectBrace) throw new FormatException("Unterminated VDF object");
                break;
            }
            if (text[pos] == '}')
            {
                if (!expectBrace) throw new FormatException("Unexpected '}' in VDF");
                pos++;
                break;
            }
            var key = ReadToken(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == '{')
            {
                node.Children[key] = ParseObject(text, ref pos, expectBrace: true);
            }
            else
            {
                node.Values[key] = ReadToken(text, ref pos);
            }
        }
        return node;
    }

    private static string ReadToken(string text, ref int pos)
    {
        if (pos >= text.Length) throw new FormatException("Unexpected end of VDF");
        if (text[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos++;
                    sb.Append(text[pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        var c => c,
                    });
                }
                else
                {
                    sb.Append(text[pos]);
                }
                pos++;
            }
            if (pos >= text.Length) throw new FormatException("Unterminated VDF string");
            pos++;
            return sb.ToString();
        }
        var start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not '{' and not '}')
            pos++;
        if (pos == start) throw new FormatException("Empty VDF token");
        return text[start..pos];
    }

    private static void Expect(string text, ref int pos, char c)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length || text[pos] != c)
            throw new FormatException($"Expected '{c}' in VDF");
        pos++;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) { pos++; continue; }
            if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
    }
}

public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
    public VdfNode? Child(string key) => Children.TryGetValue(key, out var child) ? child : null;
}
