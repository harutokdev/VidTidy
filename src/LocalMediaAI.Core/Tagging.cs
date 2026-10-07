using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LocalMediaAI.Core;

/// <summary>Tag provenance. Phase 5.4 emits Human and EmbeddingZeroShot only; the other values reserve durable interoperability states.</summary>
public enum MediaTagSource { Human, Metadata, EmbeddingZeroShot, RuleBased, Imported }

/// <summary>Suggested is AI evidence only. Confirmed/Rejected/Ignored are always separate Human judgments.</summary>
public enum MediaTagEvidenceStatus { Suggested, NotSuggested, InsufficientEvidence, Failed }
public enum MediaTagJudgmentDecision { Unreviewed, Confirmed, Rejected, Ignored }
public enum TagTextEmbeddingStatus { Completed, Failed }

public sealed record TagDefinition(
    string TagId,
    string DisplayName,
    string Group,
    string RawText,
    string DescriptiveText);

public sealed record TagTextEmbedding(
    Guid TagTextEmbeddingId,
    string TaxonomyVersion,
    string TagId,
    string DisplayName,
    string Group,
    string TextRepresentationVersion,
    string Text,
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string Dtype,
    int Dimension,
    bool Normalized,
    byte[]? VectorFloat32,
    TagTextEmbeddingStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MediaTagEvidence(
    Guid MediaTagEvidenceId,
    Guid MediaFileId,
    Guid MediaContentId,
    Guid? RepresentativeVisualSampleId,
    string TaxonomyVersion,
    string TagId,
    string DisplayName,
    string Group,
    MediaTagSource Source,
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string Dtype,
    int Dimension,
    string SourceSamplingVersion,
    string TextRepresentationVersion,
    string AggregationVersion,
    string ThresholdVersion,
    string ScoreMetric,
    double MaximumScore,
    double MeanScore,
    int MatchedSampleCount,
    int CompletedSampleCount,
    int ExpectedSampleCount,
    double CoverageRatio,
    string SourceEmbeddingDigest,
    MediaTagEvidenceStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record MediaTagJudgment(
    Guid MediaTagJudgmentId,
    Guid MediaTagEvidenceId,
    MediaTagJudgmentDecision Decision,
    MediaTagSource Source,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Manual tags are MediaFile-scoped user metadata, deliberately independent from mutable MediaContent.</summary>
public sealed record ManualMediaTag(
    Guid ManualMediaTagId,
    Guid MediaFileId,
    string CanonicalTag,
    string DisplayTag,
    MediaTagSource Source,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MediaTagReviewItem(
    MediaTagEvidence Evidence,
    MediaTagJudgmentDecision JudgmentDecision,
    string? JudgmentNote,
    string FileName,
    string Path,
    PresenceState PresenceState,
    double? RepresentativeTimestampSeconds,
    string? RepresentativeFrameRelativePath)
{
    public string StateDisplay => JudgmentDecision switch
    {
        MediaTagJudgmentDecision.Confirmed => "Confirmed (Human)",
        MediaTagJudgmentDecision.Rejected => "Rejected (Human)",
        MediaTagJudgmentDecision.Ignored => "Ignored (Human)",
        _ => "Suggested (AI candidate)"
    };

    public string ScoreDisplay => $"Tag Match Score: {Evidence.MaximumScore:F2} — Cosine similarity; Higher = stronger visual/text match";
    public string CoverageDisplay => $"Coverage: {Evidence.CompletedSampleCount}/{Evidence.ExpectedSampleCount}; matched {Evidence.MatchedSampleCount}";
    public string SourceDisplay => Evidence.Source.ToString();
}

public sealed record MediaTagSummary(
    Guid MediaFileId,
    IReadOnlyList<string> ConfirmedOrManualTags,
    IReadOnlyList<string> SuggestedTags,
    IReadOnlyList<string> RejectedTags);

public sealed record MediaTagSourceEmbeddings(
    int ExpectedSampleCount,
    IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> CompletedEmbeddings);

public sealed record TaggingAggregation(
    double MaximumScore,
    double MeanScore,
    int MatchedSampleCount,
    int CompletedSampleCount,
    int ExpectedSampleCount,
    double CoverageRatio,
    Guid? RepresentativeVisualSampleId,
    string SourceEmbeddingDigest)
{
    public bool HasCompleteCoverage => ExpectedSampleCount > 0 && CompletedSampleCount == ExpectedSampleCount && CoverageRatio >= 1d;
}

/// <summary>Fixed Phase 5.4 taxonomy. It is intentionally flat and versioned before scoring.</summary>
public static class TaggingTaxonomy
{
    public const string TaxonomyVersion = "phase54-visual-taxonomy-ja-v1";
    public const string RawTextRepresentationVersion = "tag-text-raw-ja-v1";
    public const string DescriptiveTextRepresentationVersion = "tag-text-descriptive-ja-v1";

    private static readonly IReadOnlyList<TagDefinition> Definitions =
    [
        new("indoor", "屋内", "Environment", "屋内", "屋内の場面が映っている"),
        new("outdoor", "屋外", "Environment", "屋外", "屋外の場面が映っている"),
        new("night", "夜", "Environment", "夜", "夜または夜景の場面が映っている"),
        new("nature", "自然", "Environment", "自然", "自然の風景が映っている"),
        new("ocean", "海", "Environment", "海", "海または海岸が映っている"),
        new("mountain", "山", "Environment", "山", "山の風景が映っている"),
        new("city", "街", "Environment", "街", "街または都市の風景が映っている"),
        new("building", "建物", "Environment", "建物", "建物が映っている"),
        new("person", "人", "Object", "人", "人が映っている"),
        new("car", "車", "Object", "車", "車が映っている"),
        new("dog", "犬", "Object", "犬", "犬が映っている"),
        new("cat", "猫", "Object", "猫", "猫が映っている"),
        new("train", "電車", "Object", "電車", "電車が映っている"),
        new("food", "食事", "Activity", "食事", "食事または料理が映っている"),
        new("sports", "スポーツ", "Activity", "スポーツ", "スポーツの場面が映っている")
    ];

    public static IReadOnlyList<TagDefinition> All => Definitions;

    public static TagDefinition GetRequired(string tagId) => Definitions.FirstOrDefault(tag => string.Equals(tag.TagId, tagId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown fixed taxonomy tag: {tagId}");

    public static string TextFor(TagDefinition tag, string textRepresentationVersion) => textRepresentationVersion switch
    {
        RawTextRepresentationVersion => tag.RawText,
        DescriptiveTextRepresentationVersion => tag.DescriptiveText,
        _ => throw new InvalidOperationException($"Unsupported tag text representation version: {textRepresentationVersion}")
    };
}

/// <summary>Predeclared high-precision media-level profile. Values are fixed before evaluation and versioned in every evidence row.</summary>
public sealed record TaggingVerificationProfile(
    string ThresholdVersion,
    string AggregationVersion,
    string TextRepresentationVersion,
    double MinimumSampleCosine,
    double MinimumMaximumCosine,
    double MinimumMeanCosine,
    int MinimumMatchedSamples)
{
    public static readonly TaggingVerificationProfile DescriptiveHighPrecisionV1 = new(
        "tagging-embedding-zero-shot-v1-high-precision",
        "media-sample-max-mean-v1",
        TaggingTaxonomy.DescriptiveTextRepresentationVersion,
        MinimumSampleCosine: 0.24d,
        MinimumMaximumCosine: 0.27d,
        MinimumMeanCosine: 0.18d,
        MinimumMatchedSamples: 2);

    public bool IsSuggested(TaggingAggregation aggregation) =>
        aggregation.HasCompleteCoverage &&
        aggregation.MaximumScore >= MinimumMaximumCosine &&
        aggregation.MeanScore >= MinimumMeanCosine &&
        aggregation.MatchedSampleCount >= MinimumMatchedSamples;
}

public static class TagNormalization
{
    public static string CanonicalizeManualTag(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        normalized = string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(value), "Tag must be between 1 and 120 characters after normalization.");
        // Invariant lower-casing folds Latin letters embedded in Unicode strings while leaving Japanese unchanged.
        return normalized.ToLowerInvariant();
    }

    public static string Display(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        normalized = string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(value), "Tag must be between 1 and 120 characters after normalization.");
        return normalized;
    }
}

/// <summary>CPU-only scoring over persisted normalized vectors. It performs no model inference and no Original Media access.</summary>
public static class TaggingVectors
{
    public const string ScoreMetric = "normalized-embedding-exact-cosine-v1";

    public static TaggingAggregation Aggregate(
        IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> completedEmbeddings,
        int expectedSampleCount,
        TagTextEmbedding textEmbedding,
        TaggingVerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(completedEmbeddings);
        ArgumentNullException.ThrowIfNull(textEmbedding);
        ArgumentNullException.ThrowIfNull(profile);
        if (textEmbedding.Status != TagTextEmbeddingStatus.Completed || !textEmbedding.Normalized || textEmbedding.VectorFloat32 is null)
            throw new InvalidOperationException("A completed normalized tag text embedding is required.");
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSampleCount);
        var text = ContentSimilarityVectors.Decode(textEmbedding.VectorFloat32, textEmbedding.Dimension);
        if (completedEmbeddings.Count == 0)
            return new(0d, 0d, 0, 0, expectedSampleCount, 0d, null, SourceDigest(completedEmbeddings));

        var scores = new List<(double Score, Guid SampleId)>();
        foreach (var entry in completedEmbeddings)
        {
            if (entry.Embedding.Status != VisualEmbeddingStatus.Completed || !entry.Embedding.Normalized || entry.Embedding.VectorFloat32 is null || entry.Embedding.Dimension != text.Length)
                throw new InvalidOperationException("Completed normalized visual embeddings with matching dimension are required.");
            scores.Add((ContentSimilarityVectors.Cosine(ContentSimilarityVectors.Decode(entry.Embedding.VectorFloat32, entry.Embedding.Dimension), text), entry.Sample.VisualSampleId));
        }
        var representative = scores.OrderByDescending(score => score.Score).ThenBy(score => score.SampleId).First();
        var coverage = expectedSampleCount == 0 ? 0d : Math.Clamp(completedEmbeddings.Count / (double)expectedSampleCount, 0d, 1d);
        return new(representative.Score, scores.Average(score => score.Score), scores.Count(score => score.Score >= profile.MinimumSampleCosine), completedEmbeddings.Count, expectedSampleCount, coverage, representative.SampleId, SourceDigest(completedEmbeddings));
    }

    private static string SourceDigest(IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> entries)
    {
        var canonical = string.Join("\n", entries.OrderBy(entry => entry.Sample.SampleIndex).ThenBy(entry => entry.Sample.VisualSampleId)
            .Select(entry => string.Join("|", entry.Sample.SampleIndex.ToString(CultureInfo.InvariantCulture), entry.Sample.VisualSampleId.ToString("D"), entry.Embedding.VisualEmbeddingId.ToString("D"), entry.Embedding.CacheKey, Convert.ToHexString(SHA256.HashData(entry.Embedding.VectorFloat32!)))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
