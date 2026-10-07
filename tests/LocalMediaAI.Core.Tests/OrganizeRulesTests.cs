using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class OrganizeRulesTests
{
    private static readonly Guid RootA = Guid.NewGuid(), RootB = Guid.NewGuid(), RootC = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, int> Ranks = new Dictionary<Guid, int> { [RootA] = 1, [RootB] = 2, [RootC] = 3 };

    private static KeepCandidate File(Guid root, int day, long size = 100) =>
        new(Guid.NewGuid(), root, new DateTimeOffset(2024, 1, day, 0, 0, 0, TimeSpan.Zero), size);

    [Fact]
    public void FolderPriorityWinsBeforeDate()
    {
        var older = File(RootB, 1);
        var preferred = File(RootA, 20);
        var keep = KeepRule.Default.Choose([older, preferred], Ranks);
        Assert.Equal(preferred.MediaFileId, keep);
    }

    [Fact]
    public void SameFolderFallsBackToOldest()
    {
        var newer = File(RootA, 20);
        var oldest = File(RootA, 2);
        Assert.Equal(oldest.MediaFileId, KeepRule.Default.Choose([newer, oldest], Ranks));
    }

    [Fact]
    public void DateFirstIgnoresFolderPriority()
    {
        var rule = new KeepRule([KeepCriterion.Date, KeepCriterion.FolderPriority], KeepDateDirection.Oldest);
        var olderInLowRank = File(RootC, 1);
        var newerInTopRank = File(RootA, 9);
        Assert.Equal(olderInLowRank.MediaFileId, rule.Choose([newerInTopRank, olderInLowRank], Ranks));
    }

    [Fact]
    public void NewestDirectionPicksNewest()
    {
        var rule = new KeepRule([KeepCriterion.FolderPriority, KeepCriterion.Date], KeepDateDirection.Newest);
        var old = File(RootA, 1);
        var recent = File(RootA, 9);
        Assert.Equal(recent.MediaFileId, rule.Choose([old, recent], Ranks));
    }

    [Fact]
    public void UnknownRootRanksLast()
    {
        var unknown = File(Guid.NewGuid(), 1);
        var known = File(RootC, 9);
        Assert.Equal(known.MediaFileId, KeepRule.Default.Choose([unknown, known], Ranks));
    }

    [Fact]
    public void RuleRequiresBothCriteriaExactlyOnce()
    {
        Assert.Throws<ArgumentException>(() => new KeepRule([KeepCriterion.Date, KeepCriterion.Date], KeepDateDirection.Oldest));
        Assert.Throws<ArgumentException>(() => KeepRule.Default.Choose([], Ranks));
    }

    [Fact]
    public void CleanupSummaryCountsFilesPerFolderAndSameFolderShare()
    {
        var keepA = File(RootA, 1, 10);
        var dupSameA = File(RootA, 2, 10);
        var dupB = File(RootB, 3, 20);
        var keepB = File(RootB, 1, 5);
        var dupC = File(RootC, 4, 5);
        var plans = new[]
        {
            new GroupCleanupPlan(keepA, [dupSameA, dupB]),
            new GroupCleanupPlan(keepB, [dupC])
        };
        var summary = CleanupSummary.Create(plans);

        Assert.Equal(2, summary.GroupCount);
        Assert.Equal(3, summary.FileCount);
        Assert.Equal(35, summary.TotalBytes);
        Assert.Equal(1, summary.For(RootA).FileCount);
        Assert.Equal(1, summary.For(RootA).SameFolderCount);
        Assert.Equal(1, summary.For(RootB).FileCount);
        Assert.Equal(0, summary.For(RootB).SameFolderCount);
        Assert.Equal(1, summary.For(RootC).FileCount);
        Assert.Equal(0, summary.For(Guid.NewGuid()).FileCount);
    }

    [Fact]
    public void QueueEnforcesSeparateLimitsPerKind()
    {
        var queue = new CleanupList(exactLimit: 3, otherLimit: 1);
        Assert.True(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Exact, [Guid.NewGuid(), Guid.NewGuid()]));
        Assert.False(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Exact, [Guid.NewGuid(), Guid.NewGuid()]));
        Assert.True(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Exact, [Guid.NewGuid()]));
        Assert.True(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Similar, [Guid.NewGuid()]));
        Assert.False(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Single, [Guid.NewGuid()]));
        Assert.Equal(3, queue.Count(CleanupKind.Exact));
        Assert.Equal(1, queue.OtherCount);
    }

    [Fact]
    public void QueueRejectsDuplicateFilesAndEmptyGroups()
    {
        var queue = new CleanupList();
        var file = Guid.NewGuid();
        var group = Guid.NewGuid();
        Assert.True(queue.TryAddGroup(group, CleanupKind.Exact, [file]));
        Assert.False(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Exact, [file]));
        Assert.False(queue.TryAddGroup(Guid.NewGuid(), CleanupKind.Exact, []));
        Assert.True(queue.Contains(file));
        Assert.True(queue.RemoveGroup(group));
        Assert.False(queue.Contains(file));
        Assert.Equal(100, CleanupList.DefaultExactLimit);
        Assert.Equal(25, CleanupList.DefaultOtherLimit);
    }
}
