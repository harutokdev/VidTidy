namespace LocalMediaAI.Core;

/// <summary>
/// File operation categories are intentionally broader than current execution permission.
/// Recycle and PermanentDelete remain future audit vocabulary only and have no executor.
/// </summary>
public enum FileOperationType { Move, Rename, Recycle, PermanentDelete }

/// <summary>
/// ProductionSameVolumeExecution is an explicit Phase 6.2 capability for Move/Rename only.
/// It does not authorize cross-volume copy, overwrite, recycle, permanent delete, or any automatic action.
/// </summary>
public enum FileOperationMode { ProductionDryRun, DisposableFixtureExecution }

public enum FileOperationState
{
    Proposed,
    Planned,
    Validating,
    Ready,
    ConfirmationRequired,
    FinalConfirmationRequired,
    Executing,
    Copying,
    CopyVerified,
    FinalDestinationPublished,
    Recycled,
    RecycleFailed,
    RecycleUnavailable,
    PermanentlyDeleted,
    PermanentDeleteFailed,
    Completed,
    PartiallyCompleted,
    Cancelled,
    SourceChanged,
    SourceChangedAfterCopy,
    SourceMissing,
    SourceUnavailable,
    AccessDenied,
    DestinationConflict,
    InsufficientSpace,
    FreeSpaceUnknown,
    SourceRemovalFailed,
    ValidationFailed,
    RecoveryRequired,
    RolledBack,
    CannotRollback,
    ProductionExecutionBlocked,
    Unsupported
}

public enum SourceValidationStrength { StatAndStableIdentity, StrongFullHash }
public enum OverwritePolicy { Prohibit }
public enum DestinationConflictPolicy { Abort, Skip, RenameCandidate }
public enum OperationRecoverability { Reversible, ConditionallyReversible, Irreversible }
public enum OperationItemResult { Pending, DryRun, Completed, Skipped, Failed, RecoveryRequired, Cancelled }

/// <summary>Aggregate orchestration state for a durable, single-type, explicitly selected controlled batch.</summary>
public enum BatchOperationState { Planned, DryRunCompleted, FinalConfirmationRequired, Executing, Completed, PartiallyCompleted, Cancelled, FailedBeforeExecution, RecoveryRequired }

/// <summary>Batch projection of a single durable file operation plan; it never replaces the plan's own state machine.</summary>
public enum BatchItemState { Planned, Ready, Blocked, FinalConfirmed, Executing, Completed, Failed, Cancelled, RecoveryRequired, NotStarted }

/// <summary>Durable internal progress of the distinct Phase 6.3 Cross-Volume Move transaction.</summary>
public enum CrossVolumeMoveStage
{
    None,
    PreflightReady,
    Copying,
    TempCopied,
    TempHashVerified,
    FinalPublished,
    SourceRemovalPending,
    SourceRemoved,
    DbPublished,
    RecoveryRequired
}

/// <summary>
/// A proposal can cite review context, but it never carries permission to mutate a filesystem.
/// ReviewItemId is opaque so Unified Review source-of-truth tables remain unchanged.
/// </summary>
public sealed record FileOperationProposal(
    Guid ProposalId,
    Guid MediaFileId,
    Guid MediaContentId,
    string? ReviewItemId,
    string? ReviewContext,
    FileOperationType SuggestedOperationType,
    DateTimeOffset CreatedAt);

/// <summary>Explicit user choice. Acknowledgement is persisted separately from human review judgment.</summary>
public sealed record FileOperationIntent(
    Guid IntentId,
    Guid ProposalId,
    FileOperationType OperationType,
    string? RequestedDestinationPath,
    OverwritePolicy OverwritePolicy,
    DestinationConflictPolicy ConflictPolicy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcknowledgedAt);

