using System.Collections.ObjectModel;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

public sealed record ReviewMediaEvidence(MediaFile Media, string? Frame, double Timestamp, string Duration, AsyncCommand Preview, AsyncCommand Handoff)
{
    public string FileName => Media.FileName;
    public string Path => Media.Path;
    public string Facts => $"{Media.SizeBytes:N0} bytes · {Duration}";
    public string TimestampLabel => L.F("プレビュー開始位置: {0:F2}秒", Timestamp);
    public string Availability => Media.PresenceState == PresenceState.Present ? L.T("利用可能") : L.T("元ファイルは現在利用できません。根拠・判断は保持されます。");
}

public sealed partial class MainViewModel
{
    private long _reviewEvidenceVersion;
    private bool _isReviewLoading;
    private bool _reviewDetailedMode;
    private bool _reviewBulkMode;
    public bool ReviewBulkMode { get => _reviewBulkMode; set => Set(ref _reviewBulkMode, value); }
    private string _reviewQueueMessage = L.T("候補を取得しています。");
    private string _reviewJudgmentFeedback = string.Empty;
    public ObservableCollection<ReviewMediaEvidence> ReviewEvidence { get; } = [];
    public Task ReviewEvidenceLoadTask { get; private set; } = Task.CompletedTask;
    public bool ReviewDetailedMode { get => _reviewDetailedMode; set => Set(ref _reviewDetailedMode, value); }
    public bool IsReviewLoading { get => _isReviewLoading; private set => Set(ref _isReviewLoading, value); }
    public string ReviewQueueMessage { get => _reviewQueueMessage; private set => Set(ref _reviewQueueMessage, value); }
    public string ReviewJudgmentFeedback { get => _reviewJudgmentFeedback; private set => Set(ref _reviewJudgmentFeedback, value); }
    public bool ReviewJudgmentEnabled => SelectedUnifiedReview is { } row && ReviewEvidence.Count > 0 && (!row.IsRelation || ReviewEvidence.Count == row.Item.InvolvedMediaCount);
    public string ReviewPageStatus => TotalUnifiedReviewCount == 0 ? string.Empty : L.F("{0:N0} / {1:N0} ページ", CurrentUnifiedReviewPage + 1, ((TotalUnifiedReviewCount - 1) / UnifiedReviewPageSize) + 1);
    public IReadOnlyList<UnifiedReviewAction> ReviewCompatibleBulkActions => UnifiedReviewItems.Where(r => r.IsBulkSelected).Select(r => r.ReviewType).Distinct().ToArray() is { Length: 1 } types ? UnifiedReviewBulkActions.Where(a => UnifiedReviewPolicy.IsAllowed(types[0], a)).ToArray() : [];
    public string ReviewBulkSummary => L.F("{0:N0} 件を選択。同じ種類の現在候補だけへ判断を保存できます。", SelectedUnifiedReviewBulkCount);
    public string ReviewJudgmentAvailability => SelectedUnifiedReview is { IsActionable: false } ? L.T("現在の根拠または元ファイルを確認できないため、判断は保存できません。状態を確認してから更新してください。") : string.Empty;
    public string ReviewMeaning => SelectedUnifiedReview is { IsActionable: false }
        ? L.T("保存された根拠です。現在の根拠・元ファイル状態を確認できないため、判断操作は利用できません。")
        : SelectedUnifiedReview?.ReviewType switch
    {
        UnifiedReviewType.ExactDuplicate => L.T("完全一致 — 同じ内容のファイルです。対象を確認して判断を記録してください。"),
        UnifiedReviewType.NearDuplicate => L.T("類似重複の候補 — 各メディアのフレームと再生内容を比較してください。"),
        UnifiedReviewType.ContentSimilar => L.T("内容の類似 — 重複を意味しません。内容の関連を判断してください。"),
        UnifiedReviewType.SuggestedTag => L.T("提案タグ — AIの提案です。確認するまで人が確定したタグではありません。"),
        _ => L.T("候補を選択すると、根拠と判断操作を表示します。")
    };

