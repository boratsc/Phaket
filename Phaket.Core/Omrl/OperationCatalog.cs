using System.Collections.Frozen;

namespace Phaket.Core.Omrl;

/// <summary>Declarative descriptions only; this catalog cannot execute operations.</summary>
public sealed record OperationDefinition(
    string Name, string ParameterSchema, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Postconditions, string Recovery);

public static class OperationCatalog
{
    private static readonly IReadOnlyDictionary<string, OperationDefinition> Operations =
        new[]
        {
            new OperationDefinition("directory.ensure", """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1}}}""",
                ["directory.ensure"], ["write"], ["approved-path"], ["directory-exists"], "remove-if-created-and-empty"),
            new OperationDefinition("file.copy", """{"type":"object","additionalProperties":false,"required":["from","to"],"properties":{"from":{"type":"string","minLength":1},"to":{"type":"string","minLength":1},"overwrite":{"enum":["fail","replace"]}}}""",
                ["file.copy"], ["read", "write"], ["source-exists", "approved-path", "overwrite-policy"], ["file-copied", "ownership-recorded"], "restore-backup-or-remove-created"),
            new OperationDefinition("file.remove", """{"type":"object","additionalProperties":false,"required":["path","ownership"],"properties":{"path":{"type":"string","minLength":1},"ownership":{"const":"required"}}}""",
                ["file.remove"], ["write"], ["approved-path", "ownership-required"], ["file-removed"], "restore-journal-backup"),
            new OperationDefinition("file.verify", """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string","minLength":1},"sha256":{"type":"string","pattern":"^[a-fA-F0-9]{64}$"}}}""",
                ["file.verify"], ["read"], ["approved-path"], ["file-exists-and-hash-matches-if-specified"], "none"),
            new OperationDefinition("archive.extract", """{"type":"object","additionalProperties":false,"required":["from","to"],"properties":{"from":{"type":"string","minLength":1},"to":{"type":"string","minLength":1},"stripComponents":{"type":"integer","minimum":0},"include":{"type":"array","minItems":1,"maxItems":256,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":512,"pattern":"^(?!/)(?!.*(^|/)\\.\\.(/|$))(?!.*\\\\)[^\\u0000-\\u001f]+$"}}}}""",
                ["archive.extract"], ["read", "write"], ["safe-zip", "approved-path"], ["entries-extracted", "entries-journaled"], "remove-journaled-entries"),
            new OperationDefinition("config.set", """{"type":"object","additionalProperties":false,"required":["path","format","key","value"],"properties":{"path":{"type":"string","minLength":1},"format":{"enum":["json","keyvalue"]},"key":{"type":"string","minLength":1},"value":{"type":"string"}}}""",
                ["config.set"], ["read", "write"], ["approved-path", "valid-config"], ["key-set", "ownership-recorded"], "restore-base-or-recompute-owners"),
            new OperationDefinition("loader.ensure", """{"type":"object","additionalProperties":false,"required":["id","artifact"],"properties":{"id":{"type":"string","minLength":1},"artifact":{"type":"string","minLength":1},"version":{"type":"string","minLength":1},"stripComponents":{"type":"integer","minimum":0,"maximum":4}}}""",
                ["loader.ensure"], ["read", "write"], ["declared-artifact", "approved-loader-layout"], ["compatible-loader-present", "ownership-recorded"], "remove-new-loader-files-per-ledger"),
            new OperationDefinition("profile.activate", """{"type":"object","additionalProperties":false,"required":["profile"],"properties":{"profile":{"type":"string","minLength":1}}}""",
                ["profile.activate"], ["write"], ["profile-exists"], ["profile-active"], "restore-previous-active-profile"),
            new OperationDefinition("game.provision", """{"type":"object","additionalProperties":false,"required":["store","version"],"properties":{"store":{"enum":["steam","epic","itch","msstore"]},"version":{"type":"string","pattern":"^([0-9]+(\\.[0-9]+)*|latest)$"},"manifestId":{"type":"string","minLength":1}}}""",
                ["game.provision"], ["read", "write"], ["base-game-source", "approved-path"], ["base-game-present", "ownership-recorded"], "remove-provisioned-files-per-ledger")
        }.ToDictionary(operation => operation.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<OperationDefinition> All => Operations.Values.ToArray();
    public static IReadOnlySet<string> Capabilities { get; } = Operations.Values
        .SelectMany(operation => operation.Capabilities).ToFrozenSet(StringComparer.Ordinal);
    public static bool TryGet(string name, out OperationDefinition? operation) => Operations.TryGetValue(name, out operation);
}
