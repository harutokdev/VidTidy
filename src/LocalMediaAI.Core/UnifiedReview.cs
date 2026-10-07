namespace LocalMediaAI.Core;

/// <summary>
/// Phase 5.6 review projection type. The enum deliberately excludes Person because Phase 5.5 Gate B is forbidden.
/// </summary>
public enum UnifiedReviewType { ExactDuplicate, NearDuplicate, ContentSimilar, SuggestedTag }

public enum UnifiedReviewFilter
{
    All,
    Unreviewed,
    Reviewed,
    ExactDuplicate,
    NearDuplicate,
    ContentSimilar,
    SuggestedTag,
    AvailableSourceOnly,
    MissingOrOfflineSource
}

public enum UnifiedReviewSort { Priority, CreatedNewest, MediaSizeDescending }
public enum ReviewEvidenceState { Current, Stale, SourceUnavailable }

/// <summary>Actions are intentionally vocabulary-level operations, not file operations.</summary>
public enum UnifiedReviewAction
{
    ConfirmDuplicate,
    ConfirmNearDuplicate,
    KeepBoth,
    Preferred,
    Ignore,
    NotDuplicate,
    Related,
    NotRelated,
    ConfirmTag,
    RejectTag,
    ResetToUnreviewed
}

/// <summary>
/// Read-only query projection over relation-specific source-of-truth tables. ReviewItemId is stable and never a UI row identity.
/// ScoreText preserves the source metric and is never a cross-type probability or confidence.
/// </summary>
public sealed record UnifiedReviewItem(
    string ReviewItemId,
    UnifiedReviewType ReviewType,
    string SourceStableId,
    Guid EvidenceId,
    Guid? PrimaryMediaFileId,
    Guid? PrimaryMediaContentId,
    Guid? SecondaryMediaFileId,
    Guid? SecondaryMediaContentId,
    string InvolvedMediaFileIds,
    string InvolvedMediaContentIds,
    ReviewEvidenceState EvidenceState,
    string SourceAvailability,
    string HumanDecision,
    string? HumanNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ReviewPriority,
    string PriorityReason,
    string EvidenceKind,
    string EvidenceSummary,
    string ScoreText,
    string ProvenanceSummary,
    string? RepresentativeFrameRelativePath,
    double? RepresentativeTimestampSeconds,
    long MediaSizeBytes,
    int InvolvedMediaCount,
    string? DisplayName)
{
    public string ReviewTypeDisplay => ReviewType switch
    {
        UnifiedReviewType.ExactDuplicate => "Exact Duplicate — Full SHA-256 evidence",
        UnifiedReviewType.NearDuplicate => "Near Duplicate — temporal perceptual evidence",
        UnifiedReviewType.ContentSimilar => "Content Similarity — non-transitive pair evidence",
        UnifiedReviewType.SuggestedTag => "Suggested Tag — AI evidence; human confirmation required",
        _ => ReviewType.ToString()
    };

    public string EvidenceStateDisplay => EvidenceState switch
    {
        ReviewEvidenceState.Current => "Current evidence",
        ReviewEvidenceState.Stale => "Stale evidence — retained for audit, not a current candidate",
        ReviewEvidenceState.SourceUnavailable => "Source unavailable — evidence and human decision retained",
        _ => EvidenceState.ToString()
    };

    public bool IsActionable => EvidenceState == ReviewEvidenceState.Current;
}

public sealed record UnifiedReviewQuery(
    Guid? LibraryRootId,
    UnifiedReviewFilter Filter = UnifiedReviewFilter.All,
    UnifiedReviewSort Sort = UnifiedReviewSort.Priority,
    int Offset = 0,
    int Limit = 100);

public sealed record UnifiedReviewDecisionRequest(
    string ReviewItemId,
    UnifiedReviewAction Action,
    string? Note = null,
    Guid? PreferredMediaFileId = null);

public sealed record UnifiedReviewDecisionResult(
    string ReviewItemId,
    UnifiedReviewType ReviewType,
    string PreviousDecision,
    string CurrentDecision,
    DateTimeOffset UpdatedAt);

public sealed record UnifiedReviewDecisionHistoryEntry(
    Guid ReviewDecisionHistoryId,
    string ReviewItemId,
    UnifiedReviewType ReviewType,
    string SourceStableId,
    Guid EvidenceId,
    string EventKind,
    string? PreviousDecision,
    string CurrentDecision,
    string? Note,
    DateTimeOffset OccurredAt);

