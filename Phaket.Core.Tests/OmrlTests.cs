using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public static class TestFixtures
{
    public static readonly GameDefinition AmongUs = new("among-us",
        ["BepInEx/**", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"]);

    public static readonly GameDefinition Valheim = new("valheim",
        ["BepInEx/**", "winhttp.dll", "doorstop_config.ini", ".doorstop_version"]);

    public static string Yaml(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "recipes", name));

    public static Recipe AmongUsRecipe() => RecipeParser.ParseYaml(Yaml("among-us.yaml"));
    public static Recipe ValheimRecipe() => RecipeParser.ParseYaml(Yaml("valheim.yaml"));
}

public class OmrlParserTests
{
    [Fact]
    public void ParsesAmongUsReference()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        Assert.Equal("omrl/v0alpha1", recipe.SchemaVersion);
        Assert.Equal("community.phaket.custom-roles", recipe.Id);
        Assert.Equal("among-us", recipe.Target.Game);
        Assert.Equal(3, recipe.Steps.Count);
        Assert.Single(recipe.Verify);
        Assert.Equal("Custom Roles", recipe.Metadata!.Title!["pl-PL"]);
    }

    [Fact]
    public void ParsesValheimReference()
    {
        var recipe = TestFixtures.ValheimRecipe();
        Assert.Equal("valheim", recipe.Target.Game);
        Assert.NotNull(recipe.Steps[2].When);
    }

    [Fact]
    public void JsonAndYamlProduceIdenticalCanonicalModel()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        var json = RecipeParser.ToJson(recipe);
        var fromJson = RecipeParser.ParseJson(json);
        Assert.Equal(json, RecipeParser.ToJson(fromJson));
    }

    [Fact]
    public void YamlRoundTripIsDeterministic()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        var yaml1 = RecipeParser.ToYaml(recipe);
        var yaml2 = RecipeParser.ToYaml(RecipeParser.ParseYaml(yaml1));
        Assert.Equal(yaml1, yaml2);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        var yaml = TestFixtures.Yaml("among-us.yaml") + "\nextraField: 1\n";
        var ex = Assert.Throws<PhaketException>(() => RecipeParser.ParseYaml(yaml));
        Assert.Equal(ErrorCodes.ValidationFailed, ex.Error.Code);
    }

    [Fact]
    public void DuplicateYamlKeyIsRejected()
    {
        var yaml = TestFixtures.Yaml("among-us.yaml") + "\nid: other.id\n";
        var ex = Assert.Throws<PhaketException>(() => RecipeParser.ParseYaml(yaml));
        Assert.Contains("duplicate", ex.Error.Parameters["detail"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingRequiredFieldIsRejected()
    {
        var yaml = "schemaVersion: omrl/v0alpha1\nid: a.b\nversion: 1.0.0\n";
        Assert.Throws<PhaketException>(() => RecipeParser.ParseYaml(yaml));
    }
}

public class OmrlValidatorTests
{
    private static IReadOnlyList<PhaketError> Validate(Recipe recipe, GameDefinition? game = null)
    {
        var schemaErrors = RecipeSchema.Validate(System.Text.Json.JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement);
        return [.. schemaErrors, .. RecipeValidator.Validate(recipe, game ?? TestFixtures.AmongUs)];
    }

    private static void AssertCode(string code, Recipe recipe, GameDefinition? game = null) =>
        Assert.Contains(Validate(recipe, game), e => e.Code == code);

    [Fact]
    public void AmongUsReferenceIsValid() => Assert.Empty(Validate(TestFixtures.AmongUsRecipe()));

    [Fact]
    public void ValheimReferenceIsValid() => Assert.Empty(Validate(TestFixtures.ValheimRecipe(), TestFixtures.Valheim));

    [Fact]
    public void UnknownOperationIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Steps = [.. recipe.Steps, new RecipeStep { Id = "evil", Use = "shell.execute", With = new Dictionary<string, System.Text.Json.JsonElement>() }] };
        AssertCode(ErrorCodes.UnknownOperation, recipe);
    }

    [Fact]
    public void MissingCapabilityIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Requires = new RecipeRequires { Capabilities = ["file.copy"] } };
        AssertCode(ErrorCodes.MissingCapability, recipe);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe() with { SchemaVersion = "omrl/v99" };
        AssertCode(ErrorCodes.UnsupportedSchemaVersion, recipe);
    }

    [Fact]
    public void TraversalUriIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps.Take(2), recipe.Steps[2] with
            {
                With = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["from"] = System.Text.Json.JsonSerializer.SerializeToElement("staging://custom-roles/CustomRoles.dll"),
                    ["to"] = System.Text.Json.JsonSerializer.SerializeToElement("game://../evil.dll"),
                }
            }]
        };
        AssertCode(ErrorCodes.InvalidUri, recipe);
    }

    [Fact]
    public void WriteOutsideWritableAreasIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps.Take(2), recipe.Steps[2] with
            {
                With = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["from"] = System.Text.Json.JsonSerializer.SerializeToElement("staging://custom-roles/CustomRoles.dll"),
                    ["to"] = System.Text.Json.JsonSerializer.SerializeToElement("game://Evil/payload.dll"),
                }
            }]
        };
        AssertCode(ErrorCodes.PathOutOfBounds, recipe);
    }

    [Fact]
    public void BadVersionRangeIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Target = recipe.Target with { Constraints = new TargetConstraints { GameVersion = ">=beta" } } };
        AssertCode(ErrorCodes.InvalidVersionRange, recipe);
    }

    [Fact]
    public void HttpsWithoutHashIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Artifacts = [new RecipeArtifact { Id = "mod-bundle", Source = "https://github.com/org/repo/releases/download/v1/mod.zip" }] };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void DisallowedHttpsHostIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Artifacts = [new RecipeArtifact
            {
                Id = "mod-bundle",
                Source = "https://evil.example/mod.zip",
                Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            }]
        };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void UndeclaredPackageArtifactIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps.Take(1), recipe.Steps[1] with
            {
                With = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["from"] = System.Text.Json.JsonSerializer.SerializeToElement("package://not-declared"),
                    ["to"] = System.Text.Json.JsonSerializer.SerializeToElement("staging://custom-roles"),
                }
            }, recipe.Steps[2]]
        };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void DuplicateStepIdIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Steps = [.. recipe.Steps, recipe.Steps[0]] };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void EmptyStepsAreRejectedBySchema()
    {
        var recipe = TestFixtures.AmongUsRecipe() with { Steps = [] };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void ConditionDepthLimitIsEnforced()
    {
        RecipeCondition deep = new() { Os = ["linux"] };
        for (var i = 0; i < 9; i++) deep = new RecipeCondition { Not = deep };
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with { Steps = [recipe.Steps[0] with { When = deep }, .. recipe.Steps.Skip(1)] };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void ConditionWithTwoKindsIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [recipe.Steps[0] with { When = new RecipeCondition { Os = ["linux"], Store = "steam" } }, .. recipe.Steps.Skip(1)]
        };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void VerifyStepWithWriteOperationIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Verify = [new RecipeVerifyStep
            {
                Use = "file.remove",
                With = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["path"] = System.Text.Json.JsonSerializer.SerializeToElement("game://BepInEx/plugins/CustomRoles.dll"),
                    ["ownership"] = System.Text.Json.JsonSerializer.SerializeToElement("required"),
                }
            }]
        };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }

    [Fact]
    public void DuplicateEditionsAreAmbiguous()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Target = recipe.Target with
            {
                Editions = [new EditionTarget { Store = "steam" }, new EditionTarget { Store = "steam" }]
            }
        };
        AssertCode(ErrorCodes.AmbiguousTarget, recipe);
    }

    [Fact]
    public void FileRemoveWithoutOwnershipPolicyIsRejected()
    {
        var recipe = TestFixtures.AmongUsRecipe();
        recipe = recipe with
        {
            Steps = [.. recipe.Steps, new RecipeStep
            {
                Id = "remove",
                Use = "file.remove",
                With = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["path"] = System.Text.Json.JsonSerializer.SerializeToElement("game://BepInEx/plugins/CustomRoles.dll"),
                }
            }],
            Requires = new RecipeRequires { Capabilities = [.. recipe.Requires!.Capabilities, "file.remove"] },
        };
        AssertCode(ErrorCodes.ValidationFailed, recipe);
    }
}
