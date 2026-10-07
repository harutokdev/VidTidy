using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Safe filesystem boundary. Phase 6.2 permits only explicit same-volume production Move/Rename after
/// Dry Run acknowledgement and an independent Final Execution Confirmation. Recycle, PermanentDelete,
/// overwrite, cross-volume copy/move, and automatic actions have no execution path.
/// </summary>
public sealed record ProductionSameVolumeExecutionTiming(
    Guid OperationId,
    FileOperationType OperationType,
    long FileSystemMoveMilliseconds,
    long DestinationVerificationMilliseconds,
    long DbPublishMilliseconds,
    long TotalMilliseconds);

/// <summary>Measured only from isolated production-like tests; production UI does not infer progress from this record.</summary>
public sealed record ProductionCrossVolumeMoveExecutionTiming(
    Guid OperationId,
    long CopyMilliseconds,
    long TempHashVerificationMilliseconds,
    long FinalPublishMilliseconds,
    long SourceRemovalMilliseconds,
    long DbPublishMilliseconds,
    long TotalMilliseconds);

/// <summary>Measured only in isolated production-like tests; no timing value authorizes a mutation.</summary>
public sealed record ProductionRecycleExecutionTiming(
    Guid OperationId,
    long FinalRevalidationMilliseconds,
    long RecycleShellMilliseconds,
    long SourceObservationMilliseconds,
    long DbLifecyclePublishMilliseconds,
    long TotalMilliseconds);

/// <summary>Measured only in isolated tests; no timing value authorizes irreversible deletion.</summary>
public sealed record ProductionPermanentDeleteExecutionTiming(
    Guid OperationId,
    long FinalRevalidationMilliseconds,
    long DeleteApiMilliseconds,
    long SourceObservationMilliseconds,
    long DbLifecyclePublishMilliseconds,
    long TotalMilliseconds);

public sealed partial class SafeFileOperationService
{
    private readonly ILibraryRepository _repository;
    private readonly IStructuredLogger _log;
    private readonly Func<FileOperationPlan, CancellationToken, Task>? _beforeProductionDbPublish;
    private readonly Action<ProductionSameVolumeExecutionTiming>? _productionTimingObserver;
    private readonly Func<FileOperationPlan, CrossVolumeMoveStage, CancellationToken, Task>? _beforeCrossVolumeStage;
    private readonly Action<ProductionCrossVolumeMoveExecutionTiming>? _crossVolumeTimingObserver;
    private readonly IWindowsRecycleBin _windowsRecycleBin;
    private readonly Func<FileOperationPlan, CancellationToken, Task>? _beforeProductionRecycleLifecyclePublish;
    private readonly Action<ProductionRecycleExecutionTiming>? _productionRecycleTimingObserver;
    private readonly IWindowsPermanentDelete _windowsPermanentDelete;
    private readonly Func<FileOperationPlan, CancellationToken, Task>? _beforeProductionPermanentDeleteLifecyclePublish;
    private readonly Action<ProductionPermanentDeleteExecutionTiming>? _productionPermanentDeleteTimingObserver;

    /// <summary>
    /// The optional callback is a test-only failure-injection seam invoked after filesystem verification
    /// and immediately before C# path publish. Production composition never supplies it.
    /// </summary>
    public SafeFileOperationService(
        ILibraryRepository repository,
        IStructuredLogger log,
        Func<FileOperationPlan, CancellationToken, Task>? beforeProductionDbPublish = null,
        Action<ProductionSameVolumeExecutionTiming>? productionTimingObserver = null,
        Func<FileOperationPlan, CrossVolumeMoveStage, CancellationToken, Task>? beforeCrossVolumeStage = null,
        Action<ProductionCrossVolumeMoveExecutionTiming>? crossVolumeTimingObserver = null,
        IWindowsRecycleBin? windowsRecycleBin = null,
        Func<FileOperationPlan, CancellationToken, Task>? beforeProductionRecycleLifecyclePublish = null,
        Action<ProductionRecycleExecutionTiming>? productionRecycleTimingObserver = null,
        IWindowsPermanentDelete? windowsPermanentDelete = null,
        Func<FileOperationPlan, CancellationToken, Task>? beforeProductionPermanentDeleteLifecyclePublish = null,
        Action<ProductionPermanentDeleteExecutionTiming>? productionPermanentDeleteTimingObserver = null)
    {
        _repository = repository;
        _log = log;
        _beforeProductionDbPublish = beforeProductionDbPublish;
        _productionTimingObserver = productionTimingObserver;
        _beforeCrossVolumeStage = beforeCrossVolumeStage;
        _crossVolumeTimingObserver = crossVolumeTimingObserver;
        _windowsRecycleBin = windowsRecycleBin ?? new WindowsRecycleBinShell();
        _beforeProductionRecycleLifecyclePublish = beforeProductionRecycleLifecyclePublish;
        _productionRecycleTimingObserver = productionRecycleTimingObserver;
        _windowsPermanentDelete = windowsPermanentDelete ?? new WindowsPermanentDeleteFileSystem();
        _beforeProductionPermanentDeleteLifecyclePublish = beforeProductionPermanentDeleteLifecyclePublish;
        _productionPermanentDeleteTimingObserver = productionPermanentDeleteTimingObserver;
    }

