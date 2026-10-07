using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

/// <summary>
/// Phase 5.4 review-row state. The selected checkbox is intentionally local UI state:
/// persistence occurs only after an explicit Human confirmation command is invoked.
/// </summary>
public sealed class TagReviewRow : ViewModelBase
{
    private bool _isBulkSelected;

    public TagReviewRow(MediaTagReviewItem item) => Item = item;

    public MediaTagReviewItem Item { get; }
    public MediaTagEvidence Evidence => Item.Evidence;
    public bool IsBulkSelected { get => _isBulkSelected; set => Set(ref _isBulkSelected, value); }
    public string DisplayName => L.T(Evidence.DisplayName);
    public string Group => Evidence.Group;
    public string FileName => Item.FileName;
    public string Path => Item.Path;
    public string StateDisplay => Item.StateDisplay;
    public string GeneralState => Item.JudgmentDecision switch
    {
        MediaTagJudgmentDecision.Confirmed => L.T("確認済み"), MediaTagJudgmentDecision.Rejected => L.T("却下"),
        MediaTagJudgmentDecision.Ignored => L.T("対象外"), MediaTagJudgmentDecision.Unreviewed => L.T("未確認の提案タグ"),
        _ => L.T("判断状態を確認できません")
    };
    public string GeneralExplanation => Evidence.Source == MediaTagSource.EmbeddingZeroShot
        ? L.T("AIの提案です。確認するまで人が確定したタグではありません。")
        : L.T("提案元を確認してください。まだ人が確定したタグではありません。");
    public string ScoreDisplay => Item.ScoreDisplay;
    public string CoverageDisplay => Item.CoverageDisplay;
    public string RepresentativeTimestampDisplay => Item.RepresentativeTimestampSeconds is { } timestamp ? $"{timestamp:F2}s" : "representative timestamp unavailable";
    public string AutomationName => $"{DisplayName}: {FileName}";
    public string AutomationHelpText => GeneralState;
    public string SuggestedExplanation => Evidence.Source == MediaTagSource.EmbeddingZeroShot
        ? L.T("Suggested Tagは既存LLM-jp CLIP Visual Embeddingと固定taxonomy textのCosine similarityによる候補です。Human-confirmed Tagではありません。")
        : L.T("Tag sourceを確認してください。Human-confirmed Tagではありません。");
}
