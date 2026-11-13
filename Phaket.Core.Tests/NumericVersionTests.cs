using Phaket.Core.Omrl;

namespace Phaket.Core.Tests;

public class NumericVersionTests
{
    [Theory]
    [InlineData("v1")]
    [InlineData("1.0-beta")]
    [InlineData("1..0")]
    [InlineData("-1")]
    [InlineData("")]
    public void RejectsInvalidVersion(string text) => Assert.False(NumericVersion.TryParse(text, out _));

    [Fact]
    public void ComparesSegmentsAndTreatsMissingAsZero()
    {
        Assert.True(NumericVersion.TryParse("6.0", out var version));
        Assert.True(VersionRange.TryParse([">=6", "<6.0.1"], out var range));
        Assert.True(range!.Contains(version!));
        Assert.True(NumericVersion.TryParse("6.0.0", out var equal));
        Assert.Equal(0, version!.CompareTo(equal));
        Assert.True(NumericVersion.TryParse("1000000000000000000000000", out var large));
        Assert.True(large!.CompareTo(version) > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(">=v1")]
    [InlineData("=>1")]
    [InlineData("1 2")]
    public void RejectsInvalidRange(string text) => Assert.False(VersionRange.TryParse([text], out _));
}
