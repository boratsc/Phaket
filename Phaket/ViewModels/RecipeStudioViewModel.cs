using System.Collections.ObjectModel;
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
}

public partial class ArtifactViewModel : ViewModelBase
{
    [ObservableProperty] private string id = "";
    [ObservableProperty] private string source = "";
}

// Recipe Studio PoC flow (PAK-20): game -> steps (archive.extract, file.copy,
// ...) -> YAML save/load round-trip -> validation -> OperationPlan dry-run
// preview. Visual and YAML views operate on the SAME typed model.
public partial class RecipeStudioViewModel : ViewModelBase
{
    public IReadOnlyList<GameDefinition> Games { get; } = GameDefinitions.LoadAll();
    public IReadOnlyList<string> Palette { get; } = OperationCatalog.All.Select(o => o.Name).ToArray();

    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<ArtifactViewModel> Artifacts { get; } = [];
    public ObservableCollection<string> ValidationResults { get; } = [];
    public ObservableCollection<string> PlanLines { get; } = [];

    [ObservableProperty] private GameDefinition? selectedGame;
    [ObservableProperty] private string recipeId = "community.phaket.my-recipe";
    [ObservableProperty] private string recipeVersion = "1.0.0";
    [ObservableProperty] private string yamlText = "";
    [ObservableProperty] private bool isYamlView;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private StepViewModel? selectedStep;

    public RecipeStudioViewModel()
    {
        selectedGame = Games.FirstOrDefault();
    }

    [RelayCommand]
    private void AddStep(string use)
    {
        var step = new StepViewModel
        {
            Id = $"step-{Steps.Count + 1}",
            Use = use,
            From = use is "archive.extract" or "file.copy" ? "package://mod-bundle" : "",
            To = use switch
            {
                "archive.extract" => "staging://mod",
                "file.copy" => "game://BepInEx/plugins/Mod.dll",
                "directory.ensure" => "game://BepInEx/plugins",
                "file.verify" or "file.remove" => "game://BepInEx/plugins/Mod.dll",
                "config.set" => "game://BepInEx/config/mod.cfg",
                "profile.activate" => "profile-1",
                _ => "",
            },
            Key = use == "loader.ensure" ? "bepinex" : use == "config.set" ? "Setting" : "",
            Value = use == "config.set" ? "1" : "",
        };
        if (use == "file.copy") step.From = "staging://mod/Mod.dll";
        if (use == "loader.ensure") step.From = "mod-bundle";
        Steps.Add(step);
        SelectedStep = step;
        EnsureArtifactFor(step.From);
    }

    [RelayCommand]
    private void RemoveStep(StepViewModel? step)
    {
        if (step is not null) Steps.Remove(step);
    }

    [RelayCommand]
    private void AddArtifact()
    {
        var id = $"artifact-{Artifacts.Count + 1}";
        Artifacts.Add(new ArtifactViewModel { Id = id, Source = $"package://{id}.zip" });
    }

    private void EnsureArtifactFor(string from)
    {
        if (!from.StartsWith("package://", StringComparison.Ordinal)) return;
        var artifactId = from["package://".Length..].Split('/')[0];
        if (Artifacts.All(a => a.Id != artifactId))
            Artifacts.Add(new ArtifactViewModel { Id = artifactId, Source = $"package://{artifactId}.zip" });
    }

