using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Phaket.Core.Omrl;

// Structural validation against the published JSON Schema (omrl/v0alpha1).
// Semantic cross-checks live in RecipeValidator.
public static class RecipeSchema
{
    private static readonly Lazy<JsonSchema> Schema = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Phaket.Core.schemas.omrl-v0alpha1.schema.json")
            ?? throw new InvalidOperationException("Embedded OMRL schema missing");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    });

    public static IReadOnlyList<PhaketError> Validate(JsonElement recipe)
    {
        var results = Schema.Value.Evaluate(recipe, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        if (results.IsValid) return [];
        var errors = new List<PhaketError>();
        CollectErrors(results, errors);
        if (errors.Count == 0)
            errors.Add(new PhaketError(ErrorCodes.ValidationFailed,
                new Dictionary<string, string> { ["detail"] = "schema validation failed" }));
        return errors;
    }

    private static void CollectErrors(EvaluationResults results, List<PhaketError> errors)
    {
        if (results.Errors is { Count: > 0 })
        {
            foreach (var (pointer, message) in results.Errors)
                errors.Add(new PhaketError(ErrorCodes.ValidationFailed,
                    new Dictionary<string, string> { ["detail"] = $"{pointer}: {message}" }));
        }
        if (results.Details is { } details)
            foreach (var detail in details) CollectErrors(detail, errors);
    }
}
