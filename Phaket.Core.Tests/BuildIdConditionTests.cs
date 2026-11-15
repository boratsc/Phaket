using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class BuildIdConditionTests
{
    [Theory]
    [InlineData(">=25134893", 25134893L, true)]
    [InlineData(">=25134893", 24302054L, false)]
    [InlineData("<=24302054", 24302054L, true)]
    [InlineData("==25134893", 25134893L, true)]
    [InlineData("==25134893", null, false)]
    [InlineData(">=1.2", 2L, false)]
    public void EvaluatesNumericBuildConditions(string range, long? build, bool expected)
    {
        var facts = new PlanFacts("windows", "steam", null, new HashSet<string>(), _ => false, BuildId: build);
        Assert.Equal(expected, ConditionEvaluator.Evaluate(new RecipeCondition { BuildId = range }, facts));
    }
}
