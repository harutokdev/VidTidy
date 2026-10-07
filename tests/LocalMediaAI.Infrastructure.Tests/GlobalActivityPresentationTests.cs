using System.Text.Json;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class GlobalActivityPresentationTests
{
    [Fact]
    public void HistoricalFailuresNeverBecomeCurrentProblems()
    {
        var history = new[]
        {
            CreateJob(JobState.Failed, JobType.Metadata),
            CreateJob(JobState.Partial, JobType.VisualEmbedding)
        };

        var result = GlobalActivityPresentation.Create([], [], history, Worker(WorkerAvailability.Available), new(265, 265, 0));

        Assert.Empty(result.CurrentConditions);
        Assert.Equal(0, result.ActionRequiredCount);
        Assert.Contains("要対応 0", result.CompactSummary, StringComparison.Ordinal);
        Assert.Contains("問題の履歴 2", result.CompactSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineRootIsAggregatedByCurrentCauseRatherThanFailedJobCount()
    {
        var root = new LibraryRoot(Guid.NewGuid(), @"E:\offline", @"E:\OFFLINE", "offline", null, RootAvailability.Offline, DateTimeOffset.UtcNow, null);
        var history = Enumerable.Range(0, 100).Select(_ => CreateJob(JobState.Failed, JobType.Scan, new ScanPayload(root.LibraryRootId))).ToArray();

        var result = GlobalActivityPresentation.Create([root], [], history, Worker(WorkerAvailability.Available), new(0, 0, 0));

        var problem = Assert.Single(result.CurrentConditions);
        Assert.Equal($"root-offline:{root.LibraryRootId:D}", problem.CauseKey);
        Assert.True(problem.IsActionRequired);
        Assert.Equal(1, result.ActionRequiredCount);
    }

    [Fact]
    public void RetryWaitingIsWaitingAndNotAnActionableProblem()
    {
        var retry = CreateJob(JobState.RetryWaiting, JobType.VisualEmbedding);

        var result = GlobalActivityPresentation.Create([], [retry], [retry], Worker(WorkerAvailability.Available), new(1, 0, 1));

        Assert.Single(result.Waiting);
        Assert.Equal("自動再試行待ち", result.Waiting[0].StateLabel);
        Assert.Equal(0, result.ActionRequiredCount);
        Assert.Contains(result.CurrentConditions, item => item.Kind == CurrentProblemKind.EmbeddingIncomplete && !item.IsActionRequired);
    }

    [Fact]
    public void WorkerUnavailableIsOneCurrentCapabilityProblemAndSuppressesDerivedEmbeddingCondition()
    {
        var result = GlobalActivityPresentation.Create([], [], [], Worker(WorkerAvailability.Unavailable), new(20, 0, 20));

        var problem = Assert.Single(result.CurrentConditions);
        Assert.Equal(CurrentProblemKind.WorkerUnavailable, problem.Kind);
        Assert.Equal(1, result.ActionRequiredCount);
    }

    [Fact]
    public void ProgressIsOnlyShownWhenBackendMarksItDeterminate()
    {
        var known = CreateJob(JobState.Running, JobType.Scan) with { Progress = new JobProgress(12, 30, "items", false) };
        var unknown = CreateJob(JobState.Running, JobType.Metadata) with { Progress = new JobProgress(12, 30, "items", true) };

        var result = GlobalActivityPresentation.Create([], [known, unknown], [], Worker(WorkerAvailability.Available), new(0, 0, 0));

        Assert.Equal("12 / 30 件", result.Running[0].ProgressText);
        Assert.Equal("進捗率は算出していません", result.Running[1].ProgressText);
    }

    [Fact]
    public void IncompleteCurrentScanPublicationIsOneRootProblem()
    {
        var root = OnlineRoot(4);
        var history = Enumerable.Range(0, 3).Select(_ => CreateJob(JobState.Failed, JobType.Scan, new ScanPayload(root.LibraryRootId, 4))).ToArray();

        var result = GlobalActivityPresentation.Create([root], [], history, Worker(WorkerAvailability.Available), new(0, 0, 0),
            [new(root.LibraryRootId, 4, "Interrupted", 1)]);

        var problem = Assert.Single(result.CurrentConditions);
        Assert.Equal(CurrentProblemKind.ScanIncomplete, problem.Kind);
        Assert.Equal($"scan-incomplete:{root.LibraryRootId:D}:4", problem.CauseKey);
        Assert.Equal(root.LibraryRootId, problem.LibraryRootId);
        Assert.Equal(1, result.ActionRequiredCount);
    }

    [Fact]
    public void CompletedRecoveryOrDifferentLifecycleDoesNotBecomeCurrentProblem()
    {
        var root = OnlineRoot(5);
        var current = new RootScanCurrentness(root.LibraryRootId, 5, "Completed", 0);
        var stale = new RootScanCurrentness(root.LibraryRootId, 4, "Interrupted", 1);

        var result = GlobalActivityPresentation.Create([root], [], [], Worker(WorkerAvailability.Available), new(0, 0, 0), [current, stale]);

        Assert.DoesNotContain(result.CurrentConditions, item => item.Kind == CurrentProblemKind.ScanIncomplete);
    }

    [Fact]
    public void ActiveRecoveryAndOfflineCauseSuppressIncompleteScanProblem()
    {
        var root = OnlineRoot(2);
        var retry = CreateJob(JobState.RetryWaiting, JobType.Scan, new ScanPayload(root.LibraryRootId, 2));
        var currentness = new RootScanCurrentness(root.LibraryRootId, 2, "Interrupted", 1);

        var recovering = GlobalActivityPresentation.Create([root], [retry], [retry], Worker(WorkerAvailability.Available), new(0, 0, 0), [currentness]);
        Assert.Single(recovering.Waiting);
        Assert.DoesNotContain(recovering.CurrentConditions, item => item.Kind == CurrentProblemKind.ScanIncomplete);

        var offline = GlobalActivityPresentation.Create([root with { Availability = RootAvailability.Offline }], [], [], Worker(WorkerAvailability.Available), new(0, 0, 0), [currentness]);
        Assert.Single(offline.CurrentConditions);
        Assert.Equal(CurrentProblemKind.RootOffline, offline.CurrentConditions[0].Kind);
    }

    [Fact]
    public void MainWindowSeparatesCurrentActivityFromJobHistoryAndKeepsTechnicalDetails()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "LocalMediaAI.App", "MainWindow.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"GlobalActivitySummary\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalActivityRunning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalActivityWaiting\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalActivityCurrentProblems\"", xaml, StringComparison.Ordinal);
        Assert.Contains("処理・問題の履歴（直近50件）", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding JobIssues}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"JobIssueTechnicalDetails\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ScanCurrentProblemRescan\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding JobIssueSummary}\"", xaml, StringComparison.Ordinal);
    }

    private static LibraryRoot OnlineRoot(long revision) => new(Guid.NewGuid(), @"E:\online", @"E:\ONLINE", "online", null,
        RootAvailability.Online, DateTimeOffset.UtcNow, null, RegistrationRevision: revision);

    private static WorkerRuntimeStatus Worker(WorkerAvailability availability) => new(availability, null, null, null, null, null, DateTimeOffset.UtcNow);

    private static Job CreateJob(JobState state, JobType type, object? payload = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), type, state, state.ToString(), JobReason.None, JsonSerializer.Serialize(payload ?? new { }), 1, 3, null,
            state == JobState.Pending ? null : now.AddMinutes(-1),
            state is JobState.Completed or JobState.Partial or JobState.Failed or JobState.Cancelled ? now : null,
            now, new JobProgress(0, 0, "items", true), state is JobState.Failed or JobState.Partial ? "HISTORICAL" : null, null, now.AddMinutes(-2), now);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LocalMediaAI.sln from test base directory.");
    }
}
