using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalMediaAI.Core;

public enum RootAvailability { Unknown, Online, Offline }
public enum RootRegistrationState { Active, Unregistered }
public enum LibraryRootKind { Folder, Drive }
public enum PresenceState { Present, Recycled, PermanentlyDeleted, Missing, OfflineRoot, AccessDenied, Unknown }
public enum RetentionTargetType { File, Folder, Drive, Root }
public enum RetentionCause { FileMissing, FolderMissing, DriveOffline, ExplicitUnregister }
public enum RetentionLifecycleState { Retained, CleanupCommitted, Completed }
public enum AnalysisStatus { Pending, Running, Completed, Partial, Failed, Cancelled }
public enum ArtifactDurability { Durable, RebuildableCache, Temporary }
public enum JobState { Pending, Running, Waiting, Paused, RetryWaiting, Completed, Partial, Failed, Cancelled }
public enum JobReason { None, ResourceCpu, ResourceRam, ResourceGpu, ResourceVram, ResourceIo, DatabaseBusy, Dependency, Backoff, RootOffline, MediaUnavailable, AppRestartRecovery, CancelRequested }
public enum JobType { Scan, Metadata, Thumbnail, FullHash, VisualSampling, VisualEmbedding, NearDuplicateFingerprint, ContentSimilarityRepresentation, TagSuggestion, ProtectedSourceOrchestration }
[Flags]
public enum ProtectedSourceStageEligibility
{
    None = 0,
    Metadata = 1,
    Thumbnail = 2,
    VisualSampling = 4,
    NearDuplicateFingerprint = 8,
    FullHash = 16
}
public enum VisualSampleStatus { Pending, Completed, Failed }
public enum VisualEmbeddingStatus { Pending, Completed, Failed }
public enum DuplicateRelationType { Exact, NearDuplicate, ContentSimilar }
public enum DuplicateJudgmentDecision { Unreviewed, ConfirmedDuplicate, KeepBoth, Preferred, Ignored, NotDuplicate }
/// <summary>ContentSimilar edge専用の人間判断です。Duplicate/Preferredの意味を流用しません。</summary>
public enum ContentSimilarityJudgmentDecision { Unreviewed, Related, NotRelated, KeepBoth, Ignored }

public sealed record LibraryRoot(
    Guid LibraryRootId,
    string ConfiguredPath,
    string NormalizedPath,
    string DisplayName,
    string? VolumeIdentity,
    RootAvailability Availability,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastScanAt,
    RootRegistrationState RegistrationState = RootRegistrationState.Active,
    LibraryRootKind RootKind = LibraryRootKind.Folder,
    string? RootIdentity = null,
    DateTimeOffset? UnregisteredAt = null,
    long RegistrationRevision = 0);

public sealed record RetentionSettings(long? FileDays, long? FolderDays, long? DriveDays)
{
    // DateTimeOffset's full representable date range, expressed as whole days.
    // This is an implementation bound rather than a product-selected retention cap.
    public const long MaximumSafeDays = 3_652_058;
    public static readonly RetentionSettings Defaults = new(30, 60, 365);

    public static long? Validate(long? days, string parameterName)
    {
        if (days is null) return null;
        if (days is <= 0 or > MaximumSafeDays)
            throw new ArgumentOutOfRangeException(parameterName, days, $"Retention days must be positive and no greater than {MaximumSafeDays}, or Unlimited.");
        return days;
    }

    public RetentionSettings Validate() => new(
        Validate(FileDays, nameof(FileDays)),
        Validate(FolderDays, nameof(FolderDays)),
        Validate(DriveDays, nameof(DriveDays)));
}

public sealed record RetentionRecord(
    RetentionTargetType TargetType,
    string TargetId,
    RetentionCause Cause,
    string CauseScopeId,
    DateTimeOffset AbsenceStartedAt,
    DateTimeOffset LastAuthoritativeConfirmationAt,
    RetentionLifecycleState State,
    DateTimeOffset? CleanupCommittedAt,
    DateTimeOffset? CompletedAt);

public enum RootObservationKind { Online, DriveOffline, RootFolderMissing, Unknown }
public sealed record ScannedDirectory(string FullPath, string NormalizedPath, string? VolumeIdentity, string? DirectoryIdentity);
public sealed record ScanAuthoritySnapshot(
    IReadOnlyList<ScannedDirectory> AccessibleDirectories,
    IReadOnlyList<string> UnknownDirectoryPrefixes,
    string? VolumeIdentity,
    string? RootDirectoryIdentity,
    bool CompletedAuthoritatively);
public sealed record RetentionCleanupResult(int Candidates, int Committed, int Completed, int Rescued, int DeferredUnknown);

public sealed record MediaContent(
    Guid MediaContentId,
    string? CanonicalSha256,
    long ByteLength,
    string IdentityState,
    DateTimeOffset CreatedAt);

