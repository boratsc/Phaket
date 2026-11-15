namespace Phaket.Core.Omrl;

// Facts supplied by platform/game adapters. Conditions are pure functions of facts;
// evaluation never mutates anything and is locale-independent.
public sealed record PlanFacts(
    string Os,
    string Store,
    string? GameVersion,
    IReadOnlySet<string> Capabilities,
    Func<string, bool> FileExists,
    IReadOnlyDictionary<string, string>? LoaderVersions = null,
    // Steam manifest of the selected catalog build; used by game.provision
    // when the recipe step does not pin its own manifestId.
    string? ManifestId = null,
    string? Arch = null,
    long? BuildId = null);

public static class ConditionEvaluator
{
    public static bool Evaluate(RecipeCondition? condition, PlanFacts facts)
    {
        if (condition is null) return true;
        if (condition.All is { } all) return all.All(c => Evaluate(c, facts));
        if (condition.Any is { } any) return any.Any(c => Evaluate(c, facts));
        if (condition.Not is { } not) return !Evaluate(not, facts);
        if (condition.Os is { } os) return os.Contains(facts.Os, StringComparer.Ordinal);
        if (condition.Store is { } store) return store == facts.Store;
        if (condition.Arch is { } arch) return arch == facts.Arch;
        if (condition.BuildId is { } buildRange)
        {
            if (facts.BuildId is not { } build || !TryParseBuildRange(buildRange, out var op, out var value)) return false;
            return op switch { ">=" => build >= value, "<=" => build <= value, "==" or "=" => build == value, _ => false };
        }
        if (condition.Capability is { } capability) return facts.Capabilities.Contains(capability);
        if (condition.FileExists is { } uri) return facts.FileExists(uri);
        if (condition.GameVersion is { } range)
        {
            if (facts.GameVersion is null) return false;
            if (!NumericVersion.TryParse(facts.GameVersion, out var version)) return false;
            return VersionRange.TryParse([range], out var parsed) && parsed!.Contains(version!);
        }
        return false;
    }

    internal static bool TryParseBuildRange(string range, out string op, out long value)
    {
        op = "";
        value = 0;
        var match = System.Text.RegularExpressions.Regex.Match(range, @"^(>=|<=|==|=)([0-9]+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value)) return false;
        op = match.Groups[1].Value;
        return true;
    }
}
