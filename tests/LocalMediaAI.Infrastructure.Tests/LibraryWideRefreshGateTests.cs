using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class LibraryWideRefreshGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    [Fact]
    public void EveryCheckIsDueAtTheStartOfASession()
    {
        var gate = new LibraryWideRefreshGate(() => DateTimeOffset.UtcNow, Interval);

        Assert.Equal(Enum.GetValues<LibraryWideRefresh>(), gate.Due());
    }

    [Fact]
    public void FirstCheckRunsAtOnceThenDefersWhileMoreWorkIsQueued()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new LibraryWideRefreshGate(() => now, Interval);

        Assert.True(gate.ShouldRunNow(LibraryWideRefresh.NearDuplicate, moreQueued: true));
        gate.Started(LibraryWideRefresh.NearDuplicate);
        for (var file = 0; file < 1000; file++)
            Assert.False(gate.ShouldRunNow(LibraryWideRefresh.NearDuplicate, moreQueued: true));

        Assert.Contains(LibraryWideRefresh.NearDuplicate, gate.Due());
    }

    [Fact]
    public void TheLastFileOfABatchRunsTheCheck()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new LibraryWideRefreshGate(() => now, Interval);
        gate.Started(LibraryWideRefresh.ContentSimilarity);

        Assert.False(gate.ShouldRunNow(LibraryWideRefresh.ContentSimilarity, moreQueued: true));
        Assert.True(gate.ShouldRunNow(LibraryWideRefresh.ContentSimilarity, moreQueued: false));
    }

    [Fact]
    public void LongRunsStillRefreshEveryInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new LibraryWideRefreshGate(() => now, Interval);
        gate.Started(LibraryWideRefresh.ExactGroups);

        now += Interval - TimeSpan.FromSeconds(1);
        Assert.False(gate.ShouldRunNow(LibraryWideRefresh.ExactGroups, moreQueued: true));
        now += TimeSpan.FromSeconds(2);
        Assert.True(gate.ShouldRunNow(LibraryWideRefresh.ExactGroups, moreQueued: true));
    }

    [Fact]
    public void AFailedCheckStaysDueForThePassEnd()
    {
        var gate = new LibraryWideRefreshGate(() => DateTimeOffset.UtcNow, Interval);
        gate.Started(LibraryWideRefresh.ExactGroups);
        Assert.DoesNotContain(LibraryWideRefresh.ExactGroups, gate.Due());

        gate.Failed(LibraryWideRefresh.ExactGroups);

        Assert.Contains(LibraryWideRefresh.ExactGroups, gate.Due());
    }

    [Fact]
    public void KindsAreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new LibraryWideRefreshGate(() => now, Interval);
        gate.Started(LibraryWideRefresh.NearDuplicate);

        Assert.True(gate.ShouldRunNow(LibraryWideRefresh.ContentSimilarity, moreQueued: true));
    }
}
