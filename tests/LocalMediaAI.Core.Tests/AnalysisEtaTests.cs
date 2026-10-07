using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class AnalysisEtaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoEstimateUntilProgressIsObserved()
    {
        var eta = new AnalysisEta();
        eta.Observe(T0, 100);
        Assert.Null(eta.Remaining);
        eta.Observe(T0.AddSeconds(10), 100);
        Assert.Null(eta.Remaining);
    }

    [Fact]
    public void EstimatesFromCompletionRate()
    {
        var eta = new AnalysisEta();
        eta.Observe(T0, 100);
        eta.Observe(T0.AddSeconds(60), 80);
        Assert.Equal(TimeSpan.FromMinutes(4), eta.Remaining);
    }

    [Fact]
    public void NewWorkRestartsTheWindow()
    {
        var eta = new AnalysisEta();
        eta.Observe(T0, 10);
        eta.Observe(T0.AddSeconds(10), 5);
        eta.Observe(T0.AddSeconds(20), 50);
        Assert.Null(eta.Remaining);
        eta.Observe(T0.AddSeconds(30), 40);
        Assert.Equal(TimeSpan.FromSeconds(40), eta.Remaining);
    }

    [Fact]
    public void FinishedMeansZeroAndResets()
    {
        var eta = new AnalysisEta();
        eta.Observe(T0, 10);
        eta.Observe(T0.AddSeconds(10), 0);
        Assert.Equal(TimeSpan.Zero, eta.Remaining);
        Assert.False(eta.IsRunning);
    }

    [Theory]
    [InlineData(20, "1分未満")]
    [InlineData(150, "約3分")]
    [InlineData(4000, "約1時間7分")]
    public void FormatsForPeople(int seconds, string text) => Assert.Equal(text, AnalysisEta.Format(TimeSpan.FromSeconds(seconds)));
}
