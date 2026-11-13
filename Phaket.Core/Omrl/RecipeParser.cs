using System.Globalization;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Phaket.Core.Omrl;

// YAML/JSON -> typed Recipe. Unknown fields and duplicate keys are hard errors;
// nothing is silently ignored. Serialization is deterministic (stable order).
public static class RecipeParser
{
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static Recipe ParseYaml(string yaml)
    {
        JsonElement element;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1)
                throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "expected exactly one YAML document"));
            element = ToJson(stream.Documents[0].RootNode);
        }
        catch (PhaketException) { throw; }
        catch (Exception ex)
        {
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", ex.Message));
        }
        return Deserialize(element);
    }

    public static Recipe ParseJson(string json)
    {
        JsonElement element;
        try
        {
            element = JsonDocument.Parse(json).RootElement;
        }
        catch (Exception ex)
        {
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", ex.Message));
        }
        return Deserialize(element);
    }

    public static string ToJson(Recipe recipe) =>
        JsonSerializer.Serialize(recipe, StrictOptions);

    public static string ToYaml(Recipe recipe)
    {
        var element = JsonSerializer.SerializeToElement(recipe, StrictOptions);
        var node = ToYamlNode(element);
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static Recipe Deserialize(JsonElement element)
    {
        try
        {
            return element.Deserialize<Recipe>(StrictOptions)
                ?? throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "empty recipe"));
        }
        catch (PhaketException) { throw; }
        catch (JsonException ex)
        {
            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", ex.Message));
        }
    }

    private static JsonElement ToJson(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                using var document = JsonDocument.Parse("{}");
                var writerOptions = new JsonWriterOptions { Indented = false };
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer, writerOptions))
                {
                    writer.WriteStartObject();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (key, value) in mapping.Children)
                    {
                        if (key is not YamlScalarNode scalarKey)
                            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "mapping keys must be strings"));
                        if (!seen.Add(scalarKey.Value ?? ""))
                            throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"duplicate key: {scalarKey.Value}"));
                        writer.WritePropertyName(scalarKey.Value ?? "");
                        WriteJsonValue(writer, value);
                    }
                    writer.WriteEndObject();
                }
                return JsonDocument.Parse(buffer.ToArray()).RootElement;
            }
            case YamlSequenceNode sequence:
            {
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartArray();
                    foreach (var item in sequence.Children) WriteJsonValue(writer, item);
                    writer.WriteEndArray();
                }
                return JsonDocument.Parse(buffer.ToArray()).RootElement;
            }
            case YamlScalarNode scalar:
                return JsonSerializer.SerializeToElement(ScalarValue(scalar));
            default:
                throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "unsupported YAML node"));
        }
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode scalarKey)
                        throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "mapping keys must be strings"));
                    if (!seen.Add(scalarKey.Value ?? ""))
                        throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"duplicate key: {scalarKey.Value}"));
                    writer.WritePropertyName(scalarKey.Value ?? "");
                    WriteJsonValue(writer, value);
                }
                writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children) WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                switch (ScalarValue(scalar))
                {
                    case null: writer.WriteNullValue(); break;
                    case bool b: writer.WriteBooleanValue(b); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case string s: writer.WriteStringValue(s); break;
                }
                break;
            default:
                throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", "unsupported YAML node"));
        }
    }

    private static object? ScalarValue(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.SingleQuoted
            || scalar.Style == YamlDotNet.Core.ScalarStyle.DoubleQuoted
            || scalar.Style == YamlDotNet.Core.ScalarStyle.Literal
            || scalar.Style == YamlDotNet.Core.ScalarStyle.Folded)
            return value;
        if (value is "" or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return true;
        if (value is "false" or "False" or "FALSE") return false;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && !double.IsInfinity(d)) return d;
        return value;
    }

    private static YamlNode ToYamlNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var mapping = new YamlMappingNode();
                foreach (var property in element.EnumerateObject())
                    mapping.Children.Add(new YamlScalarNode(property.Name), ToYamlNode(property.Value));
                return mapping;
            }
            case JsonValueKind.Array:
            {
                var sequence = new YamlSequenceNode();
                foreach (var item in element.EnumerateArray())
                    sequence.Children.Add(ToYamlNode(item));
                return sequence;
            }
            case JsonValueKind.String:
                return new YamlScalarNode(element.GetString()!);
            case JsonValueKind.True:
                return new YamlScalarNode("true");
            case JsonValueKind.False:
                return new YamlScalarNode("false");
            case JsonValueKind.Null:
                return new YamlScalarNode("null");
            default:
                return new YamlScalarNode(element.GetRawText());
        }
    }
}
