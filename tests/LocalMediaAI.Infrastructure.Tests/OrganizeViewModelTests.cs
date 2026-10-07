using LocalMediaAI.App.Organize;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class OrganizeViewModelTests
{
    private static readonly OrganizeFolder Sorted = new(Guid.NewGuid(), "整理済み", @"D:\動画\整理済み", IsOnline: true);
    private static readonly OrganizeFolder Downloads = new(Guid.NewGuid(), "ダウンロード", @"E:\ダウンロード", IsOnline: true);
    private static readonly OrganizeFolder Backup = new(Guid.NewGuid(), "バックアップ", @"F:\バックアップ", IsOnline: true);

    private static OrganizeFile F(OrganizeFolder folder, string name, int day, long size = 1_000, int width = 1920, long bitrate = 8_000_000, double duration = 60) =>
        new(Guid.NewGuid(), folder.Id, name, $@"{folder.Path}\{name}", size, new DateTimeOffset(2024, 1, day, 0, 0, 0, TimeSpan.Zero),
            width, width * 9 / 16, bitrate, duration, "h264");

    private static OrganizeGroupData Exact(params OrganizeFile[] files) => new(Guid.NewGuid(), GroupCategory.Exact, null, files);
    private static OrganizeGroupData Similar(double match, params OrganizeFile[] files) => new(Guid.NewGuid(), match >= 0.85 ? GroupCategory.Similar : GroupCategory.NeedsCheck, match, files);

    private static async Task<(OrganizeViewModel Vm, FakeOrganizeDataSource Source)> LoadAsync(OrganizeSettings? settings, params OrganizeGroupData[] groups)
    {
        var source = new FakeOrganizeDataSource(new OrganizeSnapshot([Sorted, Downloads, Backup], groups), settings ?? OrganizeSettings.Defaults);
        var vm = new OrganizeViewModel(source);
        await vm.LoadAsync(CancellationToken.None);
        return (vm, source);
    }

    [Fact]
    public async Task GroupsAreSplitIntoExactSimilarAndNeedsCheck()
    {
        var (vm, _) = await LoadAsync(null,
            Exact(F(Sorted, "a.mp4", 1), F(Downloads, "a (1).mp4", 2)),
            Similar(0.95, F(Sorted, "b.mp4", 1), F(Backup, "b_edit.mp4", 2)),
            Similar(0.70, F(Downloads, "c.mp4", 1), F(Backup, "c2.mp4", 2)));

        Assert.Single(vm.ExactGroups);
        Assert.Single(vm.SimilarGroups);
        Assert.Single(vm.NeedsCheckGroups);
        Assert.Equal(3, vm.OpenGroupCount);
        Assert.Equal(3, vm.Folders.Count);
        Assert.Equal([1, 2, 3], vm.Folders.Select(f => f.Rank));
    }

    [Fact]
    public async Task ExactKeepFollowsFolderPriorityThenOldest()
    {
        var newerInSorted = F(Sorted, "a.mp4", 20);
        var olderInDownloads = F(Downloads, "a (1).mp4", 1);
        var sameA = F(Downloads, "x.mp4", 9);
        var sameB = F(Downloads, "x (1).mp4", 3);
        var (vm, _) = await LoadAsync(null, Exact(olderInDownloads, newerInSorted), Exact(sameA, sameB));

        Assert.Equal(newerInSorted.MediaFileId, vm.ExactGroups[0].KeepId);
        Assert.Equal(sameB.MediaFileId, vm.ExactGroups[1].KeepId);
        Assert.Equal(1_000, vm.ExactGroups[0].CleanupBytes);
    }

    [Fact]
    public async Task SavedFolderOrderIsUsedForRanks()
    {
        var settings = OrganizeSettings.Defaults with { FolderOrder = [Downloads.Id, Sorted.Id] };
        var inSorted = F(Sorted, "a.mp4", 1);
        var inDownloads = F(Downloads, "a (1).mp4", 9);
        var (vm, _) = await LoadAsync(settings, Exact(inSorted, inDownloads));

        Assert.Equal(inDownloads.MediaFileId, vm.ExactGroups[0].KeepId);
        Assert.Equal(["ダウンロード", "整理済み", "バックアップ"], vm.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task SimilarKeepPrefersResolutionThenBitrateThenDuration()
    {
        var sharp = F(Downloads, "hd.mp4", 5, width: 3840, bitrate: 4_000_000);
        var dense = F(Sorted, "sd.mp4", 1, width: 1280, bitrate: 20_000_000);
        var (vm, _) = await LoadAsync(null, Similar(0.9, dense, sharp));
        Assert.Equal(sharp.MediaFileId, vm.SimilarGroups[0].KeepId);
    }

    [Fact]
    public async Task ManualChoiceSurvivesRuleChanges()
    {
        var a = F(Sorted, "a.mp4", 1);
        var b = F(Downloads, "a (1).mp4", 2);
        var c = F(Sorted, "c.mp4", 1);
        var d = F(Downloads, "c (1).mp4", 2);
        var (vm, source) = await LoadAsync(null, Exact(a, b), Exact(c, d));

        vm.ChooseKeep(vm.ExactGroups[0], b.MediaFileId);
        Assert.True(vm.ExactGroups[0].IsManual);

        await vm.MoveFolderAsync(Downloads.Id, 0, CancellationToken.None);

        Assert.Equal(b.MediaFileId, vm.ExactGroups[0].KeepId);
        Assert.Equal(d.MediaFileId, vm.ExactGroups[1].KeepId);
        Assert.Equal([Downloads.Id, Sorted.Id, Backup.Id], source.Settings.FolderOrder);

        vm.ResetToRule(vm.ExactGroups[0]);
        Assert.False(vm.ExactGroups[0].IsManual);
        Assert.Equal(b.MediaFileId, vm.ExactGroups[0].KeepId);
    }

    [Fact]
    public async Task ChoosingTheRuleResultIsNotManual()
    {
        var a = F(Sorted, "a.mp4", 1);
        var b = F(Downloads, "a (1).mp4", 2);
        var (vm, _) = await LoadAsync(null, Exact(a, b));
        vm.ChooseKeep(vm.ExactGroups[0], a.MediaFileId);
        Assert.False(vm.ExactGroups[0].IsManual);
    }

    [Fact]
    public async Task NextActionWalksTheFlow()
    {
        var (empty, _) = await LoadAsync(null);
        Assert.Equal(NextActionKind.AllClear, empty.NextAction.Kind);

        var (vm, _) = await LoadAsync(null,
            Exact(F(Sorted, "a.mp4", 1), F(Downloads, "a (1).mp4", 2)),
            Similar(0.9, F(Sorted, "b.mp4", 1), F(Backup, "b2.mp4", 2)));
        Assert.Equal(NextActionKind.ApproveExact, vm.NextAction.Kind);
        Assert.Contains("1組", vm.NextAction.Title);

        vm.ApproveExact();
        Assert.Equal(NextActionKind.RunQueue, vm.NextAction.Kind);
        Assert.True(vm.IsRunOnly);
        Assert.Equal(new StepStates(StepState.Done, StepState.Current, StepState.Later), vm.Steps);

        vm.ApplyCleanupResults(vm.CreateCleanupRequests().Select(r => r.Target.MediaFileId).ToArray());
        Assert.Equal(NextActionKind.ReviewSimilar, vm.NextAction.Kind);
        Assert.Equal(new StepStates(StepState.Done, StepState.Done, StepState.Current), vm.Steps);

        Assert.True(vm.Enqueue(vm.SimilarGroups[0]));
        Assert.Equal(NextActionKind.RunQueue, vm.NextAction.Kind);
        Assert.True(vm.IsRunOnly);
        Assert.Equal(1, vm.QueueFileCount);
    }

    [Fact]
    public async Task WhileTheRunWaitsOnlyTheCleanupListIsListed()
    {
        var (vm, _) = await LoadAsync(null,
            Exact(F(Sorted, "a.mp4", 1), F(Downloads, "a (1).mp4", 2)),
            Similar(0.9, F(Sorted, "b.mp4", 1), F(Backup, "b2.mp4", 2)));
        vm.ApproveExact();

        Assert.Equal([vm.ExactGroups[0]], vm.ListedGroups);
        Assert.Same(vm.ExactGroups[0], vm.Selected);
        Assert.Equal(1, vm.LaterSimilarCount);

        vm.PeekLater = true;
        Assert.Equal([vm.ExactGroups[0], vm.SimilarGroups[0]], vm.ListedGroups);
    }

    [Fact]
    public async Task ListIsLargestFirstUnlessFoundOrderIsChosen()
    {
        var small = Exact(F(Sorted, "s.mp4", 1, size: 100), F(Downloads, "s.mp4", 2, size: 100));
        var large = Exact(F(Sorted, "l.mp4", 1, size: 9_000), F(Downloads, "l.mp4", 2, size: 9_000));
        var (vm, source) = await LoadAsync(null, small, large);

        Assert.Equal([large.GroupId, small.GroupId], vm.ExactList.Select(g => g.GroupId));
        Assert.Same(vm.ExactList[0], vm.Selected);

        await vm.SetSortAsync(ListSort.Found, CancellationToken.None);
        Assert.Equal([small.GroupId, large.GroupId], vm.ExactList.Select(g => g.GroupId));
        Assert.True(source.Settings.ListInFoundOrder);
    }

    [Fact]
    public async Task GoalPutsGroupsFreeingThatDriveFirstAndMarksWhereItIsReached()
    {
        // Keeping the file in 整理済み (D:), each group frees space on E: or F: only.
        var onE = Exact(F(Sorted, "e.mp4", 1, size: 3_000), F(Downloads, "e.mp4", 2, size: 3_000));
        var onF = Exact(F(Sorted, "f.mp4", 1, size: 8_000), F(Backup, "f.mp4", 2, size: 8_000));
        var onE2 = Exact(F(Sorted, "g.mp4", 1, size: 2_000), F(Downloads, "g.mp4", 2, size: 2_000));
        var (vm, _) = await LoadAsync(null, onE, onF, onE2);

        vm.Goal = new CleanupGoal("E:", 4_000);
        Assert.Equal([onE.GroupId, onE2.GroupId, onF.GroupId], vm.ExactList.Select(g => g.GroupId));
        Assert.Equal(0, vm.ExactList[2].ListBytes);
        Assert.Equal([false, true, false], vm.ExactList.Select(g => g.IsGoalLine));
        Assert.Equal(5_000, vm.GoalReachableBytes);

        vm.Goal = new CleanupGoal("E:", 99_000);
        Assert.All(vm.ExactList, g => Assert.False(g.IsGoalLine));
    }

    [Fact]
    public async Task RulePreviewCountsWhatEachFolderGivesUpWithoutChangingAnything()
    {
        var a = Exact(F(Sorted, "a.mp4", 1, size: 10), F(Downloads, "a.mp4", 2, size: 10));
        var b = Exact(F(Sorted, "b.mp4", 1, size: 20), F(Downloads, "b.mp4", 2, size: 20));
        var (vm, source) = await LoadAsync(null, a, b);
        vm.ChooseKeep(vm.ExactGroups.Single(g => g.GroupId == b.GroupId), b.Files[1].MediaFileId);

        var preview = vm.PreviewRule(vm.Settings with { FolderOrder = [Downloads.Id, Sorted.Id, Backup.Id] });

        Assert.Equal(["ダウンロード", "整理済み", "バックアップ"], preview.Select(r => r.Folder.Name));
        var downloads = preview[0];
        var sorted = preview[1];
        Assert.Equal((1, 0), (downloads.Now, downloads.Next));   // a moves; b was chosen by hand
        Assert.Equal((1, 2), (sorted.Now, sorted.Next));
        Assert.Equal(30, sorted.NextBytes);
        Assert.Equal(OrganizeSettings.Defaults.FolderOrder, source.Settings.FolderOrder);
    }

    [Fact]
    public async Task ClearSimilarGroupsAreApprovedTogetherAndNeedNoCheckOneByOne()
    {
        var clear = Similar(0.97, F(Sorted, "c.mp4", 1, width: 1920), F(Downloads, "c_small.mp4", 2, width: 1280, bitrate: 3_000_000));
        var longer = Similar(0.97, F(Sorted, "l.mp4", 1, duration: 60), F(Downloads, "l_cut.mp4", 2, width: 1280, duration: 50));
        var (vm, _) = await LoadAsync(null, clear, longer);

        Assert.Equal(NextActionKind.ApproveClear, vm.NextAction.Kind);
        Assert.Equal([clear.GroupId], vm.ClearGroups.Select(g => g.GroupId));
        Assert.Equal(1, vm.ApproveClear());

        var request = Assert.Single(vm.CreateCleanupRequests());
        Assert.True(request.Approved);
        Assert.False(new CleanupWizardRow(request, () => { }).NeedsManualCheck);
        Assert.Equal(NextActionKind.ReviewSimilar, vm.NextAction.Kind);

        vm.Dequeue(vm.SimilarGroups.Single(g => g.GroupId == clear.GroupId));
        Assert.False(vm.SimilarGroups.Single(g => g.GroupId == clear.GroupId).IsBulkApproved);
    }

    [Fact]
    public async Task ApproveExactRespectsLimitAndSkipsOfflineGroups()
    {
        var offlineBackup = Backup with { IsOnline = false };
        var groups = Enumerable.Range(1, 5).Select(i => Exact(F(Sorted, $"s{i}.mp4", 1), F(Downloads, $"s{i} (1).mp4", 2))).ToList();
        groups.Add(Exact(F(Sorted, "off.mp4", 1), F(offlineBackup, "off (1).mp4", 2)));
        var source = new FakeOrganizeDataSource(new OrganizeSnapshot([Sorted, Downloads, offlineBackup], groups), OrganizeSettings.Defaults);
        var vm = new OrganizeViewModel(source, new CleanupList(exactLimit: 3, otherLimit: 25));
        await vm.LoadAsync(CancellationToken.None);

        var plan = vm.PrepareExactApproval();
        Assert.Equal(3, plan.Groups.Count);
        Assert.Equal(2, plan.LeftForNextRun);
        Assert.Equal(1, plan.WaitingForDrive);
        Assert.Equal(3, plan.Summary.For(Downloads.Id).FileCount);
        Assert.Equal(0, plan.Summary.For(Sorted.Id).FileCount);

        var result = vm.ApproveExact();
        Assert.Equal(3, result.Added);
        Assert.Equal(3, vm.QueueFileCount);
        Assert.False(vm.ExactGroups.Single(g => g.Files.Any(f => f.RootId == offlineBackup.Id)).IsReady);
    }

    [Fact]
    public async Task ApprovalSummaryReportsSameFolderDuplicates()
    {
        var (vm, _) = await LoadAsync(null, Exact(F(Sorted, "a.mp4", 1), F(Sorted, "a - copy.mp4", 2), F(Downloads, "a (1).mp4", 3)));
        var plan = vm.PrepareExactApproval();
        Assert.Equal(1, plan.Summary.For(Sorted.Id).FileCount);
        Assert.Equal(1, plan.Summary.For(Sorted.Id).SameFolderCount);
        Assert.Equal(1, plan.Summary.For(Downloads.Id).FileCount);
        Assert.Equal(2, plan.Summary.FileCount);
    }

    [Fact]
    public async Task SimilarQueueLimitIsEnforced()
    {
        var groups = Enumerable.Range(1, 3).Select(i => Similar(0.9, F(Sorted, $"v{i}.mp4", 1), F(Downloads, $"v{i}b.mp4", 2))).ToArray();
        var source = new FakeOrganizeDataSource(new OrganizeSnapshot([Sorted, Downloads, Backup], groups), OrganizeSettings.Defaults);
        var vm = new OrganizeViewModel(source, new CleanupList(exactLimit: 100, otherLimit: 2));
        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.Enqueue(vm.SimilarGroups[0]));
        Assert.True(vm.Enqueue(vm.SimilarGroups[1]));
        Assert.False(vm.Enqueue(vm.SimilarGroups[2]));
        Assert.Equal(GroupStatus.Queued, vm.SimilarGroups[0].Status);

        vm.Dequeue(vm.SimilarGroups[0]);
        Assert.Equal(GroupStatus.Open, vm.SimilarGroups[0].Status);
        Assert.Equal(1, vm.QueueFileCount);
    }

    [Fact]
    public async Task KeepAsIsIsRecordedForAnyGroupAndCanBeUndone()
    {
        var (vm, source) = await LoadAsync(null, Exact(F(Sorted, "a.mp4", 1), F(Downloads, "a.mp4", 2)), Similar(0.9, F(Sorted, "b.mp4", 1), F(Backup, "b2.mp4", 2)));
        var exact = vm.ExactGroups[0];
        Assert.Same(exact, vm.Selected);

        await vm.KeepAsIsAsync(exact, CancellationToken.None);
        Assert.Equal(GroupStatus.Judged, exact.Status);
        Assert.Same(vm.SimilarGroups[0], vm.Selected);
        Assert.Equal("このまま残す", vm.Judgments[0].Label);
        Assert.Contains(exact.GroupId, source.KeptAsIs);
        await vm.UndoJudgmentAsync(vm.Judgments[0], CancellationToken.None);

        var group = vm.SimilarGroups[0];
        await vm.KeepAsIsAsync(group, CancellationToken.None);
        Assert.Equal(GroupStatus.Judged, group.Status);
        Assert.Equal(1, vm.OpenGroupCount);
        Assert.Single(vm.Judgments);
        Assert.Contains(group.GroupId, source.KeptAsIs);

        await vm.UndoJudgmentAsync(vm.Judgments[0], CancellationToken.None);
        Assert.Equal(GroupStatus.Open, group.Status);
        Assert.Empty(vm.Judgments);
        Assert.Contains(group.GroupId, source.Reset);
    }

    [Fact]
    public async Task SelectionAdvancesToNextOpenGroupAfterEnqueue()
    {
        var (vm, _) = await LoadAsync(null,
            Similar(0.9, F(Sorted, "b.mp4", 1), F(Backup, "b2.mp4", 2)),
            Similar(0.9, F(Sorted, "c.mp4", 1), F(Backup, "c2.mp4", 2)));
        vm.Selected = vm.SimilarGroups[0];
        vm.Enqueue(vm.SimilarGroups[0]);
        Assert.Same(vm.SimilarGroups[1], vm.Selected);
    }

    [Fact]
    public async Task CompareRowsMarkTheBetterValueAndChoicesFollowKeep()
    {
        var sharp = F(Downloads, "hd.mp4", 5, width: 3840, bitrate: 4_000_000, duration: 60);
        var longer = F(Sorted, "sd.mp4", 1, width: 1280, bitrate: 9_000_000, duration: 95);
        var (vm, _) = await LoadAsync(null, Similar(0.9, longer, sharp));
        var group = vm.SimilarGroups[0];

        var resolution = group.CompareRows.Single(r => r.Label == "解像度");
        Assert.Equal([false, true], resolution.Cells.Select(c => c.IsBest));
        Assert.Equal([true, false], group.CompareRows.Single(r => r.Label == "ビットレート").Cells.Select(c => c.IsBest));
        Assert.Equal([true, false], group.CompareRows.Single(r => r.Label == "長さ").Cells.Select(c => c.IsBest));
        Assert.All(group.CompareRows.Single(r => r.Label == "サイズ").Cells, c => Assert.False(c.IsBest));

        Assert.Equal([false, true], group.Choices.Select(c => c.IsKeep));
        vm.ChooseKeep(group, longer.MediaFileId);
        Assert.Equal([true, false], group.Choices.Select(c => c.IsKeep));
        Assert.Contains(group.Warnings, w => w.Contains("長さ"));
        Assert.Contains(group.Warnings, w => w.Contains("解像度"));
    }

    [Fact]
    public async Task CleanupResultsRemoveDoneGroupsAndReturnFailedOnes()
    {
        var doneGroup = Exact(F(Sorted, "a.mp4", 1), F(Downloads, "a.mp4", 2));
        var failedGroup = Exact(F(Sorted, "b.mp4", 1), F(Downloads, "b.mp4", 2), F(Backup, "b.mp4", 3));
        var (vm, _) = await LoadAsync(null, doneGroup, failedGroup);
        vm.ApproveExact();
        var requests = vm.CreateCleanupRequests();
        Assert.Equal(3, requests.Count);

        var completed = requests.Where(r => r.GroupId == doneGroup.GroupId).Select(r => r.Target.MediaFileId)
            .Append(requests.First(r => r.GroupId == failedGroup.GroupId).Target.MediaFileId).ToArray();
        vm.ApplyCleanupResults(completed);

        Assert.Equal(0, vm.QueueFileCount);
        Assert.Single(vm.ExactGroups);
        Assert.Equal(failedGroup.GroupId, vm.ExactGroups[0].GroupId);
        Assert.True(vm.ExactGroups[0].IsOpen);
    }

    [Fact]
    public async Task GroupOfFindsTheGroupShowingAFile()
    {
        var a = F(Sorted, "a.mp4", 1);
        var b = F(Downloads, "a.mp4", 2);
        var (vm, _) = await LoadAsync(null, Exact(a, b));
        Assert.Same(vm.ExactGroups[0], vm.GroupOf(b.MediaFileId));
        Assert.Null(vm.GroupOf(Guid.NewGuid()));
    }

    [Fact]
    public async Task SingleFileCanBeQueuedButNeverAKeptOrQueuedFile()
    {
        var keep = F(Sorted, "a.mp4", 1);
        var dup = F(Downloads, "a.mp4", 2);
        var lone = F(Backup, "lone.mp4", 3);
        var (vm, _) = await LoadAsync(null, Exact(keep, dup));
        vm.ApproveExact();

        Assert.Equal(SingleQueueResult.Added, vm.EnqueueSingle(lone));
        Assert.Equal(SingleQueueResult.AlreadyQueued, vm.EnqueueSingle(lone));
        Assert.Equal(SingleQueueResult.AlreadyQueued, vm.EnqueueSingle(dup));
        Assert.Equal(SingleQueueResult.KeptInGroup, vm.EnqueueSingle(keep));
        Assert.Equal(1, vm.QueueOtherFileCount);
        Assert.Equal(dup.SizeBytes + lone.SizeBytes, vm.QueueBytes);

        var single = Assert.Single(vm.CreateCleanupRequests(), r => r.Kind == CleanupKind.Single);
        Assert.Null(single.Keep);
        Assert.Equal(lone, single.Target);

        vm.ApplyCleanupResults([lone.MediaFileId]);
        Assert.Equal(0, vm.QueueFileCount);
    }

    private sealed class FakeOrganizeDataSource(OrganizeSnapshot snapshot, OrganizeSettings settings) : IOrganizeDataSource
    {
        public OrganizeSettings Settings { get; private set; } = settings;
        public List<Guid> NotDuplicate { get; } = [];
        public List<Guid> KeptAsIs { get; } = [];
        public Task RecordKeepAsIsAsync(Guid groupId, CancellationToken cancellationToken) { KeptAsIs.Add(groupId); return Task.CompletedTask; }
        public List<Guid> Reset { get; } = [];
        public Task<OrganizeSnapshot> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task<OrganizeSettings> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
        public Task SaveSettingsAsync(OrganizeSettings value, CancellationToken cancellationToken) { Settings = value; return Task.CompletedTask; }
        public Task RecordNotDuplicateAsync(Guid groupId, CancellationToken cancellationToken) { NotDuplicate.Add(groupId); return Task.CompletedTask; }
        public Task ResetJudgmentAsync(Guid groupId, CancellationToken cancellationToken) { Reset.Add(groupId); return Task.CompletedTask; }
    }
}
