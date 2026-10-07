using System.Text.Json;
using System.Globalization;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

public sealed record JobIssuePresentation(
    Job Job,
    string Category,
    string StateLabel,
    string Title,
    string Meaning,
    string AffectedScope,
    string Recoverability,
    string UpdatedDisplay,
    string ActionRequiredLabel,
    bool IsActionRequired,
    bool ShowRefreshAction,
    bool CanRescan,
    Guid? LibraryRootId,
    string RelevantIdentifiers,
    string AccentBrush)
{
    public string AutomationName => $"{StateLabel}; {AffectedScope}; {ActionRequiredLabel}";

    public string TechnicalDetails => string.Join(Environment.NewLine,
    [
        $"Job ID: {Job.JobId:D}",
        $"JobType: {Job.JobType}",
        $"Durable State: {Job.State}",
        $"Phase / Reason: {Job.Phase} / {Job.Reason}",
        $"Attempt: {Job.Attempt} / {Job.MaxAttempts}",
        $"NotBefore: {FormatTime(Job.NotBefore)}",
        $"LastErrorCode: {Job.LastErrorCode ?? "none"}",
        $"LastErrorMessage: {Job.LastErrorMessage ?? "none"}",
        $"Created: {FormatTime(Job.CreatedAt)}",
        $"Updated: {FormatTime(Job.UpdatedAt)}",
        $"Started: {FormatTime(Job.StartedAt)}",
        $"Completed: {FormatTime(Job.CompletedAt)}",
        $"Relevant IDs: {RelevantIdentifiers}"
    ]);

    public static JobIssuePresentation Create(Job job, IReadOnlyDictionary<Guid, LibraryRoot> roots)
    {
        var rootId = TryReadGuid(job.PayloadJson, nameof(ScanPayload.LibraryRootId));
        var mediaId = TryReadGuid(job.PayloadJson, nameof(MediaPayload.MediaFileId));
        var visualSampleId = TryReadGuid(job.PayloadJson, nameof(VisualEmbeddingPayload.VisualSampleId));
        roots.TryGetValue(rootId ?? Guid.Empty, out var root);
        var affectedScope = DescribeScope(job.JobType, root, rootId);
        var identifiers = string.Join(", ", new[]
        {
            rootId is null ? null : $"LibraryRootId={rootId:D}",
            mediaId is null ? null : $"MediaFileId={mediaId:D}",
            visualSampleId is null ? null : $"VisualSampleId={visualSampleId:D}"
        }.Where(value => value is not null));
        if (identifiers.Length == 0) identifiers = "payload identifier unavailable";

        var resourceWait = job.State == JobState.RetryWaiting && job.Reason is
            JobReason.ResourceCpu or JobReason.ResourceRam or JobReason.ResourceGpu or JobReason.ResourceVram or JobReason.ResourceIo or JobReason.DatabaseBusy;
        var restartRecovery = job.State == JobState.RetryWaiting &&
            (job.Reason == JobReason.AppRestartRecovery || string.Equals(job.Phase, "RecoveryPending", StringComparison.OrdinalIgnoreCase));
        var rootOffline = job.Reason == JobReason.RootOffline ||
            string.Equals(job.Phase, "RootOffline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(job.LastErrorCode, "ROOT_OFFLINE", StringComparison.OrdinalIgnoreCase);
        var canRescan = job.JobType == JobType.Scan && rootId is not null && root is not null &&
            (job.State == JobState.Failed || job.State == JobState.Partial && rootOffline);

        var presentation = job.State switch
        {
            JobState.Failed => new PresentationText(
                L.T("履歴"), L.T("完了できませんでした"), L.T("過去の処理を完了できませんでした"),
                L.T("この履歴だけでは現在も問題が残っているとは判定しません。現在の状態は上段の要対応表示で確認でき、技術情報は引き続き参照できます。"),
                L.T("現在状態は別途判定"), false, true, "#7D8796"),
            JobState.RetryWaiting when resourceWait => new PresentationText(
                L.T("待機中"), L.T("リソース待ち"), L.T("利用可能なシステムリソースを待っています"),
                job.NotBefore is { } notBefore
                    ? L.F("CPU、RAM、GPU、ストレージ等の利用条件が整うまで待機しています。{0:yyyy/MM/dd HH:mm:ss}以降にアプリが自動再試行します。", notBefore.ToLocalTime())
                    : L.T("CPU、RAM、GPU、ストレージ等の利用条件が整うと、アプリが自動再試行します。"),
                L.T("自動再試行予定"), false, true, "#D7A84B"),
            JobState.RetryWaiting when restartRecovery => new PresentationText(
                L.T("待機中"), L.T("再開待ち"), L.T("前回実行中だった処理を再開待ちへ戻しました"),
                L.T("アプリ終了時に実行中だった処理です。安全な再開待ちへ移され、アプリが自動処理します。"),
                L.T("自動回復予定"), false, true, "#D7A84B"),
            JobState.RetryWaiting => new PresentationText(
                L.T("待機中"), L.T("再試行待ち"), L.T("処理は安全な再試行待ちです"),
                L.T("アプリが自動で再試行するため、手動での再試行は不要です。"),
                L.T("自動処理予定"), false, true, "#D7A84B"),
            JobState.Partial when rootOffline => new PresentationText(
                L.T("履歴"), L.T("一部未完了"), L.T("メディアフォルダーがオフラインだったため一部を処理できませんでした"),
                L.T("これは発生時点の履歴です。現在もオフラインかどうかは上段の現在状態で判定します。オフラインは元ファイルが見つからない状態とは区別します。"),
                L.T("現在状態は別途判定"), false, true, "#7D8796"),
            JobState.Partial => new PresentationText(
                L.T("履歴"), L.T("一部未完了"), L.T("過去の処理の一部を完了できませんでした"),
                L.T("この履歴だけでは現在も不足が残っているとは判定しません。発生時の理由は技術情報で確認できます。"),
                L.T("現在状態は別途判定"), false, true, "#7D8796"),
            JobState.Cancelled => new PresentationText(
                L.T("履歴"), L.T("中止済み"), L.T("処理は中止され、現在は実行されていません"),
                L.T("この処理は終了しています。自動での再開や再試行は行いません。"),
                L.T("再開なし"), false, false, "#7D8796"),
            JobState.Completed => new PresentationText(
                L.T("履歴"), L.T("完了"), L.T("処理が完了しました"), L.T("追加の対応は必要ありません。"), L.T("対応不要"), false, false, "#5E9E72"),
            JobState.Running => new PresentationText(
                L.T("処理中"), L.T("処理中"), L.T("処理を実行しています"), L.T("完了までライブラリを利用できます。結果は処理完了後に増えることがあります。"), L.T("処理中"), false, true, "#6F9FD8"),
            JobState.Pending => new PresentationText(
                L.T("処理中"), L.T("待機中"), L.T("処理の開始を待っています"), L.T("処理待ちとして登録されています。"), L.T("待機中"), false, true, "#6F9FD8"),
            JobState.Waiting => new PresentationText(
                L.T("待機中"), L.T("条件待ち"), L.T("処理に必要な条件を待っています"), L.T("待機理由は技術情報で確認できます。"), L.T("自動処理予定"), false, true, "#D7A84B"),
            JobState.Paused => new PresentationText(
                L.T("待機中"), L.T("一時停止"), L.T("処理は一時停止しています"), L.T("再開条件は技術情報で確認できます。利用可能な操作のみ表示します。"), L.T("状態確認"), false, true, "#D7A84B"),
            _ => new PresentationText(
                L.T("履歴"), L.T("状態確認"), L.T("処理状態を確認してください"), L.T("意味を安全に判断できない状態です。技術情報を確認してください。"), L.T("対応内容を確認できません"), false, false, "#7D8796")
        };

        return new JobIssuePresentation(
            job,
            presentation.Category,
            presentation.StateLabel,
            presentation.Title,
            presentation.Meaning,
            affectedScope,
            presentation.Recoverability,
            job.UpdatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture),
            presentation.ActionRequired ? L.T("対応が必要") : presentation.Category == L.T("待機中") ? L.T("自動処理を待機") : presentation.Category == L.T("履歴") ? L.T("履歴") : L.T("対応不要"),
            presentation.ActionRequired,
            presentation.ShowRefresh,
            canRescan,
            rootId,
            identifiers,
            presentation.AccentBrush);
    }

    private static string DescribeScope(JobType type, LibraryRoot? root, Guid? rootId) => type switch
    {
        JobType.Scan => root is not null ? L.F("登録フォルダー: {0}", root.DisplayName) : rootId is not null ? L.T("登録フォルダー 1件") : L.T("ライブラリのスキャン"),
        JobType.Metadata => L.T("メディア情報 1件"),
        JobType.Thumbnail => L.T("プレビュー画像 1件"),
        JobType.FullHash => L.T("完全一致確認用データ 1件"),
        JobType.VisualSampling => L.T("AI解析用フレーム 1件"),
        JobType.VisualEmbedding => L.T("AI検索用データ 1件"),
        JobType.NearDuplicateFingerprint => L.T("類似重複確認用データ 1件"),
        JobType.ContentSimilarityRepresentation => L.T("内容の類似確認用データ 1件"),
        JobType.TagSuggestion => L.T("提案タグ用データ 1件"),
        _ => L.T("処理対象 1件")
    };

    private static Guid? TryReadGuid(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.String && Guid.TryParse(property.Value.GetString(), out var parsed) ? parsed : null;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string FormatTime(DateTimeOffset? value) => value is null ? "not recorded" : value.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz", CultureInfo.CurrentCulture);

    private sealed record PresentationText(string Category, string StateLabel, string Title, string Meaning, string Recoverability, bool ActionRequired, bool ShowRefresh, string AccentBrush);
}

public sealed record JobEventPresentation(string Summary, string? TechnicalDetail)
{
    public static JobEventPresentation Create(JobEvent item) => new(
        $"{item.OccurredAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} — {item.EventName}",
        item.DetailJson);
}
