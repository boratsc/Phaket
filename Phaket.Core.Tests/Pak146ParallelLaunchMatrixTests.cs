using Phaket.Core.Launch;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

// PAK-146 C: support is declared only for a verified game × store × OS combination.
// Everything else is a coded conservative answer, never a guessed promise.
public sealed class Pak146ParallelLaunchMatrixTests
{
    private static SavesIsolationMode Env() => new(SavesIsolationMode.EnvRedirect, ["HOME", "XDG_CONFIG_HOME"]);

    private static GameDefinition Definition(SavesIsolationMode? linux = null, ParallelLaunch? parallel = null) => new(
        "among-us", ["BepInEx/**"], StoreIds: new Dictionary<string, string> { ["steam"] = "945360" },
        SavesIsolation: new SavesIsolation(new Dictionary<string, SavesIsolationMode>
        {
            ["linux"] = linux ?? Env(),
            ["windows"] = new(SavesIsolationMode.Shared, Reason: "unity-locallow-known-folder"),
        }),
        ParallelLaunch: parallel);

    private static ParallelLaunchContext Context(GameDefinition definition, string os = "linux", string store = "steam") =>
        new(definition, store, os);

    [Fact]
    public void SharedSavesAreNotSupportedAndCarryBothLanguages()
    {
        var decision = ParallelLaunchMatrix.Evaluate(Context(Definition(), "windows"));

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported, decision.Status);
        Assert.Equal("shared-saves", decision.Reason!.Code);
        Assert.Equal("Nie można uruchomić dwóch wersji tej gry naraz: dzielą zapisy i jedna zepsuje drugą. Zamknij drugą wersję i spróbuj ponownie.",
            decision.Reason.MessagePl);
        Assert.Equal("You cannot run two versions of this game at the same time: they share saves and one will break the other. Close the other version and try again.",
            decision.Reason.MessageEn);
    }

    [Fact]
    public void UndeterminedSavesUseTheSameSharedSavesBlock()
    {
        var definition = Definition() with { SavesIsolation = null };

        var decision = ParallelLaunchMatrix.Evaluate(Context(definition));

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported, decision.Status);
        Assert.Equal("shared-saves", decision.Reason!.Code);
    }

    [Fact]
    public void SteamProtocolIsNeverParallelEvenWithAnOverride()
    {
        var definition = Definition(parallel: new ParallelLaunch(new Dictionary<string, ParallelLaunchDeclaration>
        {
            ["linux"] = new("supported"),
        }));

        var decision = ParallelLaunchMatrix.Evaluate(Context(definition), viaSteamProtocol: true);

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported, decision.Status);
        Assert.Equal("steam-single-instance", decision.Reason!.Code);
        Assert.Contains("Steam", decision.Reason.MessagePl);
        Assert.Contains("Steam", decision.Reason.MessageEn);
    }

    [Fact]
    public void SharedSavesCannotBeOverriddenBySupported()
    {
        var definition = Definition(parallel: new ParallelLaunch(new Dictionary<string, ParallelLaunchDeclaration>
        {
            ["windows"] = new("supported"),
        }));

        var decision = ParallelLaunchMatrix.Evaluate(Context(definition, "windows"));

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported, decision.Status);
        Assert.Equal("shared-saves", decision.Reason!.Code);
    }

    [Fact]
    public void MissingCombinationIsUnknownInsteadOfGuessedSupported()
    {
        var decision = ParallelLaunchMatrix.Evaluate(Context(Definition(), store: "epic"));

        Assert.Equal(ParallelLaunchSupportStatus.Unknown, decision.Status);
        Assert.Equal("unverified", decision.Reason!.Code);
        Assert.Contains("nie została jeszcze sprawdzona", decision.Reason.MessagePl);
        Assert.Contains("has not been checked yet", decision.Reason.MessageEn);
    }

    [Fact]
    public void IsolatedDirectLaunchStaysUnknownUntilLiveVerification()
    {
        var decision = ParallelLaunchMatrix.Evaluate(Context(Definition()));

        Assert.Equal(ParallelLaunchSupportStatus.Unknown, decision.Status);
        Assert.Equal("unverified", decision.Reason!.Code);
    }

    [Fact]
    public void ExplicitLiveVerifiedOverrideCanSupportAnIsolatedDirectLaunch()
    {
        var definition = Definition(parallel: new ParallelLaunch(new Dictionary<string, ParallelLaunchDeclaration>
        {
            ["linux"] = new("supported"),
        }));

        var decision = ParallelLaunchMatrix.Evaluate(Context(definition));

        Assert.Equal(ParallelLaunchSupportStatus.Supported, decision.Status);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void ExplicitPortConflictRemainsACodedBlock()
    {
        var definition = Definition(parallel: new ParallelLaunch(new Dictionary<string, ParallelLaunchDeclaration>
        {
            ["linux"] = new("not-supported", "port-conflict"),
        }));

        var decision = ParallelLaunchMatrix.Evaluate(Context(definition));

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported, decision.Status);
        Assert.Equal("port-conflict", decision.Reason!.Code);
    }

    [Fact]
    public void DefinitionLoadsTheAdditiveParallelLaunchOverride()
    {
        var definition = GameDefinitionLoader.LoadYaml("""
            schemaVersion: game-definition/v0alpha1
            slug: among-us
            writableAreas: [ "BepInEx/**" ]
            storeIds: { steam: "945360" }
            savesIsolation:
              os:
                linux: { mode: env, env: [ HOME ] }
            parallelLaunch:
              os:
                linux: { status: supported }
            """);

        Assert.Equal("supported", definition.ParallelLaunchFor("linux")!.Status);
        Assert.Null(definition.ParallelLaunchFor("windows"));
    }

    [Theory]
    [InlineData("not-supported, reason: made-up")]
    [InlineData("not-supported")]
    [InlineData("supported, reason: port-conflict")]
    public void InvalidParallelLaunchDeclarationIsValidationFailed(string declaration)
    {
        var yaml = $$"""
            schemaVersion: game-definition/v0alpha1
            slug: among-us
            writableAreas: [ "BepInEx/**" ]
            parallelLaunch:
              os:
                linux: { status: {{declaration}} }
            """;

        var error = Assert.Throws<PhaketException>(() => GameDefinitionLoader.LoadYaml(yaml));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Error.Code);
    }

    [Theory]
    [InlineData("among-us.yaml")]
    [InlineData("valheim.yaml")]
    public void ShippedDefinitionsDeclareOnlyWhatTheExistingDataProves(string file)
    {
        var definition = GameDefinitionLoader.LoadYaml(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "games", file)));

        Assert.Equal(ParallelLaunchSupportStatus.NotSupported,
            ParallelLaunchMatrix.Evaluate(Context(definition, "windows")).Status);
        Assert.Equal("shared-saves", ParallelLaunchMatrix.Evaluate(Context(definition, "windows")).Reason!.Code);
        Assert.Equal(ParallelLaunchSupportStatus.Unknown,
            ParallelLaunchMatrix.Evaluate(Context(definition, "linux")).Status);
        Assert.Equal("steam-single-instance",
            ParallelLaunchMatrix.Evaluate(Context(definition, "linux"), viaSteamProtocol: true).Reason!.Code);
    }
}
