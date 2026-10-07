using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    // The generic transition APIs also support execution/recovery cancellation.
    // This narrower integration must never use those transitions after a separate read.
    public async Task<bool> TryCancelFileOperationPreparationAsync(FileOperationPreparation expected, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (expected.Plans.Select(p => p.OperationId).Distinct().Count() != expected.Plans.Count) return false;
        foreach (var plan in expected.Plans)
        {
            var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", plan.OperationId.ToString("D")));
            if (current != plan || !IsDiscardablePreparation(plan)) return false;
            // An item plan cannot be discarded behind its owning batch's lifecycle.
            var unrelatedBatchItems = await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(1) FROM batch_item WHERE operation_id=$id AND ($batch IS NULL OR batch_operation_id<>$batch);", cancellationToken,
                ("$id", plan.OperationId.ToString("D")), ("$batch", (object?)expected.Batch?.BatchOperationId.ToString("D") ?? DBNull.Value));
            if (unrelatedBatchItems != 0) return false;
        }
        if (expected.Batch is { } batch)
        {
            var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM batch_operation WHERE batch_operation_id=$id;", ReadBatchOperation, cancellationToken, ("$id", batch.BatchOperationId.ToString("D")));
            if (current != batch || batch.StartedAt is not null || batch.State is not (BatchOperationState.Planned or BatchOperationState.DryRunCompleted or BatchOperationState.FinalConfirmationRequired or BatchOperationState.FailedBeforeExecution)) return false;
            var items = await QueryListAsync(connection, transaction, "SELECT * FROM batch_item WHERE batch_operation_id=$id ORDER BY sequence;", ReadBatchItem, cancellationToken, ("$id", batch.BatchOperationId.ToString("D")));
            if (!items.SequenceEqual(expected.Items.OrderBy(i => i.Sequence)) || items.Any(i => i.State is not (BatchItemState.Planned or BatchItemState.Ready or BatchItemState.Blocked or BatchItemState.FinalConfirmed or BatchItemState.NotStarted) || !expected.Plans.Any(p => p.OperationId == i.OperationId))) return false;
        }
        else if (expected.Items.Count != 0) return false;

        var now = DateTimeOffset.UtcNow;
        foreach (var plan in expected.Plans)
        {
            FileOperationStateMachine.EnsureTransition(plan.State, FileOperationState.Cancelled);
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE file_operation_plan SET state='Cancelled',last_error_code=$code,last_error_message=$message,updated_at=$at WHERE operation_id=$id;", cancellationToken,
                ("$code", "PREPARATION_DISCARDED"), ("$message", "Unexecuted preparation discarded by explicit user input."), ("$at", ToDb(now)), ("$id", plan.OperationId.ToString("D")));
            await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), plan.OperationId, now, "StateTransition", JsonSerializer.Serialize(new { From = plan.State.ToString(), To = "Cancelled", errorCode = "PREPARATION_DISCARDED" })), cancellationToken);
        }
        if (expected.Batch is { } discardedBatch)
        {
            foreach (var item in expected.Items)
            {
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE batch_item SET plan_state='Cancelled',batch_item_state='Cancelled',updated_at=$at WHERE batch_item_id=$id;", cancellationToken, ("$at", ToDb(now)), ("$id", item.BatchItemId.ToString("D")));
                await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), discardedBatch.BatchOperationId, item.BatchItemId, now, "BatchItemUpdated", JsonSerializer.Serialize(new { planState = "Cancelled", state = "Cancelled" })), cancellationToken);
            }
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE batch_operation SET state='Cancelled',completed_at=$at,summary_code='PREPARATION_DISCARDED',summary_message=$message,updated_at=$at WHERE batch_operation_id=$id;", cancellationToken,
                ("$at", ToDb(now)), ("$message", "Unexecuted preparation discarded by explicit user input."), ("$id", discardedBatch.BatchOperationId.ToString("D")));
            await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), discardedBatch.BatchOperationId, null, now, "BatchStateTransition", JsonSerializer.Serialize(new { From = discardedBatch.State.ToString(), To = "Cancelled", summaryCode = "PREPARATION_DISCARDED" })), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool IsDiscardablePreparation(FileOperationPlan plan) =>
        (plan.CrossVolumeMoveStage is CrossVolumeMoveStage.None or CrossVolumeMoveStage.PreflightReady) &&
        (plan.State == FileOperationState.Validating && plan.FinalExecutionConfirmedAt is null ||
        plan.State is FileOperationState.Proposed or FileOperationState.Planned or FileOperationState.Ready or FileOperationState.ConfirmationRequired or FileOperationState.FinalConfirmationRequired or
            FileOperationState.SourceChanged or FileOperationState.SourceMissing or FileOperationState.SourceUnavailable or FileOperationState.AccessDenied or FileOperationState.DestinationConflict or
            FileOperationState.InsufficientSpace or FileOperationState.FreeSpaceUnknown or FileOperationState.ValidationFailed or FileOperationState.ProductionExecutionBlocked or FileOperationState.Unsupported);
}
