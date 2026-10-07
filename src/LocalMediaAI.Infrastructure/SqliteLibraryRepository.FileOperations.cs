using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    private sealed record DurableIntentFields(string Type, string Overwrite, string Conflict);

    public async Task<FileOperationProposal> CreateFileOperationProposalAsync(FileOperationProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var validSource = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE media_file_id=$media AND media_content_id=$content;", cancellationToken, ("$media", proposal.MediaFileId.ToString("D")), ("$content", proposal.MediaContentId.ToString("D")));
        if (validSource != 1) throw new InvalidOperationException("Operation proposal source is no longer current.");
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO file_operation_proposal(file_operation_proposal_id,media_file_id,media_content_id,review_item_id,review_context,suggested_operation_type,created_at) VALUES($id,$media,$content,$review,$context,$type,$created);", cancellationToken,
            ("$id", proposal.ProposalId.ToString("D")), ("$media", proposal.MediaFileId.ToString("D")), ("$content", proposal.MediaContentId.ToString("D")), ("$review", (object?)proposal.ReviewItemId ?? DBNull.Value), ("$context", (object?)proposal.ReviewContext ?? DBNull.Value), ("$type", proposal.SuggestedOperationType.ToString()), ("$created", ToDb(proposal.CreatedAt)));
        await transaction.CommitAsync(cancellationToken);
        return proposal;
    }

    public async Task<FileOperationIntent> CreateFileOperationIntentAsync(FileOperationIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.OverwritePolicy != OverwritePolicy.Prohibit) throw new InvalidOperationException("Overwrite is prohibited.");
        await using var connection = await OpenAsync(cancellationToken);
        var proposalExists = await ScalarLongAsync(connection, null, "SELECT COUNT(1) FROM file_operation_proposal WHERE file_operation_proposal_id=$proposal;", cancellationToken, ("$proposal", intent.ProposalId.ToString("D")));
        if (proposalExists != 1) throw new InvalidOperationException("Operation intent proposal was not found.");
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO file_operation_intent(file_operation_intent_id,file_operation_proposal_id,operation_type,requested_destination_path,overwrite_policy,conflict_policy,created_at,acknowledged_at) VALUES($id,$proposal,$type,$destination,$overwrite,$conflict,$created,$ack);", cancellationToken,
            ("$id", intent.IntentId.ToString("D")), ("$proposal", intent.ProposalId.ToString("D")), ("$type", intent.OperationType.ToString()), ("$destination", (object?)intent.RequestedDestinationPath ?? DBNull.Value), ("$overwrite", intent.OverwritePolicy.ToString()), ("$conflict", intent.ConflictPolicy.ToString()), ("$created", ToDb(intent.CreatedAt)), ("$ack", intent.AcknowledgedAt is null ? DBNull.Value : ToDb(intent.AcknowledgedAt.Value)));
        return intent;
    }

    public async Task<FileOperationPlan> CreateFileOperationPlanAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OverwritePolicy != OverwritePolicy.Prohibit) throw new InvalidOperationException("Overwrite is prohibited.");
        if (plan.State != FileOperationState.Planned) throw new InvalidOperationException("A new operation plan must start in Planned state.");
        if (plan.OperationType == FileOperationType.PermanentDelete)
        {
            if (!FileOperationPolicy.IsProductionPermanentDelete(plan) || plan.ProductionSameVolumeCapability || plan.ProductionCrossVolumeMoveCapability || plan.ProductionRecycleCapability)
                throw new InvalidOperationException("PermanentDelete plan requires its exclusive ProductionPermanentDeleteCapability.");
            if (plan.SelectedMediaCount != 1 || plan.ValidationStrength != SourceValidationStrength.StrongFullHash || string.IsNullOrWhiteSpace(plan.SourceFullSha256) || string.IsNullOrWhiteSpace(plan.SourceVolumeIdentity) || string.IsNullOrWhiteSpace(plan.SourceFileIdentity) || plan.Recoverability != OperationRecoverability.Irreversible)
                throw new InvalidOperationException("PermanentDelete plan requires a single-selection immutable StrongFullHash irreversible snapshot.");
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var intent = await QuerySingleAsync(connection, transaction, "SELECT operation_type,overwrite_policy,conflict_policy FROM file_operation_intent WHERE file_operation_intent_id=$intent;", reader => new DurableIntentFields(reader.GetString(0), reader.GetString(1), reader.GetString(2)), cancellationToken, ("$intent", plan.IntentId.ToString("D")));
        if (intent is null || !string.Equals(intent.Type, plan.OperationType.ToString(), StringComparison.Ordinal) || !string.Equals(intent.Overwrite, plan.OverwritePolicy.ToString(), StringComparison.Ordinal) || !string.Equals(intent.Conflict, plan.ConflictPolicy.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Operation plan does not match the durable intent.");
        var sourceCurrent = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE media_file_id=$media AND media_content_id=$content AND library_root_id=$root AND presence_state='Present';", cancellationToken, ("$media", plan.MediaFileId.ToString("D")), ("$content", plan.MediaContentId.ToString("D")), ("$root", plan.LibraryRootId.ToString("D")));
        if (sourceCurrent != 1) throw new InvalidOperationException("Operation plan source is not current.");
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO file_operation_plan(operation_id,file_operation_intent_id,library_root_id,media_file_id,media_content_id,operation_type,operation_mode,source_path,source_normalized_path,source_volume_identity,source_file_identity,source_size_bytes,source_last_write_time_utc,source_quick_fingerprint,source_full_sha256,validation_strength,destination_path,destination_normalized_path,expected_filesystem_effect,overwrite_policy,conflict_policy,recoverability,user_acknowledged,created_at,confirmed_at,final_execution_confirmed_at,production_same_volume_capability,destination_library_root_id,destination_root_path,destination_volume_identity,temporary_destination_path,temporary_destination_normalized_path,destination_free_space_snapshot_bytes,production_cross_volume_move_capability,cross_volume_move_stage,production_recycle_capability,production_permanent_delete_capability,selected_media_count,state,last_error_code,last_error_message,updated_at) VALUES($id,$intent,$root,$media,$content,$type,$mode,$source,$normalized,$volume,$fileIdentity,$size,$mtime,$quick,$full,$strength,$destination,$destinationNormalized,$effect,$overwrite,$conflict,$recoverability,$acknowledged,$created,$confirmed,$finalConfirmed,$sameCapability,$destinationRootId,$destinationRootPath,$destinationVolume,$temporaryPath,$temporaryNormalized,$freeSnapshot,$crossCapability,$crossStage,$recycleCapability,$permanentCapability,$selectedCount,$state,$code,$message,$updated);", cancellationToken,
            ("$id", plan.OperationId.ToString("D")), ("$intent", plan.IntentId.ToString("D")), ("$root", plan.LibraryRootId.ToString("D")), ("$media", plan.MediaFileId.ToString("D")), ("$content", plan.MediaContentId.ToString("D")), ("$type", plan.OperationType.ToString()), ("$mode", plan.Mode.ToString()), ("$source", plan.SourcePath), ("$normalized", plan.SourceNormalizedPath), ("$volume", (object?)plan.SourceVolumeIdentity ?? DBNull.Value), ("$fileIdentity", (object?)plan.SourceFileIdentity ?? DBNull.Value), ("$size", plan.SourceSizeBytes), ("$mtime", ToDb(plan.SourceLastWriteTimeUtc)), ("$quick", (object?)plan.SourceQuickFingerprint ?? DBNull.Value), ("$full", (object?)plan.SourceFullSha256 ?? DBNull.Value), ("$strength", plan.ValidationStrength.ToString()), ("$destination", (object?)plan.DestinationPath ?? DBNull.Value), ("$destinationNormalized", (object?)plan.DestinationNormalizedPath ?? DBNull.Value), ("$effect", plan.ExpectedFilesystemEffect), ("$overwrite", plan.OverwritePolicy.ToString()), ("$conflict", plan.ConflictPolicy.ToString()), ("$recoverability", plan.Recoverability.ToString()), ("$acknowledged", plan.UserAcknowledged ? 1 : 0), ("$created", ToDb(plan.CreatedAt)), ("$confirmed", plan.ConfirmedAt is null ? DBNull.Value : ToDb(plan.ConfirmedAt.Value)), ("$finalConfirmed", plan.FinalExecutionConfirmedAt is null ? DBNull.Value : ToDb(plan.FinalExecutionConfirmedAt.Value)), ("$sameCapability", plan.ProductionSameVolumeCapability ? 1 : 0), ("$destinationRootId", plan.DestinationLibraryRootId is null ? DBNull.Value : plan.DestinationLibraryRootId.Value.ToString("D")), ("$destinationRootPath", (object?)plan.DestinationRootPath ?? DBNull.Value), ("$destinationVolume", (object?)plan.DestinationVolumeIdentity ?? DBNull.Value), ("$temporaryPath", (object?)plan.TemporaryDestinationPath ?? DBNull.Value), ("$temporaryNormalized", (object?)plan.TemporaryDestinationNormalizedPath ?? DBNull.Value), ("$freeSnapshot", plan.DestinationFreeSpaceSnapshotBytes is null ? DBNull.Value : plan.DestinationFreeSpaceSnapshotBytes.Value), ("$crossCapability", plan.ProductionCrossVolumeMoveCapability ? 1 : 0), ("$crossStage", plan.CrossVolumeMoveStage.ToString()), ("$recycleCapability", plan.ProductionRecycleCapability ? 1 : 0), ("$permanentCapability", plan.ProductionPermanentDeleteCapability ? 1 : 0), ("$selectedCount", plan.SelectedMediaCount), ("$state", plan.State.ToString()), ("$code", (object?)plan.LastErrorCode ?? DBNull.Value), ("$message", (object?)plan.LastErrorMessage ?? DBNull.Value), ("$updated", ToDb(plan.UpdatedAt)));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), plan.OperationId, DateTimeOffset.UtcNow, "PlanCreated", JsonSerializer.Serialize(new { plan.OperationType, plan.Mode, plan.ProductionSameVolumeCapability, plan.ProductionCrossVolumeMoveCapability, plan.ProductionRecycleCapability, plan.ProductionPermanentDeleteCapability, plan.SelectedMediaCount, plan.DestinationLibraryRootId, plan.DestinationVolumeIdentity, plan.TemporaryDestinationPath, plan.ValidationStrength, plan.OverwritePolicy, plan.ConflictPolicy })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan;
    }

    public async Task<FileOperationPlan?> GetFileOperationPlanAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D")));
    }

    public async Task<FileOperationPlan> TransitionFileOperationPlanAsync(Guid operationId, FileOperationState targetState, string? errorCode, string? errorMessage, DateTimeOffset? confirmedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        FileOperationStateMachine.EnsureTransition(current.State, targetState);
        var now = DateTimeOffset.UtcNow;
        var effectiveConfirmation = confirmedAt ?? current.ConfirmedAt;
        var acknowledged = current.UserAcknowledged || confirmedAt is not null;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,user_acknowledged=$acknowledged,confirmed_at=$confirmed,last_error_code=$code,last_error_message=$message,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", targetState.ToString()), ("$acknowledged", acknowledged ? 1 : 0), ("$confirmed", effectiveConfirmation is null ? DBNull.Value : ToDb(effectiveConfirmation.Value)), ("$code", (object?)errorCode ?? DBNull.Value), ("$message", (object?)errorMessage ?? DBNull.Value), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "StateTransition", JsonSerializer.Serialize(new { From = current.State.ToString(), To = targetState.ToString(), errorCode, errorMessage, DryRunAcknowledged = confirmedAt is not null })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = targetState, UserAcknowledged = acknowledged, ConfirmedAt = effectiveConfirmation, LastErrorCode = errorCode, LastErrorMessage = errorMessage, UpdatedAt = now };
    }

    public async Task<FileOperationPlan> ConfirmProductionSameVolumeExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (current.Mode != FileOperationMode.ProductionDryRun || !current.ProductionSameVolumeCapability)
            throw new InvalidOperationException("Production same-volume capability is not present for this plan.");
        if (current.State != FileOperationState.ConfirmationRequired || !current.UserAcknowledged || current.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement and a successful preflight are required before Final Execution Confirmation.");
        if (current.FinalExecutionConfirmedAt is not null)
            throw new InvalidOperationException("Final Execution Confirmation was already recorded.");
        FileOperationStateMachine.EnsureTransition(current.State, FileOperationState.FinalConfirmationRequired);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,final_execution_confirmed_at=$confirmed,last_error_code=NULL,last_error_message=NULL,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", FileOperationState.FinalConfirmationRequired.ToString()), ("$confirmed", ToDb(now)), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "FinalExecutionConfirmed", JsonSerializer.Serialize(new { current.OperationType, current.SourcePath, current.DestinationPath, current.ProductionSameVolumeCapability })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = FileOperationState.FinalConfirmationRequired, FinalExecutionConfirmedAt = now, LastErrorCode = null, LastErrorMessage = null, UpdatedAt = now };
    }

    public async Task<FileOperationPlan> ConfirmProductionCrossVolumeMoveExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (current.Mode != FileOperationMode.ProductionDryRun || !current.ProductionCrossVolumeMoveCapability || current.ProductionSameVolumeCapability || current.OperationType != FileOperationType.Move)
            throw new InvalidOperationException("Production Cross-Volume Move capability is not present for this plan.");
        if (current.State != FileOperationState.ConfirmationRequired || !current.UserAcknowledged || current.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement and a successful preflight are required before Final Execution Confirmation.");
        if (current.FinalExecutionConfirmedAt is not null)
            throw new InvalidOperationException("Final Execution Confirmation was already recorded.");
        FileOperationStateMachine.EnsureTransition(current.State, FileOperationState.FinalConfirmationRequired);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,final_execution_confirmed_at=$confirmed,cross_volume_move_stage=$stage,last_error_code=NULL,last_error_message=NULL,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", FileOperationState.FinalConfirmationRequired.ToString()), ("$confirmed", ToDb(now)), ("$stage", CrossVolumeMoveStage.PreflightReady.ToString()), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "FinalCrossVolumeMoveExecutionConfirmed", JsonSerializer.Serialize(new { current.OperationType, current.SourcePath, current.DestinationPath, current.DestinationVolumeIdentity, current.TemporaryDestinationPath, current.ProductionCrossVolumeMoveCapability })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = FileOperationState.FinalConfirmationRequired, FinalExecutionConfirmedAt = now, CrossVolumeMoveStage = CrossVolumeMoveStage.PreflightReady, LastErrorCode = null, LastErrorMessage = null, UpdatedAt = now };
    }

    public async Task<FileOperationPlan> ConfirmProductionRecycleExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionRecycle(current) || current.ProductionSameVolumeCapability || current.ProductionCrossVolumeMoveCapability)
            throw new InvalidOperationException("Production Recycle capability is not present for this plan.");
        if (current.State != FileOperationState.ConfirmationRequired || !current.UserAcknowledged || current.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement and a successful preflight are required before Final Execution Confirmation.");
        if (current.FinalExecutionConfirmedAt is not null)
            throw new InvalidOperationException("Final Execution Confirmation was already recorded.");
        FileOperationStateMachine.EnsureTransition(current.State, FileOperationState.FinalConfirmationRequired);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,final_execution_confirmed_at=$confirmed,last_error_code=NULL,last_error_message=NULL,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", FileOperationState.FinalConfirmationRequired.ToString()), ("$confirmed", ToDb(now)), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "FinalRecycleExecutionConfirmed", JsonSerializer.Serialize(new { current.OperationType, current.SourcePath, current.SourceSizeBytes, current.SourceFullSha256, current.ProductionRecycleCapability, PermanentDelete = false })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = FileOperationState.FinalConfirmationRequired, FinalExecutionConfirmedAt = now, LastErrorCode = null, LastErrorMessage = null, UpdatedAt = now };
    }

    public async Task<FileOperationPlan> ConfirmProductionPermanentDeleteExecutionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!FileOperationPolicy.IsProductionPermanentDelete(current) || current.ProductionSameVolumeCapability || current.ProductionCrossVolumeMoveCapability || current.ProductionRecycleCapability)
            throw new InvalidOperationException("Production PermanentDelete capability is not present for this plan.");
        if (current.SelectedMediaCount != 1 || current.ValidationStrength != SourceValidationStrength.StrongFullHash || string.IsNullOrWhiteSpace(current.SourceFullSha256))
            throw new InvalidOperationException("PermanentDelete requires an immutable single-selection StrongFullHash plan.");
        if (current.State != FileOperationState.ConfirmationRequired || !current.UserAcknowledged || current.ConfirmedAt is null)
            throw new InvalidOperationException("Dry Run acknowledgement and a successful preflight are required before irreversible Final Execution Confirmation.");
        if (current.FinalExecutionConfirmedAt is not null)
            throw new InvalidOperationException("Irreversible Final Execution Confirmation was already recorded.");
        FileOperationStateMachine.EnsureTransition(current.State, FileOperationState.FinalConfirmationRequired);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,final_execution_confirmed_at=$confirmed,last_error_code=NULL,last_error_message=NULL,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", FileOperationState.FinalConfirmationRequired.ToString()), ("$confirmed", ToDb(now)), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "FinalPermanentDeleteExecutionConfirmed", JsonSerializer.Serialize(new { current.OperationType, current.SourcePath, current.SourceSizeBytes, current.SourceFullSha256, current.SelectedMediaCount, current.ProductionPermanentDeleteCapability, WindowsRecycleBinUsed = false, Irreversible = true })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = FileOperationState.FinalConfirmationRequired, FinalExecutionConfirmedAt = now, LastErrorCode = null, LastErrorMessage = null, UpdatedAt = now };
    }

    public async Task<FileOperationPlan> TransitionCrossVolumeMovePlanAsync(Guid operationId, FileOperationState targetState, CrossVolumeMoveStage stage, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", operationId.ToString("D"))) ?? throw new InvalidOperationException("Operation plan was not found.");
        if (!current.ProductionCrossVolumeMoveCapability)
            throw new InvalidOperationException("Cross-Volume transaction stage requires its explicit capability.");
        FileOperationStateMachine.EnsureTransition(current.State, targetState);
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state=$state,cross_volume_move_stage=$stage,last_error_code=$code,last_error_message=$message,updated_at=$updated WHERE operation_id=$id;", cancellationToken,
            ("$state", targetState.ToString()), ("$stage", stage.ToString()), ("$code", (object?)errorCode ?? DBNull.Value), ("$message", (object?)errorMessage ?? DBNull.Value), ("$updated", ToDb(now)), ("$id", operationId.ToString("D")));
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), operationId, now, "CrossVolumeMoveStageTransition", JsonSerializer.Serialize(new { FromState = current.State.ToString(), ToState = targetState.ToString(), FromStage = current.CrossVolumeMoveStage.ToString(), ToStage = stage.ToString(), errorCode, errorMessage })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = targetState, CrossVolumeMoveStage = stage, LastErrorCode = errorCode, LastErrorMessage = errorMessage, UpdatedAt = now };
    }

    public async Task<MediaFile> PublishCrossVolumeMoveAsync(CrossVolumeMoveDatabasePublish publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (string.IsNullOrWhiteSpace(publish.DestinationPath) || string.IsNullOrWhiteSpace(publish.DestinationNormalizedPath) || string.IsNullOrWhiteSpace(publish.DestinationVolumeIdentity) || string.IsNullOrWhiteSpace(publish.DestinationFileIdentity) || string.IsNullOrWhiteSpace(publish.DestinationQuickFingerprint) || string.IsNullOrWhiteSpace(publish.VerifiedFullSha256))
            throw new InvalidOperationException("Cross-Volume DB publish requires a fully verified destination snapshot.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$media;", ReadMediaFile, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("Cross-Volume DB publish source MediaFile was not found.");
        if (current.MediaContentId != publish.MediaContentId)
            throw new InvalidOperationException("Cross-Volume DB publish source MediaContent changed.");
        var rootExists = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM library_root WHERE library_root_id=$root;", cancellationToken, ("$root", publish.DestinationLibraryRootId.ToString("D")));
        if (rootExists != 1) throw new InvalidOperationException("Cross-Volume destination library root was not found.");
        var pathConflict = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM media_file WHERE library_root_id=$root AND normalized_path=$path AND media_file_id<>$media;", cancellationToken, ("$root", publish.DestinationLibraryRootId.ToString("D")), ("$path", publish.DestinationNormalizedPath), ("$media", publish.MediaFileId.ToString("D")));
        if (pathConflict != 0) throw new InvalidOperationException("Cross-Volume DB publish destination path is already owned by another MediaFile.");
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET library_root_id=$root,path=$path,normalized_path=$normalized,file_name=$name,extension=$extension,size_bytes=$size,creation_time_utc=$created,last_write_time_utc=$modified,volume_identity=$volume,file_identity=$fileIdentity,quick_fingerprint=$quick,full_sha256=$full,presence_state='Present',last_seen_at=$seen WHERE media_file_id=$media AND media_content_id=$content;", cancellationToken,
            ("$root", publish.DestinationLibraryRootId.ToString("D")), ("$path", publish.DestinationPath), ("$normalized", publish.DestinationNormalizedPath), ("$name", publish.FileName), ("$extension", publish.Extension), ("$size", publish.SizeBytes), ("$created", ToDb(publish.CreationTimeUtc)), ("$modified", ToDb(publish.LastWriteTimeUtc)), ("$volume", publish.DestinationVolumeIdentity), ("$fileIdentity", publish.DestinationFileIdentity), ("$quick", publish.DestinationQuickFingerprint), ("$full", publish.VerifiedFullSha256), ("$seen", ToDb(now)), ("$media", publish.MediaFileId.ToString("D")), ("$content", publish.MediaContentId.ToString("D")));
        var updated = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$media;", ReadMediaFile, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("Cross-Volume DB publish update disappeared.");
        if (updated.MediaContentId != publish.MediaContentId || updated.LibraryRootId != publish.DestinationLibraryRootId || !string.Equals(updated.NormalizedPath, publish.DestinationNormalizedPath, StringComparison.OrdinalIgnoreCase) || !string.Equals(updated.FullSha256, publish.VerifiedFullSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cross-Volume DB publish did not retain stable identity and verified destination fields.");
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), publish.OperationId, now, publish.AuditEventName, JsonSerializer.Serialize(new { publish.MediaFileId, publish.MediaContentId, publish.DestinationLibraryRootId, publish.DestinationPath, publish.DestinationVolumeIdentity })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<MediaRecycleLifecycle> PublishRecycleLifecycleAsync(RecycleDatabasePublish publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (string.IsNullOrWhiteSpace(publish.SourcePath) || string.IsNullOrWhiteSpace(publish.SourceNormalizedPath) || string.IsNullOrWhiteSpace(publish.SourceFullSha256))
            throw new InvalidOperationException("Recycle lifecycle publish requires a verified immutable source snapshot.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", publish.OperationId.ToString("D"))) ?? throw new InvalidOperationException("Recycle operation plan was not found.");
        if (!FileOperationPolicy.IsProductionRecycle(plan)) throw new InvalidOperationException("Recycle lifecycle publish requires the explicit ProductionRecycleCapability.");
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$media;", ReadMediaFile, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("Recycle lifecycle source MediaFile was not found.");
        if (current.MediaContentId != publish.MediaContentId) throw new InvalidOperationException("Recycle lifecycle source MediaContent changed.");
        var existing = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_recycle_lifecycle WHERE media_file_id=$media;", ReadMediaRecycleLifecycle, cancellationToken, ("$media", publish.MediaFileId.ToString("D")));
        if (existing is not null && existing.RecycleOperationId != publish.OperationId) throw new InvalidOperationException("MediaFile already has a lifecycle record from another Recycle operation.");
        var unavailablePath = $"recycled-unavailable://{publish.MediaFileId:N}";
        var now = publish.RecycledAt;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET path=$path,normalized_path=$normalized,presence_state='Recycled',last_seen_at=$seen WHERE media_file_id=$media AND media_content_id=$content;", cancellationToken,
            ("$path", unavailablePath), ("$normalized", unavailablePath), ("$seen", ToDb(now)), ("$media", publish.MediaFileId.ToString("D")), ("$content", publish.MediaContentId.ToString("D")));
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO media_recycle_lifecycle(media_file_id,media_content_id,recycle_operation_id,lifecycle_state,last_known_source_path,last_known_source_normalized_path,last_known_full_sha256,last_known_size_bytes,last_known_volume_identity,last_known_file_identity,source_available,recycled_at,restored_at,restored_path,restored_normalized_path) VALUES($media,$content,$operation,'Recycled',$path,$normalized,$hash,$size,$volume,$file,0,$recycled,NULL,NULL,NULL) ON CONFLICT(media_file_id) DO UPDATE SET media_content_id=excluded.media_content_id,recycle_operation_id=excluded.recycle_operation_id,lifecycle_state='Recycled',last_known_source_path=excluded.last_known_source_path,last_known_source_normalized_path=excluded.last_known_source_normalized_path,last_known_full_sha256=excluded.last_known_full_sha256,last_known_size_bytes=excluded.last_known_size_bytes,last_known_volume_identity=excluded.last_known_volume_identity,last_known_file_identity=excluded.last_known_file_identity,source_available=0,recycled_at=excluded.recycled_at,restored_at=NULL,restored_path=NULL,restored_normalized_path=NULL;", cancellationToken,
            ("$media", publish.MediaFileId.ToString("D")), ("$content", publish.MediaContentId.ToString("D")), ("$operation", publish.OperationId.ToString("D")), ("$path", publish.SourcePath), ("$normalized", publish.SourceNormalizedPath), ("$hash", publish.SourceFullSha256), ("$size", publish.SourceSizeBytes), ("$volume", (object?)publish.SourceVolumeIdentity ?? DBNull.Value), ("$file", (object?)publish.SourceFileIdentity ?? DBNull.Value), ("$recycled", ToDb(now)));
        var lifecycle = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_recycle_lifecycle WHERE media_file_id=$media;", ReadMediaRecycleLifecycle, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("Recycle lifecycle publish update disappeared.");
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), publish.OperationId, now, "RecycleLifecycleDbPublished", JsonSerializer.Serialize(new { publish.MediaFileId, publish.MediaContentId, publish.SourcePath, publish.SourceSizeBytes, publish.SourceVolumeIdentity, LifecycleState = lifecycle.LifecycleState, PermanentDelete = false })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lifecycle;
    }

    public async Task<MediaRecycleLifecycle?> GetMediaRecycleLifecycleAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM media_recycle_lifecycle WHERE media_file_id=$media;", ReadMediaRecycleLifecycle, cancellationToken, ("$media", mediaFileId.ToString("D")));
    }

    public async Task<MediaPermanentDeleteLifecycle> PublishPermanentDeleteLifecycleAsync(PermanentDeleteDatabasePublish publish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (string.IsNullOrWhiteSpace(publish.SourcePath) || string.IsNullOrWhiteSpace(publish.SourceNormalizedPath) || string.IsNullOrWhiteSpace(publish.SourceFullSha256))
            throw new InvalidOperationException("PermanentDelete lifecycle publish requires a verified immutable source snapshot.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", publish.OperationId.ToString("D"))) ?? throw new InvalidOperationException("PermanentDelete operation plan was not found.");
        if (!FileOperationPolicy.IsProductionPermanentDelete(plan) || plan.SelectedMediaCount != 1 || plan.ValidationStrength != SourceValidationStrength.StrongFullHash)
            throw new InvalidOperationException("PermanentDelete lifecycle publish requires the explicit single-selection StrongFullHash capability.");
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$media;", ReadMediaFile, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("PermanentDelete lifecycle source MediaFile was not found.");
        if (current.MediaContentId != publish.MediaContentId) throw new InvalidOperationException("PermanentDelete lifecycle source MediaContent changed.");
        var existing = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_permanent_delete_lifecycle WHERE media_file_id=$media;", ReadMediaPermanentDeleteLifecycle, cancellationToken, ("$media", publish.MediaFileId.ToString("D")));
        if (existing is not null && existing.PermanentDeleteOperationId != publish.OperationId) throw new InvalidOperationException("MediaFile already has a lifecycle record from another PermanentDelete operation.");
        var unavailablePath = $"permanently-deleted-unavailable://{publish.MediaFileId:N}";
        var now = publish.PermanentlyDeletedAt;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET path=$path,normalized_path=$normalized,presence_state='PermanentlyDeleted',last_seen_at=$seen WHERE media_file_id=$media AND media_content_id=$content;", cancellationToken,
            ("$path", unavailablePath), ("$normalized", unavailablePath), ("$seen", ToDb(now)), ("$media", publish.MediaFileId.ToString("D")), ("$content", publish.MediaContentId.ToString("D")));
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO media_permanent_delete_lifecycle(media_file_id,media_content_id,permanent_delete_operation_id,lifecycle_state,last_known_source_path,last_known_source_normalized_path,last_known_full_sha256,last_known_size_bytes,last_known_volume_identity,last_known_file_identity,source_available,permanently_deleted_at) VALUES($media,$content,$operation,'PermanentlyDeleted',$path,$normalized,$hash,$size,$volume,$file,0,$deleted) ON CONFLICT(media_file_id) DO UPDATE SET media_content_id=excluded.media_content_id,permanent_delete_operation_id=excluded.permanent_delete_operation_id,lifecycle_state='PermanentlyDeleted',last_known_source_path=excluded.last_known_source_path,last_known_source_normalized_path=excluded.last_known_source_normalized_path,last_known_full_sha256=excluded.last_known_full_sha256,last_known_size_bytes=excluded.last_known_size_bytes,last_known_volume_identity=excluded.last_known_volume_identity,last_known_file_identity=excluded.last_known_file_identity,source_available=0,permanently_deleted_at=excluded.permanently_deleted_at;", cancellationToken,
            ("$media", publish.MediaFileId.ToString("D")), ("$content", publish.MediaContentId.ToString("D")), ("$operation", publish.OperationId.ToString("D")), ("$path", publish.SourcePath), ("$normalized", publish.SourceNormalizedPath), ("$hash", publish.SourceFullSha256), ("$size", publish.SourceSizeBytes), ("$volume", (object?)publish.SourceVolumeIdentity ?? DBNull.Value), ("$file", (object?)publish.SourceFileIdentity ?? DBNull.Value), ("$deleted", ToDb(now)));
        var lifecycle = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_permanent_delete_lifecycle WHERE media_file_id=$media;", ReadMediaPermanentDeleteLifecycle, cancellationToken, ("$media", publish.MediaFileId.ToString("D"))) ?? throw new InvalidOperationException("PermanentDelete lifecycle publish update disappeared.");
        await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), publish.OperationId, now, "PermanentDeleteLifecycleDbPublished", JsonSerializer.Serialize(new { publish.MediaFileId, publish.MediaContentId, publish.SourcePath, publish.SourceSizeBytes, publish.SourceVolumeIdentity, LifecycleState = lifecycle.LifecycleState, WindowsRecycleBinUsed = false, Irreversible = true })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lifecycle;
    }

    public async Task<MediaPermanentDeleteLifecycle?> GetMediaPermanentDeleteLifecycleAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM media_permanent_delete_lifecycle WHERE media_file_id=$media;", ReadMediaPermanentDeleteLifecycle, cancellationToken, ("$media", mediaFileId.ToString("D")));
    }

    public async Task<FileOperationPreflight> RecordFileOperationPreflightAsync(FileOperationPreflight preflight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO file_operation_preflight(file_operation_preflight_id,operation_id,state,code,message,source_exists,stable_identity_matches,size_matches,mtime_matches,quick_fingerprint_matches,full_hash_matches,destination_available,destination_conflict,destination_free_bytes,same_volume_verified,cross_volume_verified,temporary_destination_available,required_destination_free_bytes,validated_at) VALUES($id,$operation,$state,$code,$message,$exists,$identity,$size,$mtime,$quick,$full,$destinationAvailable,$conflict,$free,$sameVolume,$crossVolume,$temporaryAvailable,$requiredFree,$at);", cancellationToken,
            ("$id", Guid.NewGuid().ToString("D")), ("$operation", preflight.OperationId.ToString("D")), ("$state", preflight.State.ToString()), ("$code", preflight.Code), ("$message", preflight.Message), ("$exists", preflight.SourceExists ? 1 : 0), ("$identity", preflight.StableIdentityMatches ? 1 : 0), ("$size", preflight.SizeMatches ? 1 : 0), ("$mtime", preflight.MtimeMatches ? 1 : 0), ("$quick", preflight.QuickFingerprintMatches ? 1 : 0), ("$full", preflight.FullHashMatches is null ? DBNull.Value : preflight.FullHashMatches.Value ? 1 : 0), ("$destinationAvailable", preflight.DestinationAvailable ? 1 : 0), ("$conflict", preflight.DestinationConflict ? 1 : 0), ("$free", preflight.DestinationFreeBytes is null ? DBNull.Value : preflight.DestinationFreeBytes.Value), ("$sameVolume", preflight.SameVolumeVerified ? 1 : 0), ("$crossVolume", preflight.CrossVolumeVerified ? 1 : 0), ("$temporaryAvailable", preflight.TemporaryDestinationAvailable is null ? DBNull.Value : preflight.TemporaryDestinationAvailable.Value ? 1 : 0), ("$requiredFree", preflight.RequiredDestinationFreeBytes is null ? DBNull.Value : preflight.RequiredDestinationFreeBytes.Value), ("$at", ToDb(preflight.ValidatedAt)));
        if (preflight.CrossVolumeVerified && preflight.State == FileOperationState.Ready && preflight.DestinationFreeBytes is not null && preflight.RequiredDestinationFreeBytes is not null)
        {
            await ExecuteNonQueryAsync(connection, null, "UPDATE file_operation_plan SET destination_free_space_snapshot_bytes=$free,updated_at=$updated WHERE operation_id=$id AND production_cross_volume_move_capability=1;", cancellationToken,
                ("$free", preflight.DestinationFreeBytes.Value), ("$updated", ToDb(preflight.ValidatedAt)), ("$id", preflight.OperationId.ToString("D")));
            await AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), preflight.OperationId, preflight.ValidatedAt, "CrossVolumeFreeSpaceSnapshot", JsonSerializer.Serialize(new { ObservedFreeBytes = preflight.DestinationFreeBytes, RequiredFreeBytes = preflight.RequiredDestinationFreeBytes })), cancellationToken);
        }
        await AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), preflight.OperationId, preflight.ValidatedAt, "Preflight", JsonSerializer.Serialize(new { State = preflight.State.ToString(), preflight.Code, preflight.DestinationConflict, preflight.SameVolumeVerified, preflight.CrossVolumeVerified, preflight.TemporaryDestinationAvailable, preflight.RequiredDestinationFreeBytes, preflight.FullHashMatches })), cancellationToken);
        return preflight;
    }

    public async Task<FileOperationExecution> RecordFileOperationExecutionAsync(FileOperationExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO file_operation_execution(file_operation_execution_id,operation_id,media_file_id,source_path,destination_path,result,started_at,completed_at,error_code,error_message,before_identity_json,after_identity_json,created_at,updated_at) VALUES($id,$operation,$media,$source,$destination,$result,$started,$completed,$code,$message,$before,$after,$created,$updated);", cancellationToken,
            ("$id", execution.ExecutionId.ToString("D")), ("$operation", execution.OperationId.ToString("D")), ("$media", execution.MediaFileId.ToString("D")), ("$source", execution.SourcePath), ("$destination", (object?)execution.DestinationPath ?? DBNull.Value), ("$result", execution.Result.ToString()), ("$started", execution.StartedAt is null ? DBNull.Value : ToDb(execution.StartedAt.Value)), ("$completed", execution.CompletedAt is null ? DBNull.Value : ToDb(execution.CompletedAt.Value)), ("$code", (object?)execution.ErrorCode ?? DBNull.Value), ("$message", (object?)execution.ErrorMessage ?? DBNull.Value), ("$before", (object?)execution.BeforeIdentityJson ?? DBNull.Value), ("$after", (object?)execution.AfterIdentityJson ?? DBNull.Value), ("$created", ToDb(execution.CreatedAt)), ("$updated", ToDb(execution.UpdatedAt)));
        await AddFileOperationAuditAsync(new FileOperationAuditEntry(Guid.NewGuid(), execution.OperationId, execution.UpdatedAt, "ExecutionRecorded", JsonSerializer.Serialize(new { Result = execution.Result.ToString(), execution.ErrorCode })), cancellationToken);
        return execution;
    }

    public async Task AddFileOperationAuditAsync(FileOperationAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken);
        await AddFileOperationAuditInternalAsync(connection, null, entry, cancellationToken);
    }

    public async Task<IReadOnlyList<FileOperationAuditEntry>> GetFileOperationAuditAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM file_operation_audit WHERE operation_id=$operation ORDER BY occurred_at DESC,file_operation_audit_id DESC;", ReadFileOperationAudit, cancellationToken, ("$operation", operationId.ToString("D")));
    }

    public async Task<IReadOnlyList<FileOperationPlan>> GetInterruptedFileOperationPlansAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM file_operation_plan WHERE state IN ('Executing','Copying','CopyVerified','FinalDestinationPublished','SourceRemovalFailed','PartiallyCompleted','RecoveryRequired') ORDER BY updated_at;", ReadFileOperationPlan, cancellationToken);
    }

    private static async Task AddFileOperationAuditInternalAsync(SqliteConnection connection, SqliteTransaction? transaction, FileOperationAuditEntry entry, CancellationToken cancellationToken)
        => await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO file_operation_audit(file_operation_audit_id,operation_id,occurred_at,event_name,detail_json) VALUES($id,$operation,$at,$event,$detail);", cancellationToken, ("$id", entry.OperationAuditId.ToString("D")), ("$operation", entry.OperationId.ToString("D")), ("$at", ToDb(entry.OccurredAt)), ("$event", entry.EventName), ("$detail", (object?)entry.DetailJson ?? DBNull.Value));

    private static FileOperationPlan ReadFileOperationPlan(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("operation_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("file_operation_intent_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("library_root_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))),
        Enum.Parse<FileOperationType>(reader.GetString(reader.GetOrdinal("operation_type"))),
        Enum.Parse<FileOperationMode>(reader.GetString(reader.GetOrdinal("operation_mode"))),
        reader.GetString(reader.GetOrdinal("source_path")),
        reader.GetString(reader.GetOrdinal("source_normalized_path")),
        GetNullableString(reader, "source_volume_identity"),
        GetNullableString(reader, "source_file_identity"),
        reader.GetInt64(reader.GetOrdinal("source_size_bytes")),
        ParseTime(reader.GetString(reader.GetOrdinal("source_last_write_time_utc"))),
        GetNullableString(reader, "source_quick_fingerprint"),
        GetNullableString(reader, "source_full_sha256"),
        Enum.Parse<SourceValidationStrength>(reader.GetString(reader.GetOrdinal("validation_strength"))),
        GetNullableString(reader, "destination_path"),
        GetNullableString(reader, "destination_normalized_path"),
        reader.GetString(reader.GetOrdinal("expected_filesystem_effect")),
        Enum.Parse<OverwritePolicy>(reader.GetString(reader.GetOrdinal("overwrite_policy"))),
        Enum.Parse<DestinationConflictPolicy>(reader.GetString(reader.GetOrdinal("conflict_policy"))),
        Enum.Parse<OperationRecoverability>(reader.GetString(reader.GetOrdinal("recoverability"))),
        reader.GetInt64(reader.GetOrdinal("user_acknowledged")) != 0,
        ParseTime(reader.GetString(reader.GetOrdinal("created_at"))),
        GetNullableTime(reader, "confirmed_at"),
        Enum.Parse<FileOperationState>(reader.GetString(reader.GetOrdinal("state"))),
        GetNullableString(reader, "last_error_code"),
        GetNullableString(reader, "last_error_message"),
        ParseTime(reader.GetString(reader.GetOrdinal("updated_at"))),
        GetNullableTime(reader, "final_execution_confirmed_at"),
        reader.GetInt64(reader.GetOrdinal("production_same_volume_capability")) != 0,
        reader.IsDBNull(reader.GetOrdinal("destination_library_root_id")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("destination_library_root_id"))),
        GetNullableString(reader, "destination_root_path"),
        GetNullableString(reader, "destination_volume_identity"),
        GetNullableString(reader, "temporary_destination_path"),
        GetNullableString(reader, "temporary_destination_normalized_path"),
        GetNullableLong(reader, "destination_free_space_snapshot_bytes"),
        reader.GetInt64(reader.GetOrdinal("production_cross_volume_move_capability")) != 0,
        Enum.Parse<CrossVolumeMoveStage>(reader.GetString(reader.GetOrdinal("cross_volume_move_stage"))),
        reader.GetInt64(reader.GetOrdinal("production_recycle_capability")) != 0,
        reader.GetInt64(reader.GetOrdinal("production_permanent_delete_capability")) != 0,
        reader.GetInt32(reader.GetOrdinal("selected_media_count")));

    private static MediaRecycleLifecycle ReadMediaRecycleLifecycle(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("recycle_operation_id"))),
        reader.GetString(reader.GetOrdinal("lifecycle_state")),
        reader.GetString(reader.GetOrdinal("last_known_source_path")),
        reader.GetString(reader.GetOrdinal("last_known_source_normalized_path")),
        reader.GetString(reader.GetOrdinal("last_known_full_sha256")),
        reader.GetInt64(reader.GetOrdinal("last_known_size_bytes")),
        GetNullableString(reader, "last_known_volume_identity"),
        GetNullableString(reader, "last_known_file_identity"),
        reader.GetInt64(reader.GetOrdinal("source_available")) != 0,
        ParseTime(reader.GetString(reader.GetOrdinal("recycled_at"))),
        GetNullableTime(reader, "restored_at"),
        GetNullableString(reader, "restored_path"),
        GetNullableString(reader, "restored_normalized_path"));

    private static MediaPermanentDeleteLifecycle ReadMediaPermanentDeleteLifecycle(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("permanent_delete_operation_id"))),
        reader.GetString(reader.GetOrdinal("lifecycle_state")),
        reader.GetString(reader.GetOrdinal("last_known_source_path")),
        reader.GetString(reader.GetOrdinal("last_known_source_normalized_path")),
        reader.GetString(reader.GetOrdinal("last_known_full_sha256")),
        reader.GetInt64(reader.GetOrdinal("last_known_size_bytes")),
        GetNullableString(reader, "last_known_volume_identity"),
        GetNullableString(reader, "last_known_file_identity"),
        reader.GetInt64(reader.GetOrdinal("source_available")) != 0,
        ParseTime(reader.GetString(reader.GetOrdinal("permanently_deleted_at"))));

    private static FileOperationAuditEntry ReadFileOperationAudit(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("file_operation_audit_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("operation_id"))),
        ParseTime(reader.GetString(reader.GetOrdinal("occurred_at"))),
        reader.GetString(reader.GetOrdinal("event_name")),
        GetNullableString(reader, "detail_json"));
}
