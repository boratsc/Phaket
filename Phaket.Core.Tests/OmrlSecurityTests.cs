using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class UriBoundaryTests
{
    [Theory]
    [InlineData("game://BepInEx/plugins/mod.dll")]
    [InlineData("profile://state/journal.json")]
    [InlineData("staging://work/mod.dll")]
    [InlineData("package://mod-bundle/inner/file.dll")]
    public void ValidUrisParse(string uri) => Assert.True(VirtualUri.TryParse(uri, out _, out _));

    [Theory]
    [InlineData("game://../evil.dll")]
    [InlineData("game://BepInEx/../../evil.dll")]
    [InlineData("game:///absolute/path")]
    [InlineData("game://BepInEx\\plugins\\mod.dll")]
    [InlineData("game://C:/windows/system32")]
    [InlineData("game://a//b")]
    [InlineData("game://a/./b")]
    [InlineData("ftp://example/mod.dll")]
    [InlineData("game://")]
    [InlineData("")]
    public void InvalidUrisAreRejected(string uri) => Assert.False(VirtualUri.TryParse(uri, out _, out _));

    [Fact]
    public void ControlCharactersAreRejected() =>
        Assert.False(VirtualUri.TryParse("game://mod\u0000.dll", out _, out _));

    [Fact]
    public void ExcessiveDepthIsRejected() =>
        Assert.False(VirtualUri.TryParse("game://" + string.Join("/", Enumerable.Repeat("a", 65)), out _, out _));

    [Fact]
    public void PathBoundaryAllowsWithinRoot()
    {
        var root = Directory.CreateTempSubdirectory("phaket-boundary-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "plugins"));
            var resolved = PathBoundary.Resolve(root, "BepInEx/plugins/mod.dll");
            Assert.True(PathBoundary.IsWithin(root, resolved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathBoundaryRejectsTraversal()
    {
        var root = Directory.CreateTempSubdirectory("phaket-boundary-").FullName;
        try
        {
            Assert.Throws<PhaketException>(() => PathBoundary.Resolve(root, "../outside.dll"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathBoundaryRejectsSymlinkEscape()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Symlink creation requires Linux in this suite; Windows covered by logical checks.");
        var outer = Directory.CreateTempSubdirectory("phaket-outer-").FullName;
        var root = Directory.CreateTempSubdirectory("phaket-root-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outer);
            var ex = Assert.Throws<PhaketException>(() => PathBoundary.Resolve(root, "escape/payload.dll"));
            Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outer, recursive: true);
        }
    }

    [Theory]
    [InlineData("BepInEx/**", "BepInEx/plugins/mod.dll", true)]
    [InlineData("BepInEx/**", "BepInEx/core/BepInEx.dll", true)]
    [InlineData("BepInEx/**", "Evil/mod.dll", false)]
    [InlineData("winhttp.dll", "winhttp.dll", true)]
    [InlineData("winhttp.dll", "sub/winhttp.dll", false)]
    [InlineData("*.dll", "mod.dll", true)]
    [InlineData("*.dll", "sub/mod.dll", false)]
    public void GlobMatchesWritableAreas(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, path));
}

public class ErrorMessagesTests
{
    private static readonly string[] AllCodes = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void EveryCodeHasPolishAndEnglishMessages()
    {
        var pl = ErrorMessages.Keys("pl-PL");
        var en = ErrorMessages.Keys("en");
        foreach (var code in AllCodes)
        {
            Assert.Contains(code, pl);
            Assert.Contains(code, en);
        }
        Assert.Equal(pl.OrderBy(k => k), en.OrderBy(k => k));
    }

    [Fact]
    public void PlaceholdersMatchAcrossLocales()
    {
        foreach (var code in AllCodes)
        {
            ErrorMessages.TryGetTemplate(code, "pl-PL", out var pl);
            ErrorMessages.TryGetTemplate(code, "en", out var en);
            Assert.Equal(Placeholders(pl), Placeholders(en));
        }
        static string[] Placeholders(string template) =>
            Regex.Matches(template, @"\{([a-zA-Z0-9_]+)\}").Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
    }

    [Fact]
    public void FallbackChainIsDeterministic()
    {
        // Unregistered locale falls back to en, then pl-PL.
        var message = ErrorMessages.Format(ErrorCodes.UnknownOperation, "fr-FR",
            new Dictionary<string, string> { ["operation"] = "shell.execute" });
        Assert.Contains("Unknown operation", message);
        Assert.Contains("shell.execute", message);
    }

    [Fact]
    public void AddingLocaleRequiresNoCoreChange()
    {
        ErrorMessages.RegisterLocale("de-DE", new Dictionary<string, string>
        {
            [ErrorCodes.UnknownOperation] = "Unbekannte Operation: {operation}.",
        });
        var de = ErrorMessages.Format(ErrorCodes.UnknownOperation, "de-DE",
            new Dictionary<string, string> { ["operation"] = "x" });
        Assert.Equal("Unbekannte Operation: x.", de);
        // Missing key in the new locale falls back deterministically.
        var fallback = ErrorMessages.Format(ErrorCodes.ArtifactMissing, "de-DE",
            new Dictionary<string, string> { ["artifact"] = "a" });
        Assert.Contains("Artifact file is missing", fallback);
    }

    [Fact]
    public void MissingKeyReturnsCodeInsteadOfThrowing() =>
        Assert.Equal("NO_SUCH_CODE", ErrorMessages.Format("NO_SUCH_CODE", "pl-PL"));

    [Fact]
    public void ValidationErrorsCarryCodeAndParameters()
    {
        var ex = new PhaketException(ErrorCodes.PathOutOfBounds, ("path", "game://Evil/x.dll"));
        Assert.Equal(ErrorCodes.PathOutOfBounds, ex.Error.Code);
        Assert.Contains("game://Evil/x.dll", ex.LocalizedMessage("pl-PL"));
        Assert.Contains("game://Evil/x.dll", ex.LocalizedMessage("en"));
    }
}

public class ConditionEvaluatorTests
{
    private static readonly PlanFacts Facts = new(
        "linux", "steam", "2026.3.5",
        new HashSet<string> { "file.copy" },
        uri => uri == "game://BepInEx/plugins/mod.dll");

    [Fact]
    public void NullConditionIsTrue() => Assert.True(ConditionEvaluator.Evaluate(null, Facts));

    [Fact]
    public void OsAndStoreMatch()
    {
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { Os = ["linux"] }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { Os = ["windows"] }, Facts));
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { Store = "steam" }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { Store = "epic" }, Facts));
    }

    [Fact]
    public void VersionRangeUsesNumericSemantics()
    {
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = ">=2026.1" }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = "<2026.1" }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { GameVersion = ">=2026.1" }, Facts with { GameVersion = null }));
    }

    [Fact]
    public void FileExistsAndCapabilityUseFacts()
    {
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { FileExists = "game://BepInEx/plugins/mod.dll" }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { FileExists = "game://missing.dll" }, Facts));
        Assert.True(ConditionEvaluator.Evaluate(new RecipeCondition { Capability = "file.copy" }, Facts));
        Assert.False(ConditionEvaluator.Evaluate(new RecipeCondition { Capability = "archive.extract" }, Facts));
    }

    [Fact]
    public void BooleanCombinatorsWork()
    {
        var condition = new RecipeCondition
        {
            All =
            [
                new RecipeCondition { Os = ["linux"] },
                new RecipeCondition { Any = [new RecipeCondition { Store = "epic" }, new RecipeCondition { Store = "steam" }] },
                new RecipeCondition { Not = new RecipeCondition { Os = ["windows"] } },
            ]
        };
        Assert.True(ConditionEvaluator.Evaluate(condition, Facts));
    }
}