/// <summary>
/// Immutable identity snapshot used for validation immediately before a possible filesystem action.
/// QuickFingerprint is a sentinel and never replaces FullSha256 as Exact evidence.
/// ConfirmedAt records Dry Run acknowledgement. FinalExecutionConfirmedAt is the separate Phase 6.2
/// execution confirmation and is required for ProductionSameVolumeExecution.
/// </summary>
public sealed record FileOperationPlan(
    Guid OperationId,
    Guid IntentId,
    Guid LibraryRootId,
    Guid MediaFileId,
    Guid MediaContentId,
    FileOperationType OperationType,
    FileOperationMode Mode,
    string SourcePath,
    string SourceNormalizedPath,
    string? SourceVolumeIdentity,
    string? SourceFileIdentity,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteTimeUtc,
    string? SourceQuickFingerprint,
    string? SourceFullSha256,
    SourceValidationStrength ValidationStrength,
    string? DestinationPath,
    string? DestinationNormalizedPath,
    string ExpectedFilesystemEffect,
    OverwritePolicy OverwritePolicy,
    DestinationConflictPolicy ConflictPolicy,
    OperationRecoverability Recoverability,
    bool UserAcknowledged,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    FileOperationState State,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinalExecutionConfirmedAt = null,
    bool ProductionSameVolumeCapability = false,
    Guid? DestinationLibraryRootId = null,
    string? DestinationRootPath = null,
    string? DestinationVolumeIdentity = null,
    string? TemporaryDestinationPath = null,
    string? TemporaryDestinationNormalizedPath = null,
    long? DestinationFreeSpaceSnapshotBytes = null,
    bool ProductionCrossVolumeMoveCapability = false,
    CrossVolumeMoveStage CrossVolumeMoveStage = CrossVolumeMoveStage.None,
    bool ProductionRecycleCapability = false,
    bool ProductionPermanentDeleteCapability = false,
    int SelectedMediaCount = 1);

/// <summary>Verified Cross-Volume destination metadata for a C#-only canonical path publish; it is not a scan-created new media record.</summary>
public sealed record RecycleDatabasePublish(
    Guid OperationId,
    Guid MediaFileId,
    Guid MediaContentId,
    string SourcePath,
    string SourceNormalizedPath,
    string SourceFullSha256,
    long SourceSizeBytes,
    string? SourceVolumeIdentity,
    string? SourceFileIdentity,
    DateTimeOffset RecycledAt);

/// <summary>Durable lifecycle record retained after a source is sent to the Windows Recycle Bin.</summary>
public sealed record MediaRecycleLifecycle(
    Guid MediaFileId,
    Guid MediaContentId,
    Guid RecycleOperationId,
    string LifecycleState,
    string LastKnownSourcePath,
    string LastKnownSourceNormalizedPath,
    string LastKnownFullSha256,
    long LastKnownSizeBytes,
    string? LastKnownVolumeIdentity,
    string? LastKnownFileIdentity,
    bool SourceAvailable,
    DateTimeOffset RecycledAt,
    DateTimeOffset? RestoredAt,
    string? RestoredPath,
    string? RestoredNormalizedPath);

/// <summary>Durable publish command after a verified irreversible single-file PermanentDelete.</summary>
public sealed record PermanentDeleteDatabasePublish(
    Guid OperationId,
    Guid MediaFileId,
    Guid MediaContentId,
    string SourcePath,
    string SourceNormalizedPath,
    string SourceFullSha256,
    long SourceSizeBytes,
    string? SourceVolumeIdentity,
    string? SourceFileIdentity,
    DateTimeOffset PermanentlyDeletedAt);

/// <summary>History retained after an irreversible source removal; it is never an active filesystem target.</summary>
public sealed record MediaPermanentDeleteLifecycle(
    Guid MediaFileId,
    Guid MediaContentId,
    Guid PermanentDeleteOperationId,
    string LifecycleState,
    string LastKnownSourcePath,
    string LastKnownSourceNormalizedPath,
    string LastKnownFullSha256,
    long LastKnownSizeBytes,
    string? LastKnownVolumeIdentity,
    string? LastKnownFileIdentity,
    bool SourceAvailable,
    DateTimeOffset PermanentlyDeletedAt);

public sealed record CrossVolumeMoveDatabasePublish(
    Guid OperationId,
    Guid MediaFileId,
    Guid MediaContentId,
    Guid DestinationLibraryRootId,
    string DestinationPath,
    string DestinationNormalizedPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    string DestinationVolumeIdentity,
    string DestinationFileIdentity,
    string DestinationQuickFingerprint,
    string VerifiedFullSha256,
    string AuditEventName = "CrossVolumeDbPublished");