public sealed record MediaFile(
    Guid MediaFileId,
    Guid LibraryRootId,
    Guid MediaContentId,
    string Path,
    string NormalizedPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    string? VolumeIdentity,
    string? FileIdentity,
    string? QuickFingerprint,
    string? FullSha256,
    PresenceState PresenceState,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Read-only completeness for visual embeddings that belong to the current media-content generation
/// and the requested semantic model/execution profile. Historical failed rows are intentionally excluded.
/// </summary>
public sealed record SemanticEmbeddingCompleteness(
    int EligibleSampleCount,
    int CompletedEmbeddingCount,
    int MissingEmbeddingCount);

/// <summary>The authoritative outcome of the latest persisted scan publication for an active root registration lifecycle.</summary>
public sealed record RootScanCurrentness(Guid LibraryRootId, long RegistrationRevision, string State, int ErrorCount);

public sealed record Analysis(
    Guid AnalysisId,
    Guid MediaContentId,
    string AnalysisType,
    AnalysisStatus Status,
    string ProcessorName,
    string ProcessorVersion,
    string? ModelName,
    string? ModelVersion,
    string ConfigJson,
    string ConfigHash,
    string InputIdentityHash,
    string ProcessingVersion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record Artifact(
    Guid ArtifactId,
    Guid AnalysisId,
    string ArtifactKind,
    string RelativePath,
    string Sha256,
    long ByteSize,
    string MimeType,
    ArtifactDurability DurabilityClass,
    DateTimeOffset CreatedAt);

public sealed record JobProgress(long Current, long Total, string Unit, bool IsIndeterminate);

public sealed record Job(
    Guid JobId,
    JobType JobType,
    JobState State,
    string Phase,
    JobReason Reason,
    string PayloadJson,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset? NotBefore,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? HeartbeatAt,
    JobProgress Progress,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record JobEvent(Guid JobEventId, Guid JobId, DateTimeOffset OccurredAt, string EventName, string? DetailJson);

public sealed record MediaMetadata(
    Guid MediaFileId,
    string? ContainerFormat,
    double? DurationSeconds,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? FpsText,
    long? Bitrate,
    int AudioStreamCount,
    int SubtitleStreamCount,
    string RawSummaryJson,
    DateTimeOffset UpdatedAt);

public sealed record VisualSamplingPolicy(
    string Version,
    double IntervalSeconds,
    int MinimumSamples,
    int MaximumSamples,
    double EndMarginSeconds = 0d)
{
    public static readonly VisualSamplingPolicy FixedIntervalV1 = new("fixed-interval-v1", 10d, 1, 12);
    // Phase 3.5 experiment candidate. The 24-sample cap preserves the same 120-second
    // bounded coverage as v1 while sampling at twice the temporal density.
    public static readonly VisualSamplingPolicy FixedIntervalV2 = new("fixed-interval-v2", 5d, 1, 24, 0.25d);

    public IReadOnlyList<double> CreateTimestamps(double? durationSeconds)
    {
        if (IntervalSeconds <= 0 || MinimumSamples < 1 || MaximumSamples < MinimumSamples || EndMarginSeconds < 0)
            throw new InvalidOperationException("Visual sampling policy is invalid.");
        if (durationSeconds is not { } duration || duration <= 0)
            return [0d];
        var timestamps = new List<double>();
        for (var timestamp = 0d; timestamp < duration - EndMarginSeconds && timestamps.Count < MaximumSamples; timestamp += IntervalSeconds)
            timestamps.Add(timestamp);
        if (timestamps.Count == 0) timestamps.Add(0d);
        return timestamps;
    }
}

public sealed record VisualSample(
    Guid VisualSampleId,
    Guid MediaFileId,
    Guid MediaContentId,
    string InputIdentityHash,
    int SampleIndex,
    double RequestedTimestampSeconds,
    double PresentationTimestampSeconds,
    string SamplingVersion,
    string CacheKey,
    string? FrameRelativePath,
    string? FrameSha256,
    long? FrameByteSize,
    VisualSampleStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ModelProvenance(
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string ArtifactManifestSha256,
    string License,
    bool RuntimeNetworkAllowed,
    DateTimeOffset RecordedAt);

public sealed record VisualEmbedding(
    Guid VisualEmbeddingId,
    Guid VisualSampleId,
    string InputIdentityHash,
    string CacheKey,
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string SamplingVersion,
    string Dtype,
    int Dimension,
    bool Normalized,
    byte[]? VectorFloat32,
    VisualEmbeddingStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Semantic Search ranking専用の最小read projection。完全embedding aggregateをmaterializeしない。</summary>
public sealed record SemanticSearchEmbeddingRow(
    Guid VisualEmbeddingId,
    Guid VisualSampleId,
    Guid MediaFileId,
    Guid LibraryRootId,
    string FileName,
    string Path,
    double PresentationTimestampSeconds,
    string? FrameRelativePath,
    double? DurationSeconds,
    byte[] VectorFloat32);

public sealed record FileHashEvidence(
    Guid FileHashEvidenceId,
    Guid MediaFileId,
    Guid MediaContentId,
    string Algorithm,
    string Digest,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteTimeUtc,
    string ProcessingVersion,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset ComputedAt);

public sealed record DuplicateGroup(
    Guid DuplicateGroupId,
    DuplicateRelationType RelationType,
    string EvidenceAlgorithm,
    string EvidenceDigest,
    long ByteLength,
    string ProcessingVersion,
    DateTimeOffset VerifiedAt,
    int CurrentMemberCount,
    DuplicateJudgmentDecision ReviewDecision = DuplicateJudgmentDecision.Unreviewed,
    string? ReviewNote = null);

public sealed record DuplicateMember(
    Guid DuplicateGroupId,
    Guid MediaFileId,
    Guid MediaContentId,
    Guid FileHashEvidenceId,
    DateTimeOffset AddedAt);

public sealed record DuplicateComparisonMember(
    DuplicateMember Membership,
    MediaFile Media,
    MediaMetadata? Metadata,
    FileHashEvidence Evidence,
    DuplicateJudgmentDecision JudgmentDecision = DuplicateJudgmentDecision.Unreviewed,
    string? JudgmentNote = null)
{
    public string FullHashStatusDisplay => string.Equals(Evidence.Status, "Verified", StringComparison.Ordinal) ? $"SHA-256 verified: {Evidence.Digest}" : "SHA-256 not verified";
    public string ResolutionDisplay => Metadata?.Width is { } width && Metadata.Height is { } height ? $"{width}×{height}" : "Resolution unavailable";
    public string DurationDisplay => Metadata?.DurationSeconds is { } duration ? $"{duration:F2}s" : "Duration unavailable";
}

public sealed record NearDuplicateComparisonMember(
    Guid DuplicateGroupId,
    Guid MediaFileId,
    Guid MediaContentId,
    Guid NearDuplicateEvidenceId,
    MediaFile Media,
    MediaMetadata? Metadata,
    NearDuplicateEvidence Evidence,
    DuplicateJudgmentDecision JudgmentDecision = DuplicateJudgmentDecision.Unreviewed,
    string? JudgmentNote = null)
{
    public string ResolutionDisplay => Metadata?.Width is { } width && Metadata.Height is { } height ? $"{width}×{height}" : "Resolution unavailable";
    public string DurationDisplay => Metadata?.DurationSeconds is { } duration ? $"{duration:F2}s" : "Duration unavailable";
    public string VisualEvidenceDisplay => $"{Evidence.EvidenceAlgorithm} | {Evidence.MatchedSampleCount}/{Evidence.ComparedSampleCount} matched | mean Hamming {Evidence.MeanHammingDistance:F2}/64 | score {Evidence.VisualMatchScore:P1}";
}

public sealed record DuplicateJudgment(
    Guid DuplicateJudgmentId,
    Guid DuplicateGroupId,
    Guid? MediaFileId,
    DuplicateJudgmentDecision Decision,
    string Origin,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Phase 5.2: temporal perceptual-frame fingerprint provenance. A Completed row is a reusable
/// local observation only; it is never Exact evidence and it does not imply a Near relation.
/// </summary>
public sealed record NearDuplicateFrame(
    Guid NearDuplicateFrameId,
    Guid MediaFileId,
    Guid MediaContentId,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteTimeUtc,
    int SampleIndex,
    double NormalizedPosition,
    double RequestedTimestampSeconds,
    double PresentationTimestampSeconds,
    string SamplingVersion,
    string FingerprintAlgorithm,
    string? FingerprintHex,
    string? FrameRelativePath,
    string? FrameSha256,
    long? FrameByteSize,
    AnalysisStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Phase 5.2: pair-level Near Duplicate verification provenance. The pair is canonicalized by
/// caller before persistence. Only Status=Verified can create a NearDuplicate group.
/// </summary>
public sealed record NearDuplicateEvidence(
    Guid NearDuplicateEvidenceId,
    Guid MediaFileAId,
    Guid MediaContentAId,
    Guid MediaFileBId,
    Guid MediaContentBId,
    string EvidenceAlgorithm,
    string SamplingVersion,
    string ThresholdVersion,
    string EvidenceDigest,
    double DurationASeconds,
    double DurationBSeconds,
    double DurationDifferenceRatio,
    int ComparedSampleCount,
    int MatchedSampleCount,
    int LongestConsecutiveMatchCount,
    double MeanHammingDistance,
    int MaxHammingDistance,
    double VisualMatchScore,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset VerifiedAt);

public sealed record NearDuplicateFrameMatch(
    Guid NearDuplicateEvidenceId,
    int SampleIndex,
    Guid NearDuplicateFrameAId,
    Guid NearDuplicateFrameBId,
    int HammingDistance,
    bool IsMatch);

/// <summary>A video whose near-duplicate frames have no current <see cref="NearDuplicatePreparation"/>; FramesVersion identifies the frames it must be made from.</summary>
public sealed record NearDuplicatePreparationPending(Guid MediaFileId, Guid MediaContentId, long SizeBytes, DateTimeOffset LastWriteTimeUtc, string FramesVersion);

public sealed record NearDuplicateCandidate(
    Guid MediaFileAId,
    Guid MediaContentAId,
    Guid MediaFileBId,
    Guid MediaContentBId,
    double DurationASeconds,
    double DurationBSeconds);

/// <summary>
/// Phase 5.3: existing completed visual embeddingsから再構成できるmedia-level mean representationです。
/// Vectorはrebuildable cacheであり、visual_embeddingがsource of truthです。
/// </summary>
public sealed record ContentSimilarityRepresentation(
    Guid ContentSimilarityRepresentationId,
    Guid MediaFileId,
    Guid MediaContentId,
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string SourceSamplingVersion,
    string AggregationVersion,
    string SourceEmbeddingDigest,
    string Dtype,
    int Dimension,
    bool Normalized,
    int AvailableSampleCount,
    int ExpectedSampleCount,
    double CoverageRatio,
    byte[]? VectorFloat32,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>ContentSimilar pair verificationのためのcurrent media-level candidateです。</summary>
public sealed record ContentSimilarityCandidate(
    Guid MediaFileAId,
    Guid MediaContentAId,
    Guid ContentSimilarityRepresentationAId,
    Guid MediaFileBId,
    Guid MediaContentBId,
    Guid ContentSimilarityRepresentationBId,
    double MeanCosineSimilarity);

/// <summary>
/// Phase 5.3: non-transitive ContentSimilar pair/graph edge evidence。Verified edgeだけがreview queueへ表示されます。
/// </summary>
public sealed record ContentSimilarityEvidence(
    Guid ContentSimilarityEvidenceId,
    Guid MediaFileAId,
    Guid MediaContentAId,
    Guid ContentSimilarityRepresentationAId,
    Guid MediaFileBId,
    Guid MediaContentBId,
    Guid ContentSimilarityRepresentationBId,
    Guid RepresentativeVisualSampleAId,
    Guid RepresentativeVisualSampleBId,
    string ModelName,
    string ModelRevision,
    string ProcessorVersion,
    string SourceSamplingVersion,
    string AggregationVersion,
    string ThresholdVersion,
    string SimilarityMetric,
    double MeanCosineSimilarity,
    double SampleAwareCosineSimilarity,
    double RepresentativeCosineSimilarity,
    int CoverageSampleCountA,
    int CoverageExpectedCountA,
    int CoverageSampleCountB,
    int CoverageExpectedCountB,
    string EvidenceDigest,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset VerifiedAt);

public sealed record ContentSimilarityComparisonMember(
    Guid ContentSimilarityEvidenceId,
    Guid MediaFileId,
    Guid MediaContentId,
    bool IsSideA,
    MediaFile Media,
    MediaMetadata? Metadata,
    VisualSample RepresentativeSample,
    ContentSimilarityEvidence Evidence,
    ContentSimilarityJudgmentDecision JudgmentDecision = ContentSimilarityJudgmentDecision.Unreviewed,
    string? JudgmentNote = null)
{
    public string ResolutionDisplay => Metadata?.Width is { } width && Metadata.Height is { } height ? $"{width}×{height}" : "Resolution unavailable";
    public string DurationDisplay => Metadata?.DurationSeconds is { } duration ? $"{duration:F2}s" : "Duration unavailable";
    public string SimilarityDisplay => $"Content Similarity: {Evidence.SampleAwareCosineSimilarity:F2} — Cosine similarity; higher = more visually/semantically similar";
}

public sealed record ContentSimilarityJudgment(
    Guid ContentSimilarityJudgmentId,
    Guid ContentSimilarityEvidenceId,
    ContentSimilarityJudgmentDecision Decision,
    string Origin,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ContentSimilaritySourceEmbeddings(
    int ExpectedSampleCount,
    IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> CompletedEmbeddings);

public sealed record SemanticSearchCandidate(
    Guid MediaFileId,
    string FileName,
    string Path,
    double TimestampSeconds,
    double Score,
    Guid VisualSampleId,
    Guid VisualEmbeddingId,
    string? MatchedFrameRelativePath = null,
    double? DurationSeconds = null)
{
    public string ScoreDisplay => L.F("AI類似度（cosine）: {0:F2} — 高いほど上位", Score);
    public string TimestampDisplay => L.F("一致時刻: {0:F2}s", TimestampSeconds);
    public string TimestampHelpText => $"Best matched sample at {TimestampSeconds:F2}s. This is not a scene boundary or guaranteed correct time.";
    public string DurationDisplay => DurationSeconds is { } duration ? $"{duration:F2}s" : L.T("時間情報なし");
    public string IndexStatusDisplay => VisualEmbeddingId != Guid.Empty ? L.T("解析済み") : L.T("一部解析済み");
}

public sealed record MediaListItem(
    Guid MediaFileId,
    string FileName,
    string Path,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    PresenceState PresenceState,
    string? ThumbnailRelativePath,
    MediaMetadata? Metadata,
    string? LatestJobState,
    string? LatestErrorCode,
    SemanticIndexStatus IndexingStatus = SemanticIndexStatus.NotIndexed)
{
    public string ResolutionDisplay => Metadata?.Width is { } width && Metadata.Height is { } height ? $"{width}×{height}" : string.Empty;
    public string PresenceStateDisplay => PresenceState switch
    {
        PresenceState.Present => L.T("利用可能"),
        PresenceState.Recycled => L.T("ごみ箱へ移動済み"),
        PresenceState.PermanentlyDeleted => L.T("完全削除済み"),
        PresenceState.Missing => L.T("見つかりません"),
        PresenceState.OfflineRoot => L.T("登録フォルダーがオフライン"),
        PresenceState.AccessDenied => L.T("アクセスできません"),
        PresenceState.Unknown => L.T("不明"),
        _ => PresenceState.ToString()
    };
    public string IndexingStatusDisplay => IndexingStatus switch
    {
        SemanticIndexStatus.NotIndexed => L.T("未解析"),
        SemanticIndexStatus.Queued => L.T("解析待ち"),
        SemanticIndexStatus.Indexing => L.T("解析中"),
        SemanticIndexStatus.Partial => L.T("一部解析済み"),
        SemanticIndexStatus.Indexed => L.T("解析済み"),
        SemanticIndexStatus.Failed => L.T("解析失敗"),
        _ => IndexingStatus.ToString()
    };
};

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Offset, int Limit);

public sealed record ResourceCostEstimate(
    int CpuCost,
    long RamBytesEstimate,
    bool GpuRequired,
    long VramBytesEstimate,
    int SequentialReadCost,
    int RandomReadCost,
    int WriteIoCost,
    int DbWriteCost);

/// <summary>取得不能な資源値はnullで表現し、推測値を安全判断に使いません。</summary>
public sealed record ResourceSnapshot(
    int? CpuPressurePercent,
    long? AvailableRamBytes,
    bool? GpuAvailable,
    long? AvailableVramBytes,
    int? ReadIoPressurePercent,
    int? WriteIoPressurePercent,
    int? DbWriterPressure);

public enum AdmissionDecisionKind { Admit, Wait, Reject, Unsupported }
public sealed record AdmissionDecision(AdmissionDecisionKind Kind, JobReason Reason, string Code, string Message);
public sealed record ResourceAdmissionLimits(
    int MaximumCpuPressurePercent = 90,
    int MaximumReadIoPressurePercent = 80,
    int MaximumWriteIoPressurePercent = 80,
    int MaximumDbWriterPressure = 80);

public interface IResourceAdmissionPolicy
{
    AdmissionDecision Evaluate(ResourceCostEstimate estimate, ResourceSnapshot snapshot);
}

/// <summary>
/// 将来のAI job用のdeterministic admission policyです。既存Scan/Metadata/Thumbnailの
/// stage concurrencyには介入せず、取得できないGPU/VRAMを存在するように扱いません。
/// </summary>
public sealed class ConservativeResourceAdmissionPolicy(ResourceAdmissionLimits? limits = null) : IResourceAdmissionPolicy
{
    private readonly ResourceAdmissionLimits _limits = limits ?? new ResourceAdmissionLimits();

    public AdmissionDecision Evaluate(ResourceCostEstimate estimate, ResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (estimate.GpuRequired)
        {
            if (snapshot.GpuAvailable is not true)
                return new(AdmissionDecisionKind.Reject, JobReason.ResourceGpu, "RESOURCE_GPU_UNAVAILABLE", L.T("GPU必須ジョブを開始できません。GPU状態を確認してください。"));
            if (snapshot.AvailableVramBytes is not { } availableVram || availableVram < estimate.VramBytesEstimate)
                return new(AdmissionDecisionKind.Reject, JobReason.ResourceVram, "RESOURCE_VRAM_UNAVAILABLE", L.T("GPU必須ジョブに必要なVRAMを確認できないか不足しています。"));
        }
        if (snapshot.AvailableRamBytes is { } availableRam && availableRam < estimate.RamBytesEstimate)
            return new(AdmissionDecisionKind.Wait, JobReason.ResourceRam, "RESOURCE_RAM_WAIT", L.T("利用可能RAMが不足しているため待機します。"));
        if (snapshot.DbWriterPressure is { } dbPressure && dbPressure >= _limits.MaximumDbWriterPressure)
            return new(AdmissionDecisionKind.Wait, JobReason.DatabaseBusy, "RESOURCE_DB_WAIT", L.T("SQLite writer pressureが高いため待機します。"));
        if (snapshot.ReadIoPressurePercent is { } readPressure && readPressure >= _limits.MaximumReadIoPressurePercent ||
            snapshot.WriteIoPressurePercent is { } writePressure && writePressure >= _limits.MaximumWriteIoPressurePercent)
            return new(AdmissionDecisionKind.Wait, JobReason.ResourceIo, "RESOURCE_IO_WAIT", L.T("ストレージI/O pressureが高いため待機します。"));
        if (snapshot.CpuPressurePercent is { } cpuPressure && cpuPressure >= _limits.MaximumCpuPressurePercent)
            return new(AdmissionDecisionKind.Wait, JobReason.ResourceCpu, "RESOURCE_CPU_WAIT", L.T("CPU pressureが高いため待機します。"));
        return new(AdmissionDecisionKind.Admit, JobReason.None, "RESOURCE_ADMIT", L.T("資源条件を満たしています。"));
    }
}

/// <summary>
/// HDDを含むローカルライブラリを保護するstage別admission設定です。
/// 値は無制限にせず、明示的な保守的上限内でのみ動作させます。
/// </summary>
public sealed record PipelineConcurrencyOptions
{
    public const int MaximumStageConcurrency = 4;

    public PipelineConcurrencyOptions(int scanConcurrency = 1, int metadataConcurrency = 1, int thumbnailConcurrency = 1, int visualSamplingConcurrency = 1)
    {
        ScanConcurrency = Validate(nameof(scanConcurrency), scanConcurrency);
        MetadataConcurrency = Validate(nameof(metadataConcurrency), metadataConcurrency);
        ThumbnailConcurrency = Validate(nameof(thumbnailConcurrency), thumbnailConcurrency);
        VisualSamplingConcurrency = Validate(nameof(visualSamplingConcurrency), visualSamplingConcurrency);
    }

    public int ScanConcurrency { get; }
    public int MetadataConcurrency { get; }
    public int ThumbnailConcurrency { get; }
    public int VisualSamplingConcurrency { get; }

    public static PipelineConcurrencyOptions FromEnvironment() => new(
        Read("LOCAL_MEDIA_AI_SCAN_CONCURRENCY"),
        Read("LOCAL_MEDIA_AI_METADATA_CONCURRENCY"),
        Read("LOCAL_MEDIA_AI_THUMBNAIL_CONCURRENCY"),
        Read("LOCAL_MEDIA_AI_VISUAL_SAMPLING_CONCURRENCY"));

    public int For(JobType jobType) => jobType switch
    {
        JobType.Scan => ScanConcurrency,
        JobType.Metadata => MetadataConcurrency,
        JobType.Thumbnail => ThumbnailConcurrency,
        JobType.VisualSampling => VisualSamplingConcurrency,
        _ => 1
    };

    private static int Read(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return 1;
        if (!int.TryParse(value, out var parsed))
            throw new InvalidOperationException($"{variable} must be an integer between 1 and {MaximumStageConcurrency}.");
        return parsed;
    }

    private static int Validate(string name, int value)
    {
        if (value is < 1 or > MaximumStageConcurrency)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 1 and {MaximumStageConcurrency}.");
        return value;
    }
}

public sealed record DomainError(string Code, string UserMessage, string? Diagnostic = null)
{
    public static DomainError FfmpegNotFound => new("FFMPEG_NOT_FOUND", L.T("FFmpeg/ffprobe が設定された RuntimeRoot に見つかりません。配置または設定を確認してください。"));
    public static DomainError RootOffline => new("ROOT_OFFLINE", L.T("ライブラリルートへ到達できません。ドライブまたはパスを確認してから再試行してください。"));
}

public sealed record ScanPayload(Guid LibraryRootId, long RegistrationRevision = 0);
public sealed record MediaPayload(Guid MediaFileId);
public sealed record VisualSamplingPayload(Guid MediaFileId, IReadOnlyList<int>? SampleIndexes = null, bool IsRetry = false);
public sealed record VisualEmbeddingPayload(Guid VisualSampleId);
public sealed record FullHashPayload(Guid MediaFileId, Guid MediaContentId, long ExpectedSizeBytes, DateTimeOffset ExpectedLastWriteTimeUtc, string ProcessingVersion = "full-sha256-v1");
public sealed record NearDuplicateFingerprintPayload(Guid MediaFileId, Guid MediaContentId, long ExpectedSizeBytes, DateTimeOffset ExpectedLastWriteTimeUtc, string SamplingVersion = "near-duplicate-normalized-v1", string FingerprintAlgorithm = "dhash-gray-9x8-v1", bool IsRetry = false, IReadOnlyList<int>? SampleIndexes = null);
public sealed record ContentSimilarityRepresentationPayload(Guid MediaFileId, Guid MediaContentId, long ExpectedSizeBytes, DateTimeOffset ExpectedLastWriteTimeUtc, string AggregationVersion = "mean-unit-v1", string ThresholdVersion = "content-similarity-v1-high-precision");
public sealed record TagSuggestionPayload(Guid MediaFileId, Guid MediaContentId, long ExpectedSizeBytes, DateTimeOffset ExpectedLastWriteTimeUtc, string TaxonomyVersion = TaggingTaxonomy.TaxonomyVersion, string TextRepresentationVersion = TaggingTaxonomy.DescriptiveTextRepresentationVersion, string AggregationVersion = "media-sample-max-mean-v1", string ThresholdVersion = "tagging-embedding-zero-shot-v1-high-precision");
public sealed record ProtectedSourceOrchestrationPayload(
    Guid MediaFileId,
    Guid ExpectedMediaContentId,
    ProtectedSourceStageEligibility EligibleStages = ProtectedSourceStageEligibility.None,
    string ProcessingVersion = "protected-source-orchestration-v1");

public interface ILibraryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<LibraryRoot> AddOrGetLibraryRootAsync(string configuredPath, CancellationToken cancellationToken);
    Task<LibraryRoot?> GetLibraryRootAsync(Guid rootId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LibraryRoot>> GetLibraryRootsAsync(CancellationToken cancellationToken);
    Task SetRootAvailabilityAsync(Guid rootId, RootAvailability availability, CancellationToken cancellationToken);
    Task UpdateRootIdentityAsync(Guid rootId, string configuredPath, string? volumeIdentity, string? rootIdentity, CancellationToken cancellationToken);
    Task RecordRootObservationAsync(Guid rootId, RootObservationKind observation, DateTimeOffset observedAt, CancellationToken cancellationToken);
    Task UnregisterRootAsync(Guid rootId, DateTimeOffset unregisteredAt, CancellationToken cancellationToken);
    Task<LibraryRoot> ReregisterRootAsync(string configuredPath, CancellationToken cancellationToken);
    Task<RetentionSettings> GetRetentionSettingsAsync(CancellationToken cancellationToken);
    Task SaveRetentionSettingsAsync(RetentionSettings settings, CancellationToken cancellationToken);
    Task<bool> GetDeveloperModeAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task SaveDeveloperModeAsync(bool enabled, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException("Presentation settings are not supported by this repository."));
    Task<Organize.OrganizeSettings> GetOrganizeSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Organize.OrganizeSettings.Defaults);
    Task SaveOrganizeSettingsAsync(Organize.OrganizeSettings settings, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException("Organize settings are not supported by this repository."));
    Task<Processing.ProcessingSettings> GetProcessingSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Processing.ProcessingSettings.Defaults);
    Task SaveProcessingSettingsAsync(Processing.ProcessingSettings settings, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException("Processing settings are not supported by this repository."));
    Task<IReadOnlyList<Organize.CleanupBatchSummary>> GetCleanupBatchesAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Organize.CleanupBatchSummary>>([]);
    Task<IReadOnlyList<string>> GetCleanupBatchFilesAsync(IReadOnlyList<Guid> batchIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    Task<IReadOnlyList<Organize.UnanalyzedFile>> GetUnanalyzedFilesAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Organize.UnanalyzedFile>>([]);
    Task<Organize.AnalysisProgress> GetAnalysisProgressAsync(CancellationToken cancellationToken) => Task.FromResult(Organize.AnalysisProgress.Empty);
    Task<IReadOnlyList<RetentionRecord>> GetRetentionRecordsAsync(CancellationToken cancellationToken);
    Task<RetentionCleanupResult> RunRetentionCleanupAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<Guid> StartScanAsync(Guid rootId, CancellationToken cancellationToken);
    Task CompleteScanAsync(Guid scanId, ScanCompletion completion, CancellationToken cancellationToken);
    Task<ReconcileResult> UpsertScannedFileAsync(ScannedMediaFile file, Guid scanId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReconcileResult>> ReconcileScannedFilesAsync(IReadOnlyList<ScannedMediaFile> files, Guid scanId, CancellationToken cancellationToken);
    Task MarkAbsentFilesAfterCompletedScanAsync(Guid rootId, Guid scanId, CancellationToken cancellationToken);
    Task ReconcileCompletedScanAbsenceAsync(Guid rootId, Guid scanId, ScanAuthoritySnapshot snapshot, DateTimeOffset observedAt, CancellationToken cancellationToken);
    Task<MediaFile?> GetMediaFileAsync(Guid mediaFileId, CancellationToken cancellationToken);
    Task<MediaContent?> GetMediaContentAsync(Guid mediaContentId, CancellationToken cancellationToken);
    Task<Analysis?> FindCompletedAnalysisAsync(Guid contentId, string analysisType, string processorName, string processorVersion, string configHash, string processingVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<(Guid MediaFileId, string? RelativePath)>> GetCurrentThumbnailCandidatesAsync(Guid libraryRootId, string configHash, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<(Guid MediaFileId, string? RelativePath)>>([]);
    Task<string?> GetCompletedThumbnailArtifactPathAsync(Guid mediaContentId, string configHash, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    Task<Analysis> StartAnalysisAsync(Guid contentId, string analysisType, string processorName, string processorVersion, string configJson, string configHash, string inputIdentityHash, string processingVersion, CancellationToken cancellationToken);
    Task CompleteAnalysisAsync(Guid analysisId, AnalysisStatus status, CancellationToken cancellationToken);
    Task FailAnalysisAsync(Guid analysisId, string errorCode, string userMessage, CancellationToken cancellationToken);
    Task UpsertMetadataAsync(MediaMetadata metadata, CancellationToken cancellationToken);
    Task AddArtifactAsync(Artifact artifact, CancellationToken cancellationToken);
    Task ReplaceVisualSamplesAsync(Guid mediaFileId, Guid mediaContentId, string inputIdentityHash, string samplingVersion, IReadOnlyList<VisualSample> samples, CancellationToken cancellationToken);
    Task<IReadOnlyList<VisualSample>> GetVisualSamplesAsync(Guid mediaFileId, string inputIdentityHash, string? samplingVersion, CancellationToken cancellationToken);
    Task<VisualSample?> GetVisualSampleAsync(Guid visualSampleId, CancellationToken cancellationToken);
    Task UpsertModelProvenanceAsync(ModelProvenance provenance, CancellationToken cancellationToken);
    Task<VisualEmbedding?> FindCompletedVisualEmbeddingAsync(string cacheKey, CancellationToken cancellationToken);
    Task UpsertVisualEmbeddingAsync(VisualEmbedding embedding, CancellationToken cancellationToken);
    Task<IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample, MediaFile Media, MediaMetadata? Metadata)>> GetCompletedVisualEmbeddingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SemanticSearchEmbeddingRow>> GetCurrentSemanticSearchRowsAsync(SemanticModelProfile model, SemanticExecutionProfile execution, Guid? libraryRootId, CancellationToken cancellationToken);
    Task<SemanticEmbeddingCompleteness> GetCurrentSemanticEmbeddingCompletenessAsync(SemanticModelProfile model, SemanticExecutionProfile execution, Guid? libraryRootId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RootScanCurrentness>> GetCurrentRootScanCurrentnessAsync(CancellationToken cancellationToken);
    Task<SemanticIndexStatus> GetSemanticIndexStatusAsync(Guid mediaFileId, SemanticModelProfile model, SemanticExecutionProfile execution, CancellationToken cancellationToken);
    Task<PagedResult<MediaListItem>> QueryMediaAsync(Guid? rootId, string? search, int offset, int limit, CancellationToken cancellationToken);
    Task<int?> FindLibraryMediaOffsetAsync(Guid mediaFileId, Guid? rootId, string? search, string? tag, bool includeSuggested, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
    Task<MediaListItem?> GetMediaListItemAsync(Guid mediaFileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaFile>> GetExactDuplicateHashCandidatesAsync(Guid? rootId, CancellationToken cancellationToken);
    Task<FileHashEvidence?> GetCurrentFullHashEvidenceAsync(Guid mediaFileId, Guid mediaContentId, string algorithm, string processingVersion, CancellationToken cancellationToken);
    Task<bool> RecordFullHashEvidenceAsync(FileHashEvidence evidence, CancellationToken cancellationToken);
    Task RebuildExactDuplicateGroupsAsync(CancellationToken cancellationToken);
    Task<PagedResult<DuplicateGroup>> QueryDuplicateGroupsAsync(Guid? rootId, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken);
    Task<PagedResult<DuplicateGroup>> QueryDuplicateGroupsByRelationAsync(Guid? rootId, DuplicateRelationType? relationType, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateComparisonMember>> GetDuplicateComparisonMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateComparisonMember>> GetNearDuplicateComparisonMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken);
    Task<DuplicateJudgment> UpsertDuplicateJudgmentAsync(Guid duplicateGroupId, Guid? mediaFileId, DuplicateJudgmentDecision decision, string? note, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateJudgment>> GetDuplicateJudgmentsAsync(Guid duplicateGroupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaFile>> GetNearDuplicateFingerprintCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateFrame>> GetCurrentNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken);
    Task ReplaceNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, long sourceSizeBytes, DateTimeOffset sourceLastWriteTimeUtc, string samplingVersion, string fingerprintAlgorithm, IReadOnlyList<NearDuplicateFrame> frames, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateCandidate>> GetNearDuplicateCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicatePreparationPending>> GetPendingNearDuplicatePreparationsAsync(Guid? rootId, Guid? mediaFileId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, int limit, CancellationToken cancellationToken);
    Task<bool> SaveNearDuplicatePreparationAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, string framesVersion, FrameCrop bars, IReadOnlyList<(Guid NearDuplicateFrameId, PreparedNearDuplicateFrame Prepared)> frames, CancellationToken cancellationToken);
    Task<IReadOnlyList<PreparedNearDuplicateFrame>> GetCurrentPreparedNearDuplicateFramesAsync(Guid mediaFileId, Guid mediaContentId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateEvidence>> GetNearDuplicateRechecksDueAsync(string thresholdVersion, string preparationVersion, CancellationToken cancellationToken);
    Task MarkNearDuplicateRecheckedAsync(Guid nearDuplicateEvidenceId, string preparationVersion, CancellationToken cancellationToken);
    Task<bool> SaveNearDuplicateTimelineAsync(StoredTimeline timeline, CancellationToken cancellationToken);
    Task<StoredTimeline?> GetCurrentNearDuplicateTimelineAsync(Guid mediaFileId, Guid mediaContentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaFile>> GetMediaWithoutNearDuplicateTimelineAsync(Guid? rootId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TimelineCandidate>> GetNearDuplicateTimelineCandidatesAsync(Guid mediaFileId, Guid mediaContentId, CancellationToken cancellationToken);
    Task SaveTimelineEvidenceDetailAsync(TimelineEvidenceDetail detail, CancellationToken cancellationToken);
    Task<TimelineEvidenceDetail?> GetTimelineEvidenceDetailAsync(Guid nearDuplicateEvidenceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateCandidate>> GetPreparedNearDuplicateCandidatesAsync(Guid? rootId, string samplingVersion, string fingerprintAlgorithm, string preparationVersion, CancellationToken cancellationToken);
    Task<NearDuplicateEvidence?> GetNearDuplicateEvidenceAsync(Guid mediaFileAId, Guid mediaContentAId, Guid mediaFileBId, Guid mediaContentBId, string samplingVersion, string fingerprintAlgorithm, string thresholdVersion, CancellationToken cancellationToken);
    Task<bool> UpsertNearDuplicateEvidenceAsync(NearDuplicateEvidence evidence, IReadOnlyList<NearDuplicateFrameMatch> frameMatches, CancellationToken cancellationToken);
    Task RebuildNearDuplicateGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateEvidence>> GetNearDuplicateEvidenceForGroupAsync(Guid duplicateGroupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearDuplicateFrameMatch>> GetNearDuplicateFrameMatchesAsync(Guid nearDuplicateEvidenceId, CancellationToken cancellationToken);
    Task<ContentSimilaritySourceEmbeddings> GetContentSimilaritySourceEmbeddingsAsync(Guid mediaFileId, Guid mediaContentId, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaFile>> GetContentSimilarityRepresentationCandidatesAsync(Guid? rootId, string aggregationVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task<ContentSimilarityRepresentation?> GetCurrentContentSimilarityRepresentationAsync(Guid mediaFileId, Guid mediaContentId, string aggregationVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task<bool> UpsertContentSimilarityRepresentationAsync(ContentSimilarityRepresentation representation, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContentSimilarityCandidate>> GetContentSimilarityCandidatesAsync(Guid? rootId, string aggregationVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, int topK, CancellationToken cancellationToken);
    Task<ContentSimilarityEvidence?> GetContentSimilarityEvidenceAsync(Guid mediaFileAId, Guid mediaContentAId, Guid mediaFileBId, Guid mediaContentBId, string aggregationVersion, string thresholdVersion, CancellationToken cancellationToken);
    Task<bool> UpsertContentSimilarityEvidenceAsync(ContentSimilarityEvidence evidence, CancellationToken cancellationToken);
    Task<PagedResult<ContentSimilarityEvidence>> QueryContentSimilarityEvidenceAsync(Guid? rootId, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContentSimilarityComparisonMember>> GetContentSimilarityComparisonMembersAsync(Guid contentSimilarityEvidenceId, CancellationToken cancellationToken);
    Task<ContentSimilarityJudgment> UpsertContentSimilarityJudgmentAsync(Guid contentSimilarityEvidenceId, ContentSimilarityJudgmentDecision decision, string? note, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContentSimilarityJudgment>> GetContentSimilarityJudgmentsAsync(Guid contentSimilarityEvidenceId, CancellationToken cancellationToken);
    Task<MediaTagSourceEmbeddings> GetMediaTagSourceEmbeddingsAsync(Guid mediaFileId, Guid mediaContentId, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaFile>> GetTagSuggestionCandidatesAsync(Guid? rootId, string taxonomyVersion, string textRepresentationVersion, string aggregationVersion, string thresholdVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task<TagTextEmbedding?> GetTagTextEmbeddingAsync(string taxonomyVersion, string tagId, string textRepresentationVersion, string modelName, string modelRevision, string processorVersion, string dtype, int dimension, CancellationToken cancellationToken);
    Task UpsertTagTextEmbeddingAsync(TagTextEmbedding embedding, CancellationToken cancellationToken);
    Task<MediaTagEvidence?> GetCurrentMediaTagEvidenceAsync(Guid mediaFileId, Guid mediaContentId, string tagId, string taxonomyVersion, string textRepresentationVersion, string aggregationVersion, string thresholdVersion, string modelName, string modelRevision, string processorVersion, string sourceSamplingVersion, string dtype, int dimension, string sourceEmbeddingDigest, CancellationToken cancellationToken);
    Task<bool> UpsertMediaTagEvidenceAsync(MediaTagEvidence evidence, CancellationToken cancellationToken);
    Task<PagedResult<MediaTagReviewItem>> QueryMediaTagReviewAsync(Guid? rootId, bool includeHidden, int offset, int limit, CancellationToken cancellationToken);
    Task<MediaTagJudgment> UpsertMediaTagJudgmentAsync(Guid mediaTagEvidenceId, MediaTagJudgmentDecision decision, string? note, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaTagJudgment>> GetMediaTagJudgmentsAsync(Guid mediaTagEvidenceId, CancellationToken cancellationToken);
    Task<ManualMediaTag> AddManualMediaTagAsync(Guid mediaFileId, string displayTag, string? note, CancellationToken cancellationToken);
    Task<IReadOnlyList<ManualMediaTag>> GetManualMediaTagsAsync(Guid mediaFileId, CancellationToken cancellationToken);
    Task<bool> RemoveManualMediaTagAsync(Guid mediaFileId, string canonicalTag, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaTagSummary>> GetMediaTagSummariesAsync(IReadOnlyList<Guid> mediaFileIds, CancellationToken cancellationToken);
    Task<PagedResult<UnifiedReviewItem>> QueryUnifiedReviewAsync(UnifiedReviewQuery query, CancellationToken cancellationToken);
    Task<UnifiedReviewDecisionResult> ApplyUnifiedReviewDecisionAsync(UnifiedReviewDecisionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<UnifiedReviewDecisionHistoryEntry>> GetUnifiedReviewDecisionHistoryAsync(string reviewItemId, CancellationToken cancellationToken);
    Task<UnifiedReviewResumeState> GetUnifiedReviewResumeStateAsync(CancellationToken cancellationToken);
    Task SaveUnifiedReviewResumeStateAsync(UnifiedReviewResumeState state, CancellationToken cancellationToken);
    Task<FileOperationProposal> CreateFileOperationProposalAsync(FileOperationProposal proposal, CancellationToken cancellationToken);
    Task<FileOperationIntent> CreateFileOperationIntentAsync(FileOperationIntent intent, CancellationToken cancellationToken);
    Task<FileOperationPlan> CreateFileOperationPlanAsync(FileOperationPlan plan, CancellationToken cancellationToken);
    Task<FileOperationPlan?> GetFileOperationPlanAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationPlan> TransitionFileOperationPlanAsync(Guid operationId, FileOperationState targetState, string? errorCode, string? errorMessage, DateTimeOffset? confirmedAt, CancellationToken cancellationToken);
    Task<FileOperationPlan> ConfirmProductionSameVolumeExecutionAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationPlan> ConfirmProductionCrossVolumeMoveExecutionAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationPlan> ConfirmProductionRecycleExecutionAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationPlan> ConfirmProductionPermanentDeleteExecutionAsync(Guid operationId, CancellationToken cancellationToken);
    Task<FileOperationPlan> TransitionCrossVolumeMovePlanAsync(Guid operationId, FileOperationState targetState, CrossVolumeMoveStage stage, string? errorCode, string? errorMessage, CancellationToken cancellationToken);
    Task<MediaFile> PublishCrossVolumeMoveAsync(CrossVolumeMoveDatabasePublish publish, CancellationToken cancellationToken);
    Task<MediaRecycleLifecycle> PublishRecycleLifecycleAsync(RecycleDatabasePublish publish, CancellationToken cancellationToken);
    Task<MediaRecycleLifecycle?> GetMediaRecycleLifecycleAsync(Guid mediaFileId, CancellationToken cancellationToken);
    Task<MediaPermanentDeleteLifecycle> PublishPermanentDeleteLifecycleAsync(PermanentDeleteDatabasePublish publish, CancellationToken cancellationToken);
    Task<MediaPermanentDeleteLifecycle?> GetMediaPermanentDeleteLifecycleAsync(Guid mediaFileId, CancellationToken cancellationToken);
    Task<FileOperationPreflight> RecordFileOperationPreflightAsync(FileOperationPreflight preflight, CancellationToken cancellationToken);
    Task<FileOperationExecution> RecordFileOperationExecutionAsync(FileOperationExecution execution, CancellationToken cancellationToken);
    Task AddFileOperationAuditAsync(FileOperationAuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileOperationAuditEntry>> GetFileOperationAuditAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileOperationPlan>> GetInterruptedFileOperationPlansAsync(CancellationToken cancellationToken);
    Task<BatchOperation> CreateBatchOperationAsync(BatchOperation batch, IReadOnlyList<BatchItem> items, CancellationToken cancellationToken);
    Task<BatchOperation?> GetBatchOperationAsync(Guid batchOperationId, CancellationToken cancellationToken);
    Task<bool> TryCancelFileOperationPreparationAsync(FileOperationPreparation expected, CancellationToken cancellationToken);
    Task<IReadOnlyList<BatchItem>> GetBatchItemsAsync(Guid batchOperationId, CancellationToken cancellationToken);
    Task<BatchOperation> TransitionBatchOperationAsync(Guid batchOperationId, BatchOperationState state, string? summaryCode, string? summaryMessage, CancellationToken cancellationToken);
    Task<BatchOperation> RequestBatchCancellationAsync(Guid batchOperationId, CancellationToken cancellationToken);
    Task<BatchItem> UpdateBatchItemAsync(Guid batchItemId, FileOperationState planState, BatchItemState state, string? revalidationState, string? lifecycleResult, string? recoveryState, string? errorCode, string? errorMessage, CancellationToken cancellationToken);
    Task AddBatchOperationAuditAsync(BatchOperationAuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<BatchOperationAuditEntry>> GetBatchOperationAuditAsync(Guid batchOperationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BatchOperation>> GetInterruptedBatchOperationsAsync(CancellationToken cancellationToken);
    Task<PagedResult<MediaListItem>> QueryMediaByTagAsync(Guid? rootId, string canonicalOrTaxonomyTag, bool includeSuggested, int offset, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> FilterMediaIdsByTagAsync(IReadOnlyList<Guid> orderedMediaFileIds, string canonicalOrTaxonomyTag, bool includeSuggested, CancellationToken cancellationToken);
    Task<bool> IntegrityCheckAsync(CancellationToken cancellationToken);
    Task CheckpointAsync(CancellationToken cancellationToken);
}

public interface IJobStore
{
    Task<Job> EnqueueAsync(JobType type, object payload, int maxAttempts, DateTimeOffset? notBefore, CancellationToken cancellationToken);
    Task<Job> EnqueueUniqueAsync(JobType type, object payload, int maxAttempts, DateTimeOffset? notBefore, CancellationToken cancellationToken);
    Task<Job?> TryAcquireNextAsync(CancellationToken cancellationToken);
    Task UpdateProgressAsync(Guid jobId, string phase, JobProgress progress, CancellationToken cancellationToken);
    Task HeartbeatAsync(Guid jobId, string phase, CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, JobState state, string phase, CancellationToken cancellationToken);
    Task FailOrRetryAsync(Guid jobId, string errorCode, string userMessage, bool retryable, CancellationToken cancellationToken);
    Task DeferForResourceAsync(Guid jobId, JobReason reason, string code, string userMessage, DateTimeOffset notBefore, CancellationToken cancellationToken);
    Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Job>> GetActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Job>> GetRecentAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IStructuredLogger
{
    void Write(string level, string eventName, object fields, Exception? exception = null);
}

public sealed record ScannedMediaFile(
    Guid LibraryRootId,
    string FullPath,
    string NormalizedPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    string? VolumeIdentity,
    string? FileIdentity,
    string? QuickFingerprint);

public sealed record ReconcileResult(MediaFile MediaFile, bool IsNew, bool MetadataInvalidated);
public sealed record ScanCompletion(string State, int FilesSeen, int FilesAdded, int FilesUpdated, int FilesUnavailable, int ErrorCount, DateTimeOffset CompletedAt);

public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Pending] = [JobState.Running, JobState.Cancelled],
        [JobState.Running] = [JobState.Waiting, JobState.Paused, JobState.RetryWaiting, JobState.Completed, JobState.Partial, JobState.Failed, JobState.Cancelled],
        [JobState.Waiting] = [JobState.Running, JobState.Paused, JobState.Cancelled],
        [JobState.Paused] = [JobState.Pending, JobState.Cancelled],
        [JobState.RetryWaiting] = [JobState.Pending, JobState.Running, JobState.Cancelled],
        [JobState.Completed] = [],
        [JobState.Partial] = [JobState.Pending, JobState.Cancelled],
        [JobState.Failed] = [JobState.Pending, JobState.Cancelled],
        [JobState.Cancelled] = []
    };

    public static bool CanTransition(JobState from, JobState to) => Allowed.TryGetValue(from, out var choices) && choices.Contains(to);

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid job transition: {from} -> {to}.");
    }
}

public sealed class SearchRequestGate
{
    private long _latest;
    public long Begin() => Interlocked.Increment(ref _latest);
    public bool IsCurrent(long requestId) => requestId == Volatile.Read(ref _latest);
}

public static class AnalysisKey
{
    public static string ConfigHash(object config) => Sha256(CanonicalJson(config));
    public static string ArtifactCacheKey(string inputIdentityHash, string stage, string processingVersion, string processorVersion, string configHash)
        => Sha256(string.Join("\n", inputIdentityHash, stage, processingVersion, processorVersion, configHash));

    public static string VisualEmbeddingCacheKey(string inputIdentityHash, double presentationTimestampSeconds, string modelRevision, string processorVersion, string samplingVersion, string dtype)
        => VisualEmbeddingCacheKey(inputIdentityHash, presentationTimestampSeconds, presentationTimestampSeconds, modelRevision, processorVersion, samplingVersion, dtype);

    public static string VisualEmbeddingCacheKey(string inputIdentityHash, double requestedTimestampSeconds, double presentationTimestampSeconds, string modelRevision, string processorVersion, string samplingVersion, string dtype)
        => Sha256(string.Join("\n", inputIdentityHash, requestedTimestampSeconds.ToString("F6", CultureInfo.InvariantCulture), presentationTimestampSeconds.ToString("F6", CultureInfo.InvariantCulture), modelRevision, processorVersion, samplingVersion, dtype));

    /// <summary>
    /// Profile-complete cache identity for semantic embeddings. Model ID, revision, adapter,
    /// processor, dtype and dimension are all semantic inputs and must not share a cache row.
    /// </summary>
    public static string VisualEmbeddingCacheKey(string inputIdentityHash, double requestedTimestampSeconds, double presentationTimestampSeconds, string modelId, string modelRevision, string adapter, string processorVersion, string samplingVersion, string dtype, int dimension)
        => Sha256(string.Join("\n", inputIdentityHash, requestedTimestampSeconds.ToString("F6", CultureInfo.InvariantCulture), presentationTimestampSeconds.ToString("F6", CultureInfo.InvariantCulture), modelId, modelRevision, adapter, processorVersion, samplingVersion, dtype, dimension.ToString(CultureInfo.InvariantCulture)));

    public static string CanonicalJson(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var builder = new StringBuilder();
        WriteElement(document.RootElement, builder);
        return builder.ToString();
    }

    private static void WriteElement(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    WriteElement(property.Value, builder);
                    builder.Append(',');
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in element.EnumerateArray()) { WriteElement(item, builder); builder.Append(','); }
                builder.Append(']');
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    public static string Sha256(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
