using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Phaket.Core.Profiles;
using Phaket.Services;

namespace Phaket.ViewModels;

public sealed record GraphNodeVm(string Name, string Version, IReadOnlyList<string> Dependencies, bool IsLoader)
{
    public bool HasDependencies => Dependencies.Count > 0;
    public string DependencyNames => string.Join(", ", Dependencies);
}

public sealed record GraphConflictVm(string From, string To);

public partial class GraphViewModel : ViewModelBase
{
    private readonly Action back;
    private readonly IReadOnlyList<string>? cycle;

    public GraphViewModel(GameCatalogItem game, Action back)
    {
        Game = game;
        this.back = back;
        var mods = new ProfileStore().Active(game.Definition.Slug)?.Mods ?? [];
        var graph = DependencyGraph.Sample(mods.Select(m => (m.Name, m.Version)));
        string NameOf(string id) => graph.Nodes.FirstOrDefault(n => n.Id == id)?.Name ?? id;
        Nodes = new ObservableCollection<GraphNodeVm>(graph.Nodes.Select(n => new GraphNodeVm(
            n.Name, n.Version, graph.TransitiveDependencies(n.Id).Select(NameOf).ToArray(),
            n.Name is "BepInEx" or "BepInEx IL2CPP")));
        ConflictEdges = graph.Conflicts().Select(e => new GraphConflictVm(NameOf(e.From), NameOf(e.To))).ToArray();
        cycle = graph.DetectCycle()?.Select(NameOf).ToArray();
        EdgeCount = graph.Edges.Count(e => e.Kind == DependencyKind.Requires);
    }

    public GameCatalogItem Game { get; }
    public string GameTitle => Game.Title;
    public ObservableCollection<GraphNodeVm> Nodes { get; }
    public IReadOnlyList<GraphConflictVm> ConflictEdges { get; }
    public string? CycleWarning => cycle is null ? null : $"{LocalizationService.Instance["Graph.Cycle"]}: {string.Join(" → ", cycle)}";
    public int NodeCount => Nodes.Count;
    public int EdgeCount { get; }
    public bool HasConflicts => ConflictEdges.Count > 0;
    public bool Empty => Nodes.Count == 0;

    [RelayCommand] private void Back() => back();
}
