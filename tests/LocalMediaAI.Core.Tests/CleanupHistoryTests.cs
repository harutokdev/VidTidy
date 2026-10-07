using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class CleanupHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    private static CleanupBatchSummary Batch(int startMinute, int endMinute, int count, long bytes, string op = "Recycle") =>
        new(Guid.NewGuid(), op, T0.AddMinutes(endMinute), T0.AddMinutes(startMinute), count, bytes);

    [Fact]
    public void ConsecutiveBatchesFormOneRun()
    {
        var runs = CleanupHistory.GroupIntoRuns([Batch(0, 1, 25, 100), Batch(1, 2, 25, 200), Batch(2, 3, 10, 50)]);
        var run = Assert.Single(runs);
        Assert.Equal(60, run.FileCount);
        Assert.Equal(350, run.Bytes);
        Assert.Equal(3, run.BatchIds.Count);
    }

    [Fact]
    public void GapsAndDifferentOperationsSplitRuns()
    {
        var runs = CleanupHistory.GroupIntoRuns([Batch(0, 1, 5, 10), Batch(30, 31, 5, 10), Batch(31, 32, 2, 1, "Move"), Batch(40, 41, 0, 0)]);
        Assert.Equal(3, runs.Count);
        Assert.Equal("Move", runs[0].Operation);
        Assert.True(runs[0].CompletedAt > runs[1].CompletedAt);
    }
}
