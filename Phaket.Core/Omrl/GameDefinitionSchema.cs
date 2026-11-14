using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Phaket.Core.Omrl;

public static class GameDefinitionSchema
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Phaket.Core.schemas.game-definition-v0alpha1.schema.json")
            ?? throw new InvalidOperationException("Embedded game definition schema missing");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    });

    public static IReadOnlyList<PhaketError> Validate(JsonElement definition)
    {
        var results = Schema.Value.Evaluate(definition, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return [];
        var errors = new List<PhaketError>();
        Collect(results, errors);
        if (errors.Count == 0)
            errors.Add(new PhaketError(ErrorCodes.ValidationFailed,
                new Dictionary<string, string> { ["detail"] = "game definition schema validation failed" }));
        return errors;
    }

    private static void Collect(EvaluationResults results, List<PhaketError> errors)
    {
        if (results.Errors is { Count: > 0 })
            foreach (var (pointer, message) in results.Errors)
                errors.Add(new PhaketError(ErrorCodes.ValidationFailed,
                    new Dictionary<string, string> { ["detail"] = $"{pointer}: {message}" }));
        if (results.Details is { } details)
            foreach (var detail in details) Collect(detail, errors);
    }
}
