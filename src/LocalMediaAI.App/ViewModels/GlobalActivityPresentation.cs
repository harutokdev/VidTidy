using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;
using System.Globalization;

namespace LocalMediaAI.App.ViewModels;

public enum CurrentProblemKind { RootOffline, ScanIncomplete, WorkerUnavailable, EmbeddingIncomplete }

public sealed record CurrentProblemPresentation(
    string CauseKey,
    CurrentProblemKind Kind,
    string Title,
    string Meaning,
    string ActionLabel,
    bool IsActionRequired,
    string AccentBrush,
    Guid? LibraryRootId = null);

public sealed record JobActivityPresentation(Guid JobId, string StateLabel, string Title, string ProgressText, string UpdatedDisplay)
{
    public static JobActivityPresentation Create(Job job)
    {
        var title = job.JobType switch
        {
            JobType.Scan => L.T("メディアのスキャン"),
            JobType.Metadata => L.T("メディア情報の取得"),
            JobType.Thumbnail => L.T("プレビュー画像の作成"),
            JobType.FullHash => L.T("重複確認用データの作成"),
            JobType.VisualSampling => L.T("AI解析用フレームの準備"),
            JobType.VisualEmbedding => L.T("AI検索用データの作成"),
            JobType.NearDuplicateFingerprint => L.T("類似ファイル確認用データの作成"),
            JobType.ContentSimilarityRepresentation => L.T("内容類似度データの作成"),
            JobType.TagSuggestion => L.T("タグ候補の作成"),
            JobType.ProtectedSourceOrchestration => L.T("メディア解析"),
            _ => L.T("バックグラウンド処理")
        };
        var state = job.State switch
        {
            JobState.Running => L.T("実行中"),
            JobState.Pending => L.T("待機中"),
            JobState.RetryWaiting => L.T("自動再試行待ち"),
            JobState.Waiting => L.T("条件待ち"),
            JobState.Paused => L.T("一時停止"),
            _ => job.State.ToString()
        };
        var unit = job.Progress.Unit switch { "files" => L.T("ファイル"), "items" => L.T("件"), "samples" => L.T("フレーム"), _ => string.Empty };
        var progress = !job.Progress.IsIndeterminate && job.Progress.Total > 0
            ? $"{job.Progress.Current:N0} / {job.Progress.Total:N0} {unit}".TrimEnd()
            : L.T("進捗率は算出していません");
        return new(job.JobId, state, title, progress, job.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture));
    }
}

/// <summary>Read-only current-state projection. Terminal Job history is never a current-problem authority.</summary>
public sealed record GlobalActivityPresentation(
    string CompactSummary,
    IReadOnlyList<JobActivityPresentation> Running,
    IReadOnlyList<JobActivityPresentation> Waiting,
    IReadOnlyList<CurrentProblemPresentation> CurrentConditions,
    int ActionRequiredCount,
    int RecentCompletedCount,
    int ProblemHistoryCount)
{
    public static GlobalActivityPresentation Create(
        IReadOnlyList<LibraryRoot> roots,
        IReadOnlyList<Job> activeJobs,
        IReadOnlyList<Job> recentHistory,
        WorkerRuntimeStatus worker,
        SemanticEmbeddingCompleteness embeddingCompleteness,
        IReadOnlyList<RootScanCurrentness>? rootScanCurrentness = null)
    {
        var running = activeJobs.Where(job => job.State == JobState.Running).Select(JobActivityPresentation.Create).ToArray();
        var waiting = activeJobs.Where(job => job.State is JobState.Pending or JobState.Waiting or JobState.Paused or JobState.RetryWaiting).Select(JobActivityPresentation.Create).ToArray();
        var conditions = new List<CurrentProblemPresentation>();

        foreach (var root in roots.Where(root => root.Availability == RootAvailability.Offline))
        {
            conditions.Add(new($"root-offline:{root.LibraryRootId:D}", CurrentProblemKind.RootOffline,
                L.T("登録フォルダーへアクセスできません"),
                L.F("{0} は現在オフラインです。関連する過去処理の件数ではなく、この登録フォルダーを1件の原因として表示しています。", root.DisplayName),
                L.T("接続状態を確認して再スキャン"), true, "#D98282", root.LibraryRootId));
        }

        var activeScanLifecycles = activeJobs
            .Where(job => job.JobType == JobType.Scan)
            .Select(job => TryReadScanPayload(job.PayloadJson))
            .Where(payload => payload is not null)
            .Select(payload => (payload!.LibraryRootId, payload.RegistrationRevision))
            .ToHashSet();
        var rootsByLifecycle = roots.ToDictionary(root => (root.LibraryRootId, root.RegistrationRevision));
        foreach (var scan in rootScanCurrentness ?? [])
        {
            if (!rootsByLifecycle.TryGetValue((scan.LibraryRootId, scan.RegistrationRevision), out var root) ||
                root.Availability == RootAvailability.Offline ||
                activeScanLifecycles.Contains((scan.LibraryRootId, scan.RegistrationRevision)) ||
                (scan.State == "Completed" && scan.ErrorCount == 0)) continue;
            conditions.Add(new($"scan-incomplete:{scan.LibraryRootId:D}:{scan.RegistrationRevision}", CurrentProblemKind.ScanIncomplete,
                L.T("フォルダーの更新が完了していません"),
                L.F("{0} の現在の登録期間における最新のScan publicationが正常完了していません。過去Jobの状態ではなく、現在のLibrary更新結果から判定しています。", root.DisplayName),
                L.T("再スキャン"), true, "#D98282", root.LibraryRootId));
        }

        if (worker.Availability == WorkerAvailability.Unavailable)
        {
            conditions.Add(new("worker-unavailable", CurrentProblemKind.WorkerUnavailable,
                L.T("AI機能を利用できません"),
                worker.UserMessage ?? L.T("AI検索と新しいAI解析を現在利用できません。ライブラリの閲覧は続けられます。"),
                L.T("AI機能を再確認"), true, "#D98282"));
        }

        var embeddingWorkActive = activeJobs.Any(job => job.JobType == JobType.VisualEmbedding);
        if (embeddingCompleteness.MissingEmbeddingCount > 0 && worker.Availability != WorkerAvailability.Unavailable)
        {
            conditions.Add(new("embedding-incomplete:current-profile", CurrentProblemKind.EmbeddingIncomplete,
                L.T("AI検索の準備が完了していません"),
                L.F("現在のメディア内容とAIモデル設定に対して、{0:N0}件の解析対象が残っています。", embeddingCompleteness.MissingEmbeddingCount),
                embeddingWorkActive ? L.T("バックグラウンド処理中") : L.T("現在の状態を更新"), false, "#D7A84B"));
        }

        var actionable = conditions.Count(item => item.IsActionRequired);
        var recentCompleted = recentHistory.Count(job => job.State == JobState.Completed);
        var problemHistory = recentHistory.Count(job => job.State is JobState.Failed or JobState.Partial);
        return new(L.F("実行中 {0:N0} | 待機 {1:N0} | 要対応 {2:N0} | 問題の履歴 {3:N0} | 最近完了 {4:N0}", running.Length, waiting.Length, actionable, problemHistory, recentCompleted),
            running, waiting, conditions, actionable, recentCompleted, problemHistory);
    }

    private static ScanPayload? TryReadScanPayload(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<ScanPayload>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
