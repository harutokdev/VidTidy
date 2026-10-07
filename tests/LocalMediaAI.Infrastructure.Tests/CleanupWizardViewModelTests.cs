using LocalMediaAI.App.Organize;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class CleanupWizardViewModelTests
{
    private static readonly Guid Root = Guid.NewGuid();

    private static OrganizeFile File(string name, long size = 100) =>
        new(Guid.NewGuid(), Root, name, $"C:\\v\\{name}", size, DateTimeOffset.UnixEpoch, null, null, null, null, null);

    private static CleanupRequest Request(CleanupKind kind, string name, long size = 100) =>
        new(Guid.NewGuid(), kind, File("keep-" + name), File(name, size));

    private sealed class FakeExecutor : ICleanupExecutor
    {
        public HashSet<Guid> BlockOnCheck { get; } = [];
        public HashSet<Guid> FailOnRun { get; } = [];
        public List<Guid> Ran { get; } = [];

        public Task<IReadOnlyList<CleanupCheckResult>> CheckAsync(IReadOnlyList<CleanupRequest> requests, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CleanupCheckResult>>(requests
                .Select(r => BlockOnCheck.Contains(r.Target.MediaFileId) ? new CleanupCheckResult(r, false, "ファイルが変わっています") : new CleanupCheckResult(r, true, null))
                .ToArray());

        public Task<IReadOnlyList<CleanupRunResult>> RunAsync(IReadOnlyList<CleanupRequest> requests, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
        {
            var results = new List<CleanupRunResult>();
            foreach (var r in requests)
            {
                Ran.Add(r.Target.MediaFileId);
                results.Add(FailOnRun.Contains(r.Target.MediaFileId) ? new CleanupRunResult(r, false, "使用中です") : new CleanupRunResult(r, true, null));
                progress?.Report(new CleanupProgress(results.Count, requests.Count, r.Target.FileName));
            }
            return Task.FromResult<IReadOnlyList<CleanupRunResult>>(results);
        }
    }

    [Fact]
    public async Task ExactRowsNeedNoManualCheckButSimilarRowsDo()
    {
        var exact = Request(CleanupKind.Exact, "a.mp4");
        var similar = Request(CleanupKind.Similar, "b.mp4");
        var wizard = new CleanupWizardViewModel([exact, similar], new FakeExecutor());

        await wizard.CheckAsync(CancellationToken.None);

        Assert.Equal(CleanupWizardStep.Ready, wizard.Step);
        Assert.False(wizard.CanRun);
        Assert.True(wizard.Rows.Single(r => r.Request == exact).IsChecked);
        wizard.Rows.Single(r => r.Request == similar).IsChecked = true;
        Assert.True(wizard.CanRun);
    }

    [Fact]
    public async Task CannotRunBeforeCheck()
    {
        var wizard = new CleanupWizardViewModel([Request(CleanupKind.Exact, "a.mp4")], new FakeExecutor());
        Assert.Equal(CleanupWizardStep.Review, wizard.Step);
        Assert.False(wizard.CanRun);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wizard.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BlockedRowsAreShownAndNeverRun()
    {
        var ok = Request(CleanupKind.Exact, "a.mp4");
        var changed = Request(CleanupKind.Exact, "b.mp4");
        var executor = new FakeExecutor();
        executor.BlockOnCheck.Add(changed.Target.MediaFileId);
        var wizard = new CleanupWizardViewModel([ok, changed], executor);

        await wizard.CheckAsync(CancellationToken.None);
        Assert.Equal(1, wizard.ReadyCount);
        Assert.Equal(1, wizard.BlockedCount);
        Assert.Equal("ファイルが変わっています", wizard.Rows.Single(r => r.Request == changed).Reason);

        await wizard.RunAsync(CancellationToken.None);
        Assert.Equal([ok.Target.MediaFileId], executor.Ran);
    }

    [Fact]
    public async Task UncheckedSimilarRowsAreLeftOutOfTheRun()
    {
        var exact = Request(CleanupKind.Exact, "a.mp4");
        var similar = Request(CleanupKind.Similar, "b.mp4");
        var executor = new FakeExecutor();
        var wizard = new CleanupWizardViewModel([exact, similar], executor);
        await wizard.CheckAsync(CancellationToken.None);
        wizard.Rows.Single(r => r.Request == similar).IsChecked = true;
        wizard.Rows.Single(r => r.Request == similar).IsChecked = false;
        wizard.Rows.Single(r => r.Request == exact).IsChecked = true;
        Assert.False(wizard.CanRun);
        wizard.Rows.Single(r => r.Request == similar).IsIncluded = false;
        Assert.True(wizard.CanRun);

        await wizard.RunAsync(CancellationToken.None);
        Assert.Equal([exact.Target.MediaFileId], executor.Ran);
    }

    [Fact]
    public async Task FinishedSummaryReportsDoneAndFailed()
    {
        var a = Request(CleanupKind.Exact, "a.mp4", 300);
        var b = Request(CleanupKind.Exact, "b.mp4", 200);
        var executor = new FakeExecutor();
        executor.FailOnRun.Add(b.Target.MediaFileId);
        var wizard = new CleanupWizardViewModel([a, b], executor);
        await wizard.CheckAsync(CancellationToken.None);

        await wizard.RunAsync(CancellationToken.None);

        Assert.Equal(CleanupWizardStep.Finished, wizard.Step);
        Assert.Equal(1, wizard.DoneCount);
        Assert.Equal(300, wizard.DoneBytes);
        Assert.Equal(1, wizard.FailedCount);
        Assert.Equal("使用中です", wizard.Rows.Single(r => r.Request == b).Reason);
        Assert.Equal([a.Target.MediaFileId], wizard.CompletedFileIds);
    }

    [Fact]
    public void RequestsAreBuiltFromQueuedGroups()
    {
        var keep = File("keep.mp4");
        var dup1 = File("d1.mp4");
        var dup2 = File("d2.mp4");
        var row = new GroupRow(new OrganizeGroupData(Guid.NewGuid(), GroupCategory.Exact, null, [keep, dup1, dup2]), keep.MediaFileId);

        var requests = CleanupRequest.FromGroups([row]);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(keep, r.Keep));
        Assert.All(requests, r => Assert.Equal(CleanupKind.Exact, r.Kind));
    }
}
