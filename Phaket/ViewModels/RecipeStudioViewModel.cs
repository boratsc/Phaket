using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Domain;
using Phaket.Core.Omrl;
using Phaket.Services;

namespace Phaket.ViewModels;

public partial class StepViewModel : ViewModelBase
{
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string use = "";
    [ObservableProperty] private string from = "";
    [ObservableProperty] private string to = "";
    [ObservableProperty] private string key = "";
    [ObservableProperty] private string value = "";
    [ObservableProperty] private string version = "";
    [ObservableProperty] private string store = "";
    [ObservableProperty] private string format = "";
    [ObservableProperty] private string manifestId = "";
    [ObservableProperty] private string sha256 = "";
    [ObservableProperty] private string stripComponents = "";
    [ObservableProperty] private string overwrite = "";
    [ObservableProperty] private string conditionStore = "";
    [ObservableProperty] private string conditionArch = "";
    [ObservableProperty] private string conditionBuildId = "";
    [ObservableProperty] private int position;
    [ObservableProperty] private bool canMoveDown;
    [ObservableProperty] private bool hasOpaqueCondition;

    private bool loadingCondition;
    internal RecipeCondition? OriginalWhen { get; private set; }
    internal bool ConditionWasEdited { get; private set; }

    public string OperationName => LocalizationService.Instance[$"Studio.Operation.{Use}"];
    public bool HasFrom => Use is "file.copy" or "archive.extract" or "loader.ensure";
    public bool HasTo => Use is "file.copy" or "archive.extract" or "directory.ensure" or "file.remove"
        or "file.verify" or "config.set" or "profile.activate";
    public bool HasKey => Use is "config.set" or "loader.ensure";
    public bool HasValue => Use == "config.set";
    public bool HasVersion => Use is "loader.ensure" or "game.provision";
    public bool HasStore => Use == "game.provision";
    public bool HasOptionalFileSettings => Use is "file.copy" or "archive.extract" or "loader.ensure";
    public bool CanMoveUp => Position > 0;
    public bool HasCondition => !string.IsNullOrWhiteSpace(ConditionStore)
        || !string.IsNullOrWhiteSpace(ConditionArch) || !string.IsNullOrWhiteSpace(ConditionBuildId);
    public string ConditionSummary
    {
        get
        {
            var loc = LocalizationService.Instance;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ConditionStore)) parts.Add($"{loc["Studio.ConditionStore"]}: {ConditionStore}");
            if (!string.IsNullOrWhiteSpace(ConditionArch)) parts.Add($"{loc["Studio.ConditionArch"]}: {ConditionArch}");
            if (!string.IsNullOrWhiteSpace(ConditionBuildId)) parts.Add($"{loc["Studio.ConditionBuildId"]}: {ConditionBuildId}");
            return parts.Count > 0 ? string.Join(" · ", parts) : HasOpaqueCondition ? loc["Studio.AdvancedConditionPreserved"] : "";
        }
    }

    partial void OnUseChanged(string value)
    {
        OnPropertyChanged(nameof(OperationName));
        OnPropertyChanged(nameof(HasFrom));
        OnPropertyChanged(nameof(HasTo));
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(HasVersion));
        OnPropertyChanged(nameof(HasStore));
        OnPropertyChanged(nameof(HasOptionalFileSettings));
    }

    partial void OnPositionChanged(int value)
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }

    partial void OnConditionStoreChanged(string value) => ConditionChanged();
    partial void OnConditionArchChanged(string value) => ConditionChanged();
    partial void OnConditionBuildIdChanged(string value) => ConditionChanged();

    internal void LoadCondition(RecipeCondition? condition)
    {
        OriginalWhen = condition;
        loadingCondition = true;
        var supported = condition is not null;
        IReadOnlyList<RecipeCondition> nodes = condition is null
            ? []
            : condition.All ?? [condition];
        foreach (var node in nodes)
        {
            if (node.Store is { } store) ConditionStore = store;
            else if (node.Arch is { } arch) ConditionArch = arch;
            else if (node.BuildId is { } build) ConditionBuildId = build;
            else supported = false;
        }
        if (condition is { Any: not null } or { Not: not null } or { Os: not null } or { GameVersion: not null }
            or { FileExists: not null } or { Capability: not null }) supported = false;
        HasOpaqueCondition = condition is not null && !supported;
        loadingCondition = false;
        ConditionWasEdited = false;
        OnPropertyChanged(nameof(HasCondition));
        OnPropertyChanged(nameof(ConditionSummary));
    }

    internal RecipeCondition? BuildCondition()
    {
        var conditions = new List<RecipeCondition>();
        if (!string.IsNullOrWhiteSpace(ConditionStore)) conditions.Add(new RecipeCondition { Store = ConditionStore.Trim() });
        if (!string.IsNullOrWhiteSpace(ConditionArch)) conditions.Add(new RecipeCondition { Arch = ConditionArch.Trim() });
        if (!string.IsNullOrWhiteSpace(ConditionBuildId)) conditions.Add(new RecipeCondition { BuildId = ConditionBuildId.Trim() });
        return conditions.Count switch
        {
            0 => null,
            1 => conditions[0],
            _ => new RecipeCondition { All = conditions },
        };
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(OperationName));
        OnPropertyChanged(nameof(ConditionSummary));
    }

    private void ConditionChanged()
    {
        if (!loadingCondition) ConditionWasEdited = true;
        OnPropertyChanged(nameof(HasCondition));
        OnPropertyChanged(nameof(ConditionSummary));
    }
}

