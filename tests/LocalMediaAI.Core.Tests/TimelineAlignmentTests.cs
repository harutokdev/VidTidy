namespace LocalMediaAI.Core.Tests;

public sealed class TimelineAlignmentTests
{
    private const double Interval = 5;

    private static string Hex(int id)
    {
        var x = (ulong)(id + 1) * 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return (x ^ (x >> 31)).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A made-up film: a still shot for six seconds every half minute, otherwise a picture that changes every half second
    /// (so frames sampled a little apart never match by chance).
    /// </summary>
    private static PreparedNearDuplicateFrame Film(double t, int seed)
    {
        var id = t % 30 < 6 ? seed * 100_000 + 50_000 + (int)(t / 30) : seed * 100_000 + (int)Math.Floor(t / 0.5);
        return new PreparedNearDuplicateFrame(0, Hex(id), Hex(id + 7_000_000), Hex(id + 9_000_000), false);
    }

    /// <summary>A video showing the film from <paramref name="map"/>(t), sampled the way the app samples it.</summary>
    private static TimelineVideo Video(double seconds, Func<double, PreparedNearDuplicateFrame> picture)
    {
        var timeline = Enumerable.Range(0, (int)(seconds / Interval)).Select(i => new TimelineFrame(Interval / 2 + i * Interval, picture(Interval / 2 + i * Interval))).ToArray();
        TimelineWindowReader window = (from, to, fps, _) => Task.FromResult<IReadOnlyList<TimelineFrame>>(
            Enumerable.Range(0, (int)((to - from) * fps)).Select(i => new TimelineFrame(from + i / fps, picture(from + i / fps))).ToArray());
        TimelineFrameReader at = (times, _) => Task.FromResult<IReadOnlyList<PreparedNearDuplicateFrame?>>(times.Select(t => (PreparedNearDuplicateFrame?)picture(t)).ToArray());
        return new TimelineVideo(timeline, seconds, window, at);
    }

    [Fact]
    public async Task ACopyWithThreeSecondsCutOffIsLinedUpToTheTenthOfASecond()
    {
        var full = Video(180, t => Film(t, 1));
        var trimmed = Video(177, t => Film(t + 3, 1));
        Assert.False(TimelineMatching.Compare(trimmed.Timeline, trimmed.Seconds, full.Timeline, full.Seconds).IsContained);

        var match = await TimelineAlignment.AlignAsync(full, trimmed, Interval, CancellationToken.None);

        Assert.True(match.IsContained);
        Assert.False(match.AIsShorter);
        Assert.All(match.Pairs, p => Assert.Equal(3, p.LongerSeconds - p.ShorterSeconds, 6));
        var extra = Assert.Single(match.ExtraInLonger);
        Assert.Equal(0, extra.StartSeconds, 6);
        Assert.Equal(3, extra.EndSeconds, 6);
    }

    [Fact]
    public async Task AMinutePulledOutOfTheMiddleShowsWhereTheLongerOneHasMore()
    {
        var full = Video(300, t => Film(t, 2));
        var cut = Video(240, t => Film(t < 120 ? t : t + 60, 2));

        var match = await TimelineAlignment.AlignAsync(cut, full, Interval, CancellationToken.None);

        Assert.True(match.IsContained);
        var extra = Assert.Single(match.ExtraInLonger);
        Assert.InRange(extra.StartSeconds, 117, 123);
        Assert.InRange(extra.EndSeconds, 177, 183);
    }

    [Fact]
    public async Task FilmsSharingOnlyTheirStillShotsAreNotLinedUp()
    {
        // Same still shots (a logo, a title card), different everything else.
        var first = Video(180, t => Film(t, 3));
        var second = Video(170, t => t % 30 < 6 ? Film(t, 3) : Film(t, 4));

        var match = await TimelineAlignment.AlignAsync(first, second, Interval, CancellationToken.None);

        Assert.False(match.IsContained);
    }
}
