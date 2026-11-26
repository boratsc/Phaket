using System.Buffers.Binary;
using System.Text;

namespace Phaket.Core.Platforms.SteamShortcuts;

// Valve's binary KeyValues, the format of shortcuts.vdf.
//
// The writer is a port of steam-shortcut-editor 3.1.3 (lib/writer.js), MIT, Copyright (c) 2016
// Tommy Irish — https://github.com/tirish/steam-shortcut-editor — which Heroic uses, and it
// reproduces that library's bytes exactly. A member is a type byte (0x00 object, 0x01 string,
// 0x02 int32, 0x07 int64), a NUL-terminated key, then the value: a string plus its own 0x00,
// an int32/int64 little endian, or an object's members. Every object ends with 0x08 — including
// the root, whose members are written the same way — so a file's last bytes are 08 08 08.
//
// The reader walks that stream: a 0x08 at member position ends the dictionary and the parent
// reads the next type byte, so 00 "1" 00 starts the next entry and 00 "shortcuts" 00 the section.
// The 0x08 is what closes an object, so a separator is never mistaken for an object type. After
// the root's own 0x08 the file carries one more 0x08 (Steam pads it with 0x00 bytes); a file that
// does not close cleanly is refused, never replaced.
public enum BinaryVdfKind : byte { Dictionary = 0x00, String = 0x01, Int32 = 0x02, Int64 = 0x07, End = 0x08 }

public sealed class BinaryVdfMember
{
    public required BinaryVdfKind Kind { get; init; }
    public required string Key { get; init; }
    // String bytes without the terminator, or the int32/int64 value; dictionaries keep none.
    public byte[]? RawValue { get; init; }
    public BinaryVdfNode? Node { get; init; }
}

public sealed class BinaryVdfNode
{
    public List<BinaryVdfMember> Members { get; } = [];

    // Last member wins, like Steam reads it.
    public BinaryVdfMember? Find(string key) =>
        Members.LastOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => Find(key)?.RawValue is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public int GetInt32(string key) =>
        Find(key)?.RawValue is { Length: 4 } bytes ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : 0;

    public BinaryVdfNode? GetNode(string key) => Find(key)?.Node;

    public void Set(string key, string value) =>
        ReplaceOrAdd(new BinaryVdfMember { Kind = BinaryVdfKind.String, Key = key, RawValue = Encoding.UTF8.GetBytes(value) });

    public void Set(string key, int value) => Set(key, value, BinaryVdfKind.Int32);

    // 0x02 as Steam writes a boolean/int: four bytes, little endian.
    public void Set(string key, int value, BinaryVdfKind kind)
    {
        if (kind is not (BinaryVdfKind.Int32 or BinaryVdfKind.Int64)) throw new ArgumentOutOfRangeException(nameof(kind));
        var bytes = new byte[kind == BinaryVdfKind.Int64 ? 8 : 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        ReplaceOrAdd(new BinaryVdfMember { Kind = kind, Key = key, RawValue = bytes });
    }

    public BinaryVdfNode EnsureNode(string key)
    {
        if (Find(key) is { Node: { } node }) return node;
        Remove(key); // a scalar under this key would otherwise survive next to the dictionary
        var created = new BinaryVdfNode();
        Members.Add(new BinaryVdfMember { Kind = BinaryVdfKind.Dictionary, Key = key, Node = created });
        return created;
    }

    public void Remove(string key) => Members.RemoveAll(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    internal void ReplaceOrAdd(BinaryVdfMember member)
    {
        var index = Members.FindIndex(m => string.Equals(m.Key, member.Key, StringComparison.OrdinalIgnoreCase));
        // In place, so an updated key keeps its position in the file.
        if (index >= 0) Members[index] = member;
        else Members.Add(member);
    }
}

public static class BinaryVdf
{
    private const int MaxDepth = 8; // root, shortcuts, entry, tags

    public static BinaryVdfNode Parse(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var root = ParseNode(data, ref pos, depth: 0); // consumed the root's own 0x08
        // The reference writes one more 0x08, Steam pads with 0x00. Nothing else may follow.
        while (pos < data.Length && (data[pos] == 0x00 || data[pos] == 0x08)) pos++;
        if (pos != data.Length) throw new FormatException("Trailing bytes after the binary VDF root");
        return root;
    }

    // Byte-identical to the reference's writeBuffer for the same members.
    public static byte[] Write(BinaryVdfNode node)
    {
        var buffer = new MemoryStream();
        WriteMembers(buffer, node);
        return buffer.ToArray();
    }

    private static BinaryVdfNode ParseNode(ReadOnlySpan<byte> data, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Binary VDF is nested too deeply");
        var node = new BinaryVdfNode();
        while (true)
        {
            if (pos >= data.Length) throw new FormatException("Unterminated binary VDF dictionary");
            var kind = (BinaryVdfKind)data[pos];
            // 0x08 ends the dictionary; the parent then reads the next type byte.
            if (kind == BinaryVdfKind.End)
            {
                pos++;
                return node;
            }
            pos++;
            var key = ReadCString(data, ref pos);
            node.Members.Add(kind switch
            {
                BinaryVdfKind.Dictionary => new BinaryVdfMember { Kind = kind, Key = key, Node = ParseNode(data, ref pos, depth + 1) },
                BinaryVdfKind.String => new BinaryVdfMember { Kind = kind, Key = key, RawValue = ReadCStringBytes(data, ref pos) },
                BinaryVdfKind.Int32 => new BinaryVdfMember { Kind = kind, Key = key, RawValue = ReadRaw(data, ref pos, 4) },
                BinaryVdfKind.Int64 => new BinaryVdfMember { Kind = kind, Key = key, RawValue = ReadRaw(data, ref pos, 8) },
                _ => throw new FormatException($"Unknown binary VDF type 0x{(byte)kind:X2} at offset {pos - 1}"),
            });
        }
    }

    private static void WriteMembers(Stream stream, BinaryVdfNode node)
    {
        foreach (var member in node.Members)
        {
            stream.WriteByte((byte)member.Kind);
            WriteCString(stream, member.Key);
            if (member.Node is not null) WriteMembers(stream, member.Node);
            else stream.Write(member.RawValue ?? []);
            if (member.Kind == BinaryVdfKind.String) stream.WriteByte(0);
        }
        stream.WriteByte((byte)BinaryVdfKind.End);
    }

    private static byte[] ReadRaw(ReadOnlySpan<byte> data, ref int pos, int length)
    {
        if (pos + length > data.Length) throw new FormatException("Truncated binary VDF value");
        var value = data.Slice(pos, length).ToArray();
        pos += length;
        return value;
    }

    private static byte[] ReadCStringBytes(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos > data.Length) throw new FormatException("Truncated binary VDF string");
        var end = data[pos..].IndexOf((byte)0);
        if (end < 0) throw new FormatException("Unterminated binary VDF string");
        var value = data.Slice(pos, end).ToArray();
        pos += end + 1;
        return value;
    }

    private static string ReadCString(ReadOnlySpan<byte> data, ref int pos) =>
        Encoding.UTF8.GetString(ReadCStringBytes(data, ref pos));

    private static void WriteCString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
    }
}