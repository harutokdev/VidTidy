using LocalMediaAI.App.Organize;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class OrganizeAnalysisTests
{
    private static readonly OrganizeFolder Online = new(Guid.NewGuid(), "A", @"C:\A", IsOnline: true);
    private static readonly OrganizeFolder Offline = new(Guid.NewGuid(), "B", @"F:\B", IsOnline: false);
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    private static OrganizeFile F(OrganizeFolder folder, string name) =>
        new(Guid.NewGuid(), folder.Id, name, System.IO.Path.Combine(folder.Path, name), 100, T0, null, null, null, null, null);

    private sealed class FakeSource(OrganizeSettings settings, params OrganizeFolder[] folders) : IOrganizeDataSource
    {
        public int Loads { get; private set; }
        public List<OrganizeGroupData> Groups { get; } = [];
        public Task<OrganizeSnapshot> LoadAsync(CancellationToken cancellationToken) { Loads++; return Task.FromResult(new OrganizeSnapshot(folders.Length > 0 ? folders : [Online, Offline], Groups.ToArray())); }
        public Task<OrganizeSettings> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
        public Task SaveSettingsAsync(OrganizeSettings value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordNotDuplicateAsync(Guid groupId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResetJudgmentAsync(Guid groupId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBackground : IOrganizeBackground
    {
        public List<Guid> Checked { get; } = [];
        public int Remaining { get; set; }
        public Task RequestChangeCheckAsync(IReadOnlyList<Guid> folderIds, CancellationToken cancellationToken) { Checked.AddRange(folderIds); return Task.CompletedTask; }
        public Task<int> GetRemainingWorkAsync(CancellationToken cancellationToken) => Task.FromResult(Remaining);
        public IReadOnlyList<DriveSpace> GetDriveSpace(IEnumerable<string> folderPaths) => [new DriveSpace(@"C:\", 1_000, 10_000, 500)];
        public AnalysisProgress? Progress { get; set; }
        public Task<AnalysisProgress> GetProgressAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Progress ?? IOrganizeBackground.FromCount(Remaining));
    }

    private static ActiveAnalysisJob Job(OrganizeFolder folder, LocalMediaAI.Core.JobType type, string? file = null) =>
        new(folder.Id, type, LocalMediaAI.Core.JobState.Pending, 0, 0, file, file is null ? null : 2_000_000_000);

    [Fact]
    public async Task SecondCheckWhileAnalyzingIsNotStarted()
    {
        var background = new FakeBackground { Progress = new AnalysisProgress([Job(Online, LocalMediaAI.Core.JobType.Scan)], []) };
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults)) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        Assert.True(await vm.CheckChangesAsync(CancellationToken.None, automatic: true));
        var eta = vm.EtaText;

        Assert.False(await vm.CheckChangesAsync(CancellationToken.None));
        Assert.False(await vm.RecheckFolderAsync(Online.Id, CancellationToken.None));

        Assert.Equal([Online.Id], background.Checked);
        Assert.True(vm.IsAutoCheck);
        Assert.Equal(eta, vm.EtaText);
    }

    [Fact]
    public async Task ChangeCheckCountsDownTheScanThenTheAnalysis()
    {
        var folder = new OrganizeFolder(Guid.NewGuid(), "C", @"C:\C", IsOnline: true, FileCount: 400);
        var start = DateTimeOffset.UtcNow;
        ActiveAnalysisJob ScanJob(int seen) => new(folder.Id, LocalMediaAI.Core.JobType.Scan, LocalMediaAI.Core.JobState.Running, seen, 0, null, null);
        var scan = new LatestScan(folder.Id, "Running", start, null, 0, 0, 0, 0);
        var background = new FakeBackground { Progress = new AnalysisProgress([ScanJob(0)], [scan]) };
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults, folder)) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        Assert.True(await vm.CheckChangesAsync(CancellationToken.None));
        Assert.True(vm.IsScanning);
        Assert.Null(vm.TimeLeft(start));
        Assert.NotNull(vm.AnalysisStartedAt);

        // 100 of about 400 files in 10 s: a quarter done, 30 s left, counting down between polls.
        background.Progress = new AnalysisProgress([ScanJob(100)], [scan]);
        await vm.PollAnalysisAsync(start.AddSeconds(10), CancellationToken.None);
        Assert.Equal(25, vm.ProgressPercent);
        Assert.Equal(TimeSpan.FromSeconds(30), vm.TimeLeft(start.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(25), vm.TimeLeft(start.AddSeconds(15)));
        Assert.Equal(25, vm.FolderProgress.Single().ScanPercent);

        // The scan found new files: the analysis step starts over with its own percent and estimate.
        var done = scan with { State = "Completed", CompletedAt = start.AddSeconds(40), Seen = 410, Added = 10 };
        background.Progress = new AnalysisProgress([Job(folder, LocalMediaAI.Core.JobType.Metadata, "new.mp4")], [done]);
        await vm.PollAnalysisAsync(start.AddSeconds(41), CancellationToken.None);
        Assert.False(vm.IsScanning);
        Assert.Equal(0, vm.ProgressPercent);
        Assert.Null(vm.TimeLeft(start.AddSeconds(41)));

        background.Progress = new AnalysisProgress([], [done]);
        await vm.PollAnalysisAsync(start.AddSeconds(60), CancellationToken.None);
        Assert.Null(vm.AnalysisStartedAt);
        Assert.Null(vm.TimeLeft(start.AddSeconds(60)));
    }

    [Fact]
    public async Task FolderProgressFollowsRemainingJobsPerFolder()
    {
        var background = new FakeBackground();
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults)) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        background.Progress = new AnalysisProgress([Job(Online, LocalMediaAI.Core.JobType.FullHash), Job(Online, LocalMediaAI.Core.JobType.FullHash), Job(Online, LocalMediaAI.Core.JobType.Metadata), Job(Online, LocalMediaAI.Core.JobType.FullHash)], []);
        await vm.PollAnalysisAsync(T0, CancellationToken.None);
        var row = Assert.Single(vm.FolderProgress, f => f.Folder.Id == Online.Id);
        Assert.Equal(1, row.Stage);
        Assert.Equal(0, row.Percent);

        background.Progress = new AnalysisProgress([Job(Online, LocalMediaAI.Core.JobType.FullHash, "big.mp4")], []);
        await vm.PollAnalysisAsync(T0.AddSeconds(10), CancellationToken.None);
        row = Assert.Single(vm.FolderProgress, f => f.Folder.Id == Online.Id);
        Assert.Equal(2, row.Stage);
        Assert.Equal(75, row.Percent);
        Assert.Equal(75, vm.OverallPercent);
        Assert.Contains("big.mp4", vm.CurrentWorkText);
        Assert.Equal("解析中 75%", vm.Folders.Single(f => f.Id == Online.Id).AnalysisText?[..7]);
    }

    [Fact]
    public async Task ChangeCheckReportsScanResultsAndNewGroups()
    {
        var source = new FakeSource(OrganizeSettings.Defaults);
        var background = new FakeBackground();
        var vm = new OrganizeViewModel(source) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        background.Progress = new AnalysisProgress([Job(Online, LocalMediaAI.Core.JobType.Scan)], []);
        await vm.CheckChangesAsync(CancellationToken.None);
        Assert.True(vm.IsChangeCheck);

        source.Groups.Add(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [F(Online, "a.mp4"), F(Online, "b.mp4")]));
        background.Progress = new AnalysisProgress([], [new LatestScan(Online.Id, "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 12, 2, 1, 0)]);
        await vm.PollAnalysisAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(vm.IsChangeCheck);
        Assert.Equal(1, vm.NewGroupsFound);
        Assert.Equal(NextActionKind.ReviewNew, vm.NextAction.Kind);
        Assert.Contains("1組", vm.NextAction.Title);
    }

    [Fact]
    public async Task StartupChecksOnlyConnectedFoldersWhenEnabled()
    {
        var background = new FakeBackground();
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults)) { Background = background };
        await vm.StartAsync(CancellationToken.None);
        Assert.Equal([Online.Id], background.Checked);
    }

    [Fact]
    public async Task ManualModeDoesNotCheckOnStartup()
    {
        var background = new FakeBackground();
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults with { CheckChangesOnStartup = false })) { Background = background };
        await vm.StartAsync(CancellationToken.None);
        Assert.Empty(background.Checked);
        await vm.CheckChangesAsync(CancellationToken.None);
        Assert.Equal([Online.Id], background.Checked);
    }

    [Fact]
    public async Task FinishingAnalysisReloadsWhenCleanupListIsEmpty()
    {
        var source = new FakeSource(OrganizeSettings.Defaults);
        var background = new FakeBackground { Remaining = 10 };
        var vm = new OrganizeViewModel(source) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        await vm.PollAnalysisAsync(T0, CancellationToken.None);
        Assert.True(vm.IsAnalyzing);
        Assert.StartsWith("解析中：", vm.AnalysisText);

        background.Remaining = 5;
        await vm.PollAnalysisAsync(T0.AddSeconds(30), CancellationToken.None);
        Assert.Contains("残り 1分未満", vm.AnalysisText);

        source.Groups.Add(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [F(Online, "a.mp4"), F(Online, "b.mp4")]));
        background.Remaining = 0;
        await vm.PollAnalysisAsync(T0.AddSeconds(60), CancellationToken.None);
        Assert.False(vm.IsAnalyzing);
        Assert.Equal(2, source.Loads);
        Assert.Single(vm.ExactGroups);
    }

    [Fact]
    public async Task FinishingAnalysisKeepsTheListWhileCleanupIsQueued()
    {
        var source = new FakeSource(OrganizeSettings.Defaults);
        source.Groups.Add(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [F(Online, "a.mp4"), F(Online, "b.mp4")]));
        var background = new FakeBackground { Remaining = 3 };
        var vm = new OrganizeViewModel(source) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        vm.ApproveExact();
        await vm.PollAnalysisAsync(T0, CancellationToken.None);
        background.Remaining = 0;
        await vm.PollAnalysisAsync(T0.AddSeconds(5), CancellationToken.None);

        Assert.Equal(1, source.Loads);
        Assert.True(vm.HasNewResults);
        Assert.Equal(1, vm.QueueFileCount);
    }

    [Fact]
    public async Task HeldResultsAppearOnTheNextPollOnceTheCleanupListIsDone()
    {
        var (source, vm, restored) = await HeldResultsAsync();

        vm.ApplyCleanupResults(vm.CreateCleanupRequests().Select(r => r.Target.MediaFileId).ToArray());
        await vm.PollAnalysisAsync(T0.AddSeconds(10), CancellationToken.None);

        Assert.False(vm.HasNewResults);
        Assert.Equal(2, source.Loads);
        Assert.Contains(vm.ExactGroups, g => g.GroupId == restored.GroupId);
        Assert.Equal(1, vm.NewGroupsFound);
    }

    [Fact]
    public async Task HeldResultsAppearOnceTheCleanupListIsEmptied()
    {
        var (source, vm, _) = await HeldResultsAsync();

        await vm.PollAnalysisAsync(T0.AddSeconds(10), CancellationToken.None);
        Assert.Equal(1, source.Loads); // still held while the list has files

        vm.ClearQueue();
        await vm.PollAnalysisAsync(T0.AddSeconds(12), CancellationToken.None);
        Assert.Equal(2, source.Loads);
        Assert.False(vm.HasNewResults);
    }

    [Fact]
    public async Task ShowingHeldResultsNowEmptiesTheCleanupList()
    {
        var (source, vm, restored) = await HeldResultsAsync();

        await vm.ShowNewResultsNowAsync(CancellationToken.None);

        Assert.Equal(0, vm.QueueFileCount);
        Assert.False(vm.HasNewResults);
        Assert.Equal(2, vm.ExactGroups.Count);
        Assert.Contains(vm.ExactGroups, g => g.GroupId == restored.GroupId);
    }

    /// <summary>A change check ends while one group is in the cleanup list; a restored group is then waiting to be shown.</summary>
    private static async Task<(FakeSource Source, OrganizeViewModel Vm, OrganizeGroupData Restored)> HeldResultsAsync()
    {
        var source = new FakeSource(OrganizeSettings.Defaults);
        source.Groups.Add(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [F(Online, "a.mp4"), F(Online, "b.mp4")]));
        var background = new FakeBackground();
        var vm = new OrganizeViewModel(source) { Background = background };
        await vm.LoadAsync(CancellationToken.None);
        vm.ApproveExact();
        background.Progress = new AnalysisProgress([Job(Online, LocalMediaAI.Core.JobType.Scan)], []);
        await vm.CheckChangesAsync(CancellationToken.None);
        var restored = new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [F(Online, "c.mp4"), F(Online, "c-copy.mp4")]);
        source.Groups.Add(restored);
        background.Progress = new AnalysisProgress([], []);
        await vm.PollAnalysisAsync(T0.AddSeconds(5), CancellationToken.None);
        Assert.True(vm.HasNewResults);
        Assert.Equal(1, source.Loads);
        return (source, vm, restored);
    }

    [Fact]
    public async Task DriveSpaceShowsRecycleBinDoubleDisplay()
    {
        var vm = new OrganizeViewModel(new FakeSource(OrganizeSettings.Defaults)) { Background = new FakeBackground() };
        await vm.LoadAsync(CancellationToken.None);
        var drive = Assert.Single(vm.Drives);
        Assert.Contains("空き", drive.FreeText);
        Assert.Contains("空にすると", drive.RecycleText);
        Assert.Null(new DriveSpace(@"D:\", 1, 2, 0).RecycleText);
    }
}