public sealed record FileOperationPreflight(
    Guid OperationId,
    FileOperationState State,
    string Code,
    string Message,
    bool SourceExists,
    bool StableIdentityMatches,
    bool SizeMatches,
    bool MtimeMatches,
    bool QuickFingerprintMatches,
    bool? FullHashMatches,
    bool DestinationAvailable,
    bool DestinationConflict,
    long? DestinationFreeBytes,
    DateTimeOffset ValidatedAt,
    bool SameVolumeVerified = false,
    bool CrossVolumeVerified = false,
    bool? TemporaryDestinationAvailable = null,
    long? RequiredDestinationFreeBytes = null);

/// <summary>One item per plan in Phase 6.2, retained as a separate record for bounded partial batches.</summary>
public sealed record FileOperationExecution(
    Guid ExecutionId,
    Guid OperationId,
    Guid MediaFileId,
    string SourcePath,
    string? DestinationPath,
    OperationItemResult Result,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? BeforeIdentityJson,
    string? AfterIdentityJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Append-only operational audit. This does not replace Unified Review decision history.</summary>
public sealed record FileOperationAuditEntry(
    Guid OperationAuditId,
    Guid OperationId,
    DateTimeOffset OccurredAt,
    string EventName,
    string? DetailJson);

/// <summary>Capability supplied by disposable-fixture test setup only; it is not created by production UI.</summary>
public sealed record DisposableFixtureCapability(string FixtureRoot, string MarkerPath);

public sealed record FileOperationDryRun(
    FileOperationPlan Plan,
    FileOperationPreflight Preflight,
    string ExpectedDiskEffect,
    string RecoverabilitySummary,
    string WarningText,
    bool ExecutionEnabled);

public sealed record FileOperationRecoveryResult(
    Guid OperationId,
    FileOperationState State,
    string Code,
    string Message,
    DateTimeOffset RecoveredAt);

/// <summary>Batch parent. It owns selection freeze, aggregate lifecycle and cancellation state, never filesystem authority.</summary>
public sealed record BatchOperation(
    Guid BatchOperationId,
    FileOperationType OperationType,
    int SelectedItemCount,
    long TotalSourceBytes,
    BatchOperationState State,
    bool CancellationRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DryRunCompletedAt,
    DateTimeOffset? FinalExecutionConfirmedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CancellationRequestedAt,
    DateTimeOffset? CompletedAt,
    string? SummaryCode,
    string? SummaryMessage,
    DateTimeOffset UpdatedAt);

/// <summary>One explicitly selected item. OperationId links to the immutable single-file plan that performs all mutation gates.</summary>
public sealed record BatchItem(
    Guid BatchItemId,
    Guid BatchOperationId,
    int Sequence,
    Guid OperationId,
    Guid MediaFileId,
    Guid MediaContentId,
    string SourcePath,
    string? DestinationPath,
    FileOperationState PlanState,
    BatchItemState State,
    string? RevalidationState,
    string? LifecycleResult,
    string? RecoveryState,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Append-only batch orchestration audit, separate from the per-operation audit and Unified Review history.</summary>
public sealed record BatchOperationAuditEntry(Guid BatchOperationAuditId, Guid BatchOperationId, Guid? BatchItemId, DateTimeOffset OccurredAt, string EventName, string? DetailJson);

/// <summary>Explicitly selected source and item-specific destination. There is no selected-all, review, or AI-derived input form.</summary>
public sealed record ControlledBatchItemRequest(Guid MediaFileId, string? DestinationPath, string? ReviewItemId = null, string? ReviewContext = null);

/// <summary>Batch dry-run view retains every durable item rather than silently dropping blocked selections.</summary>
public sealed record BatchOperationDryRun(BatchOperation Batch, IReadOnlyList<BatchItem> Items, int ReadyCount, int BlockedCount, int ChangedCount, int UnavailableCount, int ConflictCount, int IrreversibleCount, long EstimatedBytes, bool FinalConfirmationEnabled, string WarningText);

/// <summary>Aggregate view only; each item retains its own durable plan, execution and audit trail.</summary>
public sealed record FileOperationBatchResult(
    Guid BatchId,
    IReadOnlyList<FileOperationPlan> Items,
    int CompletedCount,
    int FailedCount,
    int SkippedCount,
    int RecoveryRequiredCount,
    FileOperationState AggregateState,
    DateTimeOffset CompletedAt);

public static class FileOperationPolicy
{
    public const string FixtureMarkerFileName = ".local-media-ai-disposable-fixture";
    public const int ControlledBatchMaxItems = 25;
    public const long CrossVolumeMinimumSafetyMarginBytes = 4L * 1024L * 1024L;

    public static long RequiredCrossVolumeDestinationFreeBytes(long sourceSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSizeBytes);
        var percentageMargin = checked((sourceSizeBytes + 99L) / 100L);
        return checked(sourceSizeBytes + Math.Max(CrossVolumeMinimumSafetyMarginBytes, percentageMargin));
    }

    public static bool IsCrossVolumeMove(FileOperationPlan plan) =>
        plan.Mode == FileOperationMode.ProductionDryRun && plan.OperationType == FileOperationType.Move && plan.ProductionCrossVolumeMoveCapability;

    public static bool IsSameVolumeCrossRootMove(FileOperationPlan plan) =>
        plan.Mode == FileOperationMode.ProductionDryRun && plan.OperationType == FileOperationType.Move && plan.ProductionSameVolumeCapability &&
        !plan.ProductionCrossVolumeMoveCapability && plan.DestinationLibraryRootId is { } destinationRootId && destinationRootId != plan.LibraryRootId;

    public static bool IsProductionRecycle(FileOperationPlan plan) =>
        plan.Mode == FileOperationMode.ProductionDryRun && plan.OperationType == FileOperationType.Recycle && plan.ProductionRecycleCapability;

    public static bool IsProductionPermanentDelete(FileOperationPlan plan) =>
        plan.Mode == FileOperationMode.ProductionDryRun && plan.OperationType == FileOperationType.PermanentDelete && plan.ProductionPermanentDeleteCapability;

    public static OperationRecoverability RecoverabilityFor(FileOperationType type) => type switch
    {
        FileOperationType.Move or FileOperationType.Rename => OperationRecoverability.ConditionallyReversible,
        FileOperationType.Recycle => OperationRecoverability.ConditionallyReversible,
        FileOperationType.PermanentDelete => OperationRecoverability.Irreversible,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown operation type.")
    };

    public static bool IsMoveRenameExecutorSupported(FileOperationType type) => type is FileOperationType.Move or FileOperationType.Rename;

    // Retained for Phase 6.1 fixture test compatibility; semantic meaning is Move/Rename only.
    public static bool IsPhase61ExecutorSupported(FileOperationType type) => IsMoveRenameExecutorSupported(type);

    public static bool CanExecute(FileOperationPlan plan, bool isDisposableFixture, out string code, out string message)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OverwritePolicy != OverwritePolicy.Prohibit)
        {
            code = "OVERWRITE_NOT_ALLOWED";
            message = L.T("overwriteは許可されません。");
            return false;
        }
        if (plan.Mode == FileOperationMode.DisposableFixtureExecution)
        {
            if (!IsMoveRenameExecutorSupported(plan.OperationType))
            {
                code = "FIXTURE_OPERATION_TYPE_UNSUPPORTED";
                message = L.T("disposable fixture executionはMove/Rename専用です。");
                return false;
            }
            if (!isDisposableFixture)
            {
                code = "DISPOSABLE_FIXTURE_REQUIRED";
                message = L.T("fixture executionには専用disposable fixture capabilityが必要です。");
                return false;
            }
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null)
            {
                code = "CONFIRMATION_REQUIRED";
                message = L.T("fixture executionには明示的な確認が必要です。");
                return false;
            }
            if (plan.State != FileOperationState.ConfirmationRequired)
            {
                code = "OPERATION_NOT_READY";
                message = L.T("fixture operation planは確認待ち状態ではありません。");
                return false;
            }
            code = "FIXTURE_EXECUTION_ALLOWED";
            message = L.T("disposable fixtureのMove/Renameだけを実行できます。");
            return true;
        }
        if (plan.Mode == FileOperationMode.ProductionDryRun && plan.ProductionPermanentDeleteCapability)
        {
            if (isDisposableFixture)
            {
                code = "PRODUCTION_CAPABILITY_REQUIRED";
                message = L.T("Production PermanentDeleteはfixture capability経由では実行できません。");
                return false;
            }
            if (plan.OperationType != FileOperationType.PermanentDelete || plan.ProductionSameVolumeCapability || plan.ProductionCrossVolumeMoveCapability || plan.ProductionRecycleCapability)
            {
                code = "PERMANENT_DELETE_CAPABILITY_INVALID";
                message = L.T("Production PermanentDelete capabilityは不可逆な単一PermanentDelete専用で、Move/Recycle capabilityと併用できません。");
                return false;
            }
            if (plan.SelectedMediaCount != 1)
            {
                code = "SINGLE_EXPLICIT_SELECTION_REQUIRED";
                message = L.T("Production PermanentDeleteは選択件数が1件の場合だけ実行できます。");
                return false;
            }
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null)
            {
                code = "DRY_RUN_ACKNOWLEDGEMENT_REQUIRED";
                message = L.T("PermanentDeleteにはDry Run acknowledgementが必要です。");
                return false;
            }
            if (plan.FinalExecutionConfirmedAt is null)
            {
                code = "IRREVERSIBLE_FINAL_CONFIRMATION_REQUIRED";
                message = L.T("PermanentDeleteには不可逆であることを明示する別途Final Execution Confirmationが必要です。");
                return false;
            }
            if (plan.State != FileOperationState.FinalConfirmationRequired)
            {
                code = "OPERATION_NOT_READY";
                message = L.T("operation planはFinal Confirmation後のPermanentDelete実行可能状態ではありません。");
                return false;
            }
            if (plan.ValidationStrength != SourceValidationStrength.StrongFullHash || string.IsNullOrWhiteSpace(plan.SourceFullSha256) || string.IsNullOrWhiteSpace(plan.SourceVolumeIdentity) || string.IsNullOrWhiteSpace(plan.SourceFileIdentity))
            {
                code = "PERMANENT_DELETE_SNAPSHOT_INCOMPLETE";
                message = L.T("Production PermanentDeleteにはStrongFullHash、Full SHA-256、Windows volume/file identityのimmutable snapshotが必要です。");
                return false;
            }
            code = "PRODUCTION_PERMANENT_DELETE_EXECUTION_ALLOWED";
            message = L.T("明示選択された単一ファイルをWindows Recycle Binを使用せずに不可逆削除できます。");
            return true;
        }
        if (plan.OperationType == FileOperationType.PermanentDelete)
        {
            code = "PERMANENT_DELETE_CAPABILITY_REQUIRED";
            message = L.T("PermanentDeleteには明示的なProductionPermanentDeleteCapabilityが必要です。");
            return false;
        }
        if (plan.Mode == FileOperationMode.ProductionDryRun && plan.ProductionRecycleCapability)
        {
            if (isDisposableFixture)
            {
                code = "PRODUCTION_CAPABILITY_REQUIRED";
                message = L.T("Production Recycleはfixture capability経由では実行できません。");
                return false;
            }
            if (plan.OperationType != FileOperationType.Recycle || plan.ProductionSameVolumeCapability || plan.ProductionCrossVolumeMoveCapability)
            {
                code = "RECYCLE_CAPABILITY_INVALID";
                message = L.T("Production Recycle capabilityはRecycle専用で、Move capabilityと併用できません。");
                return false;
            }
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null)
            {
                code = "DRY_RUN_ACKNOWLEDGEMENT_REQUIRED";
                message = L.T("Windows Recycle Binへの移動にはDry Run acknowledgementが必要です。");
                return false;
            }
            if (plan.FinalExecutionConfirmedAt is null)
            {
                code = "FINAL_EXECUTION_CONFIRMATION_REQUIRED";
                message = L.T("Windows Recycle Binへの移動には別途Final Execution Confirmationが必要です。");
                return false;
            }
            if (plan.State != FileOperationState.FinalConfirmationRequired)
            {
                code = "OPERATION_NOT_READY";
                message = L.T("operation planはFinal Confirmation後のRecycle実行可能状態ではありません。");
                return false;
            }
            if (string.IsNullOrWhiteSpace(plan.SourceFullSha256) || string.IsNullOrWhiteSpace(plan.SourceVolumeIdentity) || string.IsNullOrWhiteSpace(plan.SourceFileIdentity))
            {
                code = "RECYCLE_SNAPSHOT_INCOMPLETE";
                message = L.T("Production RecycleにはFull SHA-256とWindows volume/file identityのimmutable snapshotが必要です。");
                return false;
            }
            code = "PRODUCTION_RECYCLE_EXECUTION_ALLOWED";
            message = L.T("Windows Recycle Binへの単一ファイル移動だけを実行できます。PermanentDeleteは実行されません。");
            return true;
        }
        if (!IsMoveRenameExecutorSupported(plan.OperationType))
        {
            code = "RECYCLE_NOT_IMPLEMENTED";
            message = L.T("このoperation typeには実行capabilityがありません。");
            return false;
        }
        if (plan.Mode == FileOperationMode.ProductionDryRun && plan.ProductionCrossVolumeMoveCapability)
        {
            if (isDisposableFixture)
            {
                code = "PRODUCTION_CAPABILITY_REQUIRED";
                message = L.T("Production Cross-Volume Moveはfixture capability経由では実行できません。");
                return false;
            }
            if (plan.OperationType != FileOperationType.Move || plan.ProductionSameVolumeCapability)
            {
                code = "CROSS_VOLUME_CAPABILITY_INVALID";
                message = L.T("Cross-Volume capabilityはMove専用で、same-volume capabilityと併用できません。");
                return false;
            }
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null)
            {
                code = "DRY_RUN_ACKNOWLEDGEMENT_REQUIRED";
                message = L.T("Dry Run acknowledgementが必要です。");
                return false;
            }
            if (plan.FinalExecutionConfirmedAt is null)
            {
                code = "FINAL_EXECUTION_CONFIRMATION_REQUIRED";
                message = L.T("verified copy後にsource removalを伴うCross-Volume Moveには別途Final Execution Confirmationが必要です。");
                return false;
            }
            if (plan.State != FileOperationState.FinalConfirmationRequired)
            {
                code = "OPERATION_NOT_READY";
                message = L.T("operation planはFinal Confirmation後のCross-Volume実行可能状態ではありません。");
                return false;
            }
            if (string.IsNullOrWhiteSpace(plan.SourceFullSha256) || string.IsNullOrWhiteSpace(plan.DestinationVolumeIdentity) || string.IsNullOrWhiteSpace(plan.TemporaryDestinationPath) || plan.DestinationLibraryRootId is null)
            {
                code = "CROSS_VOLUME_SNAPSHOT_INCOMPLETE";
                message = L.T("Cross-Volume MoveにはFull SHA-256、destination volume、temporary path、destination rootのimmutable snapshotが必要です。");
                return false;
            }
            code = "PRODUCTION_CROSS_VOLUME_MOVE_EXECUTION_ALLOWED";
            message = L.T("Cross-Volume Move transactionだけを実行できます。");
            return true;
        }
        if (plan.Mode == FileOperationMode.ProductionDryRun && plan.ProductionSameVolumeCapability)
        {
            if (isDisposableFixture)
            {
                code = "PRODUCTION_CAPABILITY_REQUIRED";
                message = L.T("Production same-volume executionはfixture capability経由では実行できません。");
                return false;
            }
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null)
            {
                code = "DRY_RUN_ACKNOWLEDGEMENT_REQUIRED";
                message = L.T("Dry Run acknowledgementが必要です。");
                return false;
            }
            if (plan.FinalExecutionConfirmedAt is null)
            {
                code = "FINAL_EXECUTION_CONFIRMATION_REQUIRED";
                message = L.T("Production Move/Renameには別途Final Execution Confirmationが必要です。");
                return false;
            }
            if (plan.State != FileOperationState.FinalConfirmationRequired)
            {
                code = "OPERATION_NOT_READY";
                message = L.T("operation planはFinal Confirmation後の実行可能状態ではありません。");
                return false;
            }
            code = "PRODUCTION_SAME_VOLUME_EXECUTION_ALLOWED";
            message = L.T("same-volume Move/Renameのみ実行できます。");
            return true;
        }
        code = "PRODUCTION_DRY_RUN_ONLY";
        message = L.T("Production Original MediaではDry Runのみです。実ファイル操作は実行できません。");
        return false;
    }
}

