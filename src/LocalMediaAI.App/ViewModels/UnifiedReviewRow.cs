using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

/// <summary>UI-only explicit bulk selection state. Durable identity and evidence remain in UnifiedReviewItem.</summary>
public sealed class UnifiedReviewRow(UnifiedReviewItem item) : ViewModelBase
{
    private bool _isBulkSelected;
    private string? _generalMediaLabel;
    public string GeneralMediaLabel { get => _generalMediaLabel ?? (Guid.TryParse(Item.DisplayName, out _) ? L.F("{0}件のメディア", Item.InvolvedMediaCount) : DisplayName); set => Set(ref _generalMediaLabel, value); }

    public UnifiedReviewItem Item { get; } = item;
    public bool IsBulkSelected { get => _isBulkSelected; set => Set(ref _isBulkSelected, value); }
    public string ReviewItemId => Item.ReviewItemId;
    public UnifiedReviewType ReviewType => Item.ReviewType;
    public string ReviewTypeDisplay => Item.ReviewTypeDisplay;
    public string GeneralType => ReviewType switch { UnifiedReviewType.ExactDuplicate => L.T("完全一致"), UnifiedReviewType.NearDuplicate => L.T("類似重複"), UnifiedReviewType.ContentSimilar => L.T("内容の類似"), UnifiedReviewType.SuggestedTag => L.T("提案タグ"), _ => L.T("レビュー") };
    public string GeneralDecision => HumanDecision switch { "Unreviewed" => L.T("未確認"), "ConfirmedDuplicate" => L.T("完全一致を確認済み"), "ConfirmedNearDuplicate" => L.T("類似重複を確認済み"), "KeepBoth" => L.T("両方を保持"), "Ignored" => L.T("対象外"), "NotDuplicate" => L.T("重複ではない"), "Related" => L.T("関連あり"), "NotRelated" => L.T("関連なし"), "Confirmed" => L.T("タグを確認済み"), "Rejected" => L.T("タグを却下"), "Preferred" => L.T("優先ファイルを指定済み"), _ => L.T("判断記録あり（詳細を参照）") };
    public bool IsExact => ReviewType == UnifiedReviewType.ExactDuplicate;
    public bool IsNear => ReviewType == UnifiedReviewType.NearDuplicate;
    public bool IsContent => ReviewType == UnifiedReviewType.ContentSimilar;
    public bool IsTag => ReviewType == UnifiedReviewType.SuggestedTag;
    public bool IsRelation => !IsTag;
    public string EvidenceStateDisplay => Item.EvidenceStateDisplay;
    public string GeneralEvidenceState => Item.EvidenceState switch
    {
        ReviewEvidenceState.Current => L.T("現在の対象の根拠です"),
        ReviewEvidenceState.Stale => L.T("以前の根拠です。現在の判断対象ではありません"),
        ReviewEvidenceState.SourceUnavailable => L.T("元ファイルを利用できません。根拠と判断は保持されています"),
        _ => L.T("根拠の状態を確認できません")
    };
    public string SourceAvailability => Item.SourceAvailability;
    public string HumanDecision => Item.HumanDecision;
    public string? HumanNote => Item.HumanNote;
    public string PriorityReason => Item.PriorityReason;
    public string EvidenceKind => Item.EvidenceKind;
    public string EvidenceSummary => Item.EvidenceSummary;
    public string ScoreText => Item.ScoreText;
    public string ProvenanceSummary => Item.ProvenanceSummary;
    public string? RepresentativeFrameRelativePath => Item.RepresentativeFrameRelativePath;
    public string RepresentativeTimestampDisplay => Item.RepresentativeTimestampSeconds is { } timestamp ? $"Representative timestamp: {timestamp:F2}s" : "Representative timestamp unavailable";
    public string DisplayName => Item.DisplayName ?? L.T("対象メディア（根拠を参照）");
    public string AutomationName => $"{GeneralType}: {GeneralMediaLabel}";
    public string AutomationHelpText => L.F("判断: {0}。{1}", GeneralDecision, (IsActionable ? L.T("判断を記録できます。") : L.T("現在の根拠または元ファイルを確認できないため、判断は保存できません。")));
    public bool IsActionable => Item.IsActionable;
}
