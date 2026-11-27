using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;
using Avalonia.Media.Imaging;

namespace Phaket.Tests;

public sealed class RecipeStudioTests
{
    [Fact]
    public void Switching_modes_keeps_the_same_editable_draft()
    {
        var vm = new RecipeStudioViewModel();
        vm.AddStepCommand.Execute("file.copy");
        vm.Steps[0].To = "game://BepInEx/plugins/Keep.dll";
        var step = vm.Steps[0];

        vm.SwitchModeCommand.Execute("editor");
        Assert.True(vm.IsAdvancedMode);
        Assert.Same(step, vm.Steps[0]);
        Assert.Equal("game://BepInEx/plugins/Keep.dll", vm.Steps[0].To);

        vm.SwitchModeCommand.Execute("wizard");
        Assert.True(vm.IsWizardMode);
        Assert.Same(step, vm.Steps[0]);
        Assert.Equal("game://BepInEx/plugins/Keep.dll", vm.Steps[0].To);
    }

    [Fact]
    public void Reordering_steps_changes_serialized_omrl_order()
    {
        var vm = new RecipeStudioViewModel();
        vm.AddStepCommand.Execute("archive.extract");
        vm.AddStepCommand.Execute("file.copy");
        var firstId = vm.Steps[0].Id;
        var secondId = vm.Steps[1].Id;

        vm.MoveStepDownCommand.Execute(vm.Steps[0]);
        using var document = System.Text.Json.JsonDocument.Parse(vm.OmrlText);
        var ids = document.RootElement.GetProperty("steps").EnumerateArray()
            .Select(step => step.GetProperty("id").GetString()!).ToArray();

        Assert.Equal([secondId, firstId], ids);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pl-PL")]
    public void Invalid_rule_returns_a_localized_readable_error(string language)
    {
        var localization = LocalizationService.Instance;
        var previous = localization.CurrentLanguage;
        try
        {
            localization.CurrentLanguage = language;
            var vm = new RecipeStudioViewModel();
            vm.AddStepCommand.Execute("file.copy");
            vm.Steps[0].To = "game://../outside.dll";

            Assert.False(vm.ValidateCore());
            Assert.Contains(ErrorMessages.Format(ErrorCodes.InvalidUri, language,
                new Dictionary<string, string> { ["uri"] = "game://../outside.dll" }), vm.ValidationResults);
            Assert.DoesNotContain(vm.ValidationResults, message => message == ErrorCodes.PathOutOfBounds);
        }
        finally
        {
            localization.CurrentLanguage = previous;
        }
    }

    [Fact]
    public void Example_plans_select_different_architecture_pinned_loader_artifacts()
    {
        var vm = new RecipeStudioViewModel();
        vm.Artifacts.Clear();
        vm.Artifacts.Add(new ArtifactViewModel { Id = "loader-x86", Source = "package://loader-x86.zip", Arch = "x86" });
        vm.Artifacts.Add(new ArtifactViewModel { Id = "loader-x64", Source = "package://loader-x64.zip", Arch = "x64" });

        vm.AddStepCommand.Execute("loader.ensure");
        var x86 = vm.Steps[^1];
        x86.Id = "loader-x86-step";
        x86.Key = "bepinex";
        x86.From = "loader-x86";
        x86.ConditionArch = "x86";
        x86.Version = "6.0.0";

        vm.AddStepCommand.Execute("loader.ensure");
        var x64 = vm.Steps[^1];
        x64.Id = "loader-x64-step";
        x64.Key = "bepinex";
        x64.From = "loader-x64";
        x64.ConditionArch = "x64";
        x64.Version = "6.0.0";

        Assert.True(vm.ComparePlansCore());
        var x86Plan = Assert.Single(vm.PlanComparisons, plan => plan.Store == "steam" && plan.Arch == "x86");
        var x64Plan = Assert.Single(vm.PlanComparisons, plan => plan.Store == "steam" && plan.Arch == "x64");
        Assert.Contains(x86Plan.Operations, operation => operation.Source == "package://loader-x86.zip");
        Assert.DoesNotContain(x86Plan.Operations, operation => operation.Source == "package://loader-x64.zip");
        Assert.Contains(x64Plan.Operations, operation => operation.Source == "package://loader-x64.zip");
        Assert.DoesNotContain(x64Plan.Operations, operation => operation.Source == "package://loader-x86.zip");
    }

    [Fact]
    public void Example_graph_marks_both_versions_in_a_blocking_pair()
    {
        var game = new GameCatalogItem(new GameDefinition("among-us", ["BepInEx/**"],
            Title: new Dictionary<string, string> { ["en"] = "Among Us", ["pl-PL"] = "Among Us" }), "", "");
        var vm = new GraphViewModel(game, () => { });

        Assert.Contains(vm.ConflictEdges, edge => edge.From == "Custom Roles" && edge.To == "Szyfrowy Mod");
        Assert.All(vm.ConflictEdges, edge =>
        {
            Assert.Contains(vm.Nodes, node => node.Name == edge.From && node.IsBlocked);
            Assert.Contains(vm.Nodes, node => node.Name == edge.To && node.IsBlocked);
        });
        Assert.Contains(vm.Edges, edge => edge.To == "BepInEx IL2CPP");
        Assert.Contains(vm.Edges, edge => edge.To == "Among Us");
    }

    [AvaloniaFact]
    public void Headless_renders_wizard_editor_and_dependency_graph()
    {
        var previous = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.CurrentLanguage = "pl-PL";
        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var tokens = new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Phaket/Styles/Tokens.axaml"))
        {
            Source = new Uri("avares://Phaket/Styles/Tokens.axaml"),
        };
        Application.Current.Resources.MergedDictionaries.Add(tokens);
        var directory = RenderArtifacts.Directory;
        Directory.CreateDirectory(directory);
        try
        {
            var studio = new RecipeStudioViewModel();
            studio.AddStepCommand.Execute("loader.ensure");
            studio.AddStepCommand.Execute("file.copy");
            studio.ValidateCore();
            var studioWindow = new Window
            {
                Width = 1280,
                Height = 800,
                Content = new RecipeStudioView { DataContext = studio },
            };
            studioWindow.Show();
            Dispatcher.UIThread.RunJobs();
            using (var frame = studioWindow.CaptureRenderedFrame())
                (frame ?? throw new InvalidOperationException("Headless renderer returned no frame for the wizard.")).Save(RenderArtifacts.Path("33-studio-wizard.png"), PngBitmapEncoderOptions.Default);
            Assert.Equal(0, studio.WizardStage);
            for (var stage = 1; stage <= 3; stage++)
            {
                studio.NextStageCommand.Execute(null);
                Assert.Equal(stage, studio.WizardStage);
            }
            Assert.False(studio.CanGoNext);

            studio.SwitchModeCommand.Execute("editor");
            Dispatcher.UIThread.RunJobs();
            using (var frame = studioWindow.CaptureRenderedFrame())
                (frame ?? throw new InvalidOperationException("Headless renderer returned no frame for the editor.")).Save(RenderArtifacts.Path("34-studio-editor.png"), PngBitmapEncoderOptions.Default);
            studioWindow.Close();

            var game = new GameCatalogItem(new GameDefinition("among-us", ["BepInEx/**"],
                Title: new Dictionary<string, string> { ["en"] = "Among Us", ["pl-PL"] = "Among Us" }), "", "");
            var graphWindow = new Window
            {
                Width = 1280,
                Height = 800,
                Content = new GraphView { DataContext = new GraphViewModel(game, () => { }) },
            };
            graphWindow.Show();
            Dispatcher.UIThread.RunJobs();
            using (var frame = graphWindow.CaptureRenderedFrame())
                (frame ?? throw new InvalidOperationException("Headless renderer returned no frame for the graph.")).Save(RenderArtifacts.Path("35-graph.png"), PngBitmapEncoderOptions.Default);
            graphWindow.Close();
        }
        finally
        {
            LocalizationService.Instance.CurrentLanguage = previous;
        }
    }
}
