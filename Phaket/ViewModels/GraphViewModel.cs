using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Profiles;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed class GraphNodeVm(string id, string name, string version, IReadOnlyList<string> dependencies, bool isLoader, bool isGame, bool isBlocked) : ViewModelBase
{
    public string Id { get; } = id;
    public string Name { get; private set; } = name;
    public string Version { get; } = version;
    public IReadOnlyList<string> Dependencies { get; } = dependencies;
    public bool IsLoader { get; } = isLoader;
    public bool IsGame { get; } = isGame;
    public bool IsBlocked { get; } = isBlocked;
    public bool HasDependencies => Dependencies.Count > 0;
    public string DependencyNames => string.Join(", ", Dependencies);
    public string KindLabel => LocalizationService.Instance[IsGame ? "Graph.Node.Game" : IsLoader ? "Graph.Node.Loader" : "Graph.Node.Mod"];

}

public sealed class GraphEdgeVm(GraphNodeVm from, GraphNodeVm to) : ViewModelBase
{
    public GraphNodeVm FromNode { get; } = from;
    public GraphNodeVm ToNode { get; } = to;
    public string From => FromNode.Name;
    public string FromVersion => FromNode.Version;
    public string To => ToNode.Name;
    public string ToVersion => ToNode.Version;
    public string Label => LocalizationService.Instance["Graph.Requires"];
}

public sealed class GraphConflictVm(GraphNodeVm from, GraphNodeVm to) : ViewModelBase
{
    public GraphNodeVm FromNode { get; } = from;
    public GraphNodeVm ToNode { get; } = to;
    public string From => FromNode.Name;
    public string FromVersion => FromNode.Version;
    public string To => ToNode.Name;
    public string ToVersion => ToNode.Version;
    public string Pair => $"{From} {FromVersion} ↔ {To} {ToVersion}";
}

public partial class GraphViewModel : ViewModelBase
{
    private IReadOnlyList<string>? cycle;
    private IReadOnlyList<string>? cycleNames;
    private readonly Action back;

    public GraphViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        this.back = back;
        Nodes = [];
        Edges = [];
        ConflictEdges = [];
        BuildSampleGraph();
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public GameCatalogItem Game { get; }
    public string GameTitle => Game.Title;
    public ObservableCollection<GraphNodeVm> Nodes { get; }
    public ObservableCollection<GraphEdgeVm> Edges { get; }
    public ObservableCollection<GraphConflictVm> ConflictEdges { get; }
    public string? CycleWarning => cycleNames is null ? null
        : $"{LocalizationService.Instance["Graph.Cycle"]}: {string.Join(" → ", cycleNames)}";
    public int NodeCount => Nodes.Count;
    public int EdgeCount => Edges.Count + ConflictEdges.Count;
    public bool HasConflicts => ConflictEdges.Count > 0;
    public bool Empty => Nodes.Count == 0;
    public string SampleNote => LocalizationService.Instance["Graph.SampleNote"];

    private void BuildSampleGraph()
    {
        var isValheim = Game.Definition.Slug.Equals("valheim", StringComparison.Ordinal);
        var loader = isValheim ? "BepInEx" : "BepInEx IL2CPP";
        var gameVersion = isValheim ? "0.221.10" : "17.0.0";
        (string Name, string Version)[] mods;
        if (isValheim)
            mods =
            [
                (loader, "5.4.2333"),
                ("Valheim Plus", "0.9.16"),
                ("Equipment and Quick Slots", "2.1.13"),
                ("PlantEverything", "1.18.0"),
                ("CraftFromContainers", "1.9.0"),
            ];
        else
            mods =
            [
                (loader, "6.0.0"),
                ("Custom Roles", "2.0.0"),
                ("Szyfrowy Mod", "1.3.0"),
                ("Reactor", "2.3.0"),
            ];

        var sample = DependencyGraph.Sample(mods);
        var gameId = $"game:{Game.Definition.Slug}";
        var nodes = sample.Nodes.Append(new DependencyNode(gameId, GameTitle, gameVersion)).ToArray();
        var edges = sample.Edges.ToList();
        edges.Add(new DependencyEdge(loader, gameId, DependencyKind.Requires));
        if (!isValheim)
            edges.Add(new DependencyEdge("Custom Roles", "Szyfrowy Mod", DependencyKind.Conflicts));

        var graph = new DependencyGraph(nodes, edges);
        var nodeById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var conflicts = graph.Conflicts();
        var blockedIds = conflicts.SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet(StringComparer.Ordinal);
        var nodeViews = new Dictionary<string, GraphNodeVm>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var dependencies = graph.Edges.Where(edge => edge.From == node.Id && edge.Kind == DependencyKind.Requires)
                .Select(edge => nodeById.TryGetValue(edge.To, out var target) ? $"{target.Name} {target.Version}" : edge.To)
                .ToArray();
            var viewModel = new GraphNodeVm(node.Id, node.Name, node.Version, dependencies,
                node.Id == loader, node.Id == gameId, blockedIds.Contains(node.Id));
            nodeViews.Add(node.Id, viewModel);
            Nodes.Add(viewModel);
        }

        foreach (var edge in graph.Edges.Where(edge => edge.Kind == DependencyKind.Requires))
            Edges.Add(new GraphEdgeVm(nodeViews[edge.From], nodeViews[edge.To]));
        foreach (var edge in conflicts)
            ConflictEdges.Add(new GraphConflictVm(nodeViews[edge.From], nodeViews[edge.To]));

        cycle = graph.DetectCycle();
        cycleNames = cycle?.Select(id => nodeById.TryGetValue(id, out var node) ? $"{node.Name} {node.Version}" : id).ToArray();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.CurrentLanguage) or "Item[]")) return;
        OnPropertyChanged(nameof(GameTitle));
        OnPropertyChanged(nameof(SampleNote));
        Nodes.Clear();
        Edges.Clear();
        ConflictEdges.Clear();
        BuildSampleGraph();
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(EdgeCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(CycleWarning));
    }

    [RelayCommand] private void Back() => back();
}
