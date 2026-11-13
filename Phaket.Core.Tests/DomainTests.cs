using System.Text;
using Phaket.Core.Domain;

namespace Phaket.Core.Tests;

public class DomainTests
{
    [Fact]
    public void PlanIdentityIsStableAndExcludesItself()
    {
        var plan = new OperationPlan(OperationPlan.CurrentSchemaVersion, new("org.test.mod", "1.0.0"),
            "installation-1", "profile-1", [], [], [], false, []);
        var json = Encoding.UTF8.GetString(CanonicalJson.Serialize(plan));
        Assert.DoesNotContain("planId", json);
        Assert.StartsWith("{\"conflicts\":", json);
        Assert.Equal(plan.PlanId, (plan with { Warnings = [] }).PlanId);
        Assert.NotEqual(plan.PlanId, (plan with { ProfileRef = "profile-2" }).PlanId);
        Assert.Equal(64, plan.PlanId.Length);
    }

    [Fact]
    public void DifferentDomainIdentifiersRemainDistinct()
    {
        var game = new Game("valheim");
        var edition = new GameEdition("valheim-steam", game.GameId, "steam");
        var installation = new Installation("install-1", edition.EditionId, "/fixture");
        var profile = new Profile("profile-1", installation.InstallationId, []);
        Assert.Equal("valheim", edition.GameId);
        Assert.Equal(edition.EditionId, installation.EditionId);
        Assert.Equal(installation.InstallationId, profile.InstallationId);
    }

    [Fact]
    public void JournalVersionAndStateAreSerialized()
    {
        var journal = new ExecutionJournal(ExecutionJournal.CurrentSchemaVersion, "tx", "plan", JournalState.RolledBack, []);
        var json = Encoding.UTF8.GetString(CanonicalJson.Serialize(journal));
        Assert.Contains("\"schemaVersion\":\"execution-journal/v1\"", json);
        Assert.Contains("\"state\":\"rolledBack\"", json);
    }
}
