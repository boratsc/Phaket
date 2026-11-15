using System.Text.Json;
using Json.Schema;
using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class OperationCatalogTests
{
    [Theory]
    [InlineData("steam", true)]
    [InlineData("epic", true)]
    [InlineData("itch", true)]
    [InlineData("msstore", true)]
    [InlineData("unknown", false)]
    public void ProvisionStoreValidation(string store, bool valid)
    {
        Assert.True(OperationCatalog.TryGet("game.provision", out var operation));
        var schema = JsonSchema.FromText(operation!.ParameterSchema);
        Assert.Equal(valid, schema.Evaluate(JsonSerializer.SerializeToElement(new { store, version = "latest" })).IsValid);
    }

    [Fact]
    public void CatalogHasNineUniqueOperationsWithClosedParameterSchemas()
    {
        var expected = new[] { "directory.ensure", "file.copy", "file.remove", "file.verify", "archive.extract", "config.set", "loader.ensure", "profile.activate", "game.provision" };
        Assert.Equal(expected.Order(), OperationCatalog.All.Select(x => x.Name).Order());
        foreach (var op in OperationCatalog.All)
        {
            using var schema = JsonDocument.Parse(op.ParameterSchema);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.All(op.Capabilities, capability => Assert.Contains(capability, OperationCatalog.Capabilities));
            Assert.NotEmpty(op.Preconditions);
            Assert.NotEmpty(op.Postconditions);
            Assert.NotEmpty(op.Recovery);
        }
        Assert.False(OperationCatalog.TryGet("shell.execute", out _));
        Assert.False(OperationCatalog.TryGet("FILE.COPY", out _));
    }

    [Fact]
    public void DestructiveRemovalRequiresOwnership()
    {
        Assert.True(OperationCatalog.TryGet("file.remove", out var operation));
        using var schema = JsonDocument.Parse(operation!.ParameterSchema);
        Assert.Contains(schema.RootElement.GetProperty("required").EnumerateArray(), x => x.GetString() == "ownership");
        Assert.Equal("required", schema.RootElement.GetProperty("properties").GetProperty("ownership").GetProperty("const").GetString());
    }
}
