using Phaket.Core.Omrl;
using Phaket.ViewModels;

namespace Phaket.Tests;

// Launch options must come from the declarative definition (data + provenance),
// never from a hard-coded UI string: Among Us needs the winhttp DLL override on
// Linux/Proton and nothing on Windows (docs.bepinex.dev, verified 2026-11-13).
public class GameViewModelTests
{
    private static GameCatalogItem AmongUs() => new(
        GameDefinitionLoader.LoadYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "definitions", "games", "among-us.yaml"))),
        "", "");

    private static GameViewModel Create(GameCatalogItem game) => new(game, () => { });

    [Fact]
    public void Linux_proposal_carries_winedlloverrides_from_definition() =>
        Assert.Equal("WINEDLLOVERRIDES=\"winhttp.dll=n,b\" %command%",
            Create(AmongUs()).BuildLaunchProposal("linux"));

    [Fact]
    public void Windows_needs_no_extra_launch_options() =>
        Assert.Null(Create(AmongUs()).BuildLaunchProposal("windows"));

    [Fact]
    public void Definition_without_requirements_proposes_nothing()
    {
        var plain = new GameCatalogItem(new GameDefinition("plain-game", ["BepInEx/**"]), "", "");
        Assert.Null(Create(plain).BuildLaunchProposal("linux"));
    }
}
