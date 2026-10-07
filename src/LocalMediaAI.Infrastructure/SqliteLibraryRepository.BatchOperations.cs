using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    public async Task<BatchOperation> CreateBatchOperationAsync(BatchOperation batch, IReadOnlyList<BatchItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);
        if (batch.OperationType is not (FileOperationType.Move or FileOperationType.Rename or FileOperationType.Recycle)) throw new InvalidOperationException("Controlled batch supports Same-volume Move/Rename, Cross-volume Move, or Recycle only. PermanentDelete batch is deferred.");
        if (batch.SelectedItemCount != items.Count || items.Count is < 1 or > FileOperationPolicy.ControlledBatchMaxItems) throw new InvalidOperationException("Batch item count is outside the controlled safety limit.");
        if (items.Select(item => item.MediaFileId).Distinct().Count() != items.Count || items.Select(item => item.OperationId).Distinct().Count() != items.Count || items.Select(item => item.Sequence).Distinct().Count() != items.Count) throw new InvalidOperationException("Batch items must be unique explicit selections with stable ordering.");
        if (batch.State != BatchOperationState.Planned || batch.CancellationRequested) throw new InvalidOperationException("A new batch must begin in Planned state without cancellation.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in items)
        {
            var plan = await QuerySingleAsync(connection, transaction, "SELECT * FROM file_operation_plan WHERE operation_id=$id;", ReadFileOperationPlan, cancellationToken, ("$id", item.OperationId.ToString("D"))) ?? throw new InvalidOperationException("Batch item operation plan was not found.");
            if (plan.MediaFileId != item.MediaFileId || plan.MediaContentId != item.MediaContentId || plan.OperationType != batch.OperationType || plan.Mode != FileOperationMode.ProductionDryRun || plan.SelectedMediaCount != 1) throw new InvalidOperationException("Batch item does not match a single-selection production operation plan.");
            if (plan.OperationType == FileOperationType.PermanentDelete) throw new InvalidOperationException("PermanentDelete batch is deferred.");
        }
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO batch_operation(batch_operation_id,operation_type,selected_item_count,total_source_bytes,state,cancellation_requested,created_at,dry_run_completed_at,final_execution_confirmed_at,started_at,cancellation_requested_at,completed_at,summary_code,summary_message,updated_at) VALUES($id,$type,$count,$bytes,$state,0,$created,NULL,NULL,NULL,NULL,NULL,NULL,NULL,$updated);", cancellationToken,
            ("$id", batch.BatchOperationId.ToString("D")), ("$type", batch.OperationType.ToString()), ("$count", batch.SelectedItemCount), ("$bytes", batch.TotalSourceBytes), ("$state", batch.State.ToString()), ("$created", ToDb(batch.CreatedAt)), ("$updated", ToDb(batch.UpdatedAt)));
        foreach (var item in items.OrderBy(item => item.Sequence))
            await InsertBatchItemAsync(connection, transaction, item, cancellationToken);
        await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), batch.BatchOperationId, null, DateTimeOffset.UtcNow, "BatchCreated", JsonSerializer.Serialize(new { batch.OperationType, batch.SelectedItemCount, batch.TotalSourceBytes, MaxItems = FileOperationPolicy.ControlledBatchMaxItems, PermanentDeleteBatch = "Deferred" })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch;
    }

    public async Task<BatchOperation?> GetBatchOperationAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM batch_operation WHERE batch_operation_id=$id;", ReadBatchOperation, cancellationToken, ("$id", batchOperationId.ToString("D")));
    }

    public async Task<IReadOnlyList<BatchItem>> GetBatchItemsAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryListAsync(connection, null, "SELECT * FROM batch_item WHERE batch_operation_id=$id ORDER BY sequence;", ReadBatchItem, cancellationToken, ("$id", batchOperationId.ToString("D")));
    }

    public async Task<BatchOperation> TransitionBatchOperationAsync(Guid batchOperationId, BatchOperationState state, string? summaryCode, string? summaryMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM batch_operation WHERE batch_operation_id=$id;", ReadBatchOperation, cancellationToken, ("$id", batchOperationId.ToString("D"))) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (current.State is BatchOperationState.Completed or BatchOperationState.PartiallyCompleted or BatchOperationState.Cancelled && state != current.State) throw new InvalidOperationException("Terminal batch state cannot be changed.");
        var now = DateTimeOffset.UtcNow;
        var dryRunAt = state == BatchOperationState.DryRunCompleted ? now : current.DryRunCompletedAt;
        var finalAt = state == BatchOperationState.FinalConfirmationRequired ? now : current.FinalExecutionConfirmedAt;
        var startedAt = state == BatchOperationState.Executing ? current.StartedAt ?? now : current.StartedAt;
        var completedAt = state is BatchOperationState.Completed or BatchOperationState.PartiallyCompleted or BatchOperationState.Cancelled or BatchOperationState.FailedBeforeExecution ? now : current.CompletedAt;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE batch_operation SET state=$state,dry_run_completed_at=$dry,final_execution_confirmed_at=$final,started_at=$started,completed_at=$completed,summary_code=$code,summary_message=$message,updated_at=$updated WHERE batch_operation_id=$id;", cancellationToken,
            ("$state", state.ToString()), ("$dry", dryRunAt is null ? DBNull.Value : ToDb(dryRunAt.Value)), ("$final", finalAt is null ? DBNull.Value : ToDb(finalAt.Value)), ("$started", startedAt is null ? DBNull.Value : ToDb(startedAt.Value)), ("$completed", completedAt is null ? DBNull.Value : ToDb(completedAt.Value)), ("$code", (object?)summaryCode ?? DBNull.Value), ("$message", (object?)summaryMessage ?? DBNull.Value), ("$updated", ToDb(now)), ("$id", batchOperationId.ToString("D")));
        await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), batchOperationId, null, now, "BatchStateTransition", JsonSerializer.Serialize(new { From = current.State.ToString(), To = state.ToString(), summaryCode, summaryMessage })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { State = state, DryRunCompletedAt = dryRunAt, FinalExecutionConfirmedAt = finalAt, StartedAt = startedAt, CompletedAt = completedAt, SummaryCode = summaryCode, SummaryMessage = summaryMessage, UpdatedAt = now };
    }

    public async Task<BatchOperation> RequestBatchCancellationAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM batch_operation WHERE batch_operation_id=$id;", ReadBatchOperation, cancellationToken, ("$id", batchOperationId.ToString("D"))) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (current.State is BatchOperationState.Completed or BatchOperationState.PartiallyCompleted or BatchOperationState.Cancelled or BatchOperationState.FailedBeforeExecution) throw new InvalidOperationException("Terminal batch cannot be cancelled.");
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE batch_operation SET cancellation_requested=1,cancellation_requested_at=COALESCE(cancellation_requested_at,$at),updated_at=$at WHERE batch_operation_id=$id;", cancellationToken, ("$at", ToDb(now)), ("$id", batchOperationId.ToString("D")));
        await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), batchOperationId, null, now, "BatchCancellationRequested", null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { CancellationRequested = true, CancellationRequestedAt = current.CancellationRequestedAt ?? now, UpdatedAt = now };
    }

    public async Task<BatchItem> UpdateBatchItemAsync(Guid batchItemId, FileOperationState planState, BatchItemState state, string? revalidationState, string? lifecycleResult, string? recoveryState, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM batch_item WHERE batch_item_id=$id;", ReadBatchItem, cancellationToken, ("$id", batchItemId.ToString("D"))) ?? throw new InvalidOperationException("Batch item was not found.");
        var now = DateTimeOffset.UtcNow;
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE batch_item SET plan_state=$plan,batch_item_state=$state,revalidation_state=$revalidation,lifecycle_result=$lifecycle,recovery_state=$recovery,error_code=$code,error_message=$message,updated_at=$updated WHERE batch_item_id=$id;", cancellationToken,
            ("$plan", planState.ToString()), ("$state", state.ToString()), ("$revalidation", (object?)revalidationState ?? DBNull.Value), ("$lifecycle", (object?)lifecycleResult ?? DBNull.Value), ("$recovery", (object?)recoveryState ?? DBNull.Value), ("$code", (object?)errorCode ?? DBNull.Value), ("$message", (object?)errorMessage ?? DBNull.Value), ("$updated", ToDb(now)), ("$id", batchItemId.ToString("D")));
        await AddBatchAuditInternalAsync(connection, transaction, new BatchOperationAuditEntry(Guid.NewGuid(), current.BatchOperationId, current.BatchItemId, now, "BatchItemUpdated", JsonSerializer.Serialize(new { planState, state, revalidationState, lifecycleResult, recoveryState, errorCode })), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current with { PlanState = planState, State = state, RevalidationState = revalidationState, LifecycleResult = lifecycleResult, RecoveryState = recoveryState, ErrorCode = errorCode, ErrorMessage = errorMessage, UpdatedAt = now };
    }

    public async Task AddBatchOperationAuditAsync(BatchOperationAuditEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await AddBatchAuditInternalAsync(connection, null, entry, cancellationToken);
    }

    public async Task<IReadOnlyList<BatchOperationAuditEntry>> GetBatchOperationAuditAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryListAsync(connection, null, "SELECT * FROM batch_operation_audit WHERE batch_operation_id=$id ORDER BY occurred_at;", ReadBatchAudit, cancellationToken, ("$id", batchOperationId.ToString("D")));
    }

    public async Task<IReadOnlyList<BatchOperation>> GetInterruptedBatchOperationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryListAsync(connection, null, "SELECT * FROM batch_operation WHERE state IN ('Executing','RecoveryRequired') OR cancellation_requested=1 ORDER BY updated_at;", ReadBatchOperation, cancellationToken);
    }

    private static async Task InsertBatchItemAsync(SqliteConnection connection, SqliteTransaction transaction, BatchItem item, CancellationToken cancellationToken) =>
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO batch_item(batch_item_id,batch_operation_id,sequence,operation_id,media_file_id,media_content_id,source_path,destination_path,plan_state,batch_item_state,revalidation_state,lifecycle_result,recovery_state,error_code,error_message,created_at,updated_at) VALUES($id,$batch,$sequence,$operation,$media,$content,$source,$destination,$plan,$state,$revalidation,$lifecycle,$recovery,$code,$message,$created,$updated);", cancellationToken,
            ("$id", item.BatchItemId.ToString("D")), ("$batch", item.BatchOperationId.ToString("D")), ("$sequence", item.Sequence), ("$operation", item.OperationId.ToString("D")), ("$media", item.MediaFileId.ToString("D")), ("$content", item.MediaContentId.ToString("D")), ("$source", item.SourcePath), ("$destination", (object?)item.DestinationPath ?? DBNull.Value), ("$plan", item.PlanState.ToString()), ("$state", item.State.ToString()), ("$revalidation", (object?)item.RevalidationState ?? DBNull.Value), ("$lifecycle", (object?)item.LifecycleResult ?? DBNull.Value), ("$recovery", (object?)item.RecoveryState ?? DBNull.Value), ("$code", (object?)item.ErrorCode ?? DBNull.Value), ("$message", (object?)item.ErrorMessage ?? DBNull.Value), ("$created", ToDb(item.CreatedAt)), ("$updated", ToDb(item.UpdatedAt)));

    private static async Task AddBatchAuditInternalAsync(SqliteConnection connection, SqliteTransaction? transaction, BatchOperationAuditEntry entry, CancellationToken cancellationToken) =>
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO batch_operation_audit(batch_operation_audit_id,batch_operation_id,batch_item_id,occurred_at,event_name,detail_json) VALUES($id,$batch,$item,$at,$event,$detail);", cancellationToken, ("$id", entry.BatchOperationAuditId.ToString("D")), ("$batch", entry.BatchOperationId.ToString("D")), ("$item", entry.BatchItemId is null ? DBNull.Value : entry.BatchItemId.Value.ToString("D")), ("$at", ToDb(entry.OccurredAt)), ("$event", entry.EventName), ("$detail", (object?)entry.DetailJson ?? DBNull.Value));

    private static async Task<IReadOnlyList<T>> QueryListAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> read, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(read(reader));
        return results;
    }

    private static DateTimeOffset ParseBatchDate(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? GetNullableBatchDate(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : ParseBatchDate(reader.GetString(reader.GetOrdinal(name)));

    private static BatchOperation ReadBatchOperation(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("batch_operation_id"))), Enum.Parse<FileOperationType>(reader.GetString(reader.GetOrdinal("operation_type"))), reader.GetInt32(reader.GetOrdinal("selected_item_count")), reader.GetInt64(reader.GetOrdinal("total_source_bytes")), Enum.Parse<BatchOperationState>(reader.GetString(reader.GetOrdinal("state"))), reader.GetInt32(reader.GetOrdinal("cancellation_requested")) != 0, ParseBatchDate(reader.GetString(reader.GetOrdinal("created_at"))), GetNullableBatchDate(reader, "dry_run_completed_at"), GetNullableBatchDate(reader, "final_execution_confirmed_at"), GetNullableBatchDate(reader, "started_at"), GetNullableBatchDate(reader, "cancellation_requested_at"), GetNullableBatchDate(reader, "completed_at"), GetNullableString(reader, "summary_code"), GetNullableString(reader, "summary_message"), ParseBatchDate(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static BatchItem ReadBatchItem(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("batch_item_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("batch_operation_id"))), reader.GetInt32(reader.GetOrdinal("sequence")), Guid.Parse(reader.GetString(reader.GetOrdinal("operation_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("media_content_id"))), reader.GetString(reader.GetOrdinal("source_path")), GetNullableString(reader, "destination_path"), Enum.Parse<FileOperationState>(reader.GetString(reader.GetOrdinal("plan_state"))), Enum.Parse<BatchItemState>(reader.GetString(reader.GetOrdinal("batch_item_state"))), GetNullableString(reader, "revalidation_state"), GetNullableString(reader, "lifecycle_result"), GetNullableString(reader, "recovery_state"), GetNullableString(reader, "error_code"), GetNullableString(reader, "error_message"), ParseBatchDate(reader.GetString(reader.GetOrdinal("created_at"))), ParseBatchDate(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static BatchOperationAuditEntry ReadBatchAudit(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(reader.GetOrdinal("batch_operation_audit_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("batch_operation_id"))), reader.IsDBNull(reader.GetOrdinal("batch_item_id")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("batch_item_id"))), ParseBatchDate(reader.GetString(reader.GetOrdinal("occurred_at"))), reader.GetString(reader.GetOrdinal("event_name")), GetNullableString(reader, "detail_json"));
}
