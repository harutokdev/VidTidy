using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class AnalysisCountdownTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScanPercentCountsSeenFilesAgainstLastTime()
    {
        var parts = new[] { new ScanPart(100, 100, true), new ScanPart(300, 100, false), new ScanPart(100, 0, false) };
        Assert.Equal(40, ChangeCheckEstimate.Percent(parts));
    }

    [Fact]
    public void ScanPercentStaysBelowHundredUntilEveryFolderIsDone()
    {
        Assert.Equal(99, ChangeCheckEstimate.Percent([new ScanPart(10, 50, false)]));
        Assert.Equal(100, ChangeCheckEstimate.Percent([new ScanPart(10, 50, true)]));
    }

    [Fact]
    public void ScanTimeLeftUsesTheRateSoFar()
    {
        // 200 files in 20 s (10 per second); 200 + 100 left.
        var parts = new[]
        {
            new ScanPart(100, 100, true, T0, T0.AddSeconds(10)),
            new ScanPart(300, 100, false, T0.AddSeconds(10)),
            new ScanPart(100, 0, false)
        };
        Assert.Equal(TimeSpan.FromSeconds(30), ChangeCheckEstimate.Remaining(parts, T0.AddSeconds(20)));
    }

    [Fact]
    public void NoScanTimeLeftBeforeAnyFileIsSeen()
    {
        Assert.Null(ChangeCheckEstimate.Remaining([new ScanPart(100, 0, false, T0)], T0.AddSeconds(5)));
        Assert.Null(ChangeCheckEstimate.Remaining([new ScanPart(100, 25, false, T0)], T0.AddSeconds(1)));
    }

    [Fact]
    public void ClockCountsDownBetweenEstimates()
    {
        var clock = new FinishClock();
        clock.Observe(T0, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(4), clock.Left(T0.AddMinutes(1)));
        Assert.Equal(TimeSpan.Zero, clock.Left(T0.AddMinutes(9)));
    }

    [Fact]
    public void ClockMovesOnlyPartWayTowardANoisyEstimate()
    {
        var clock = new FinishClock();
        clock.Observe(T0, TimeSpan.FromMinutes(5));
        clock.Observe(T0, TimeSpan.FromSeconds(5 * 60 + 40));
        Assert.Equal(T0.AddSeconds(300 + 12), clock.FinishAt);
    }

    [Fact]
    public void ClockTakesABigChangeAtOnce()
    {
        var clock = new FinishClock();
        clock.Observe(T0, TimeSpan.FromMinutes(2));
        clock.Observe(T0, TimeSpan.FromMinutes(30));
        Assert.Equal(T0.AddMinutes(30), clock.FinishAt);
    }

    [Fact]
    public void ClockKeepsItsTimeWhenThereIsNoEstimate()
    {
        var clock = new FinishClock();
        clock.Observe(T0, TimeSpan.FromMinutes(2));
        clock.Observe(T0.AddSeconds(10), null);
        Assert.Equal(T0.AddMinutes(2), clock.FinishAt);
    }

    [Theory]
    [InlineData(12, "約15秒")]
    [InlineData(200, "約3分20秒")]
    [InlineData(1500, "約25分")]
    public void FormatsTheCountdown(int seconds, string text) => Assert.Equal(text, FinishClock.Format(TimeSpan.FromSeconds(seconds)));
}