    private void SelectReviewEvidence(UnifiedReviewRow? row)
    {
        var version = ++_reviewEvidenceVersion;
        ReviewEvidence.Clear(); ReviewJudgmentFeedback = string.Empty; Raise(nameof(ReviewMeaning)); Raise(nameof(ReviewJudgmentAvailability));
        Raise(nameof(ReviewJudgmentEnabled));
        ReviewEvidenceLoadTask = LoadReviewEvidenceAsync(row, version);
    }
    private async Task LoadReviewEvidenceAsync(UnifiedReviewRow? row, long version)
    {
        if (row is null) return;
        try
        {
            var cards = new List<ReviewMediaEvidence>();
            if (row.IsExact)
            {
                foreach (var member in await _library.GetDuplicateComparisonMembersAsync(Guid.Parse(row.Item.SourceStableId), _lifetime.Token))
                    cards.Add(Card(member.Media, null, 0, member.Metadata?.DurationSeconds));
            }
            else if (row.IsNear)
            {
                foreach (var member in await _library.GetNearDuplicateComparisonMembersAsync(Guid.Parse(row.Item.SourceStableId), _lifetime.Token))
                {
                    var frames = await _library.GetCurrentNearDuplicateFramesAsync(member.MediaFileId, member.MediaContentId, member.Evidence.SamplingVersion, PerceptualFrameFingerprint.Algorithm, _lifetime.Token);
                    var frame = frames.Where(f => f.Status == AnalysisStatus.Completed).OrderBy(f => f.SampleIndex).FirstOrDefault();
                    cards.Add(Card(member.Media, frame?.FrameRelativePath, frame?.PresentationTimestampSeconds ?? 0, member.Metadata?.DurationSeconds));
                }
            }
            else if (row.IsContent)
            {
                foreach (var member in await _library.GetContentSimilarityComparisonMembersAsync(row.Item.EvidenceId, _lifetime.Token))
                    cards.Add(Card(member.Media, member.RepresentativeSample.FrameRelativePath, member.RepresentativeSample.PresentationTimestampSeconds, member.Metadata?.DurationSeconds));
            }
            else if (row.Item.PrimaryMediaFileId is { } id && await _library.GetMediaFileAsync(id, _lifetime.Token) is { } media)
                cards.Add(Card(media, row.RepresentativeFrameRelativePath, row.Item.RepresentativeTimestampSeconds ?? 0, null));
            // Never display a partial relation if a member cannot be resolved.
            if (version != _reviewEvidenceVersion || SelectedUnifiedReview != row) return;
            if (row.IsRelation && cards.Count != row.Item.InvolvedMediaCount)
            { ReviewJudgmentFeedback = L.T("構成メディアをすべて取得できませんでした。更新して再確認してください。"); return; }
            foreach (var card in cards) ReviewEvidence.Add(card);
            Raise(nameof(ReviewJudgmentEnabled));
            if (cards.FirstOrDefault() is { } primary && primary.Media.PresenceState == PresenceState.Present)
                SetPreviewTarget(new PreviewTarget(primary.Path, primary.Timestamp, "Review evidence", L.T("根拠時刻から読み取り専用プレビューを開きます。")));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _reviewEvidenceVersion) ReviewJudgmentFeedback = L.T("根拠を取得できませんでした。候補を更新して再試行できます。");
            _log.Write("Warning", "ReviewEvidenceLoadFailed", new { row.ReviewItemId }, ex);
        }
        ReviewMediaEvidence Card(MediaFile media, string? frame, double timestamp, double? duration) => new(media, frame ?? MediaItems.FirstOrDefault(m => m.MediaFileId == media.MediaFileId)?.ThumbnailRelativePath, timestamp,
            duration is { } seconds ? L.F("{0:F2}秒", seconds) : L.T("再生時間は未取得"),
            new AsyncCommand(async () =>
            {
                if (version != _reviewEvidenceVersion) return;
                SetPreviewTarget(new PreviewTarget(media.Path, timestamp, "Review evidence", L.T("表示中の根拠時刻から読み取り専用プレビューを開きます。")));
                await OpenPreviewAsync();
            }, () => version == _reviewEvidenceVersion && media.PresenceState == PresenceState.Present),
            new AsyncCommand(async () => { if (version == _reviewEvidenceVersion) await HandoffMediaToOperationAsync(media.MediaFileId, "Review evidence explicit handoff", row.ReviewItemId); }, () => version == _reviewEvidenceVersion && OperationEditorsEnabled));
    }

    private async Task UpdateReviewQueueMessageAsync(long requestId, PagedResult<UnifiedReviewItem> result, CancellationToken cancellationToken)
    {
        var all = UnifiedReviewFilter == UnifiedReviewFilter.All ? result : await _library.QueryUnifiedReviewAsync(new(CurrentLibraryScope.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 1), cancellationToken);
        var active = await _jobs.GetActiveAsync(cancellationToken);
        var unreviewed = all.TotalCount > 0 ? await _library.QueryUnifiedReviewAsync(new(CurrentLibraryScope.LibraryRootId, UnifiedReviewFilter.Unreviewed, UnifiedReviewSort.Priority, 0, 1), cancellationToken) : null;
        if (requestId != Volatile.Read(ref _unifiedReviewRequestId)) return;
        ReviewQueueMessage = UnifiedReviewFilter == UnifiedReviewFilter.Unreviewed && all.TotalCount > 0 && unreviewed?.TotalCount == 0 ? L.T("現在の対象範囲の候補は確認済みです。確認が完了しました。判断の記録は保持されています。") :
            result.TotalCount == 0 && all.TotalCount > 0 ? L.T("現在の条件に一致する候補はありません。絞り込みを解除できます。") :
            all.TotalCount == 0 && active.Any(j => j.JobType is JobType.Scan or JobType.FullHash or JobType.VisualSampling or JobType.VisualEmbedding or JobType.NearDuplicateFingerprint or JobType.ContentSimilarityRepresentation or JobType.TagSuggestion or JobType.ProtectedSourceOrchestration) ? L.T("レビュー候補を準備しています。バックグラウンド解析中です。") :
            all.TotalCount == 0 ? L.T("現在の対象範囲に確認する候補はありません。解析状況は画面下の「現在の処理」で確認できます。") :
            unreviewed?.TotalCount == 0 ? L.T("現在の対象範囲の候補は確認済みです。確認が完了しました。判断の記録は保持されています。") : L.F("{0:N0}件の候補。根拠を見て判断を記録してください。", result.TotalCount);
    }
}