// PAK-9: adapter planning must be dry — no filesystem mutation without approval.
public class AdapterDryTests
{
    private sealed class DryFakeAdapter : IGameAdapter
    {
        public IReadOnlyList<Installation> DetectInstallations(PlatformSnapshot snapshot) => [];
        public IReadOnlyList<string> ProbeVersion(InstallationFacts facts) => ["2026.3.5"];
        public IReadOnlyList<ModdingMethod> SupportedMethods(InstallationFacts facts) => [];
        public OperationPlan PlanInstall(Profile profile, InstallationFacts facts) =>
            new(OperationPlan.CurrentSchemaVersion, new RecipeRef("org.test.mod", "1.0.0"),
                facts.Installation.InstallationId, profile.ProfileId, [], [], [], false, []);
        public Task<ExecutionJournal> ApplyPlan(OperationPlan plan, IPlanExecutorHost operations, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Apply requires the host-provided executor.");
        public ExecutionJournal Complete(OperationPlan plan, ExecutionJournal journal) =>
            throw new InvalidOperationException("Complete requires the host-provided executor.");
        public Task<VerificationResult> Verify(InstallationFacts facts, Profile profile, IPlanExecutorHost operations, CancellationToken cancellationToken) =>
            Task.FromResult(new VerificationResult(true, []));
        public Task<ExecutionJournal> Rollback(ExecutionJournal journal, IPlanExecutorHost operations, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Rollback requires the broker.");
        public LaunchSpec BuildLaunchSpec(Profile profile, InstallationFacts facts) =>
            new("game.exe", [], facts.Installation.RootPath, new Dictionary<string, string>());
    }

    [Fact]
    public void PlanInstallDoesNotMutateFilesystem()
    {
        var root = Directory.CreateTempSubdirectory("phaket-dry-").FullName;
        try
        {
            var gameFile = Path.Combine(root, "game.dll");
            File.WriteAllText(gameFile, "original");
            var before = SHA256.HashData(File.ReadAllBytes(gameFile));
            var directoryBefore = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);

            var adapter = new DryFakeAdapter();
            var installation = new Installation("install-1", "among-us-steam", root);
            var facts = new InstallationFacts(installation, ["2026.3.5"], ["file.copy"]);
            var profile = new Profile("profile-1", installation.InstallationId, []);
            var plan = adapter.PlanInstall(profile, facts);

            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(gameFile)));
            Assert.Equal(directoryBefore, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories));
            Assert.Equal(64, plan.PlanId.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