public static class FileOperationStateMachine
{
    private static readonly Dictionary<FileOperationState, FileOperationState[]> Allowed =
        new Dictionary<FileOperationState, FileOperationState[]>
        {
            [FileOperationState.Proposed] = [FileOperationState.Planned, FileOperationState.Cancelled],
            [FileOperationState.Planned] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.Validating] = [FileOperationState.Ready, FileOperationState.ConfirmationRequired, FileOperationState.FinalConfirmationRequired, FileOperationState.SourceChanged, FileOperationState.SourceMissing, FileOperationState.SourceUnavailable, FileOperationState.AccessDenied, FileOperationState.DestinationConflict, FileOperationState.InsufficientSpace, FileOperationState.FreeSpaceUnknown, FileOperationState.ValidationFailed, FileOperationState.RecycleUnavailable, FileOperationState.ProductionExecutionBlocked, FileOperationState.Unsupported, FileOperationState.Cancelled],
            [FileOperationState.Ready] = [FileOperationState.ConfirmationRequired, FileOperationState.Cancelled],
            [FileOperationState.ConfirmationRequired] = [FileOperationState.ConfirmationRequired, FileOperationState.FinalConfirmationRequired, FileOperationState.Executing, FileOperationState.Cancelled, FileOperationState.ProductionExecutionBlocked, FileOperationState.Validating],
            [FileOperationState.FinalConfirmationRequired] = [FileOperationState.FinalConfirmationRequired, FileOperationState.Executing, FileOperationState.Copying, FileOperationState.Cancelled, FileOperationState.Validating],
            [FileOperationState.Executing] = [FileOperationState.Completed, FileOperationState.Recycled, FileOperationState.RecycleFailed, FileOperationState.RecycleUnavailable, FileOperationState.PermanentlyDeleted, FileOperationState.PermanentDeleteFailed, FileOperationState.PartiallyCompleted, FileOperationState.RecoveryRequired, FileOperationState.Cancelled],
            [FileOperationState.Copying] = [FileOperationState.Copying, FileOperationState.CopyVerified, FileOperationState.RecoveryRequired, FileOperationState.Cancelled],
            [FileOperationState.CopyVerified] = [FileOperationState.FinalDestinationPublished, FileOperationState.DestinationConflict, FileOperationState.RecoveryRequired, FileOperationState.Cancelled],
            [FileOperationState.FinalDestinationPublished] = [FileOperationState.FinalDestinationPublished, FileOperationState.Completed, FileOperationState.SourceChangedAfterCopy, FileOperationState.SourceRemovalFailed, FileOperationState.RecoveryRequired, FileOperationState.CannotRollback],
            [FileOperationState.PartiallyCompleted] = [FileOperationState.RecoveryRequired, FileOperationState.Cancelled],
            [FileOperationState.RecoveryRequired] = [FileOperationState.Completed, FileOperationState.Recycled, FileOperationState.RecycleFailed, FileOperationState.PermanentlyDeleted, FileOperationState.PermanentDeleteFailed, FileOperationState.RolledBack, FileOperationState.CannotRollback, FileOperationState.SourceUnavailable, FileOperationState.SourceChangedAfterCopy, FileOperationState.SourceRemovalFailed, FileOperationState.ValidationFailed],
            [FileOperationState.SourceChanged] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.SourceChangedAfterCopy] = [],
            [FileOperationState.SourceMissing] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.SourceUnavailable] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.AccessDenied] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.DestinationConflict] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.InsufficientSpace] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.FreeSpaceUnknown] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.SourceRemovalFailed] = [FileOperationState.RecoveryRequired, FileOperationState.CannotRollback],
            [FileOperationState.ValidationFailed] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.ProductionExecutionBlocked] = [FileOperationState.Validating, FileOperationState.Cancelled],
            [FileOperationState.Unsupported] = [FileOperationState.Cancelled],
            [FileOperationState.Completed] = [],
            [FileOperationState.Recycled] = [],
            [FileOperationState.RecycleFailed] = [],
            [FileOperationState.RecycleUnavailable] = [],
            [FileOperationState.PermanentlyDeleted] = [],
            [FileOperationState.PermanentDeleteFailed] = [],
            [FileOperationState.Cancelled] = [],
            [FileOperationState.RolledBack] = [],
            [FileOperationState.CannotRollback] = []
        };

    public static bool CanTransition(FileOperationState from, FileOperationState to) => Allowed.TryGetValue(from, out var choices) && choices.Contains(to);

    public static void EnsureTransition(FileOperationState from, FileOperationState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid file operation transition: {from} -> {to}.");
    }
}