public sealed record UnifiedReviewResumeState(
    UnifiedReviewFilter Filter,
    UnifiedReviewSort Sort,
    int Offset,
    string? AnchorReviewItemId,
    DateTimeOffset UpdatedAt)
{
    public static readonly UnifiedReviewResumeState Default = new(UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, null, DateTimeOffset.MinValue);
}

public static class UnifiedReviewPolicy
{
    public static string CreateReviewItemId(UnifiedReviewType reviewType, Guid sourceId) => reviewType switch
    {
        UnifiedReviewType.ExactDuplicate => "exact:" + sourceId.ToString("D"),
        UnifiedReviewType.NearDuplicate => "near:" + sourceId.ToString("D"),
        UnifiedReviewType.ContentSimilar => "content:" + sourceId.ToString("D"),
        UnifiedReviewType.SuggestedTag => "tag:" + sourceId.ToString("D"),
        _ => throw new ArgumentOutOfRangeException(nameof(reviewType), reviewType, null)
    };

    public static bool TryParseReviewItemId(string? reviewItemId, out UnifiedReviewType reviewType, out Guid sourceId)
    {
        reviewType = default;
        sourceId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(reviewItemId)) return false;
        var separator = reviewItemId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !Guid.TryParse(reviewItemId[(separator + 1)..], out sourceId)) return false;
        reviewType = reviewItemId[..separator] switch
        {
            "exact" => UnifiedReviewType.ExactDuplicate,
            "near" => UnifiedReviewType.NearDuplicate,
            "content" => UnifiedReviewType.ContentSimilar,
            "tag" => UnifiedReviewType.SuggestedTag,
            _ => default
        };
        return reviewItemId[..separator] is "exact" or "near" or "content" or "tag";
    }

    public static IReadOnlyList<UnifiedReviewAction> GetAllowedActions(UnifiedReviewType reviewType) => reviewType switch
    {
        UnifiedReviewType.ExactDuplicate => [UnifiedReviewAction.ConfirmDuplicate, UnifiedReviewAction.KeepBoth, UnifiedReviewAction.Preferred, UnifiedReviewAction.Ignore, UnifiedReviewAction.ResetToUnreviewed],
        UnifiedReviewType.NearDuplicate => [UnifiedReviewAction.ConfirmNearDuplicate, UnifiedReviewAction.KeepBoth, UnifiedReviewAction.NotDuplicate, UnifiedReviewAction.Ignore, UnifiedReviewAction.ResetToUnreviewed],
        UnifiedReviewType.ContentSimilar => [UnifiedReviewAction.Related, UnifiedReviewAction.NotRelated, UnifiedReviewAction.KeepBoth, UnifiedReviewAction.Ignore, UnifiedReviewAction.ResetToUnreviewed],
        UnifiedReviewType.SuggestedTag => [UnifiedReviewAction.ConfirmTag, UnifiedReviewAction.RejectTag, UnifiedReviewAction.Ignore, UnifiedReviewAction.ResetToUnreviewed],
        _ => Array.Empty<UnifiedReviewAction>()
    };

    public static bool IsAllowed(UnifiedReviewType reviewType, UnifiedReviewAction action) => GetAllowedActions(reviewType).Contains(action);

    public static int ComputePriority(UnifiedReviewType reviewType, string decision, ReviewEvidenceState state)
    {
        var stateOffset = state == ReviewEvidenceState.Current ? 0 : state == ReviewEvidenceState.Stale ? 100 : 200;
        var decisionOffset = string.Equals(decision, "Unreviewed", StringComparison.Ordinal) ? 0 : 30;
        var typeOffset = reviewType switch
        {
            UnifiedReviewType.ExactDuplicate => 0,
            UnifiedReviewType.NearDuplicate => 5,
            UnifiedReviewType.ContentSimilar => 10,
            UnifiedReviewType.SuggestedTag => 15,
            _ => 20
        };
        return stateOffset + decisionOffset + typeOffset;
    }

    public static string PriorityReason(UnifiedReviewType reviewType, string decision, ReviewEvidenceState state) =>
        state == ReviewEvidenceState.Current && string.Equals(decision, "Unreviewed", StringComparison.Ordinal)
            ? $"Current {reviewType} evidence is unreviewed; priority is not confidence."
            : state == ReviewEvidenceState.Current
                ? $"Human decision already exists; priority is lower than unreviewed current evidence."
                : $"{state} evidence is retained for audit and ordered after current candidates.";

    public static string ResumeSettingKey => "unified-review-resume-v1";
}
