namespace LocalMediaAI.Core.Tests;

public sealed class TimelineMatchingTests
{
    private const double Step = 2;

    /// <summary>A fingerprint per scene, about half its bits away from any other scene's.</summary>
    private static string Scene(int id)
    {
        var x = (ulong)(id + 1) * 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return (x ^ (x >> 31)).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One frame every two seconds showing the given scenes in turn; scene -1 is a black frame.</summary>
    private static IReadOnlyList<TimelineFrame> Timeline(params int[] scenes) =>
        scenes.Select((scene, index) => new TimelineFrame(index * Step + 1,
            new PreparedNearDuplicateFrame(index, scene < 0 ? "0000000000000000" : Scene(scene), Scene(1000 + Math.Max(scene, 0)), Scene(2000 + Math.Max(scene, 0)), scene < 0))).ToArray();

    private static double Length(IReadOnlyList<TimelineFrame> timeline) => timeline.Count * Step;

    private static int[] Range(int from, int count) => Enumerable.Range(from, count).ToArray();

    [Fact]
    public void AVideoWithItsStartCutOffIsContainedAndTheLongerStartIsShown()
    {
        var full = Timeline(Range(0, 20));
        var trimmed = Timeline(Range(3, 17));
        var match = TimelineMatching.Compare(full, Length(full), trimmed, Length(trimmed));
        Assert.True(match.IsContained);
        Assert.False(match.AIsShorter);
        Assert.Equal(1.0, match.ShorterCoverage);
        var extra = Assert.Single(match.ExtraInLonger);
        Assert.Equal(0, extra.StartSeconds);
        Assert.InRange(extra.EndSeconds, 5, 7);
        Assert.Empty(match.ExtraInShorter);
    }

    [Fact]
    public void AVideoWithAScenePulledOutOfTheMiddleShowsWhereTheLongerOneHasMore()
    {
        var full = Timeline(Range(0, 30));
        var cut = Timeline([.. Range(0, 12), .. Range(18, 12)]);
        var match = TimelineMatching.Compare(cut, Length(cut), full, Length(full));
        Assert.True(match.IsContained);
        Assert.True(match.AIsShorter);
        var extra = Assert.Single(match.ExtraInLonger);
        Assert.InRange(extra.StartSeconds, 22, 25);
        Assert.InRange(extra.EndSeconds, 35, 38);
    }

    [Fact]
    public void VideosSharingOnlyAnOpeningAreNotContained()
    {
        var first = Timeline([.. Range(0, 5), .. Range(100, 15)]);
        var second = Timeline([.. Range(0, 5), .. Range(200, 18)]);
        var match = TimelineMatching.Compare(first, Length(first), second, Length(second));
        Assert.False(match.IsContained);
        Assert.Equal(5, match.MatchedFrames);
    }

    [Fact]
    public void RepeatingOneSceneDoesNotLineUpWithAVideoShowingItOnce()
    {
        var repeated = Timeline(Enumerable.Repeat(7, 10).ToArray());
        var once = Timeline([.. Range(50, 10), 7, .. Range(60, 10)]);
        var match = TimelineMatching.Compare(repeated, Length(repeated), once, Length(once));
        Assert.Equal(1, match.MatchedFrames);
        Assert.False(match.IsContained);
    }

    [Fact]
    public void ScenesOutOfOrderOnlyCountAsFarAsTheyKeepTheirOrder()
    {
        var original = Timeline(Range(0, 10));
        var shuffled = Timeline([.. Range(5, 5), .. Range(0, 5)]);
        var match = TimelineMatching.Compare(original, Length(original), shuffled, Length(shuffled));
        Assert.Equal(5, match.MatchedFrames);
        Assert.False(match.IsContained);
    }

    [Fact]
    public void BlackFramesNeitherCountForNorAgainst()
    {
        var withFades = Timeline([-1, .. Range(0, 8), -1, -1, .. Range(8, 8), -1]);
        var plain = Timeline(Range(0, 16));
        var match = TimelineMatching.Compare(withFades, Length(withFades), plain, Length(plain));
        Assert.True(match.IsContained);
        Assert.Equal(16, match.MatchedFrames);
        Assert.Empty(match.ExtraInLonger);
    }

    [Fact]
    public void TooFewFramesInCommonNeverPass()
    {
        var short1 = Timeline(Range(0, 3));
        var match = TimelineMatching.Compare(short1, Length(short1), short1, Length(short1));
        Assert.Equal(1.0, match.ShorterCoverage);
        Assert.False(match.IsContained);
    }
}