    // One typed model for both views (spec §6.6: single source of truth).
    public Recipe BuildRecipe()
    {
        var capabilities = Steps
            .Select(s => OperationCatalog.TryGet(s.Use, out var op) ? op!.Capabilities : [])
            .SelectMany(c => c)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new Recipe
        {
            SchemaVersion = "omrl/v0alpha1",
            Id = RecipeId,
            Version = RecipeVersion,
            Target = new RecipeTarget
            {
                Game = SelectedGame?.Slug ?? "among-us",
                Systems = ["windows", "linux"],
            },
            Requires = new RecipeRequires { Capabilities = capabilities },
            Artifacts = Artifacts.Select(a => new RecipeArtifact { Id = a.Id, Source = a.Source }).ToArray(),
            Steps = Steps.Select(s => new RecipeStep
            {
                Id = s.Id,
                Use = s.Use,
                With = BuildParameters(s),
            }).ToArray(),
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildParameters(StepViewModel step)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (step.Use)
        {
            case "file.copy":
            case "archive.extract":
                p["from"] = step.From; p["to"] = step.To; break;
            case "directory.ensure":
            case "file.verify":
                p["path"] = step.To; break;
            case "file.remove":
                p["path"] = step.To; p["ownership"] = "required"; break;
            case "config.set":
                p["path"] = step.To; p["format"] = "keyvalue"; p["key"] = step.Key; p["value"] = step.Value; break;
            case "loader.ensure":
                p["id"] = step.Key; p["artifact"] = step.From; break;
            case "profile.activate":
                p["profile"] = step.To; break;
        }
        return p.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
    }

    public string SaveYamlCore()
    {
        YamlText = RecipeParser.ToYaml(BuildRecipe());
        Status = LocalizationService.Instance["Studio.Saved"];
        return YamlText;
    }

    public void LoadYamlCore(string yaml)
    {
        var recipe = RecipeParser.ParseYaml(yaml);
        RecipeId = recipe.Id;
        RecipeVersion = recipe.Version;
        SelectedGame = Games.FirstOrDefault(g => g.Slug == recipe.Target.Game) ?? SelectedGame;
        Steps.Clear();
        foreach (var step in recipe.Steps)
        {
            var vm = new StepViewModel { Id = step.Id, Use = step.Use };
            if (step.With.TryGetValue("from", out var from)) vm.From = from.GetString() ?? "";
            if (step.With.TryGetValue("to", out var to)) vm.To = to.GetString() ?? "";
            if (step.With.TryGetValue("path", out var path)) vm.To = path.GetString() ?? "";
            if (step.With.TryGetValue("key", out var key)) vm.Key = key.GetString() ?? "";
            if (step.With.TryGetValue("value", out var value)) vm.Value = value.GetString() ?? "";
            if (step.Use == "loader.ensure" && step.With.TryGetValue("artifact", out var artifact)) vm.From = artifact.GetString() ?? "";
            if (step.Use == "profile.activate" && step.With.TryGetValue("profile", out var profile)) vm.To = profile.GetString() ?? "";
            Steps.Add(vm);
        }
        Artifacts.Clear();
        foreach (var artifact in recipe.Artifacts)
            Artifacts.Add(new ArtifactViewModel { Id = artifact.Id, Source = artifact.Source });
        YamlText = yaml;
        Status = LocalizationService.Instance["Studio.Loaded"];
    }

    public bool RoundTripIsLossless()
    {
        var yaml = RecipeParser.ToYaml(BuildRecipe());
        var reloaded = RecipeParser.ParseYaml(yaml);
        return RecipeParser.ToJson(reloaded) == RecipeParser.ToJson(BuildRecipe());
    }

    public bool ValidateCore()
    {
        ValidationResults.Clear();
        var loc = LocalizationService.Instance;
        try
        {
            var recipe = BuildRecipe();
            var element = JsonDocument.Parse(RecipeParser.ToJson(recipe)).RootElement;
            foreach (var error in RecipeSchema.Validate(element))
                ValidationResults.Add(ErrorMessages.Format(error.Code, loc.CurrentLanguage, error.Parameters));
            foreach (var error in RecipeValidator.Validate(recipe, SelectedGame ?? Games[0]))
                ValidationResults.Add(ErrorMessages.Format(error.Code, loc.CurrentLanguage, error.Parameters));
        }
        catch (PhaketException ex)
        {
            ValidationResults.Add(ex.LocalizedMessage(loc.CurrentLanguage));
        }
        if (ValidationResults.Count == 0)
            ValidationResults.Add(loc["Studio.ValidationOk"]);
        return ValidationResults.Count == 1 && ValidationResults[0] == loc["Studio.ValidationOk"];
    }

    // Dry-run preview on a fixture sandbox — never the real game (spec §6.6).
    public bool PlanCore()
    {
        PlanLines.Clear();
        var loc = LocalizationService.Instance;
        var sandbox = Path.Combine(Path.GetTempPath(), "phaket-studio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gameRoot = Path.Combine(sandbox, "game");
            var profileRoot = Path.Combine(sandbox, "profile");
            var stagingRoot = Path.Combine(sandbox, "staging");
            var packageRoot = Path.Combine(sandbox, "packages");
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            Directory.CreateDirectory(profileRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(packageRoot);
            foreach (var artifact in Artifacts.Where(a => a.Source.StartsWith("package://", StringComparison.Ordinal)))
            {
                var fileName = artifact.Source["package://".Length..];
                File.WriteAllBytes(Path.Combine(packageRoot, fileName), []);
            }
            var definition = SelectedGame ?? Games[0];
            var facts = new PlanFacts("linux", "steam", "2026.3.5", OperationCatalog.Capabilities, _ => false);
            var context = new PlannerContext("studio-fixture", "studio-profile",
                gameRoot, profileRoot, stagingRoot, packageRoot, definition, facts);
            var plan = OperationPlanner.Plan(BuildRecipe(), context);
            PlanLines.Add(loc["Studio.SimulationNote"]);
            foreach (var operation in plan.Operations)
                PlanLines.Add($"{operation.OpId} · {operation.Kind} → {operation.TargetPath}");
            foreach (var conflict in plan.Conflicts)
                PlanLines.Add($"{loc["Studio.Conflicts"]}: {conflict.Path} ({string.Join(", ", conflict.Owners)}) → {conflict.Resolution}");
            foreach (var warning in plan.Warnings)
                PlanLines.Add($"{loc["Studio.Warnings"]}: {warning}");
            if (plan.RequiresDownload)
                PlanLines.Add("requires-download: true");
            return true;
        }
        catch (PhaketException ex)
        {
            PlanLines.Add(ex.LocalizedMessage(loc.CurrentLanguage));
            return false;
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    [RelayCommand]
    private void Validate() => ValidateCore();

    [RelayCommand]
    private void Plan() => PlanCore();

    [RelayCommand]
    private void SwitchView() => IsYamlView = !IsYamlView;

    [RelayCommand]
    private async Task SaveYamlAsync()
    {
        var yaml = SaveYamlCore();
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow : null;
        if (top?.StorageProvider is { } storage)
        {
            var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["Studio.SaveYaml"],
                SuggestedFileName = RecipeId + ".yaml",
                FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("YAML") { Patterns = ["*.yaml", "*.yml"] }],
            });
            if (file is not null)
            {
                await using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream);
                await writer.WriteAsync(yaml);
            }
        }
    }

    [RelayCommand]
    private async Task LoadYamlAsync()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow : null;
        if (top?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Studio.LoadYaml"],
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("YAML") { Patterns = ["*.yaml", "*.yml"] }],
            AllowMultiple = false,
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        LoadYamlCore(await reader.ReadToEndAsync());
    }
}