    public async Task<FileOperationPlan> CreatePlanAsync(
        Guid mediaFileId,
        FileOperationType operationType,
        FileOperationMode mode,
        string? destinationPath,
        string? reviewItemId,
        string? reviewContext,
        SourceValidationStrength validationStrength,
        CancellationToken cancellationToken)
    {
        var media = await _repository.GetMediaFileAsync(mediaFileId, cancellationToken) ?? throw new InvalidOperationException("Operation source media was not found.");
        if (media.PresenceState != PresenceState.Present)
            throw new InvalidOperationException("Only a Present media item can create a filesystem operation plan. Recycled, unavailable, and missing rows retain history but are not operation targets.");
        var root = await _repository.GetLibraryRootAsync(media.LibraryRootId, cancellationToken) ?? throw new InvalidOperationException("Operation source root was not found.");
        if (operationType is FileOperationType.Move or FileOperationType.Rename && string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Move/Rename requires an explicit destination.", nameof(destinationPath));
        if (mode == FileOperationMode.DisposableFixtureExecution && operationType is not (FileOperationType.Move or FileOperationType.Rename))
            throw new InvalidOperationException("Fixture execution supports Move/Rename only.");

        var now = DateTimeOffset.UtcNow;
        var proposal = new FileOperationProposal(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, reviewItemId, reviewContext, operationType, now);
        await _repository.CreateFileOperationProposalAsync(proposal, cancellationToken);
        var intent = new FileOperationIntent(Guid.NewGuid(), proposal.ProposalId, operationType, destinationPath, OverwritePolicy.Prohibit, DestinationConflictPolicy.Abort, now, null);
        await _repository.CreateFileOperationIntentAsync(intent, cancellationToken);

        var fullHashEvidence = await _repository.GetCurrentFullHashEvidenceAsync(media.MediaFileId, media.MediaContentId, "SHA-256", "full-sha256-v1", cancellationToken);
        var sourceFullSha256 = fullHashEvidence?.Digest;
        var operationId = Guid.NewGuid();
        var destinationNormalized = string.IsNullOrWhiteSpace(destinationPath) ? null : PathPolicy.NormalizeForIdentity(destinationPath);
        LibraryRoot? destinationRoot = null;
        string? destinationVolumeIdentity = null;
        string? temporaryDestinationPath = null;
        string? temporaryDestinationNormalizedPath = null;
        if (!string.IsNullOrWhiteSpace(destinationPath) && Path.IsPathFullyQualified(destinationPath))
        {
            destinationRoot = (await _repository.GetLibraryRootsAsync(cancellationToken))
                .Where(candidate => PathPolicy.IsWithinRoot(candidate.ConfiguredPath, destinationPath))
                .OrderByDescending(candidate => candidate.NormalizedPath.Length)
                .FirstOrDefault();
            var destinationParent = Path.GetDirectoryName(destinationPath);
            if (destinationRoot is not null && !string.IsNullOrWhiteSpace(destinationParent) && Directory.Exists(destinationParent) && !PathPolicy.IsReparsePoint(new DirectoryInfo(destinationParent)))
            {
                destinationVolumeIdentity = WindowsVolumeIdentityReader.TryGetForPath(destinationParent);
                temporaryDestinationPath = Path.Combine(destinationParent, $".local-media-ai-operation-{operationId:N}.partial");
                temporaryDestinationNormalizedPath = PathPolicy.NormalizeForIdentity(temporaryDestinationPath);
            }
        }
        var productionSameVolumeCapability = mode == FileOperationMode.ProductionDryRun && FileOperationPolicy.IsMoveRenameExecutorSupported(operationType) &&
            !string.IsNullOrWhiteSpace(media.VolumeIdentity) && string.Equals(media.VolumeIdentity, destinationVolumeIdentity, StringComparison.OrdinalIgnoreCase);
        var productionCrossVolumeMoveCapability = mode == FileOperationMode.ProductionDryRun && operationType == FileOperationType.Move && destinationRoot is not null &&
            !string.IsNullOrWhiteSpace(media.VolumeIdentity) && !string.IsNullOrWhiteSpace(destinationVolumeIdentity) && !string.Equals(media.VolumeIdentity, destinationVolumeIdentity, StringComparison.OrdinalIgnoreCase);
        var productionRecycleCapability = mode == FileOperationMode.ProductionDryRun && operationType == FileOperationType.Recycle && media.PresenceState == PresenceState.Present &&
            !string.IsNullOrWhiteSpace(media.VolumeIdentity) && !string.IsNullOrWhiteSpace(media.FileIdentity);
        var productionPermanentDeleteCapability = mode == FileOperationMode.ProductionDryRun && operationType == FileOperationType.PermanentDelete && media.PresenceState == PresenceState.Present &&
            !string.IsNullOrWhiteSpace(media.VolumeIdentity) && !string.IsNullOrWhiteSpace(media.FileIdentity);
        if (productionSameVolumeCapability || productionCrossVolumeMoveCapability || productionRecycleCapability || productionPermanentDeleteCapability)
        {
            // Every production mutation plan snapshots Full SHA-256 at creation independently of background hash-job availability.
            validationStrength = SourceValidationStrength.StrongFullHash;
            sourceFullSha256 ??= await FileHash.Sha256Async(media.Path, cancellationToken);
        }
        var plan = new FileOperationPlan(
            operationId, intent.IntentId, root.LibraryRootId, media.MediaFileId, media.MediaContentId, operationType, mode,
            media.Path, media.NormalizedPath, media.VolumeIdentity, media.FileIdentity, media.SizeBytes, media.LastWriteTimeUtc,
            media.QuickFingerprint, sourceFullSha256, validationStrength, destinationPath, destinationNormalized,
            DescribeEffect(operationType, media.Path, destinationPath, productionCrossVolumeMoveCapability), OverwritePolicy.Prohibit, DestinationConflictPolicy.Abort,
            FileOperationPolicy.RecoverabilityFor(operationType), false, now, null, FileOperationState.Planned, null, null, now,
            FinalExecutionConfirmedAt: null, ProductionSameVolumeCapability: productionSameVolumeCapability,
            DestinationLibraryRootId: destinationRoot?.LibraryRootId, DestinationRootPath: destinationRoot?.ConfiguredPath,
            DestinationVolumeIdentity: destinationVolumeIdentity, TemporaryDestinationPath: temporaryDestinationPath,
            TemporaryDestinationNormalizedPath: temporaryDestinationNormalizedPath, DestinationFreeSpaceSnapshotBytes: null,
            ProductionCrossVolumeMoveCapability: productionCrossVolumeMoveCapability, CrossVolumeMoveStage: CrossVolumeMoveStage.None,
            ProductionRecycleCapability: productionRecycleCapability,
            ProductionPermanentDeleteCapability: productionPermanentDeleteCapability,
            SelectedMediaCount: 1);
        return await _repository.CreateFileOperationPlanAsync(plan, cancellationToken);
    }

    public async Task<FileOperationDryRun> RunDryRunAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (IsTerminal(plan.State)) throw new InvalidOperationException("Terminal operation plans cannot be revalidated.");
        if (plan.State != FileOperationState.Validating)
            plan = await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.Validating, null, null, null, cancellationToken);

        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        var target = preflight.State == FileOperationState.Ready ? FileOperationState.ConfirmationRequired : preflight.State;
        plan = await _repository.TransitionFileOperationPlanAsync(plan.OperationId, target, target == FileOperationState.ConfirmationRequired ? null : preflight.Code, target == FileOperationState.ConfirmationRequired ? null : preflight.Message, null, cancellationToken);
        var executionEnabled = FileOperationPolicy.CanExecute(plan, false, out _, out var policyMessage);
        var capabilityMessage = plan.ProductionPermanentDeleteCapability
            ? L.T("Production PERMANENT DELETEはWindows Recycle Binを使用せず、単一の明示選択を不可逆に削除します。Dry Run acknowledgementと別の不可逆Final Execution Confirmation後だけ実行できます。")
            : plan.ProductionRecycleCapability
                ? L.T("Production RECYCLEはWindows Recycle Binへの移動だけを実行し、PermanentDeleteではありません。Dry Run acknowledgementと別のFinal Execution Confirmation後だけ実行できます。")
                : plan.ProductionCrossVolumeMoveCapability
                ? L.T("Production CROSS-VOLUME MOVEはtemporary copy、Full SHA-256 verification、final publish、source removalの順で実行され、Dry Run acknowledgementと別のFinal Execution Confirmation後だけ実行できます。")
                : plan.ProductionSameVolumeCapability
                    ? L.T("Production same-volume Move/Renameは、Dry Run acknowledgementと別のFinal Execution Confirmation後だけ実行できます。")
                    : policyMessage;
        return new FileOperationDryRun(plan, preflight, plan.ExpectedFilesystemEffect, RecoverabilitySummary(plan), $"{preflight.Message} {capabilityMessage}", executionEnabled);
    }

    /// <summary>
    /// Records Dry Run acknowledgement only. It is deliberately not Final Execution Confirmation and never mutates a file.
    /// Move/Rename production plans remain in ConfirmationRequired so a separate final confirmation can follow.
    /// </summary>
    public async Task<FileOperationPlan> AcknowledgeProductionDryRunAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (plan.Mode != FileOperationMode.ProductionDryRun) throw new InvalidOperationException("This acknowledgement is for production Dry Run only.");
        if (plan.State != FileOperationState.ConfirmationRequired) throw new InvalidOperationException("Operation plan requires a successful preflight before acknowledgement.");

        if (plan.ProductionSameVolumeCapability || plan.ProductionCrossVolumeMoveCapability || plan.ProductionRecycleCapability || plan.ProductionPermanentDeleteCapability)
        {
            var acknowledged = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, DateTimeOffset.UtcNow, cancellationToken);
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, acknowledged.MediaFileId, acknowledged.SourcePath, acknowledged.DestinationPath, OperationItemResult.DryRun, null, DateTimeOffset.UtcNow, "DRY_RUN_ACKNOWLEDGED", "Dry Run acknowledgement was recorded; Final Execution Confirmation is still required and no filesystem mutation was attempted.", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
            return acknowledged;
        }

        var blocked = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ProductionExecutionBlocked, "PRODUCTION_DRY_RUN_ONLY", "This operation type has no Phase 6.2 production executor. No filesystem mutation was attempted.", DateTimeOffset.UtcNow, cancellationToken);
        await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, blocked.MediaFileId, blocked.SourcePath, blocked.DestinationPath, OperationItemResult.DryRun, null, DateTimeOffset.UtcNow, "PRODUCTION_DRY_RUN_ONLY", "Dry Run was acknowledged; no filesystem mutation was attempted.", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
        return blocked;
    }

    /// <summary>
    /// Executes final source/destination revalidation and only then records the distinct Final Execution Confirmation.
    /// This is not called by Dry Run acknowledgement and does not perform a filesystem mutation.
    /// </summary>
    public async Task<FileOperationPlan> ConfirmProductionSameVolumeExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (plan.Mode != FileOperationMode.ProductionDryRun || !plan.ProductionSameVolumeCapability)
            throw new InvalidOperationException("Only an explicit production same-volume Move/Rename plan can receive Final Execution Confirmation.");
        if (plan.State != FileOperationState.ConfirmationRequired || !plan.UserAcknowledged || plan.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement is required before Final Execution Confirmation.");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, null, cancellationToken);
        var confirmed = await _repository.ConfirmProductionSameVolumeExecutionAsync(operationId, cancellationToken);
        await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), operationId, DateTimeOffset.UtcNow, "FinalConfirmationPreflightReady", JsonSerializer.Serialize(new { preflight.SameVolumeVerified, preflight.DestinationConflict, preflight.FullHashMatches })), cancellationToken);
        return confirmed;
    }

    /// <summary>
    /// Revalidates and durably records the separate Final Execution Confirmation for a Cross-Volume Move.
    /// No copy, publish, or source removal happens here.
    /// </summary>
    public async Task<FileOperationPlan> ConfirmProductionCrossVolumeMoveExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsCrossVolumeMove(plan))
            throw new InvalidOperationException("Only an explicit Production Cross-Volume Move plan can receive this Final Execution Confirmation.");
        if (plan.State != FileOperationState.ConfirmationRequired || !plan.UserAcknowledged || plan.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement is required before Final Execution Confirmation.");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, null, cancellationToken);
        var confirmed = await _repository.ConfirmProductionCrossVolumeMoveExecutionAsync(operationId, cancellationToken);
        await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), operationId, DateTimeOffset.UtcNow, "FinalCrossVolumeConfirmationPreflightReady", JsonSerializer.Serialize(new { preflight.CrossVolumeVerified, preflight.DestinationConflict, preflight.TemporaryDestinationAvailable, preflight.RequiredDestinationFreeBytes, preflight.FullHashMatches })), cancellationToken);
        return confirmed;
    }

    /// <summary>
    /// Revalidates and durably records the separate Final Execution Confirmation for an explicit Windows Recycle Bin operation.
    /// No Shell mutation happens here.
    /// </summary>
    public async Task<FileOperationPlan> ConfirmProductionRecycleExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionRecycle(plan))
            throw new InvalidOperationException("Only an explicit Production Recycle plan can receive this Final Execution Confirmation.");
        if (plan.State != FileOperationState.ConfirmationRequired || !plan.UserAcknowledged || plan.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement is required before Final Execution Confirmation.");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, null, cancellationToken);
        var confirmed = await _repository.ConfirmProductionRecycleExecutionAsync(operationId, cancellationToken);
        await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), operationId, DateTimeOffset.UtcNow, "FinalRecycleConfirmationPreflightReady", JsonSerializer.Serialize(new { preflight.FullHashMatches, preflight.StableIdentityMatches, preflight.SizeMatches, preflight.MtimeMatches, preflight.QuickFingerprintMatches, PermanentDelete = false })), cancellationToken);
        return confirmed;
    }

    /// <summary>
    /// Revalidates and durably records the independent irreversible Final Execution Confirmation for one explicit PermanentDelete.
    /// No filesystem mutation happens here.
    /// </summary>
    public async Task<FileOperationPlan> ConfirmProductionPermanentDeleteExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionPermanentDelete(plan) || plan.SelectedMediaCount != 1)
            throw new InvalidOperationException("Only an explicit single-selection Production PermanentDelete plan can receive irreversible Final Execution Confirmation.");
        if (plan.State != FileOperationState.ConfirmationRequired || !plan.UserAcknowledged || plan.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement is required before irreversible Final Execution Confirmation.");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, null, cancellationToken);
        var confirmed = await _repository.ConfirmProductionPermanentDeleteExecutionAsync(operationId, cancellationToken);
        await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), operationId, DateTimeOffset.UtcNow, "FinalPermanentDeleteConfirmationPreflightReady", JsonSerializer.Serialize(new { preflight.FullHashMatches, preflight.StableIdentityMatches, preflight.SizeMatches, preflight.MtimeMatches, preflight.QuickFingerprintMatches, confirmed.SelectedMediaCount, WindowsRecycleBinUsed = false, Irreversible = true })), cancellationToken);
        return confirmed;
    }

    /// <summary>Test-only acknowledgement for a plan already validated in a disposable fixture.</summary>
    public async Task<FileOperationPlan> AcknowledgeDisposableFixtureAsync(Guid operationId, DisposableFixtureCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        EnsureFixtureContainment(plan, capability);
        if (plan.Mode != FileOperationMode.DisposableFixtureExecution || plan.State != FileOperationState.ConfirmationRequired)
            throw new InvalidOperationException("Disposable fixture acknowledgement requires a validated fixture-only plan.");
        return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<FileOperationPlan> ExecuteDisposableFixtureAsync(Guid operationId, DisposableFixtureCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        EnsureFixtureContainment(plan, capability);
        if (plan.State != FileOperationState.ConfirmationRequired) throw new InvalidOperationException("Fixture execution requires successful dry-run and acknowledgement state.");
        if (!FileOperationPolicy.CanExecute(plan, true, out var code, out var message)) throw new InvalidOperationException($"{code}: {message}");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);
        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.ConfirmationRequired, null, null, null, cancellationToken);
        if (!FileOperationPolicy.CanExecute(plan, true, out code, out message)) throw new InvalidOperationException($"{code}: {message}");

        cancellationToken.ThrowIfCancellationRequested();
        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Executing, null, null, null, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        var beforeIdentity = await ReadIdentityJsonAsync(plan.SourcePath, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(plan.SourcePath, plan.DestinationPath!, false);
            cancellationToken.ThrowIfCancellationRequested();
            var afterIdentity = await VerifyDestinationAsync(plan, cancellationToken);
            await ReconcileRootAsync(plan.LibraryRootId, cancellationToken);
            await EnsureDatabasePublishAsync(plan, cancellationToken);
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.Completed, started, DateTimeOffset.UtcNow, null, null, beforeIdentity, afterIdentity, started, DateTimeOffset.UtcNow), cancellationToken);
            return await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.Completed, null, null, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, null, "CANCELLED_DURING_EXECUTION", "Cancellation occurred during fixture execution; filesystem state requires recovery inspection.", CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, null, "FIXTURE_OPERATION_FAILED", "Fixture operation did not complete deterministically; filesystem state requires recovery inspection.", CancellationToken.None, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The only Phase 6.2 production mutation boundary. It accepts only an already final-confirmed,
    /// same-volume Move/Rename plan and never derives authority from review or AI state.
    /// </summary>
    public async Task<FileOperationPlan> ExecuteProductionSameVolumeAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.CanExecute(plan, false, out var code, out var message)) throw new InvalidOperationException($"{code}: {message}");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.FinalConfirmationRequired, null, null, null, cancellationToken);
        if (!FileOperationPolicy.CanExecute(plan, false, out code, out message)) throw new InvalidOperationException($"{code}: {message}");
        cancellationToken.ThrowIfCancellationRequested();

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Executing, null, null, null, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        var beforeIdentity = await ReadIdentityJsonAsync(plan.SourcePath, cancellationToken);
        try
        {
            var totalTimer = Stopwatch.StartNew();
            var fileSystemTimer = Stopwatch.StartNew();
            File.Move(plan.SourcePath, plan.DestinationPath!, false);
            fileSystemTimer.Stop();
            var destinationVerificationTimer = Stopwatch.StartNew();
            var afterIdentity = await VerifyDestinationAsync(plan, cancellationToken);
            destinationVerificationTimer.Stop();
            var dbPublishTimer = Stopwatch.StartNew();
            if (_beforeProductionDbPublish is not null) await _beforeProductionDbPublish(plan, cancellationToken);
            if (FileOperationPolicy.IsSameVolumeCrossRootMove(plan))
                await _repository.PublishCrossVolumeMoveAsync(await BuildSameVolumeCrossRootPublishAsync(plan, cancellationToken), cancellationToken);
            else
                await ReconcileRootAsync(plan.LibraryRootId, cancellationToken);
            await EnsureDatabasePublishAsync(plan, cancellationToken);
            dbPublishTimer.Stop();
            totalTimer.Stop();
            PublishProductionTiming(new ProductionSameVolumeExecutionTiming(plan.OperationId, plan.OperationType, fileSystemTimer.ElapsedMilliseconds, destinationVerificationTimer.ElapsedMilliseconds, dbPublishTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds));
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.Completed, started, DateTimeOffset.UtcNow, null, null, beforeIdentity, afterIdentity, started, DateTimeOffset.UtcNow), cancellationToken);
            return await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.Completed, null, null, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, null, "CANCELLED_DURING_EXECUTION", "Cancellation occurred after Production execution began; filesystem state requires recovery inspection.", CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, null, "PRODUCTION_OPERATION_OR_DB_PUBLISH_FAILED", "Filesystem effect or C# DB publish did not complete deterministically; recovery inspection is required.", CancellationToken.None, ex.Message);
            return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
        }
    }

    /// <summary>
    /// The only Phase 6.3 cross-volume mutation boundary. It is not a generic Move fallback: it accepts
    /// only an already Final-Confirmed single-item Move plan with the explicit Cross-Volume capability.
    /// The original source is removed only after an operation-specific destination temp copy and final
    /// destination both satisfy the immutable Full SHA-256 snapshot.
    /// </summary>
    public async Task<FileOperationPlan> ExecuteProductionCrossVolumeMoveAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsCrossVolumeMove(plan)) throw new InvalidOperationException("CROSS_VOLUME_CAPABILITY_REQUIRED: explicit Cross-Volume Move capability is required.");
        if (!FileOperationPolicy.CanExecute(plan, false, out var code, out var message)) throw new InvalidOperationException($"{code}: {message}");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);
        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.FinalConfirmationRequired, null, null, null, cancellationToken);
        if (!FileOperationPolicy.CanExecute(plan, false, out code, out message)) throw new InvalidOperationException($"{code}: {message}");

        var started = DateTimeOffset.UtcNow;
        var beforeIdentity = await ReadIdentityJsonAsync(plan.SourcePath, cancellationToken);
        var finalPublished = false;
        var sourceRemovalAttempted = false;
        try
        {
            var totalTimer = Stopwatch.StartNew();
            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.Copying, CrossVolumeMoveStage.Copying, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.Copying, cancellationToken);

            var copyTimer = Stopwatch.StartNew();
            await CopySourceToTemporaryAsync(plan, cancellationToken);
            copyTimer.Stop();
            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.Copying, CrossVolumeMoveStage.TempCopied, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.TempCopied, cancellationToken);

            var tempHashTimer = Stopwatch.StartNew();
            await VerifyCrossVolumeTemporaryAsync(plan, cancellationToken);
            tempHashTimer.Stop();
            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.CopyVerified, CrossVolumeMoveStage.TempHashVerified, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.TempHashVerified, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(plan.DestinationPath!) || Directory.Exists(plan.DestinationPath!))
                return await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.DestinationConflict, CrossVolumeMoveStage.TempHashVerified, "DESTINATION_CONFLICT_AFTER_COPY", "final destination appeared before temporary artifact could be published; source was preserved.", cancellationToken);
            var finalPublishTimer = Stopwatch.StartNew();
            File.Move(plan.TemporaryDestinationPath!, plan.DestinationPath!, false);
            finalPublishTimer.Stop();
            finalPublished = true;
            var destinationPublish = await VerifyCrossVolumeFinalDestinationAsync(plan, cancellationToken);
            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.FinalDestinationPublished, CrossVolumeMoveStage.FinalPublished, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.FinalPublished, cancellationToken);

            var sourceRevalidation = await ValidateSourceForCrossVolumeRemovalAsync(plan, cancellationToken);
            if (!sourceRevalidation.Matches)
            {
                await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, sourceRevalidation.Code, sourceRevalidation.Message, beforeIdentity, await ReadIdentityJsonAsync(plan.DestinationPath!, cancellationToken), started, DateTimeOffset.UtcNow), cancellationToken);
                return await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.SourceChangedAfterCopy, CrossVolumeMoveStage.FinalPublished, sourceRevalidation.Code, sourceRevalidation.Message, cancellationToken);
            }

            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.FinalDestinationPublished, CrossVolumeMoveStage.SourceRemovalPending, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.SourceRemovalPending, cancellationToken);
            sourceRemovalAttempted = true;
            var sourceRemovalTimer = Stopwatch.StartNew();
            File.Delete(plan.SourcePath);
            sourceRemovalTimer.Stop();
            if (File.Exists(plan.SourcePath)) throw new IOException("Cross-Volume source removal did not remove the source path.");
            plan = await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.FinalDestinationPublished, CrossVolumeMoveStage.SourceRemoved, null, null, cancellationToken);
            await InvokeCrossVolumeStageAsync(plan, CrossVolumeMoveStage.SourceRemoved, cancellationToken);

            var dbPublishTimer = Stopwatch.StartNew();
            if (_beforeProductionDbPublish is not null) await _beforeProductionDbPublish(plan, cancellationToken);
            await _repository.PublishCrossVolumeMoveAsync(destinationPublish, cancellationToken);
            dbPublishTimer.Stop();
            totalTimer.Stop();
            var afterIdentity = await ReadIdentityJsonAsync(plan.DestinationPath!, cancellationToken);
            PublishCrossVolumeTiming(new ProductionCrossVolumeMoveExecutionTiming(plan.OperationId, copyTimer.ElapsedMilliseconds, tempHashTimer.ElapsedMilliseconds, finalPublishTimer.ElapsedMilliseconds, sourceRemovalTimer.ElapsedMilliseconds, dbPublishTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds));
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.Completed, started, DateTimeOffset.UtcNow, null, null, beforeIdentity, afterIdentity, started, DateTimeOffset.UtcNow), cancellationToken);
            return await _repository.TransitionCrossVolumeMovePlanAsync(operationId, FileOperationState.Completed, CrossVolumeMoveStage.DbPublished, null, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkCrossVolumeRecoveryRequiredAsync(plan, started, beforeIdentity, "CANCELLED_DURING_CROSS_VOLUME_EXECUTION", "Cross-Volume Move was cancelled after execution began; source and operation-specific artifacts were preserved for recovery inspection.", CancellationToken.None);
            throw;
        }
        catch (UnauthorizedAccessException ex) when (finalPublished && sourceRemovalAttempted)
        {
            await MarkCrossVolumeSourceRemovalFailedAsync(plan, started, beforeIdentity, "SOURCE_REMOVAL_ACCESS_DENIED", "verified destination was preserved but original source removal was denied; no retry or automatic deletion was performed.", ex.Message, CancellationToken.None);
            return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkCrossVolumeRecoveryRequiredAsync(plan, started, beforeIdentity, "CROSS_VOLUME_COPY_OR_PUBLISH_FAILED", "Cross-Volume Move did not complete deterministically; source, temporary artifact, and destination require observation-based recovery inspection.", CancellationToken.None, ex.Message);
            return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
        }
    }

    /// <summary>
    /// The only Phase 6.4 Production Recycle mutation boundary. It accepts one already final-confirmed,
    /// StrongFullHash-validated Recycle plan and invokes only the Windows Shell Recycle Bin adapter.
    /// It contains no File.Delete, PermanentDelete, SHFileOperation, or retry/fallback path.
    /// </summary>
    public async Task<FileOperationPlan> ExecuteProductionRecycleAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionRecycle(plan)) throw new InvalidOperationException("RECYCLE_CAPABILITY_REQUIRED: explicit Production Recycle capability is required.");
        if (!FileOperationPolicy.CanExecute(plan, false, out var code, out var message)) throw new InvalidOperationException($"{code}: {message}");

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var finalValidationTimer = Stopwatch.StartNew();
        var preflight = await ValidateAsync(plan, cancellationToken);
        finalValidationTimer.Stop();
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);
        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.FinalConfirmationRequired, null, null, null, cancellationToken);
        if (!FileOperationPolicy.CanExecute(plan, false, out code, out message)) throw new InvalidOperationException($"{code}: {message}");

        var started = DateTimeOffset.UtcNow;
        var beforeIdentity = await ReadIdentityJsonAsync(plan.SourcePath, cancellationToken);
        var totalTimer = Stopwatch.StartNew();
        try
        {
            plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Executing, null, null, null, cancellationToken);
            var shellTimer = Stopwatch.StartNew();
            var shellResult = await _windowsRecycleBin.RecycleAsync(plan.SourcePath, cancellationToken);
            shellTimer.Stop();
            var observationTimer = Stopwatch.StartNew();
            var sourceObservation = ObserveSourcePath(plan.SourcePath);
            observationTimer.Stop();

            if (!shellResult.ShellSucceeded)
            {
                var terminalState = shellResult.ErrorCode?.StartsWith("RECYCLE_UNAVAILABLE", StringComparison.Ordinal) == true ? FileOperationState.RecycleUnavailable : FileOperationState.RecycleFailed;
                var failureCode = shellResult.ErrorCode ?? "RECYCLE_SHELL_FAILED";
                var failureMessage = shellResult.ErrorMessage ?? "Windows Recycle Bin operation failed; no PermanentDelete fallback was attempted.";
                await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, failureCode, failureMessage, beforeIdentity, null, started, DateTimeOffset.UtcNow), cancellationToken);
                return await _repository.TransitionFileOperationPlanAsync(operationId, terminalState, failureCode, failureMessage, null, cancellationToken);
            }
            if (shellResult.AnyOperationAborted)
            {
                const string abortedCode = "RECYCLE_SHELL_ABORTED";
                const string abortedMessage = "Windows Shell reported an aborted Recycle operation; no PermanentDelete fallback or automatic retry was attempted.";
                await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, abortedCode, abortedMessage, beforeIdentity, null, started, DateTimeOffset.UtcNow), cancellationToken);
                return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.RecycleFailed, abortedCode, abortedMessage, null, cancellationToken);
            }
            if (sourceObservation != SourcePathObservation.Absent)
            {
                var observationCode = sourceObservation == SourcePathObservation.Present ? "RECYCLE_SHELL_SUCCESS_SOURCE_STILL_PRESENT" : "RECYCLE_SOURCE_OBSERVATION_UNKNOWN";
                var observationMessage = sourceObservation == SourcePathObservation.Present
                    ? "Windows Shell returned without abort, but the original source path remains; Recycled was not inferred and no second Recycle was attempted."
                    : "Windows Shell returned without abort, but the original source path could not be observed as absent; Recycled was not inferred and no retry was attempted.";
                await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), observationCode, observationMessage, CancellationToken.None);
                return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
            }

            var dbTimer = Stopwatch.StartNew();
            if (_beforeProductionRecycleLifecyclePublish is not null) await _beforeProductionRecycleLifecyclePublish(plan, cancellationToken);
            var lifecycle = await _repository.PublishRecycleLifecycleAsync(new RecycleDatabasePublish(plan.OperationId, plan.MediaFileId, plan.MediaContentId, plan.SourcePath, plan.SourceNormalizedPath, plan.SourceFullSha256!, plan.SourceSizeBytes, plan.SourceVolumeIdentity, plan.SourceFileIdentity, DateTimeOffset.UtcNow), cancellationToken);
            dbTimer.Stop();
            totalTimer.Stop();
            PublishProductionRecycleTiming(new ProductionRecycleExecutionTiming(operationId, finalValidationTimer.ElapsedMilliseconds, shellTimer.ElapsedMilliseconds, observationTimer.ElapsedMilliseconds, dbTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds));
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Completed, started, DateTimeOffset.UtcNow, null, "Windows Recycle Bin accepted the source and C# lifecycle state was published; PermanentDelete was not used.", beforeIdentity, JsonSerializer.Serialize(new { lifecycle.LifecycleState, lifecycle.RecycledAt, lifecycle.SourceAvailable }), started, DateTimeOffset.UtcNow), cancellationToken);
            return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Recycled, null, null, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), "RECYCLE_CANCELLED_AFTER_EXECUTION_STARTED", "Cancellation occurred after Recycle execution began; restart observation must not re-run Windows Shell Recycle.", CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), "RECYCLE_OR_LIFECYCLE_PUBLISH_FAILED", "Recycle filesystem effect or C# lifecycle publish did not complete deterministically; recovery observation is required and no retry will occur.", CancellationToken.None, ex.Message);
            return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
        }
    }

    /// <summary>
    /// The only Phase 6.5 permanent filesystem mutation boundary. It accepts one already final-confirmed
    /// StrongFullHash ProductionPermanentDelete plan and never calls Windows Recycle Bin or any generic delete helper.
    /// </summary>
    public async Task<FileOperationPlan> ExecuteProductionPermanentDeleteAsync(Guid operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionPermanentDelete(plan) || plan.SelectedMediaCount != 1)
            throw new InvalidOperationException("PERMANENT_DELETE_CAPABILITY_REQUIRED: explicit single-selection PermanentDelete capability is required.");
        if (!FileOperationPolicy.CanExecute(plan, false, out var code, out var message)) throw new InvalidOperationException($"{code}: {message}");

        var finalRevalidationTimer = Stopwatch.StartNew();
        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Validating, null, null, null, cancellationToken);
        var preflight = await ValidateAsync(plan, cancellationToken);
        await _repository.RecordFileOperationPreflightAsync(preflight, cancellationToken);
        if (preflight.State != FileOperationState.Ready)
            return await _repository.TransitionFileOperationPlanAsync(operationId, preflight.State, preflight.Code, preflight.Message, null, cancellationToken);
        finalRevalidationTimer.Stop();

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.FinalConfirmationRequired, null, null, null, cancellationToken);
        if (!FileOperationPolicy.CanExecute(plan, false, out code, out message)) throw new InvalidOperationException($"{code}: {message}");
        cancellationToken.ThrowIfCancellationRequested();

        plan = await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.Executing, null, null, null, cancellationToken);
        var started = DateTimeOffset.UtcNow;
        var beforeIdentity = await ReadIdentityJsonAsync(plan.SourcePath, cancellationToken);
        var totalTimer = Stopwatch.StartNew();
        try
        {
            var deleteTimer = Stopwatch.StartNew();
            var deleteResult = await _windowsPermanentDelete.PermanentlyDeleteSingleFileAsync(plan.SourcePath, cancellationToken);
            deleteTimer.Stop();
            var observationTimer = Stopwatch.StartNew();
            var sourceObservation = ObserveSourcePath(plan.SourcePath);
            observationTimer.Stop();

            if (!deleteResult.Succeeded)
            {
                var failureCode = deleteResult.ErrorCode ?? "PERMANENT_DELETE_API_FAILED";
                var failureMessage = deleteResult.ErrorMessage ?? "PermanentDelete API failed; Windows Recycle Bin was not used and no fallback was attempted.";
                await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, failureCode, failureMessage, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), started, DateTimeOffset.UtcNow), cancellationToken);
                return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.PermanentDeleteFailed, failureCode, failureMessage, null, cancellationToken);
            }
            if (sourceObservation != SourcePathObservation.Absent)
            {
                var failureCode = sourceObservation == SourcePathObservation.Present ? "PERMANENT_DELETE_API_SUCCESS_SOURCE_STILL_PRESENT" : "PERMANENT_DELETE_SOURCE_OBSERVATION_UNKNOWN";
                var failureMessage = sourceObservation == SourcePathObservation.Present
                    ? "PermanentDelete API returned success but the source remains present; lifecycle completion was refused and no second delete was attempted."
                    : "PermanentDelete API returned success but source absence could not be observed; lifecycle completion was refused and no second delete was attempted.";
                await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, failureCode, failureMessage, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), started, DateTimeOffset.UtcNow), cancellationToken);
                return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.PermanentDeleteFailed, failureCode, failureMessage, null, cancellationToken);
            }

            var dbTimer = Stopwatch.StartNew();
            if (_beforeProductionPermanentDeleteLifecyclePublish is not null) await _beforeProductionPermanentDeleteLifecyclePublish(plan, cancellationToken);
            var lifecycle = await _repository.PublishPermanentDeleteLifecycleAsync(CreatePermanentDeleteDatabasePublish(plan, DateTimeOffset.UtcNow), cancellationToken);
            dbTimer.Stop();
            totalTimer.Stop();
            PublishProductionPermanentDeleteTiming(new ProductionPermanentDeleteExecutionTiming(operationId, finalRevalidationTimer.ElapsedMilliseconds, deleteTimer.ElapsedMilliseconds, observationTimer.ElapsedMilliseconds, dbTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds));
            await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), operationId, plan.MediaFileId, plan.SourcePath, null, OperationItemResult.Completed, started, DateTimeOffset.UtcNow, null, "PermanentDelete source absence was observed and C# permanent lifecycle state was published; Windows Recycle Bin was not used.", beforeIdentity, JsonSerializer.Serialize(new { lifecycle.LifecycleState, lifecycle.PermanentlyDeletedAt, lifecycle.SourceAvailable, WindowsRecycleBinUsed = false }), started, DateTimeOffset.UtcNow), cancellationToken);
            return await _repository.TransitionFileOperationPlanAsync(operationId, FileOperationState.PermanentlyDeleted, null, null, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), "PERMANENT_DELETE_CANCELLED_AFTER_EXECUTION_STARTED", "Cancellation occurred after PermanentDelete execution began; restart observation must not re-run delete.", CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await MarkRecoveryRequiredAsync(plan, started, beforeIdentity, await ReadIdentityJsonAsync(plan.SourcePath, CancellationToken.None), "PERMANENT_DELETE_OR_LIFECYCLE_PUBLISH_FAILED", "PermanentDelete filesystem effect or C# lifecycle publish did not complete deterministically; recovery observation is required and delete will not be retried.", CancellationToken.None, ex.Message);
            return (await _repository.GetFileOperationPlanAsync(operationId, CancellationToken.None))!;
        }
    }

    /// <summary>Fixture-only batch execution. Each plan remains independently durable and a mixed result is never collapsed into Failed.</summary>
    public async Task<FileOperationBatchResult> ExecuteDisposableFixtureBatchAsync(IReadOnlyList<Guid> operationIds, DisposableFixtureCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        ArgumentNullException.ThrowIfNull(capability);
        if (operationIds.Count == 0) throw new ArgumentException("At least one explicit operation ID is required.", nameof(operationIds));
        if (operationIds.Distinct().Count() != operationIds.Count) throw new ArgumentException("Operation IDs must be unique.", nameof(operationIds));
        var results = new List<FileOperationPlan>();
        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { results.Add(await ExecuteDisposableFixtureAsync(operationId, capability, cancellationToken)); }
            catch (InvalidOperationException) { results.Add((await _repository.GetFileOperationPlanAsync(operationId, cancellationToken)) ?? throw new InvalidOperationException("Operation plan disappeared during fixture batch execution.")); }
        }
        var completed = results.Count(item => item.State == FileOperationState.Completed);
        var recovery = results.Count(item => item.State == FileOperationState.RecoveryRequired);
        var skipped = results.Count(item => item.State is FileOperationState.SourceChanged or FileOperationState.SourceMissing or FileOperationState.SourceUnavailable or FileOperationState.AccessDenied or FileOperationState.DestinationConflict or FileOperationState.InsufficientSpace or FileOperationState.ValidationFailed or FileOperationState.Cancelled or FileOperationState.Unsupported or FileOperationState.ProductionExecutionBlocked);
        var failed = results.Count - completed - recovery - skipped;
        var aggregate = completed == results.Count ? FileOperationState.Completed : completed > 0 ? FileOperationState.PartiallyCompleted : recovery > 0 ? FileOperationState.RecoveryRequired : FileOperationState.ValidationFailed;
        var now = DateTimeOffset.UtcNow;
        foreach (var item in results)
            await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), item.OperationId, now, "BatchAggregateObserved", JsonSerializer.Serialize(new { BatchId = operationIds.Count, Completed = completed, Failed = failed, Skipped = skipped, RecoveryRequired = recovery, Aggregate = aggregate.ToString() })), cancellationToken);
        return new FileOperationBatchResult(Guid.NewGuid(), results, completed, failed, skipped, recovery, aggregate, now);
    }

    public async Task<IReadOnlyList<FileOperationRecoveryResult>> RecoverInterruptedOperationsAsync(DisposableFixtureCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var plans = await _repository.GetInterruptedFileOperationPlansAsync(cancellationToken);
        var results = new List<FileOperationRecoveryResult>();
        foreach (var plan in plans.Where(item => item.Mode == FileOperationMode.DisposableFixtureExecution))
        {
            EnsureFixtureContainment(plan, capability);
            results.Add(await RecoverPlanAsync(plan, allowProductionPublish: false, cancellationToken));
        }
        return results;
    }

    public async Task<IReadOnlyList<FileOperationRecoveryResult>> RecoverInterruptedProductionSameVolumeOperationsAsync(CancellationToken cancellationToken)
    {
        var plans = await _repository.GetInterruptedFileOperationPlansAsync(cancellationToken);
        var results = new List<FileOperationRecoveryResult>();
        foreach (var plan in plans.Where(item => item.Mode == FileOperationMode.ProductionDryRun && item.ProductionSameVolumeCapability))
            results.Add(await RecoverPlanAsync(plan, allowProductionPublish: true, cancellationToken));
        return results;
    }

    public async Task<IReadOnlyList<FileOperationRecoveryResult>> RecoverInterruptedProductionCrossVolumeMoveOperationsAsync(CancellationToken cancellationToken)
    {
        var plans = await _repository.GetInterruptedFileOperationPlansAsync(cancellationToken);
        var results = new List<FileOperationRecoveryResult>();
        foreach (var plan in plans.Where(FileOperationPolicy.IsCrossVolumeMove))
            results.Add(await RecoverCrossVolumeMovePlanAsync(plan, cancellationToken));
        return results;
    }

    /// <summary>Filesystem-observation recovery for interrupted Production Recycle only. It never calls the Shell Recycle API.</summary>
    public async Task<IReadOnlyList<FileOperationRecoveryResult>> RecoverInterruptedProductionRecycleOperationsAsync(CancellationToken cancellationToken)
    {
        var plans = await _repository.GetInterruptedFileOperationPlansAsync(cancellationToken);
        var results = new List<FileOperationRecoveryResult>();
        foreach (var plan in plans.Where(FileOperationPolicy.IsProductionRecycle))
            results.Add(await RecoverRecyclePlanAsync(plan, cancellationToken));
        return results;
    }

    /// <summary>Filesystem-observation recovery for interrupted Production PermanentDelete only. It never invokes delete again.</summary>
    public async Task<IReadOnlyList<FileOperationRecoveryResult>> RecoverInterruptedProductionPermanentDeleteOperationsAsync(CancellationToken cancellationToken)
    {
        var plans = await _repository.GetInterruptedFileOperationPlansAsync(cancellationToken);
        var results = new List<FileOperationRecoveryResult>();
        foreach (var plan in plans.Where(FileOperationPolicy.IsProductionPermanentDelete))
            results.Add(await RecoverPermanentDeletePlanAsync(plan, cancellationToken));
        return results;
    }

    private async Task<FileOperationRecoveryResult> RecoverCrossVolumeMovePlanAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        var current = plan;
        if (current.State != FileOperationState.RecoveryRequired)
            current = await _repository.TransitionCrossVolumeMovePlanAsync(current.OperationId, FileOperationState.RecoveryRequired, CrossVolumeMoveStage.RecoveryRequired, "APP_RESTART_CROSS_VOLUME_RECOVERY", "The application restarted while Cross-Volume Move was in progress; filesystem observation is required.", cancellationToken);
        var sourceExists = File.Exists(current.SourcePath);
        var temporaryExists = !string.IsNullOrWhiteSpace(current.TemporaryDestinationPath) && File.Exists(current.TemporaryDestinationPath);
        var finalExists = !string.IsNullOrWhiteSpace(current.DestinationPath) && File.Exists(current.DestinationPath);
        if (!sourceExists && finalExists && !temporaryExists)
        {
            try
            {
                var destinationPublish = await VerifyCrossVolumeFinalDestinationAsync(current, cancellationToken, requireSourcePresent: false);
                await _repository.PublishCrossVolumeMoveAsync(destinationPublish, cancellationToken);
                var completed = await _repository.TransitionCrossVolumeMovePlanAsync(current.OperationId, FileOperationState.Completed, CrossVolumeMoveStage.DbPublished, null, null, cancellationToken);
                await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "CrossVolumeRecoveryFinalVerifiedAndDbPublished", JsonSerializer.Serialize(new { current.SourcePath, current.TemporaryDestinationPath, current.DestinationPath })), cancellationToken);
                return new FileOperationRecoveryResult(current.OperationId, completed.State, "RECOVERY_FINAL_VERIFIED_AND_DB_PUBLISHED", "Verified final destination was published to C# SQLite after restart.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_FINAL_OR_DB_UNVERIFIABLE", "Final destination or C# DB publish could not be verified after interruption.", cancellationToken, ex.Message);
            }
        }
        if (sourceExists && finalExists && !temporaryExists)
        {
            try { await VerifyCrossVolumeFinalDestinationAsync(current, cancellationToken, requireSourcePresent: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_BOTH_SOURCE_AND_FINAL_UNVERIFIABLE", "Both source and final destination exist but destination verification failed; neither file was removed.", cancellationToken, ex.Message);
            }
            var preserved = await _repository.TransitionCrossVolumeMovePlanAsync(current.OperationId, FileOperationState.SourceChangedAfterCopy, CrossVolumeMoveStage.FinalPublished, "RECOVERY_BOTH_SOURCE_AND_FINAL", "Both source and verified final destination exist after interruption; source removal is never inferred or retried automatically.", cancellationToken);
            return new FileOperationRecoveryResult(current.OperationId, preserved.State, "RECOVERY_SOURCE_AND_FINAL_PRESERVED", "Both source and verified final destination were preserved; no automatic source removal occurred.", DateTimeOffset.UtcNow);
        }
        if (sourceExists && temporaryExists && !finalExists)
            return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_SOURCE_AND_TEMP_PRESENT", "Source and operation-specific temporary copy exist after interruption; no resume, publish, or cleanup was inferred.", cancellationToken);
        if (sourceExists && !temporaryExists && !finalExists)
        {
            await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "CrossVolumeRecoveryNoEffectObserved", JsonSerializer.Serialize(new { current.SourcePath, current.TemporaryDestinationPath, current.DestinationPath })), cancellationToken);
            return new FileOperationRecoveryResult(current.OperationId, current.State, "RECOVERY_NO_EFFECT_OBSERVED", "Source remains and no destination artifact exists; no success was inferred and a new explicit plan is required.", DateTimeOffset.UtcNow);
        }
        if (!sourceExists && temporaryExists && !finalExists)
            return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_SOURCE_MISSING_TEMP_PRESENT", "Source is absent while only a temporary artifact exists; no automatic completion or cleanup was performed.", cancellationToken);
        if (!sourceExists && !temporaryExists && !finalExists)
            return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_SOURCE_TEMP_FINAL_MISSING", "Source, temporary artifact, and final destination are absent; no success was inferred.", cancellationToken);
        return await MarkCrossVolumeCannotRollbackAsync(current, "RECOVERY_AMBIGUOUS_CROSS_VOLUME_STATE", "Cross-Volume filesystem state is ambiguous; no automatic cleanup, source removal, or completion was performed.", cancellationToken);
    }

    private async Task<FileOperationRecoveryResult> RecoverRecyclePlanAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        var current = plan;
        if (current.State != FileOperationState.RecoveryRequired)
            current = await _repository.TransitionFileOperationPlanAsync(current.OperationId, FileOperationState.RecoveryRequired, "APP_RESTART_RECYCLE_RECOVERY", "The application restarted while Windows Recycle Bin execution was in progress; filesystem observation is required and Recycle will not be retried.", null, cancellationToken);
        var observation = ObserveSourcePath(current.SourcePath);
        if (observation == SourcePathObservation.Absent)
        {
            try
            {
                var lifecycle = await _repository.PublishRecycleLifecycleAsync(CreateRecycleDatabasePublish(current, DateTimeOffset.UtcNow), cancellationToken);
                var recycled = await _repository.TransitionFileOperationPlanAsync(current.OperationId, FileOperationState.Recycled, null, null, null, cancellationToken);
                await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "RecycleRecoverySourceAbsentLifecyclePublished", JsonSerializer.Serialize(new { lifecycle.LifecycleState, lifecycle.RecycledAt, ShellRecycleRetried = false })), cancellationToken);
                return new FileOperationRecoveryResult(current.OperationId, recycled.State, "RECOVERY_RECYCLE_SOURCE_ABSENT_LIFECYCLE_PUBLISHED", "Source was observed absent and the Recycled lifecycle was published without retrying Windows Shell Recycle.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return await MarkCannotRollbackAsync(current, "RECOVERY_RECYCLE_LIFECYCLE_UNVERIFIABLE", "Source is absent but the durable Recycled lifecycle could not be published after restart.", cancellationToken, ex.Message);
            }
        }
        if (observation == SourcePathObservation.Present)
        {
            await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "RecycleRecoverySourceStillPresent", JsonSerializer.Serialize(new { current.SourcePath, ShellRecycleRetried = false })), cancellationToken);
            return new FileOperationRecoveryResult(current.OperationId, current.State, "RECOVERY_RECYCLE_NO_EFFECT_OBSERVED", "Source remains present; no Recycled success was inferred and Windows Shell Recycle was not retried.", DateTimeOffset.UtcNow);
        }
        return await MarkCannotRollbackAsync(current, "RECOVERY_RECYCLE_SOURCE_OBSERVATION_UNKNOWN", "Source path could not be observed as present or absent after interruption; Windows Shell Recycle was not retried.", cancellationToken);
    }

    private async Task<FileOperationRecoveryResult> RecoverPermanentDeletePlanAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        var current = plan;
        if (current.State != FileOperationState.RecoveryRequired)
            current = await _repository.TransitionFileOperationPlanAsync(current.OperationId, FileOperationState.RecoveryRequired, "APP_RESTART_PERMANENT_DELETE_RECOVERY", "The application restarted while PermanentDelete was in progress; filesystem observation is required and delete will not be retried.", null, cancellationToken);
        var observation = ObserveSourcePath(current.SourcePath);
        if (observation == SourcePathObservation.Absent)
        {
            try
            {
                var lifecycle = await _repository.PublishPermanentDeleteLifecycleAsync(CreatePermanentDeleteDatabasePublish(current, DateTimeOffset.UtcNow), cancellationToken);
                var deleted = await _repository.TransitionFileOperationPlanAsync(current.OperationId, FileOperationState.PermanentlyDeleted, null, null, null, cancellationToken);
                await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "PermanentDeleteRecoverySourceAbsentLifecyclePublished", JsonSerializer.Serialize(new { lifecycle.LifecycleState, lifecycle.PermanentlyDeletedAt, DeleteRetried = false, WindowsRecycleBinUsed = false })), cancellationToken);
                return new FileOperationRecoveryResult(current.OperationId, deleted.State, "RECOVERY_PERMANENT_DELETE_SOURCE_ABSENT_LIFECYCLE_PUBLISHED", "Source was observed absent and the permanent lifecycle was published without retrying delete.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return await MarkCannotRollbackAsync(current, "RECOVERY_PERMANENT_DELETE_LIFECYCLE_UNVERIFIABLE", "Source is absent but the durable permanent lifecycle could not be published after restart.", cancellationToken, ex.Message);
            }
        }
        if (observation == SourcePathObservation.Present)
        {
            await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), current.OperationId, DateTimeOffset.UtcNow, "PermanentDeleteRecoverySourceStillPresent", JsonSerializer.Serialize(new { current.SourcePath, DeleteRetried = false, WindowsRecycleBinUsed = false })), cancellationToken);
            return new FileOperationRecoveryResult(current.OperationId, current.State, "RECOVERY_PERMANENT_DELETE_NO_EFFECT_OBSERVED", "Source remains present; no PermanentDelete success was inferred and delete was not retried.", DateTimeOffset.UtcNow);
        }
        return new FileOperationRecoveryResult(current.OperationId, current.State, "RECOVERY_PERMANENT_DELETE_SOURCE_OBSERVATION_UNKNOWN", "Source path could not be observed as present or absent after interruption; delete was not retried.", DateTimeOffset.UtcNow);
    }

    private async Task<FileOperationRecoveryResult> RecoverPlanAsync(FileOperationPlan plan, bool allowProductionPublish, CancellationToken cancellationToken)
    {
        var state = plan.State;
        if (state is FileOperationState.Executing or FileOperationState.PartiallyCompleted)
            state = (await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.RecoveryRequired, "APP_RESTART_RECOVERY", "The application restarted while execution was in progress.", null, cancellationToken)).State;
        var sourceExists = File.Exists(plan.SourcePath);
        var destinationExists = plan.DestinationPath is not null && File.Exists(plan.DestinationPath);
        if (!sourceExists && destinationExists)
        {
            try
            {
                await VerifyDestinationAsync(plan, cancellationToken);
                await ReconcileRootAsync(plan.LibraryRootId, cancellationToken);
                await EnsureDatabasePublishAsync(plan, cancellationToken);
                var completed = await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.Completed, null, null, null, cancellationToken);
                await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), plan.OperationId, DateTimeOffset.UtcNow, "RecoveryDestinationVerifiedAndDbPublished", JsonSerializer.Serialize(new { plan.SourcePath, plan.DestinationPath, allowProductionPublish })), cancellationToken);
                return new FileOperationRecoveryResult(plan.OperationId, completed.State, "RECOVERY_DESTINATION_VERIFIED", "Destination identity and C# DB publish were verified after restart.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return await MarkCannotRollbackAsync(plan, "RECOVERY_DESTINATION_OR_DB_UNVERIFIABLE", "Destination identity or C# DB publish could not be verified after interruption.", cancellationToken, ex.Message);
            }
        }
        if (sourceExists && !destinationExists)
        {
            await _repository.AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), plan.OperationId, DateTimeOffset.UtcNow, "RecoveryNoEffectObserved", JsonSerializer.Serialize(new { plan.SourcePath, plan.DestinationPath })), cancellationToken);
            return new FileOperationRecoveryResult(plan.OperationId, state, "RECOVERY_NO_EFFECT_OBSERVED", "Source remains and destination is absent; no success was inferred.", DateTimeOffset.UtcNow);
        }
        if (sourceExists && destinationExists)
            return await MarkCannotRollbackAsync(plan, "RECOVERY_BOTH_SOURCE_AND_DESTINATION", "Source and destination both exist; no source removal or inferred completion was performed.", cancellationToken);
        return await MarkCannotRollbackAsync(plan, "RECOVERY_SOURCE_AND_DESTINATION_MISSING", "Source and destination are both absent; no success was inferred.", cancellationToken);
    }

    private void PublishProductionTiming(ProductionSameVolumeExecutionTiming timing)
    {
        _log.Write("Information", "ProductionSameVolumeExecutionTiming", new { timing.OperationId, OperationType = timing.OperationType.ToString(), timing.FileSystemMoveMilliseconds, timing.DestinationVerificationMilliseconds, timing.DbPublishMilliseconds, timing.TotalMilliseconds });
        try { _productionTimingObserver?.Invoke(timing); }
        catch (Exception ex) { _log.Write("Warning", "ProductionTimingObserverFailed", new { timing.OperationId }, ex); }
    }

    private static async Task CopySourceToTemporaryAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.TemporaryDestinationPath)) throw new InvalidOperationException("Cross-Volume Move lacks a temporary destination path.");
        if (File.Exists(plan.TemporaryDestinationPath) || Directory.Exists(plan.TemporaryDestinationPath)) throw new IOException("Cross-Volume temporary destination already exists; overwrite is prohibited.");
        const int bufferBytes = 1024 * 1024;
        await using var source = new FileStream(plan.SourcePath, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = bufferBytes });
        await using var temporary = new FileStream(plan.TemporaryDestinationPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough, BufferSize = bufferBytes });
        await source.CopyToAsync(temporary, bufferBytes, cancellationToken);
        await temporary.FlushAsync(cancellationToken);
    }

    private static async Task VerifyCrossVolumeTemporaryAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.TemporaryDestinationPath) || !File.Exists(plan.TemporaryDestinationPath)) throw new IOException("Cross-Volume temporary copy is absent.");
        if (string.IsNullOrWhiteSpace(plan.SourceFullSha256) || string.IsNullOrWhiteSpace(plan.DestinationVolumeIdentity)) throw new InvalidOperationException("Cross-Volume temporary verification lacks immutable hash or destination volume.");
        var info = new FileInfo(plan.TemporaryDestinationPath);
        if (PathPolicy.IsReparsePoint(info)) throw new InvalidOperationException("Cross-Volume temporary copy is a reparse point.");
        if (info.Length != plan.SourceSizeBytes) throw new IOException("Cross-Volume temporary copy size does not match the immutable source plan.");
        var identity = WindowsFileIdentityReader.TryGet(plan.TemporaryDestinationPath);
        if (identity is null || !string.Equals(identity.VolumeIdentity, plan.DestinationVolumeIdentity, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cross-Volume temporary copy is not on the immutable destination volume.");
        var hash = await FileHash.Sha256Async(plan.TemporaryDestinationPath, cancellationToken);
        if (!string.Equals(hash, plan.SourceFullSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cross-Volume temporary copy Full SHA-256 does not match the immutable source plan.");
    }

    private async Task<CrossVolumeMoveDatabasePublish> VerifyCrossVolumeFinalDestinationAsync(FileOperationPlan plan, CancellationToken cancellationToken, bool requireSourcePresent = true)
    {
        if (string.IsNullOrWhiteSpace(plan.DestinationPath) || string.IsNullOrWhiteSpace(plan.TemporaryDestinationPath) || string.IsNullOrWhiteSpace(plan.SourceFullSha256) || plan.DestinationLibraryRootId is null || string.IsNullOrWhiteSpace(plan.DestinationVolumeIdentity)) throw new InvalidOperationException("Cross-Volume final verification snapshot is incomplete.");
        if ((requireSourcePresent && !File.Exists(plan.SourcePath)) || !File.Exists(plan.DestinationPath) || File.Exists(plan.TemporaryDestinationPath)) throw new IOException("Cross-Volume final verification requires final destination present, temporary artifact absent, and source present before source removal.");
        var destinationRoot = await _repository.GetLibraryRootAsync(plan.DestinationLibraryRootId.Value, cancellationToken) ?? throw new InvalidOperationException("Cross-Volume destination root is unavailable.");
        if (!PathPolicy.IsWithinRoot(destinationRoot.ConfiguredPath, plan.DestinationPath)) throw new InvalidOperationException("Cross-Volume final destination escaped its authorized root.");
        var info = new FileInfo(plan.DestinationPath);
        if (PathPolicy.IsReparsePoint(info)) throw new InvalidOperationException("Cross-Volume final destination is a reparse point.");
        if (info.Length != plan.SourceSizeBytes) throw new IOException("Cross-Volume final destination size does not match the immutable source plan.");
        var identity = WindowsFileIdentityReader.TryGet(plan.DestinationPath);
        if (identity is null || !string.Equals(identity.VolumeIdentity, plan.DestinationVolumeIdentity, StringComparison.OrdinalIgnoreCase) || string.Equals(identity.VolumeIdentity, plan.SourceVolumeIdentity, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Cross-Volume final destination volume identity is invalid.");
        var quick = await FileSystemMediaScanner.QuickFingerprintAsync(plan.DestinationPath, info.Length, cancellationToken);
        if (!MatchesNullable(plan.SourceQuickFingerprint, quick)) throw new IOException("Cross-Volume final destination quick fingerprint does not match the immutable source plan.");
        var hash = await FileHash.Sha256Async(plan.DestinationPath, cancellationToken);
        if (!string.Equals(hash, plan.SourceFullSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cross-Volume final destination Full SHA-256 does not match the immutable source plan.");
        return new CrossVolumeMoveDatabasePublish(plan.OperationId, plan.MediaFileId, plan.MediaContentId, plan.DestinationLibraryRootId.Value, plan.DestinationPath, plan.DestinationNormalizedPath ?? PathPolicy.NormalizeForIdentity(plan.DestinationPath), Path.GetFileName(plan.DestinationPath), Path.GetExtension(plan.DestinationPath), info.Length, new DateTimeOffset(info.CreationTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc), identity.VolumeIdentity, identity.FileIdentity, quick, hash);
    }

    private static async Task<(bool Matches, string Code, string Message)> ValidateSourceForCrossVolumeRemovalAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        if (!File.Exists(plan.SourcePath)) return (false, "SOURCE_MISSING_AFTER_COPY", "verified destination was preserved because source disappeared before removal.");
        try
        {
            var info = new FileInfo(plan.SourcePath);
            if (PathPolicy.IsReparsePoint(info)) return (false, "SOURCE_REPARSE_POINT_AFTER_COPY", "verified destination was preserved because source became a reparse point before removal.");
            if (info.Length != plan.SourceSizeBytes || new DateTimeOffset(info.LastWriteTimeUtc) != plan.SourceLastWriteTimeUtc) return (false, "SOURCE_CHANGED_AFTER_COPY", "verified destination was preserved because source size or mtime changed after copy.");
            var identity = WindowsFileIdentityReader.TryGet(plan.SourcePath);
            if (identity is null || !MatchesNullable(plan.SourceVolumeIdentity, identity.VolumeIdentity) || !MatchesNullable(plan.SourceFileIdentity, identity.FileIdentity)) return (false, "SOURCE_IDENTITY_CHANGED_AFTER_COPY", "verified destination was preserved because source stable identity changed after copy.");
            var quick = await FileSystemMediaScanner.QuickFingerprintAsync(plan.SourcePath, info.Length, cancellationToken);
            if (!MatchesNullable(plan.SourceQuickFingerprint, quick)) return (false, "SOURCE_CHANGED_AFTER_COPY", "verified destination was preserved because source quick fingerprint changed after copy.");
            if (string.IsNullOrWhiteSpace(plan.SourceFullSha256)) return (false, "FULL_HASH_REQUIRED", "source removal is blocked because immutable Full SHA-256 evidence is absent.");
            var hash = await FileHash.Sha256Async(plan.SourcePath, cancellationToken);
            return string.Equals(hash, plan.SourceFullSha256, StringComparison.OrdinalIgnoreCase)
                ? (true, "SOURCE_REVALIDATED_FOR_REMOVAL", "source remains identical after verified copy.")
                : (false, "SOURCE_CHANGED_AFTER_COPY", "verified destination was preserved because source Full SHA-256 changed after copy.");
        }
        catch (UnauthorizedAccessException) { return (false, "SOURCE_REMOVAL_ACCESS_DENIED", "verified destination was preserved because source cannot be read before removal."); }
        catch (IOException) { return (false, "SOURCE_READ_UNAVAILABLE_AFTER_COPY", "verified destination was preserved because source cannot be revalidated before removal."); }
    }

    private async Task InvokeCrossVolumeStageAsync(FileOperationPlan plan, CrossVolumeMoveStage stage, CancellationToken cancellationToken)
    {
        if (_beforeCrossVolumeStage is not null) await _beforeCrossVolumeStage(plan, stage, cancellationToken);
    }

    private void PublishCrossVolumeTiming(ProductionCrossVolumeMoveExecutionTiming timing)
    {
        _log.Write("Information", "ProductionCrossVolumeMoveExecutionTiming", new { timing.OperationId, timing.CopyMilliseconds, timing.TempHashVerificationMilliseconds, timing.FinalPublishMilliseconds, timing.SourceRemovalMilliseconds, timing.DbPublishMilliseconds, timing.TotalMilliseconds });
        try { _crossVolumeTimingObserver?.Invoke(timing); }
        catch (Exception ex) { _log.Write("Warning", "CrossVolumeTimingObserverFailed", new { timing.OperationId }, ex); }
    }

    private async Task MarkCrossVolumeRecoveryRequiredAsync(FileOperationPlan plan, DateTimeOffset started, string? beforeIdentity, string code, string userMessage, CancellationToken cancellationToken, string? technicalMessage = null)
    {
        await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.RecoveryRequired, started, DateTimeOffset.UtcNow, code, userMessage, beforeIdentity, await ReadIdentityJsonAsync(plan.DestinationPath ?? string.Empty, cancellationToken), started, DateTimeOffset.UtcNow), cancellationToken);
        await _repository.TransitionCrossVolumeMovePlanAsync(plan.OperationId, FileOperationState.RecoveryRequired, CrossVolumeMoveStage.RecoveryRequired, code, technicalMessage ?? userMessage, cancellationToken);
    }

    private async Task MarkCrossVolumeSourceRemovalFailedAsync(FileOperationPlan plan, DateTimeOffset started, string? beforeIdentity, string code, string userMessage, string? technicalMessage, CancellationToken cancellationToken)
    {
        await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.Failed, started, DateTimeOffset.UtcNow, code, userMessage, beforeIdentity, await ReadIdentityJsonAsync(plan.DestinationPath ?? string.Empty, cancellationToken), started, DateTimeOffset.UtcNow), cancellationToken);
        await _repository.TransitionCrossVolumeMovePlanAsync(plan.OperationId, FileOperationState.SourceRemovalFailed, CrossVolumeMoveStage.FinalPublished, code, technicalMessage ?? userMessage, cancellationToken);
    }

    private async Task<FileOperationRecoveryResult> MarkCrossVolumeCannotRollbackAsync(FileOperationPlan plan, string code, string message, CancellationToken cancellationToken, string? technicalMessage = null)
    {
        var current = await _repository.GetFileOperationPlanAsync(plan.OperationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (current.State != FileOperationState.RecoveryRequired)
            current = await _repository.TransitionCrossVolumeMovePlanAsync(current.OperationId, FileOperationState.RecoveryRequired, CrossVolumeMoveStage.RecoveryRequired, code, technicalMessage ?? message, cancellationToken);
        var terminal = await _repository.TransitionCrossVolumeMovePlanAsync(current.OperationId, FileOperationState.CannotRollback, CrossVolumeMoveStage.RecoveryRequired, code, technicalMessage ?? message, cancellationToken);
        return new FileOperationRecoveryResult(terminal.OperationId, terminal.State, code, message, DateTimeOffset.UtcNow);
    }

    private async Task MarkRecoveryRequiredAsync(FileOperationPlan plan, DateTimeOffset started, string? beforeIdentity, string? afterIdentity, string code, string userMessage, CancellationToken cancellationToken, string? technicalMessage = null)
    {
        await _repository.RecordFileOperationExecutionAsync(new FileOperationExecution(Guid.NewGuid(), plan.OperationId, plan.MediaFileId, plan.SourcePath, plan.DestinationPath, OperationItemResult.RecoveryRequired, started, DateTimeOffset.UtcNow, code, userMessage, beforeIdentity, afterIdentity, started, DateTimeOffset.UtcNow), cancellationToken);
        await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.RecoveryRequired, code, technicalMessage ?? userMessage, null, cancellationToken);
    }

    private async Task<FileOperationRecoveryResult> MarkCannotRollbackAsync(FileOperationPlan plan, string code, string message, CancellationToken cancellationToken, string? technicalMessage = null)
    {
        var current = await _repository.GetFileOperationPlanAsync(plan.OperationId, cancellationToken) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (current.State is FileOperationState.Executing or FileOperationState.PartiallyCompleted)
            current = await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.RecoveryRequired, code, technicalMessage ?? message, null, cancellationToken);
        var updated = await _repository.TransitionFileOperationPlanAsync(plan.OperationId, FileOperationState.CannotRollback, code, technicalMessage ?? message, null, cancellationToken);
        return new FileOperationRecoveryResult(plan.OperationId, updated.State, code, message, DateTimeOffset.UtcNow);
    }

    private async Task<FileOperationPreflight> ValidateAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var root = await _repository.GetLibraryRootAsync(plan.LibraryRootId, cancellationToken);
        if (root is null || root.Availability == RootAvailability.Offline || !Directory.Exists(root.ConfiguredPath))
            return Preflight(plan, FileOperationState.SourceUnavailable, "ROOT_OFFLINE", L.T("ライブラリルートへ到達できません。"), false, false, false, false, false, null, false, false, null, now);
        if (!Path.IsPathFullyQualified(plan.SourcePath) || !PathPolicy.IsWithinRoot(root.ConfiguredPath, plan.SourcePath) || !File.Exists(plan.SourcePath))
            return Preflight(plan, FileOperationState.SourceMissing, "SOURCE_MISSING", L.T("計画時のsource pathが存在しないか、library root外です。"), false, false, false, false, false, null, false, false, null, now);
        if (PathPolicy.HasReparsePointAncestor(root.ConfiguredPath, plan.SourcePath))
            return Preflight(plan, FileOperationState.ValidationFailed, "SOURCE_REPARSE_ANCESTOR", L.T("source pathのauthorized root内ancestorにreparse pointが存在するため操作できません。"), true, false, false, false, false, null, false, false, null, now);
        var sourceInfo = new FileInfo(plan.SourcePath);
        if (PathPolicy.IsReparsePoint(sourceInfo))
            return Preflight(plan, FileOperationState.ValidationFailed, "SOURCE_REPARSE_POINT", L.T("reparse pointをsourceとして操作できません。"), true, false, false, false, false, null, false, false, null, now);

        var productionSameVolumeCapability = plan.Mode == FileOperationMode.ProductionDryRun && plan.ProductionSameVolumeCapability;
        var productionCrossVolumeCapability = FileOperationPolicy.IsCrossVolumeMove(plan);
        var productionRecycleCapability = FileOperationPolicy.IsProductionRecycle(plan);
        var productionPermanentDeleteCapability = FileOperationPolicy.IsProductionPermanentDelete(plan);
        var productionCapability = productionSameVolumeCapability || productionCrossVolumeCapability || productionRecycleCapability || productionPermanentDeleteCapability;
        if (productionCapability && (string.IsNullOrWhiteSpace(plan.SourceVolumeIdentity) || string.IsNullOrWhiteSpace(plan.SourceFileIdentity)))
            return Preflight(plan, FileOperationState.ValidationFailed, "PLAN_IDENTITY_UNAVAILABLE", L.T("Production executionにはimmutable plan内のWindows volume/file identityが必要です。"), true, false, false, false, false, null, false, false, null, now);
        var sizeMatches = sourceInfo.Length == plan.SourceSizeBytes;
        var mtimeMatches = new DateTimeOffset(sourceInfo.LastWriteTimeUtc) == plan.SourceLastWriteTimeUtc;
        string? quick;
        try { quick = await FileSystemMediaScanner.QuickFingerprintAsync(plan.SourcePath, sourceInfo.Length, cancellationToken); }
        catch (UnauthorizedAccessException) { return Preflight(plan, FileOperationState.AccessDenied, "MEDIA_ACCESS_DENIED", L.T("sourceへread accessできません。"), true, false, sizeMatches, mtimeMatches, false, null, false, false, null, now); }
        catch (IOException) { return Preflight(plan, FileOperationState.SourceUnavailable, "SOURCE_READ_UNAVAILABLE", L.T("sourceをreadして再検証できません。"), true, false, sizeMatches, mtimeMatches, false, null, false, false, null, now); }
        var identity = WindowsFileIdentityReader.TryGet(plan.SourcePath);
        if (productionCapability && identity is null)
            return Preflight(plan, FileOperationState.SourceUnavailable, "SOURCE_IDENTITY_UNAVAILABLE", L.T("sourceのWindows volume/file identityを再取得できません。source lockまたはaccess状態を確認してください。"), true, false, sizeMatches, mtimeMatches, false, null, false, false, null, now);
        var identityMatches = MatchesNullable(plan.SourceVolumeIdentity, identity?.VolumeIdentity) && MatchesNullable(plan.SourceFileIdentity, identity?.FileIdentity);
        var quickMatches = MatchesNullable(plan.SourceQuickFingerprint, quick);
        if (!FileOperationPolicy.IsMoveRenameExecutorSupported(plan.OperationType) && !productionRecycleCapability && !productionPermanentDeleteCapability)
            return Preflight(plan, FileOperationState.Unsupported, plan.OperationType == FileOperationType.PermanentDelete ? "PERMANENT_DELETE_CAPABILITY_REQUIRED" : "RECYCLE_NOT_IMPLEMENTED", L.T("このoperation typeには実行capabilityがありません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, null, false, false, null, now);

        bool? fullMatches = null;
        if (plan.ValidationStrength == SourceValidationStrength.StrongFullHash)
        {
            if (string.IsNullOrWhiteSpace(plan.SourceFullSha256))
                return Preflight(plan, FileOperationState.ValidationFailed, "FULL_HASH_REQUIRED", L.T("strong revalidationにはcurrent Full SHA-256 evidenceが必要です。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, false, false, false, null, now);
            fullMatches = string.Equals(await FileHash.Sha256Async(plan.SourcePath, cancellationToken), plan.SourceFullSha256, StringComparison.OrdinalIgnoreCase);
        }
        if (!identityMatches || !sizeMatches || !mtimeMatches || !quickMatches || fullMatches is false)
            return Preflight(plan, FileOperationState.SourceChanged, "SOURCE_CHANGED", L.T("計画時のsource versionと現在のsourceが一致しません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);

        if (productionPermanentDeleteCapability)
        {
            if (plan.SelectedMediaCount != 1 || plan.ValidationStrength != SourceValidationStrength.StrongFullHash || string.IsNullOrWhiteSpace(plan.SourceFullSha256))
                return Preflight(plan, FileOperationState.ValidationFailed, "PERMANENT_DELETE_SNAPSHOT_INCOMPLETE", L.T("PermanentDeleteにはsingle explicit selectionとStrongFullHash snapshotが必要です。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
            return Preflight(plan, FileOperationState.Ready, "PREFLIGHT_PERMANENT_DELETE_READY", L.T("source identity、authorized root、reparse-point safety、single explicit selection、Full SHA-256、不可逆PermanentDelete専用capability、Windows Recycle Bin非使用を確認しました。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, false, null, now);
        }

        if (productionRecycleCapability)
            return Preflight(plan, FileOperationState.Ready, "PREFLIGHT_RECYCLE_READY", L.T("source identity、authorized root、reparse-point safety、Full SHA-256、Windows Recycle Bin専用capability、PermanentDelete非使用を確認しました。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, false, null, now);

        if (string.IsNullOrWhiteSpace(plan.DestinationPath) || !Path.IsPathFullyQualified(plan.DestinationPath))
            return Preflight(plan, FileOperationState.ValidationFailed, "INVALID_DESTINATION", L.T("destinationはauthorized library root内のfully-qualified pathである必要があります。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        var destinationRoot = root;
        if (productionCrossVolumeCapability)
        {
            if (plan.DestinationLibraryRootId is null || string.IsNullOrWhiteSpace(plan.DestinationRootPath) || string.IsNullOrWhiteSpace(plan.DestinationVolumeIdentity) || string.IsNullOrWhiteSpace(plan.TemporaryDestinationPath))
                return Preflight(plan, FileOperationState.ValidationFailed, "CROSS_VOLUME_SNAPSHOT_INCOMPLETE", L.T("Cross-Volume Moveのdestination root、volume、temporary path snapshotが不完全です。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
            destinationRoot = await _repository.GetLibraryRootAsync(plan.DestinationLibraryRootId.Value, cancellationToken);
            if (destinationRoot is null || destinationRoot.Availability == RootAvailability.Offline || !Directory.Exists(destinationRoot.ConfiguredPath))
                return Preflight(plan, FileOperationState.SourceUnavailable, "DESTINATION_ROOT_OFFLINE", L.T("destination library rootへ到達できません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        }
        else if (FileOperationPolicy.IsSameVolumeCrossRootMove(plan))
        {
            // Same-volume Move into a different registered root: the destination root snapshotted at plan
            // creation must still be Active and reachable; it is never inferred from the source root.
            destinationRoot = await _repository.GetLibraryRootAsync(plan.DestinationLibraryRootId!.Value, cancellationToken);
            if (destinationRoot is null || destinationRoot.RegistrationState != RootRegistrationState.Active)
                return Preflight(plan, FileOperationState.ValidationFailed, "DESTINATION_ROOT_UNREGISTERED", L.T("destination library rootが登録解除されています。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
            if (destinationRoot.Availability == RootAvailability.Offline || !Directory.Exists(destinationRoot.ConfiguredPath))
                return Preflight(plan, FileOperationState.SourceUnavailable, "DESTINATION_ROOT_OFFLINE", L.T("destination library rootへ到達できません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        }
        if (destinationRoot is null || !PathPolicy.IsWithinRoot(destinationRoot.ConfiguredPath, plan.DestinationPath))
            return Preflight(plan, FileOperationState.ValidationFailed, "INVALID_DESTINATION", L.T("destinationはauthorized library root内のfully-qualified pathである必要があります。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        if (!ValidateDestinationFileName(plan.SourcePath, plan.DestinationPath, out var filenameCode, out var filenameMessage))
            return Preflight(plan, FileOperationState.ValidationFailed, filenameCode, filenameMessage, true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        var parent = Path.GetDirectoryName(plan.DestinationPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            return Preflight(plan, FileOperationState.ValidationFailed, "DESTINATION_DIRECTORY_UNAVAILABLE", L.T("destination directoryを確認できません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        if (PathPolicy.IsReparsePoint(new DirectoryInfo(parent)))
            return Preflight(plan, FileOperationState.ValidationFailed, "DESTINATION_REPARSE_POINT", L.T("destination parentがreparse pointです。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);

        var destinationVolumeIdentity = WindowsVolumeIdentityReader.TryGetForPath(parent);
        var sameVolume = !string.IsNullOrWhiteSpace(destinationVolumeIdentity) && !string.IsNullOrWhiteSpace(identity?.VolumeIdentity) && string.Equals(identity!.VolumeIdentity, destinationVolumeIdentity, StringComparison.OrdinalIgnoreCase);
        var crossVolume = !string.IsNullOrWhiteSpace(destinationVolumeIdentity) && !string.IsNullOrWhiteSpace(identity?.VolumeIdentity) && !sameVolume;
        if (productionSameVolumeCapability && !sameVolume)
            return Preflight(plan, FileOperationState.ValidationFailed, string.IsNullOrWhiteSpace(destinationVolumeIdentity) ? "SAME_VOLUME_UNVERIFIABLE" : "SAME_VOLUME_CHANGED", L.T("same-volume destinationのWindows volume identityを確認できないか、計画時と異なります。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        if (!productionCrossVolumeCapability && !sameVolume)
            return Preflight(plan, FileOperationState.ValidationFailed, string.IsNullOrWhiteSpace(destinationVolumeIdentity) ? "SAME_VOLUME_UNVERIFIABLE" : "CROSS_VOLUME_NOT_SUPPORTED", L.T("Cross-volume Move/Copyはこのplanの実行capabilityではありません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now);
        if (productionCrossVolumeCapability && (!crossVolume || !string.Equals(destinationVolumeIdentity, plan.DestinationVolumeIdentity, StringComparison.OrdinalIgnoreCase)))
            return Preflight(plan, FileOperationState.ValidationFailed, string.IsNullOrWhiteSpace(destinationVolumeIdentity) ? "CROSS_VOLUME_UNVERIFIABLE" : "CROSS_VOLUME_CHANGED", L.T("Cross-Volume destinationのWindows volume identityを確認できないか、immutable plan snapshotと異なります。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now, false, false);
        var destinationConflict = File.Exists(plan.DestinationPath) || Directory.Exists(plan.DestinationPath);
        if (destinationConflict)
            return Preflight(plan, FileOperationState.DestinationConflict, "DESTINATION_CONFLICT", L.T("destinationが既に存在します。overwriteは許可されません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, true, null, now, sameVolume, crossVolume);
        var temporaryAvailable = (bool?)null;
        if (productionCrossVolumeCapability)
        {
            var tempParent = Path.GetDirectoryName(plan.TemporaryDestinationPath!);
            if (string.IsNullOrWhiteSpace(tempParent) || !PathPolicy.IsWithinRoot(destinationRoot.ConfiguredPath, plan.TemporaryDestinationPath!) || !string.Equals(Path.GetFullPath(tempParent), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase) || PathPolicy.IsReparsePoint(new DirectoryInfo(tempParent)))
                return Preflight(plan, FileOperationState.ValidationFailed, "INVALID_TEMP_DESTINATION", L.T("operation-specific temporary destinationはfinal destinationと同じauthorized parent内である必要があります。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, false, null, now, false, crossVolume, false);
            temporaryAvailable = !File.Exists(plan.TemporaryDestinationPath!) && !Directory.Exists(plan.TemporaryDestinationPath!);
            if (temporaryAvailable is false)
                return Preflight(plan, FileOperationState.DestinationConflict, "TEMP_DESTINATION_CONFLICT", L.T("operation-specific temporary destinationが既に存在します。overwriteまたは他operation artifactの引継ぎは許可されません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, true, null, now, false, crossVolume, false);
        }
        long? freeBytes;
        try { freeBytes = new DriveInfo(Path.GetPathRoot(plan.DestinationPath)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Preflight(plan, FileOperationState.FreeSpaceUnknown, "DESTINATION_SPACE_UNKNOWN", L.T("destination free spaceを確認できないためReadyにできません。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, false, false, null, now, sameVolume, crossVolume, temporaryAvailable); }
        var requiredFreeBytes = productionCrossVolumeCapability ? FileOperationPolicy.RequiredCrossVolumeDestinationFreeBytes(sourceInfo.Length) : sourceInfo.Length;
        if (freeBytes < requiredFreeBytes)
            return Preflight(plan, FileOperationState.InsufficientSpace, "DESTINATION_SPACE_INSUFFICIENT", L.T("destinationの空き容量が安全marginを含めて不足しています。"), true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, false, freeBytes, now, sameVolume, crossVolume, temporaryAvailable, requiredFreeBytes);
        var message = productionCrossVolumeCapability ? L.T("source identity、different-volume destination、temporary artifact、Full SHA-256 requirement、overwrite prohibition、free-space safety marginを確認しました。") : L.T("source identity、same-volume destination、overwrite prohibitionを確認しました。");
        return Preflight(plan, FileOperationState.Ready, "PREFLIGHT_READY", message, true, identityMatches, sizeMatches, mtimeMatches, quickMatches, fullMatches, true, false, freeBytes, now, sameVolume, crossVolume, temporaryAvailable, requiredFreeBytes);
    }

    private static FileOperationPreflight Preflight(FileOperationPlan plan, FileOperationState state, string code, string message, bool sourceExists, bool identity, bool size, bool mtime, bool quick, bool? full, bool destinationAvailable, bool destinationConflict, long? destinationFreeBytes, DateTimeOffset validatedAt, bool sameVolumeVerified = false, bool crossVolumeVerified = false, bool? temporaryDestinationAvailable = null, long? requiredDestinationFreeBytes = null)
        => new(plan.OperationId, state, code, message, sourceExists, identity, size, mtime, quick, full, destinationAvailable, destinationConflict, destinationFreeBytes, validatedAt, sameVolumeVerified, crossVolumeVerified, temporaryDestinationAvailable, requiredDestinationFreeBytes);

    private static async Task<string> VerifyDestinationAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.DestinationPath) || !File.Exists(plan.DestinationPath) || File.Exists(plan.SourcePath))
            throw new IOException("Destination verification requires destination present and source absent.");
        var destinationInfo = new FileInfo(plan.DestinationPath);
        if (PathPolicy.IsReparsePoint(destinationInfo)) throw new InvalidOperationException("Destination is a reparse point.");
        var identity = WindowsFileIdentityReader.TryGet(plan.DestinationPath);
        if (identity is null || !string.Equals(identity.VolumeIdentity, plan.SourceVolumeIdentity, StringComparison.OrdinalIgnoreCase) || !string.Equals(identity.FileIdentity, plan.SourceFileIdentity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Destination stable identity does not match the immutable source plan.");
        if (destinationInfo.Length != plan.SourceSizeBytes) throw new InvalidOperationException("Destination size does not match the immutable source plan.");
        var quick = await FileSystemMediaScanner.QuickFingerprintAsync(plan.DestinationPath, destinationInfo.Length, cancellationToken);
        if (!MatchesNullable(plan.SourceQuickFingerprint, quick)) throw new InvalidOperationException("Destination quick fingerprint does not match the immutable source plan.");
        if (plan.ValidationStrength == SourceValidationStrength.StrongFullHash)
        {
            if (string.IsNullOrWhiteSpace(plan.SourceFullSha256)) throw new InvalidOperationException("Strong destination verification lacks source hash evidence.");
            var hash = await FileHash.Sha256Async(plan.DestinationPath, cancellationToken);
            if (!string.Equals(hash, plan.SourceFullSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Destination Full SHA-256 does not match the immutable source plan.");
        }
        return (await ReadIdentityJsonAsync(plan.DestinationPath, cancellationToken)) ?? throw new IOException("Destination identity could not be read after filesystem operation.");
    }

    /// <summary>
    /// Builds the explicit DB publish for a same-volume Move into another registered root. Rescanning only the
    /// source root would mark the moved file Missing, so the verified destination is published directly while
    /// keeping MediaFileId/MediaContentId. The destination must still lie inside the snapshotted root.
    /// </summary>
    private async Task<CrossVolumeMoveDatabasePublish> BuildSameVolumeCrossRootPublishAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.DestinationPath) || string.IsNullOrWhiteSpace(plan.SourceFullSha256) || plan.DestinationLibraryRootId is null)
            throw new InvalidOperationException("Same-volume cross-root publish snapshot is incomplete.");
        var destinationRoot = await _repository.GetLibraryRootAsync(plan.DestinationLibraryRootId.Value, cancellationToken) ?? throw new InvalidOperationException("Same-volume cross-root destination root is unavailable.");
        if (!PathPolicy.IsWithinRoot(destinationRoot.ConfiguredPath, plan.DestinationPath)) throw new InvalidOperationException("Same-volume cross-root destination escaped its authorized root.");
        var info = new FileInfo(plan.DestinationPath);
        var identity = WindowsFileIdentityReader.TryGet(plan.DestinationPath) ?? throw new InvalidOperationException("Same-volume cross-root destination identity could not be read.");
        var quick = await FileSystemMediaScanner.QuickFingerprintAsync(plan.DestinationPath, info.Length, cancellationToken)
            ?? throw new InvalidOperationException("Same-volume cross-root destination quick fingerprint could not be read.");
        return new CrossVolumeMoveDatabasePublish(plan.OperationId, plan.MediaFileId, plan.MediaContentId, destinationRoot.LibraryRootId, plan.DestinationPath,
            plan.DestinationNormalizedPath ?? PathPolicy.NormalizeForIdentity(plan.DestinationPath), Path.GetFileName(plan.DestinationPath), Path.GetExtension(plan.DestinationPath),
            info.Length, new DateTimeOffset(info.CreationTimeUtc), new DateTimeOffset(info.LastWriteTimeUtc), identity.VolumeIdentity, identity.FileIdentity, quick,
            plan.SourceFullSha256, AuditEventName: "SameVolumeCrossRootDbPublished");
    }

    private async Task ReconcileRootAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var root = await _repository.GetLibraryRootAsync(rootId, cancellationToken) ?? throw new InvalidOperationException("Library root was not found.");
        var scanId = await _repository.StartScanAsync(rootId, cancellationToken);
        var scanner = new FileSystemMediaScanner();
        var seen = 0;
        var added = 0;
        var updated = 0;
        await foreach (var scanned in scanner.EnumerateAsync(root, cancellationToken))
        {
            var result = await _repository.UpsertScannedFileAsync(scanned, scanId, cancellationToken);
            seen++;
            if (result.IsNew) added++; else updated++;
        }
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", seen, added, updated, 0, 0, DateTimeOffset.UtcNow), cancellationToken);
        await _repository.MarkAbsentFilesAfterCompletedScanAsync(rootId, scanId, cancellationToken);
    }

    private async Task EnsureDatabasePublishAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        var current = await _repository.GetMediaFileAsync(plan.MediaFileId, cancellationToken) ?? throw new InvalidOperationException("C# DB publish did not retain MediaFileId.");
        if (current.MediaContentId != plan.MediaContentId || !string.Equals(PathPolicy.NormalizeForIdentity(current.Path), plan.DestinationNormalizedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("C# DB publish did not retain stable MediaContentId and destination path.");
    }

    private static bool ValidateDestinationFileName(string source, string destination, out string code, out string message)
    {
        code = string.Empty;
        message = string.Empty;
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(fileName)) { code = "EMPTY_FILENAME"; message = L.T("destination filenameが空です。"); return false; }
        if (fileName.EndsWith(' ') || fileName.EndsWith('.')) { code = "TRAILING_DOT_OR_SPACE"; message = L.T("Windows filenameの末尾にdotまたはspaceは使用できません。"); return false; }
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Any(ch => ch < 32 || "<>:\"/\\|?*".Contains(ch))) { code = "INVALID_WINDOWS_FILENAME"; message = L.T("destination filenameにWindowsで無効な文字があります。"); return false; }
        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd('.', ' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9") { code = "RESERVED_WINDOWS_FILENAME"; message = L.T("destination filenameはWindows予約名です。"); return false; }
        var fullDestination = Path.GetFullPath(destination);
        if (fullDestination.Length >= 260) { code = "PATH_LENGTH_NOT_SUPPORTED"; message = L.T("Phase 6.2の安全実行では260文字以上のdestination pathを許可しません。"); return false; }
        if (string.Equals(Path.GetFullPath(source), fullDestination, StringComparison.OrdinalIgnoreCase)) { code = "SOURCE_DESTINATION_EQUAL_OR_CASE_ONLY"; message = L.T("sourceとdestinationが同一またはcase-only renameです。Phase 6.2では実行しません。"); return false; }
        return true;
    }

    private enum SourcePathObservation { Present, Absent, Unknown }

    private static SourcePathObservation ObserveSourcePath(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.Exists(path) ? SourcePathObservation.Present : SourcePathObservation.Unknown;
        const uint InvalidFileAttributes = 0xFFFFFFFF;
        const int ErrorFileNotFound = 2;
        const int ErrorPathNotFound = 3;
        var attributes = GetFileAttributes(path);
        if (attributes != InvalidFileAttributes) return SourcePathObservation.Present;
        var error = Marshal.GetLastWin32Error();
        return error is ErrorFileNotFound or ErrorPathNotFound ? SourcePathObservation.Absent : SourcePathObservation.Unknown;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);

    private static RecycleDatabasePublish CreateRecycleDatabasePublish(FileOperationPlan plan, DateTimeOffset recycledAt)
    {
        if (!FileOperationPolicy.IsProductionRecycle(plan) || string.IsNullOrWhiteSpace(plan.SourceFullSha256))
            throw new InvalidOperationException("Recycle lifecycle publish requires a final-confirmed Production Recycle plan with Full SHA-256.");
        return new RecycleDatabasePublish(plan.OperationId, plan.MediaFileId, plan.MediaContentId, plan.SourcePath, plan.SourceNormalizedPath, plan.SourceFullSha256, plan.SourceSizeBytes, plan.SourceVolumeIdentity, plan.SourceFileIdentity, recycledAt);
    }

    private static PermanentDeleteDatabasePublish CreatePermanentDeleteDatabasePublish(FileOperationPlan plan, DateTimeOffset permanentlyDeletedAt)
    {
        if (!FileOperationPolicy.IsProductionPermanentDelete(plan) || plan.SelectedMediaCount != 1 || string.IsNullOrWhiteSpace(plan.SourceFullSha256))
            throw new InvalidOperationException("PermanentDelete lifecycle publish requires a final-confirmed single-selection Production PermanentDelete plan with Full SHA-256.");
        return new PermanentDeleteDatabasePublish(plan.OperationId, plan.MediaFileId, plan.MediaContentId, plan.SourcePath, plan.SourceNormalizedPath, plan.SourceFullSha256, plan.SourceSizeBytes, plan.SourceVolumeIdentity, plan.SourceFileIdentity, permanentlyDeletedAt);
    }

    private void PublishProductionPermanentDeleteTiming(ProductionPermanentDeleteExecutionTiming timing)
    {
        _log.Write("Information", "ProductionPermanentDeleteExecutionTiming", new { timing.OperationId, timing.FinalRevalidationMilliseconds, timing.DeleteApiMilliseconds, timing.SourceObservationMilliseconds, timing.DbLifecyclePublishMilliseconds, timing.TotalMilliseconds });
        try { _productionPermanentDeleteTimingObserver?.Invoke(timing); }
        catch (Exception ex) { _log.Write("Warning", "ProductionPermanentDeleteTimingObserverFailed", new { timing.OperationId }, ex); }
    }

    private void PublishProductionRecycleTiming(ProductionRecycleExecutionTiming timing)
    {
        _log.Write("Information", "ProductionRecycleExecutionTiming", new { timing.OperationId, timing.FinalRevalidationMilliseconds, timing.RecycleShellMilliseconds, timing.SourceObservationMilliseconds, timing.DbLifecyclePublishMilliseconds, timing.TotalMilliseconds });
        try { _productionRecycleTimingObserver?.Invoke(timing); }
        catch (Exception ex) { _log.Write("Warning", "ProductionRecycleTimingObserverFailed", new { timing.OperationId }, ex); }
    }

    private static bool MatchesNullable(string? expected, string? actual) => string.IsNullOrWhiteSpace(expected) ? string.IsNullOrWhiteSpace(actual) : string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(FileOperationState state) => state is FileOperationState.Completed or FileOperationState.Recycled or FileOperationState.RecycleFailed or FileOperationState.RecycleUnavailable or FileOperationState.PermanentlyDeleted or FileOperationState.PermanentDeleteFailed or FileOperationState.Cancelled or FileOperationState.RolledBack or FileOperationState.CannotRollback;

    private static string DescribeEffect(FileOperationType type, string source, string? destination, bool crossVolumeMove) => type switch
    {
        FileOperationType.Move when crossVolumeMove => $"Cross-Volume Move '{source}' to '{destination}' via operation-specific destination temp copy, Full SHA-256 verification, final publish, source revalidation, source removal, and C# DB publish; overwrite is prohibited.",
        FileOperationType.Move => $"Move '{source}' to '{destination}' without overwrite and only after same-volume verification.",
        FileOperationType.Rename => $"Rename '{source}' to '{destination}' without overwrite and only after same-volume verification.",
        FileOperationType.Recycle => $"Move '{source}' to the Windows Recycle Bin only after Full SHA-256 revalidation, source-path observation, C# lifecycle publish, and durable audit; PermanentDelete is not used.",
        FileOperationType.PermanentDelete => $"PERMANENT DELETE — CANNOT BE UNDONE: permanently delete single explicit source '{source}' without Windows Recycle Bin after StrongFullHash final revalidation, independent irreversible Final Confirmation, source absence verification, C# permanent lifecycle publish, and durable audit; secure erase, recursive directory delete, wildcard, batch, and overwrite are not used.",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown operation type.")
    };

    private static string RecoverabilitySummary(FileOperationPlan plan)
    {
        if (plan.OperationType == FileOperationType.Recycle)
            return L.T("Windows Recycle Binを利用する条件付きrecoverable removalです。アプリ独自Restoreは実装せず、Windows側のRestore後は次scanでreconciliationします。FilesystemとSQLite publishは非atomicのためinspection-based recoveryを必要とします。PermanentDeleteは実行されません。");
        if (plan.OperationType == FileOperationType.PermanentDelete)
            return L.T("Windows Recycle Binを使用しない不可逆な単一ファイル削除です。アプリから元に戻せず、通常のRestore機能はありません。source absenceとC# permanent lifecycle publishは非atomicのため、restart recoveryはfilesystem observationだけで行いdeleteを再実行しません。");
        return plan.Recoverability switch
        {
            OperationRecoverability.ConditionallyReversible => L.T("Move/Renameは条件付きで復旧可能ですが、Cross-Volume Moveはcopy、hash verification、source removal、SQLite publishが非atomicであり、inspection-based recoveryを必要とします。"),
            OperationRecoverability.Reversible => L.T("可逆操作として設計されています。"),
            OperationRecoverability.Irreversible => L.T("不可逆操作は実行されません。"),
            _ => L.T("Recoverabilityは不明です。")
        };
    }

    private static void EnsureFixtureContainment(FileOperationPlan plan, DisposableFixtureCapability capability)
    {
        if (string.IsNullOrWhiteSpace(capability.FixtureRoot) || string.IsNullOrWhiteSpace(capability.MarkerPath) || !File.Exists(capability.MarkerPath) || !string.Equals(Path.GetFileName(capability.MarkerPath), FileOperationPolicy.FixtureMarkerFileName, StringComparison.Ordinal))
            throw new InvalidOperationException("DISPOSABLE_FIXTURE_REQUIRED: fixture marker is missing.");
        if (!PathPolicy.IsWithinRoot(capability.FixtureRoot, plan.SourcePath) || string.IsNullOrWhiteSpace(plan.DestinationPath) || !PathPolicy.IsWithinRoot(capability.FixtureRoot, plan.DestinationPath))
            throw new InvalidOperationException("DISPOSABLE_FIXTURE_REQUIRED: source/destination is outside the disposable fixture root.");
        if ((File.Exists(plan.SourcePath) && PathPolicy.IsReparsePoint(new FileInfo(plan.SourcePath))) || (File.Exists(plan.DestinationPath) && PathPolicy.IsReparsePoint(new FileInfo(plan.DestinationPath))))
            throw new InvalidOperationException("DISPOSABLE_FIXTURE_REQUIRED: reparse points are not allowed.");
    }

    private static async Task<string?> ReadIdentityJsonAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        var identity = WindowsFileIdentityReader.TryGet(path);
        string? quick = null;
        try { quick = await FileSystemMediaScanner.QuickFingerprintAsync(path, info.Length, cancellationToken); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return JsonSerializer.Serialize(new { path, bytes = info.Length, lastWriteTimeUtc = info.LastWriteTimeUtc, identity?.VolumeIdentity, identity?.FileIdentity, quick });
    }
}