public partial class ArtifactViewModel : ViewModelBase
{
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string source = "";
    [ObservableProperty] private string arch = "";
    [ObservableProperty] private string sha256 = "";

    internal bool ArchWasEdited { get; private set; }
    internal bool Sha256WasEdited { get; private set; }

    partial void OnArchChanged(string value) => ArchWasEdited = true;
    partial void OnSha256Changed(string value) => Sha256WasEdited = true;

    internal void Load(RecipeArtifact artifact)
    {
        Id = artifact.Id;
        Source = artifact.Source;
        Arch = artifact.Arch ?? "";
        Sha256 = artifact.Sha256 ?? "";
        ArchWasEdited = false;
        Sha256WasEdited = false;
    }
}

public sealed class OperationOptionViewModel(string id) : ViewModelBase
{
    public string Id { get; } = id;
    public string Name => LocalizationService.Instance[$"Studio.Operation.{Id}"];
    internal void RefreshLocalization() => OnPropertyChanged(nameof(Name));
}

public sealed record PlanOperationPreviewViewModel(string Id, string Kind, string Target, string Source, string Variant)
{
    public bool HasSource => !string.IsNullOrWhiteSpace(Source);
    public bool HasVariant => !string.IsNullOrWhiteSpace(Variant);
}

public sealed record PlanComparisonViewModel(
    string Store, string Arch, string Label, bool IsSuccessful,
    IReadOnlyList<PlanOperationPreviewViewModel> Operations,
    IReadOnlyList<string> Conflicts, string? Error)
{
    public bool HasError => !IsSuccessful;
    public bool HasOperations => Operations.Count > 0;
    public bool HasConflicts => Conflicts.Count > 0;
}

// Both editing modes operate on this single Recipe/step collection.
public partial class RecipeStudioViewModel : ViewModelBase
{
    private readonly RecipeStudioPlatformService platform;
    private Recipe? baseRecipe;
    private bool loadingRecipe;
    private bool gameVersionWasEdited;

    public IReadOnlyList<GameDefinition> Games { get; } = GameDefinitions.LoadAll();
    public IReadOnlyList<OperationOptionViewModel> Palette { get; } = OperationCatalog.All
        .Select(operation => new OperationOptionViewModel(operation.Name))
        .OrderBy(option => option.Id, StringComparer.Ordinal)
        .ToArray();

    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<StepViewModel> LoaderSteps { get; } = [];
    public ObservableCollection<StepViewModel> FileSteps { get; } = [];
    public ObservableCollection<ArtifactViewModel> Artifacts { get; } = [];
    public ObservableCollection<string> ValidationResults { get; } = [];
    public ObservableCollection<string> PlanLines { get; } = [];
    public ObservableCollection<PlanComparisonViewModel> PlanComparisons { get; } = [];

    [ObservableProperty] private GameDefinition? selectedGame;
    [ObservableProperty] private OperationOptionViewModel? selectedOperationOption;
    [ObservableProperty] private string recipeId = "community.phaket.my-recipe";
    [ObservableProperty] private string recipeVersion = "1.0.0";
    [ObservableProperty] private string gameVersionRange = "";
    [ObservableProperty] private string yamlText = "";
    [ObservableProperty] private bool isYamlView;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private StepViewModel? selectedStep;
    [ObservableProperty] private string mode = "wizard";
    [ObservableProperty] private int wizardStage;
    [ObservableProperty] private bool isGraphVisible;

    public GraphViewModel? Graph { get; private set; }

    public RecipeStudioViewModel() : this(null) { }

    internal RecipeStudioViewModel(RecipeStudioPlatformService? platform)
    {
        this.platform = platform ?? new RecipeStudioPlatformService();
        Steps.CollectionChanged += OnStepsCollectionChanged;
        Artifacts.CollectionChanged += OnArtifactsChanged;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        SelectedGame = Games.FirstOrDefault();
        SelectedOperationOption = Palette.FirstOrDefault(option => option.Id == "file.copy") ?? Palette.FirstOrDefault();
        Artifacts.Add(new ArtifactViewModel { Id = "mod-bundle", Source = "package://mod-bundle.zip" });
    }

