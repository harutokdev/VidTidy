using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class ClearSimilarRuleTests
{
    private static readonly VideoFacts Keep = new(1920 * 1080, 8_000_000, 120);

    [Fact]
    public void SameLengthAndALowerQualityCopyIsClear() =>
        Assert.True(ClearSimilarRule.IsClear(0.96, Keep, [new VideoFacts(1280 * 720, 3_000_000, 120.4)]));

    [Fact]
    public void IdenticalQualityIsClear() =>
        Assert.True(ClearSimilarRule.IsClear(0.95, Keep, [Keep]));

    [Theory]
    [InlineData(0.94, 1280 * 720, 3_000_000, 120)]   // match too low
    [InlineData(0.99, 1280 * 720, 3_000_000, 122)]   // a scene only one has
    [InlineData(0.99, 3840 * 2160, 3_000_000, 120)]  // the other is sharper
    [InlineData(0.99, 1280 * 720, 9_000_000, 120)]   // the other has more detail
    public void DoubtfulGroupsAreNotClear(double match, long pixels, long bitrate, double duration) =>
        Assert.False(ClearSimilarRule.IsClear(match, Keep, [new VideoFacts(pixels, bitrate, duration)]));

    [Fact]
    public void UnknownFactsAreNotClear()
    {
        Assert.False(ClearSimilarRule.IsClear(null, Keep, [Keep]));
        Assert.False(ClearSimilarRule.IsClear(0.99, Keep, [new VideoFacts(1280 * 720, null, 120)]));
        Assert.False(ClearSimilarRule.IsClear(0.99, Keep with { DurationSeconds = null }, [Keep]));
        Assert.False(ClearSimilarRule.IsClear(0.99, Keep, []));
    }

    [Fact]
    public void EveryOtherFileMustPass() =>
        Assert.False(ClearSimilarRule.IsClear(0.99, Keep, [new VideoFacts(1280 * 720, 3_000_000, 120), new VideoFacts(3840 * 2160, 3_000_000, 120)]));
}
