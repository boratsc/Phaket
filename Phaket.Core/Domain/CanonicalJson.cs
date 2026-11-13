using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phaket.Core.Domain;

public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return stream.ToArray();
    }

    public static string Sha256(OperationPlan plan) =>
        Convert.ToHexStringLower(SHA256.HashData(Serialize(plan)));

    // Exchange representation: canonical content PLUS the planId. The hash
    // itself is always computed over the content without the identifier.
    public static byte[] SerializeWithId(OperationPlan plan)
    {
        var element = JsonSerializer.SerializeToElement(plan, Options);
        var map = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        map["planId"] = JsonSerializer.SerializeToElement(plan.PlanId, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, JsonSerializer.SerializeToElement(map, Options));
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
