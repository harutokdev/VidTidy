namespace LocalMediaAI.Core.Tests;

public sealed class TimelineSamplingPolicyTests
{
    [Fact]
    public void UpToHalfAnHourFramesAreFiveSecondsApartInTheMiddleOfEachInterval()
    {
        var times = TimelineSamplingPolicy.Times(60);
        Assert.Equal([2.5, 7.5, 12.5, 17.5, 22.5, 27.5, 32.5, 37.5, 42.5, 47.5, 52.5, 57.5], times);
        Assert.Equal(360, TimelineSamplingPolicy.Times(1800).Count);
    }

    [Fact]
    public void LongerVideosAreSpreadOverAtMost360Frames()
    {
        Assert.Equal(10, TimelineSamplingPolicy.Interval(3600));
        var times = TimelineSamplingPolicy.Times(7200);
        Assert.Equal(360, times.Count);
        Assert.Equal(10, times[0]);
        Assert.True(times[^1] < 7200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(19.9)]
    [InlineData(double.NaN)]
    public void ShortOrUnknownVideosGetNoTimeline(double? seconds) => Assert.Empty(TimelineSamplingPolicy.Times(seconds));

    [Theory]
    [InlineData(2.0, 5.0, false)]
    [InlineData(8.0, 5.0, true)]
    [InlineData(10.0, 10.0, false)]
    [InlineData(20.0, 10.0, true)]
    [InlineData(null, 5.0, false)]
    public void TheFileIsReadOnceWhenFrameByFrameWouldReadItMoreThanOnce(double? keyframeSpacing, double interval, bool sequential) =>
        Assert.Equal(sequential, TimelineSamplingPolicy.ReadSequentially(keyframeSpacing, interval));
}
