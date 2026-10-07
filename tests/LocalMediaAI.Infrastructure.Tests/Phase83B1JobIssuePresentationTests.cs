using System.Text.Json;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase83B1JobIssuePresentationTests
{
    [Fact]
    public void FailedAndOfflineScanExposeOnlyGroundedRescan()
    {
        var root = CreateRoot(RootAvailability.Offline);
        var roots = new Dictionary<Guid, LibraryRoot> { [root.LibraryRootId] = root };
        var failed = CreateJob(JobState.Failed, JobType.Scan, "Failed", JobReason.None, new ScanPayload(root.LibraryRootId), "SCAN_FAILED");
        var partial = CreateJob(JobState.Partial, JobType.Scan, "RootOffline", JobReason.RootOffline, new ScanPayload(root.LibraryRootId), "ROOT_OFFLINE");

        var failedPresentation = JobIssuePresentation.Create(failed, roots);
        var partialPresentation = JobIssuePresentation.Create(partial, roots);

        Assert.Equal("過去の処理を完了できませんでした", failedPresentation.Title);
        Assert.False(failedPresentation.IsActionRequired);
        Assert.True(failedPresentation.CanRescan);
        Assert.Contains(root.DisplayName, failedPresentation.AffectedScope, StringComparison.Ordinal);
        Assert.Equal("メディアフォルダーがオフラインだったため一部を処理できませんでした", partialPresentation.Title);
        Assert.False(partialPresentation.IsActionRequired);
        Assert.True(partialPresentation.CanRescan);
        Assert.Contains("元ファイルが見つからない状態とは区別", partialPresentation.Meaning, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryWaitingDistinguishesResourceAndRestartRecoveryWithoutManualRetry()
    {
        var root = CreateRoot(RootAvailability.Online);
        var roots = new Dictionary<Guid, LibraryRoot> { [root.LibraryRootId] = root };
        var resource = CreateJob(JobState.RetryWaiting, JobType.VisualEmbedding, "ResourceWaiting", JobReason.ResourceVram, new VisualEmbeddingPayload(Guid.NewGuid()), "RESOURCE_VRAM_UNAVAILABLE", DateTimeOffset.UtcNow.AddMinutes(1));
        var restart = CreateJob(JobState.RetryWaiting, JobType.Scan, "RecoveryPending", JobReason.AppRestartRecovery, new ScanPayload(root.LibraryRootId), "APP_RESTART_RECOVERY");

        var resourcePresentation = JobIssuePresentation.Create(resource, roots);
        var restartPresentation = JobIssuePresentation.Create(restart, roots);

        Assert.Equal("利用可能なシステムリソースを待っています", resourcePresentation.Title);
        Assert.Equal("自動再試行予定", resourcePresentation.Recoverability);
        Assert.False(resourcePresentation.IsActionRequired);
        Assert.False(resourcePresentation.CanRescan);
        Assert.Equal("前回実行中だった処理を再開待ちへ戻しました", restartPresentation.Title);
        Assert.Equal("自動回復予定", restartPresentation.Recoverability);
        Assert.DoesNotContain("manual retry", restartPresentation.Meaning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelledIsTerminalAndUnknownPartialFallsBackWithoutInventedAction()
    {
        var cancelled = JobIssuePresentation.Create(CreateJob(JobState.Cancelled, JobType.Metadata, "Cancelled", JobReason.CancelRequested, new MediaPayload(Guid.NewGuid())), new Dictionary<Guid, LibraryRoot>());
        var partial = JobIssuePresentation.Create(CreateJob(JobState.Partial, JobType.TagSuggestion, "UnknownPartialReason", JobReason.None, new TagSuggestionPayload(Guid.NewGuid(), Guid.NewGuid(), 10, DateTimeOffset.UtcNow)), new Dictionary<Guid, LibraryRoot>());

        Assert.Equal("処理は中止され、現在は実行されていません", cancelled.Title);
        Assert.False(cancelled.ShowRefreshAction);
        Assert.False(cancelled.CanRescan);
        Assert.Equal("現在状態は別途判定", partial.Recoverability);
        Assert.False(partial.CanRescan);
    }

    [Fact]
    public void XamlProvidesOnDemandMasterDetailTechnicalDisclosureAndExplicitOfflineRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = LocalizedSource.Read(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml"));
        var codeBehind = LocalizedSource.Read(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"IssuesPane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding JobIssues}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedJobIssue}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding AutomationName}\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"JobIssueDetail\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"JobIssueTechnicalDetails\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"AI機能を再確認\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AI検索と新しいAI解析は現在利用できません。ライブラリの閲覧は続けられます。", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"登録フォルダーを再スキャン\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Retry\"", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Text=\"オフライン\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SelectedOfflineRootDetail\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IssuesPane.IsExpanded = true;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SelectOnlyActionRequiredJobIssue", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"68\"", xaml, StringComparison.Ordinal);
    }

    private static LibraryRoot CreateRoot(RootAvailability availability) => new(
        Guid.NewGuid(), @"E:\isolated\media", @"E:\ISOLATED\MEDIA", "isolated-media", null, availability, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

    private static Job CreateJob(JobState state, JobType type, string phase, JobReason reason, object payload, string? errorCode = null, DateTimeOffset? notBefore = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Job(Guid.NewGuid(), type, state, phase, reason, JsonSerializer.Serialize(payload), 1, 3, notBefore, now.AddMinutes(-1), state is JobState.Failed or JobState.Partial or JobState.Cancelled ? now : null, now, new JobProgress(0, 1, "items", true), errorCode, errorCode is null ? null : "利用者向けfixture message", now.AddMinutes(-2), now);
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