    public bool IsWizardMode => Mode == "wizard";
    public bool IsAdvancedMode => Mode == "editor";
    public bool IsGameSourceStage => WizardStage == 0;
    public bool IsLoaderStage => WizardStage == 1;
    public bool IsFilesStage => WizardStage == 2;
    public bool IsPlanStage => WizardStage == 3;
    public bool CanGoPrevious => WizardStage > 0;
    public bool CanGoNext => WizardStage < 3;
    public int StageProgress => (WizardStage + 1) * 25;
    public string StageCounter => string.Format(CultureInfo.InvariantCulture,
        LocalizationService.Instance["Studio.StageCounter"], WizardStage + 1, 4);
    public string StageTitle => LocalizationService.Instance[WizardStage switch
    {
        0 => "Studio.Stage.GameSource",
        1 => "Studio.Stage.Loader",
        2 => "Studio.Stage.Files",
        _ => "Studio.Stage.Plan",
    }];
    public string StageDescription => LocalizationService.Instance[WizardStage switch
    {
        0 => "Studio.StageDescription.GameSource",
        1 => "Studio.StageDescription.Loader",
        2 => "Studio.StageDescription.Files",
        _ => "Studio.StageDescription.Plan",
    }];

    public string OmrlText
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(RecipeParser.ToJson(BuildRecipe()));
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"phaket: OMRL preview failed: {ex}");
                return "{}";
            }
        }
    }

    partial void OnModeChanged(string value)
    {
        if (value is not ("wizard" or "editor"))
        {
            Mode = "wizard";
            return;
        }
        OnPropertyChanged(nameof(IsWizardMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
    }

    partial void OnWizardStageChanged(int value)
    {
        OnPropertyChanged(nameof(IsGameSourceStage));
        OnPropertyChanged(nameof(IsLoaderStage));
        OnPropertyChanged(nameof(IsFilesStage));
        OnPropertyChanged(nameof(IsPlanStage));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(StageProgress));
        OnPropertyChanged(nameof(StageCounter));
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(StageDescription));
    }

    partial void OnSelectedGameChanged(GameDefinition? value)
    {
        DraftChanged();
        if (Graph is not null)
        {
            Graph.Dispose();
            Graph = new GraphViewModel(new GameCatalogItem(value ?? Games.FirstOrDefault() ??
                new GameDefinition("unknown-game", ["BepInEx/**"]), "", ""), () => IsGraphVisible = false);
            OnPropertyChanged(nameof(Graph));
        }
    }
    partial void OnRecipeIdChanged(string value) => DraftChanged();
    partial void OnRecipeVersionChanged(string value) => DraftChanged();
    partial void OnGameVersionRangeChanged(string value)
    {
        if (!loadingRecipe) gameVersionWasEdited = true;
        DraftChanged();
    }

    [RelayCommand]
    private void SwitchMode(string? mode) => Mode = mode == "editor" ? "editor" : "wizard";

    [RelayCommand]
    private void ToggleGraph()
    {
        if (IsGraphVisible)
        {
            IsGraphVisible = false;
            return;
        }
        var game = new GameCatalogItem(SelectedGame ?? Games.FirstOrDefault() ??
            new GameDefinition("unknown-game", ["BepInEx/**"]), "", "");
        Graph?.Dispose();
        Graph = new GraphViewModel(game, () => IsGraphVisible = false);
        IsGraphVisible = true;
        OnPropertyChanged(nameof(Graph));
    }

    [RelayCommand]
    private void CloseGraph() => IsGraphVisible = false;

    [RelayCommand]
    private void AddStep(string? use)
    {
        if (string.IsNullOrWhiteSpace(use)) return;
        var step = new StepViewModel
        {
            Id = NextStepId(),
            Use = use,
            From = use switch
            {
                "archive.extract" => "package://mod-bundle",
                "file.copy" => "staging://mod/Mod.dll",
                "loader.ensure" => $"loader-{SelectedGame?.DefaultArch ?? "x64"}",
                _ => "",
            },
            To = use switch
            {
                "archive.extract" => "staging://mod",
                "file.copy" => "game://BepInEx/plugins/Mod.dll",
                "directory.ensure" => "game://BepInEx/plugins",
                "file.verify" or "file.remove" => "game://BepInEx/plugins/Mod.dll",
                "config.set" => "game://BepInEx/config/mod.cfg",
                "profile.activate" => "default",
                _ => "",
            },
            Key = use == "loader.ensure" ? "bepinex" : use == "config.set" ? "Setting" : "",
            Value = use == "config.set" ? "1" : "",
            Version = use == "loader.ensure" ? (SelectedGame?.Slug == "valheim" ? "5.4.2333" : "6.0.0") : use == "game.provision" ? "1.0.0" : "",
            Store = use == "game.provision" ? "steam" : "",
            Format = use == "config.set" ? "keyvalue" : "",
        };
        Steps.Add(step);
        SelectedStep = step;
        EnsureArtifactFor(step);
    }

    [RelayCommand]
    private void AddOperation() => AddStep(SelectedOperationOption?.Id);

    [RelayCommand]
    private void RemoveStep(StepViewModel? step)
    {
        if (step is null || !Steps.Remove(step)) return;
        if (ReferenceEquals(SelectedStep, step)) SelectedStep = Steps.FirstOrDefault();
    }

    [RelayCommand]
    private void MoveStepUp(StepViewModel? step) => MoveStep(step, -1);

    [RelayCommand]
    private void MoveStepDown(StepViewModel? step) => MoveStep(step, 1);

    private void MoveStep(StepViewModel? step, int direction)
    {
        if (step is null) return;
        var index = Steps.IndexOf(step);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Steps.Count) return;
        Steps.Move(index, target);
        SelectedStep = step;
    }

    [RelayCommand]
    private void AddArtifact()
    {
        var index = 1;
        while (Artifacts.Any(artifact => artifact.Id == $"artifact-{index}")) index++;
        var id = $"artifact-{index}";
        Artifacts.Add(new ArtifactViewModel { Id = id, Source = $"package://{id}.zip" });
    }

    [RelayCommand]
    private void RemoveArtifact(ArtifactViewModel? artifact)
    {
        if (artifact is not null) Artifacts.Remove(artifact);
    }

    private string NextStepId()
    {
        var index = 1;
        while (Steps.Any(step => step.Id == $"step-{index}")) index++;
        return $"step-{index}";
    }

    private void EnsureArtifactFor(StepViewModel step)
    {
        string artifactId;
        if (step.Use == "loader.ensure")
        {
            artifactId = step.From;
            if (string.IsNullOrWhiteSpace(artifactId) || artifactId.Length > 100
                || artifactId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                return;
            if (Artifacts.All(artifact => artifact.Id != artifactId))
                Artifacts.Add(new ArtifactViewModel
                {
                    Id = artifactId,
                    Source = $"package://{artifactId}.zip",
                    Arch = artifactId.EndsWith("-x86", StringComparison.Ordinal) ? "x86" : "x64",
                });
            return;
        }
        else if (step.From.StartsWith("package://", StringComparison.Ordinal))
            artifactId = step.From["package://".Length..].Split('/')[0];
        else
            return;

        if (string.IsNullOrWhiteSpace(artifactId) || artifactId is "." or ".."
            || artifactId.Length > 100 || artifactId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            return;
        if (Artifacts.All(artifact => artifact.Id != artifactId))
            Artifacts.Add(new ArtifactViewModel { Id = artifactId, Source = $"package://{artifactId}.zip" });
    }

    // Full parsed recipes remain the source of truth for fields the editor does not expose.
    public Recipe BuildRecipe()
    {
        var capabilities = Steps
            .Select(step => OperationCatalog.TryGet(step.Use, out var operation) ? operation!.Capabilities : [])
            .SelectMany(values => values)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var game = SelectedGame?.Slug ?? Games.FirstOrDefault()?.Slug ?? "unknown-game";
        var baseMatches = baseRecipe is not null && baseRecipe.Target.Game == game;

        var artifacts = Artifacts.Select(artifact =>
        {
            var original = baseMatches ? baseRecipe!.Artifacts.FirstOrDefault(item => item.Id == artifact.Id) : null;
            return new RecipeArtifact
            {
                Id = artifact.Id,
                Source = artifact.Source,
                Sha256 = artifact.Sha256WasEdited ? EmptyToNull(artifact.Sha256) : original?.Sha256,
                SizeBytes = original?.SizeBytes,
                Arch = artifact.ArchWasEdited ? EmptyToNull(artifact.Arch) : original?.Arch,
                Format = original?.Format ?? "zip",
            };
        }).ToArray();

        var steps = Steps.Select(step =>
        {
            var original = baseMatches ? baseRecipe!.Steps.FirstOrDefault(item => item.Id == step.Id) : null;
            var parameters = original is null
                ? BuildParameters(step)
                : MergeParameters(original.With, BuildParameters(step));
            return new RecipeStep
            {
                Id = step.Id,
                Use = step.Use,
                With = parameters,
                When = step.ConditionWasEdited ? step.BuildCondition() : original?.When ?? step.OriginalWhen,
            };
        }).ToArray();

        if (baseMatches)
        {
            var original = baseRecipe!;
            var constraints = gameVersionWasEdited
                ? (string.IsNullOrWhiteSpace(GameVersionRange) ? null : new TargetConstraints { GameVersion = GameVersionRange.Trim() })
                : original.Target.Constraints;
            return original with
            {
                Id = RecipeId,
                Version = RecipeVersion,
                Target = original.Target with { Game = game, Constraints = constraints },
                Requires = new RecipeRequires
                {
                    Capabilities = capabilities.Union(original.Requires?.Capabilities ?? [], StringComparer.Ordinal).ToArray(),
                    Loaders = original.Requires?.Loaders ?? [],
                },
                Artifacts = artifacts,
                Steps = steps,
            };
        }

        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = RecipeId,
            Version = RecipeVersion,
            Target = new RecipeTarget
            {
                Game = game,
                Systems = ["windows", "linux"],
                Constraints = string.IsNullOrWhiteSpace(GameVersionRange) ? null : new TargetConstraints { GameVersion = GameVersionRange.Trim() },
            },
            Requires = new RecipeRequires { Capabilities = capabilities },
            Artifacts = artifacts,
            Steps = steps,
        };
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // UI-exposed parameters override the original; all other original keys survive.
    private static IReadOnlyDictionary<string, JsonElement> MergeParameters(
        IReadOnlyDictionary<string, JsonElement> original, IReadOnlyDictionary<string, JsonElement> exposed)
    {
        var merged = new Dictionary<string, JsonElement>(original, StringComparer.Ordinal);
        foreach (var (key, value) in exposed) merged[key] = value;
        return merged;
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildParameters(StepViewModel step)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        void Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[key] = JsonSerializer.SerializeToElement(value.Trim());
        }
        void AddInt(string key, string value)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                values[key] = JsonSerializer.SerializeToElement(number);
            else if (!string.IsNullOrWhiteSpace(value))
                values[key] = JsonSerializer.SerializeToElement(value.Trim());
        }

        switch (step.Use)
        {
            case "file.copy":
                Add("from", step.From); Add("to", step.To); Add("overwrite", step.Overwrite); break;
            case "archive.extract":
                Add("from", step.From); Add("to", step.To); AddInt("stripComponents", step.StripComponents); break;
            case "directory.ensure":
                Add("path", step.To); break;
            case "file.verify":
                Add("path", step.To); Add("sha256", step.Sha256); break;
            case "file.remove":
                Add("path", step.To); values["ownership"] = JsonSerializer.SerializeToElement("required"); break;
            case "config.set":
                Add("path", step.To); Add("format", step.Format); Add("key", step.Key); Add("value", step.Value); break;
            case "loader.ensure":
                Add("id", step.Key); Add("artifact", step.From); Add("version", step.Version); AddInt("stripComponents", step.StripComponents); break;
            case "game.provision":
                Add("store", step.Store); Add("version", step.Version); Add("manifestId", step.ManifestId); break;
            case "profile.activate":
                Add("profile", step.To); break;
        }
        return values;
    }

    public string SaveYamlCore()
    {
        YamlText = RecipeParser.ToYaml(BuildRecipe());
        Status = LocalizationService.Instance["Studio.Saved"];
        return YamlText;
    }

    public void LoadYamlCore(string yaml) => LoadRecipeCore(RecipeParser.ParseYaml(yaml), yaml);

    public void LoadOmrlCore(string content)
    {
        var text = content.TrimStart();
        var recipe = text.StartsWith("{", StringComparison.Ordinal) ? RecipeParser.ParseJson(content) : RecipeParser.ParseYaml(content);
        LoadRecipeCore(recipe, content);
    }

    private void LoadRecipeCore(Recipe recipe, string source)
    {
        loadingRecipe = true;
        baseRecipe = recipe;
        RecipeId = recipe.Id;
        RecipeVersion = recipe.Version;
        GameVersionRange = recipe.Target.Constraints?.GameVersion ?? "";
        SelectedGame = Games.FirstOrDefault(game => game.Slug == recipe.Target.Game) ?? SelectedGame;
        Steps.Clear();
        foreach (var step in recipe.Steps)
        {
            var model = new StepViewModel { Id = step.Id, Use = step.Use };
            if (step.With.TryGetValue("from", out var from)) model.From = Str(from);
            if (step.With.TryGetValue("to", out var to)) model.To = Str(to);
            if (step.With.TryGetValue("path", out var path)) model.To = Str(path);
            if (step.With.TryGetValue("key", out var key)) model.Key = Str(key);
            if (step.With.TryGetValue("value", out var value)) model.Value = Str(value);
            if (step.With.TryGetValue("version", out var version)) model.Version = Str(version);
            if (step.With.TryGetValue("store", out var store)) model.Store = Str(store);
            if (step.With.TryGetValue("format", out var format)) model.Format = Str(format);
            if (step.With.TryGetValue("manifestId", out var manifestId)) model.ManifestId = Str(manifestId);
            if (step.With.TryGetValue("sha256", out var sha256)) model.Sha256 = Str(sha256);
            if (step.With.TryGetValue("overwrite", out var overwrite)) model.Overwrite = Str(overwrite);
            if (step.With.TryGetValue("stripComponents", out var strip)) model.StripComponents = Str(strip);
            if (step.Use == "loader.ensure" && step.With.TryGetValue("artifact", out var artifact)) model.From = Str(artifact);
            if (step.Use == "loader.ensure" && step.With.TryGetValue("id", out var loaderId)) model.Key = Str(loaderId);
            if (step.Use == "profile.activate" && step.With.TryGetValue("profile", out var profile)) model.To = Str(profile);
            model.LoadCondition(step.When);
            Steps.Add(model);
        }
        Artifacts.Clear();
        foreach (var artifact in recipe.Artifacts)
        {
            var model = new ArtifactViewModel();
            model.Load(artifact);
            Artifacts.Add(model);
        }
        if (recipe.Artifacts.Count == 0)
            Artifacts.Add(new ArtifactViewModel { Id = "mod-bundle", Source = "package://mod-bundle.zip" });
        YamlText = source;
        loadingRecipe = false;
        gameVersionWasEdited = false;
        RefreshStepCollections();
        Status = LocalizationService.Instance["Studio.Loaded"];
        DraftChanged();
    }

    // YAML scalars may be numbers/bools (unquoted); GetString() would throw.
    private static string Str(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();

    public bool RoundTripIsLossless()
    {
        var serialized = RecipeParser.ToJson(BuildRecipe());
        var reloaded = RecipeParser.ParseJson(serialized);
        return RecipeParser.ToJson(reloaded) == serialized;
    }

    public bool ValidateCore()
    {
        ValidationResults.Clear();
        var loc = LocalizationService.Instance;
        try
        {
            var recipe = BuildRecipe();
            if (SelectedGame is null)
                ValidationResults.Add(loc["Studio.GameRequired"]);
            else
            {
                using var document = JsonDocument.Parse(RecipeParser.ToJson(recipe));
                foreach (var error in RecipeSchema.Validate(document.RootElement))
                    ValidationResults.Add(ErrorMessages.Format(error.Code, loc.CurrentLanguage, error.Parameters));
                foreach (var error in RecipeValidator.Validate(recipe, SelectedGame))
                    ValidationResults.Add(ErrorMessages.Format(error.Code, loc.CurrentLanguage, error.Parameters));
            }
        }
        catch (PhaketException ex)
        {
            ValidationResults.Add(ErrorMessages.Format(ex.Error.Code, loc.CurrentLanguage, ex.Error.Parameters));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: recipe validation failed: {ex}");
            ValidationResults.Add(loc["Studio.ValidationUnexpectedError"]);
        }
        if (ValidationResults.Count == 0) ValidationResults.Add(loc["Studio.ValidationOk"]);
        return ValidationResults.Count == 1 && ValidationResults[0] == loc["Studio.ValidationOk"];
    }

    // Every plan uses a temporary fixture sandbox; no real game path is ever passed to Core.
    public bool PlanCore() => ComparePlansCore();

    public bool ComparePlansCore()
    {
        PlanComparisons.Clear();
        PlanLines.Clear();
        var loc = LocalizationService.Instance;
        if (!ValidateCore())
        {
            foreach (var error in ValidationResults) PlanLines.Add(error);
            return false;
        }

        var recipe = BuildRecipe();
        var game = SelectedGame!;
        var sandbox = Path.Combine(Path.GetTempPath(), "phaket-studio-" + Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(sandbox, "game");
        var profileRoot = Path.Combine(sandbox, "profile");
        var stagingRoot = Path.Combine(sandbox, "staging");
        var packageRoot = Path.Combine(sandbox, "packages");
        PlanLines.Add(loc["Studio.SimulationNote"]);
        try
        {
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            Directory.CreateDirectory(profileRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(packageRoot);
            CreatePackageFixtures(recipe, packageRoot);

            var os = game.Platforms?.FirstOrDefault() ?? "windows";
            var scenarios = new[]
            {
                new SampleInstallation("steam", "x64", 1001, "current"),
                new SampleInstallation("epic", "x64", 1001, "current"),
                new SampleInstallation("steam", "x86", 801, "older"),
                new SampleInstallation("epic", "x86", 801, "older"),
            };
            foreach (var scenario in scenarios)
            {
                var storeName = scenario.Store == "steam" ? "Steam" : "Epic";
                var buildName = loc[scenario.Build == "current" ? "Studio.CurrentBuild" : "Studio.OlderBuild"];
                var label = string.Format(CultureInfo.InvariantCulture, loc["Studio.SampleInstallLabel"],
                    storeName, scenario.Arch, buildName, loc["Studio.SampleInstall"]);
                try
                {
                    var facts = new PlanFacts(os, scenario.Store, "17.0.0", OperationCatalog.Capabilities,
                        _ => false, Arch: scenario.Arch, BuildId: scenario.BuildId);
                    var context = new PlannerContext("studio-fixture", "studio-profile",
                        gameRoot, profileRoot, stagingRoot, packageRoot, game, facts);
                    var plan = OperationPlanner.Plan(recipe, context);
                    var operations = plan.Operations.Select(operation =>
                    {
                        var target = operation.Parameters?.GetValueOrDefault("to")
                            ?? operation.Parameters?.GetValueOrDefault("path")
                            ?? (operation.Kind == "loader.ensure" ? "game://" : operation.TargetPath);
                        var source = operation.SourceRef ?? "";
                        var step = Steps.FirstOrDefault(item => item.Id == operation.OpId);
                        var variant = step?.ConditionSummary ?? "";
                        return new PlanOperationPreviewViewModel(operation.OpId, operation.Kind, target, source, variant);
                    }).ToArray();
                    var conflicts = plan.Conflicts.Select(conflict =>
                        $"{conflict.Path} · {string.Join(", ", conflict.Owners)} → {conflict.Resolution}").ToArray();
                    var empty = operations.Length == 0;
                    var error = empty ? ErrorMessages.Format(ErrorCodes.PlanEmpty, loc.CurrentLanguage) : null;
                    PlanComparisons.Add(new PlanComparisonViewModel(scenario.Store, scenario.Arch, label,
                        !empty, operations, conflicts, error));
                    PlanLines.Add(label);
                    foreach (var operation in operations)
                        PlanLines.Add($"{operation.Id} · {operation.Kind} → {operation.Target} · {operation.Source} {operation.Variant}".Trim());
                    foreach (var conflict in conflicts) PlanLines.Add($"{loc["Studio.Conflict"]}: {conflict}");
                    foreach (var warning in plan.Warnings) PlanLines.Add($"{loc["Studio.Warning"]}: {warning}");
                    if (plan.RequiresDownload) PlanLines.Add(loc["Studio.DownloadRequired"]);
                    if (error is not null) PlanLines.Add(error);
                }
                catch (PhaketException ex)
                {
                    var message = ErrorMessages.Format(ex.Error.Code, loc.CurrentLanguage, ex.Error.Parameters);
                    PlanComparisons.Add(new PlanComparisonViewModel(scenario.Store, scenario.Arch, label,
                        false, [], [], message));
                    PlanLines.Add($"{label}: {message}");
                }
            }
            return PlanComparisons.All(plan => plan.IsSuccessful);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: recipe plan failed: {ex}");
            var message = loc["Studio.PlanUnexpectedError"];
            PlanLines.Add(message);
            if (PlanComparisons.Count == 0)
                PlanComparisons.Add(new PlanComparisonViewModel("", "", "", false, [], [], message));
            return false;
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"phaket: recipe fixture cleanup failed: {ex}"); }
        }
    }

    private static void CreatePackageFixtures(Recipe recipe, string packageRoot)
    {
        foreach (var artifact in recipe.Artifacts.Where(item => item.Source.StartsWith("package://", StringComparison.Ordinal)))
        {
            var fileName = artifact.Source["package://".Length..];
            if (fileName.Length == 0 || fileName.Contains('/') || fileName.Contains('\\')
                || fileName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(fileName)
                || fileName.Any(c => char.IsControl(c) || c == ':'))
                continue;
            File.WriteAllBytes(Path.Combine(packageRoot, fileName), []);
        }
    }

    private sealed record SampleInstallation(string Store, string Arch, long BuildId, string Build);

    [RelayCommand]
    private void Validate() => ValidateCore();

    [RelayCommand]
    private void Plan() => PlanCore();

    [RelayCommand]
    private void ComparePlans() => ComparePlansCore();

    [RelayCommand]
    private void SwitchView() => IsYamlView = !IsYamlView;

    [RelayCommand]
    private async Task CopyOmrlAsync()
    {
        try
        {
            Status = await platform.CopyTextAsync(OmrlText)
                ? LocalizationService.Instance["Studio.CopySuccess"]
                : LocalizationService.Instance["Studio.CopyUnavailable"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: OMRL copy failed: {ex}");
            Status = LocalizationService.Instance["Studio.CopyFailed"];
        }
    }

    [RelayCommand]
    private async Task PrepareReleaseAsync()
    {
        if (!ValidateCore())
        {
            Status = LocalizationService.Instance["Studio.ReleaseValidationFailed"];
            return;
        }
        try
        {
            var exported = await platform.ExportOmrlAsync(RecipeId + ".omrl", RecipeParser.ToJson(BuildRecipe()));
            var identity = DeviceIdentityServices.Current;
            Status = exported
                ? LocalizationService.Instance[identity is null || !identity.KeyStore.IsOnline || identity.RegistrationRequired
                    ? "Studio.ExportSuccessNoSigner"
                    : "Studio.ExportSuccessPublishUnavailable"]
                : LocalizationService.Instance["Studio.ExportCanceled"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: OMRL export failed: {ex}");
            Status = LocalizationService.Instance["Studio.ExportFailed"];
        }
    }

    [RelayCommand]
    private async Task SaveYamlAsync()
    {
        try
        {
            var yaml = SaveYamlCore();
            var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.StorageProvider is not { } storage) return;
            var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["Studio.SaveYaml"],
                SuggestedFileName = RecipeId + ".yaml",
                FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("YAML") { Patterns = ["*.yaml", "*.yml"] }],
            });
            if (file is not null)
            {
                await using var writer = new StreamWriter(await file.OpenWriteAsync());
                await writer.WriteAsync(yaml.AsMemory());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: recipe save failed: {ex}");
            Status = LocalizationService.Instance["Studio.SaveError"];
        }
    }

    [RelayCommand]
    private async Task LoadYamlAsync()
    {
        try
        {
            var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["Studio.LoadYaml"],
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("OMRL / YAML") { Patterns = ["*.omrl", "*.json", "*.yaml", "*.yml"] }],
                AllowMultiple = false,
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            LoadOmrlCore(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"phaket: recipe load failed: {ex}");
            Status = LocalizationService.Instance["Studio.LoadError"];
        }
    }

    [RelayCommand]
    private void NextStage()
    {
        if (WizardStage >= 3 || !ValidateWizardStage(WizardStage)) return;
        WizardStage++;
    }

    [RelayCommand]
    private void PreviousStage()
    {
        if (WizardStage > 0) WizardStage--;
    }

    private bool ValidateWizardStage(int stage)
    {
        ValidationResults.Clear();
        var loc = LocalizationService.Instance;
        if (stage == 0)
        {
            if (SelectedGame is null) ValidationResults.Add(loc["Studio.GameRequired"]);
            if (Artifacts.Count == 0 || Artifacts.All(artifact => string.IsNullOrWhiteSpace(artifact.Source)))
                ValidationResults.Add(loc["Studio.SourceRequired"]);
            if (!string.IsNullOrWhiteSpace(GameVersionRange)
                && !VersionRange.TryParse([GameVersionRange.Trim()], out _))
                ValidationResults.Add(ErrorMessages.Format(ErrorCodes.InvalidVersionRange, loc.CurrentLanguage,
                    new Dictionary<string, string> { ["range"] = GameVersionRange.Trim() }));
        }
        else if (stage == 1 && !Steps.Any(step => step.Use == "loader.ensure"))
            ValidationResults.Add(loc["Studio.LoaderRequired"]);
        else if (stage == 2 && !Steps.Any(step => step.Use is "archive.extract" or "file.copy" or "directory.ensure" or "config.set"))
            ValidationResults.Add(loc["Studio.FileOperationRequired"]);

        if (ValidationResults.Count > 0) return false;
        if (stage > 0 && !ValidateCore()) return false;
        return true;
    }

    private void OnStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (loadingRecipe) return;
        if (e.OldItems is not null)
            foreach (StepViewModel step in e.OldItems) step.PropertyChanged -= OnStepPropertyChanged;
        if (e.NewItems is not null)
            foreach (StepViewModel step in e.NewItems) step.PropertyChanged += OnStepPropertyChanged;
        RefreshStepCollections();
        DraftChanged();
    }

    private void OnArtifactsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ArtifactViewModel artifact in e.OldItems) artifact.PropertyChanged -= OnArtifactPropertyChanged;
        if (e.NewItems is not null)
            foreach (ArtifactViewModel artifact in e.NewItems) artifact.PropertyChanged += OnArtifactPropertyChanged;
        DraftChanged();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StepViewModel.OperationName) or nameof(StepViewModel.ConditionSummary)
            or nameof(StepViewModel.HasFrom) or nameof(StepViewModel.HasTo) or nameof(StepViewModel.HasKey)
            or nameof(StepViewModel.HasValue) or nameof(StepViewModel.HasVersion) or nameof(StepViewModel.HasStore)
            or nameof(StepViewModel.HasOptionalFileSettings) or nameof(StepViewModel.HasCondition)
            or nameof(StepViewModel.CanMoveUp) or nameof(StepViewModel.CanMoveDown)) return;
        if (sender is StepViewModel step && e.PropertyName == nameof(StepViewModel.From)) EnsureArtifactFor(step);
        DraftChanged();
    }

    private void OnArtifactPropertyChanged(object? sender, PropertyChangedEventArgs e) => DraftChanged();

    private void RefreshStepCollections()
    {
        foreach (var step in Steps)
        {
            step.Position = Steps.IndexOf(step);
            step.CanMoveDown = step.Position < Steps.Count - 1;
        }
        LoaderSteps.Clear();
        FileSteps.Clear();
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Use == "loader.ensure") LoaderSteps.Add(Steps[index]);
            else FileSteps.Add(Steps[index]);
        }
    }

    private void DraftChanged()
    {
        if (loadingRecipe) return;
        OnPropertyChanged(nameof(OmrlText));
        ValidationResults.Clear();
        PlanComparisons.Clear();
        PlanLines.Clear();
        Status = "";
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.CurrentLanguage) or "Item[]")) return;
        OnPropertyChanged(nameof(StageCounter));
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(StageDescription));
        foreach (var option in Palette) option.RefreshLocalization();
        foreach (var step in Steps) step.RefreshLocalization();
    }
}
