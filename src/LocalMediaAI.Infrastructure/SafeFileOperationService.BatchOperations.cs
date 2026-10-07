using System.Text.Json;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SafeFileOperationService
{
    public async Task<BatchOperationDryRun> CreateControlledBatchAsync(FileOperationType operationType, IReadOnlyList<ControlledBatchItemRequest> requestedItems, CancellationToken cancellationToken,
        Action<FileOperationPlan>? onPlanCreated = null, Action<BatchOperation>? onBatchCreated = null)
    {
        ArgumentNullException.ThrowIfNull(requestedItems);
        if (operationType is not (FileOperationType.Move or FileOperationType.Rename or FileOperationType.Recycle)) throw new InvalidOperationException("CONTROLLED_BATCH_TYPE_UNSUPPORTED: PermanentDelete batch is deferred; only Move, Rename, and Recycle can be explicitly planned.");
        if (requestedItems.Count is < 1 or > FileOperationPolicy.ControlledBatchMaxItems) throw new ArgumentOutOfRangeException(nameof(requestedItems), $"Controlled batch requires 1 through {FileOperationPolicy.ControlledBatchMaxItems} explicit selections.");
        if (requestedItems.Select(item => item.MediaFileId).Distinct().Count() != requestedItems.Count) throw new InvalidOperationException("CONTROLLED_BATCH_DUPLICATE_SELECTION: Every batch MediaFileId must be unique.");
        if (operationType is FileOperationType.Move or FileOperationType.Rename && requestedItems.Any(item => string.IsNullOrWhiteSpace(item.DestinationPath))) throw new InvalidOperationException("CONTROLLED_BATCH_DESTINATION_REQUIRED: Move/Rename requires a distinct explicit destination for every item.");
        if (operationType == FileOperationType.Recycle && requestedItems.Any(item => !string.IsNullOrWhiteSpace(item.DestinationPath))) throw new InvalidOperationException("CONTROLLED_BATCH_RECYCLE_DESTINATION_FORBIDDEN: Recycle has no destination path.");
        var destinationPaths = requestedItems.Where(item => !string.IsNullOrWhiteSpace(item.DestinationPath)).Select(item => PathPolicy.NormalizeForIdentity(item.DestinationPath!)).ToArray();
        if (destinationPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinationPaths.Length) throw new InvalidOperationException("CONTROLLED_BATCH_DUPLICATE_DESTINATION: Two batch items cannot target the same path.");

        var plans = new List<FileOperationPlan>(requestedItems.Count);
        foreach (var item in requestedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await CreatePlanAsync(item.MediaFileId, operationType, FileOperationMode.ProductionDryRun, item.DestinationPath, item.ReviewItemId, item.ReviewContext, SourceValidationStrength.StrongFullHash, cancellationToken);
            plans.Add(plan);
            onPlanCreated?.Invoke(plan);
        }
        var now = DateTimeOffset.UtcNow;
        var batch = new BatchOperation(Guid.NewGuid(), operationType, plans.Count, plans.Sum(plan => plan.SourceSizeBytes), BatchOperationState.Planned, false, now, null, null, null, null, null, null, null, now);
        var items = plans.Select((plan, index) => new BatchItem(Guid.NewGuid(), batch.BatchOperationId, index, plan.OperationId, plan.MediaFileId, plan.MediaContentId, plan.SourcePath, plan.DestinationPath, plan.State, BatchItemState.Planned, null, null, null, null, null, now, now)).ToArray();
        await _repository.CreateBatchOperationAsync(batch, items, cancellationToken);
        onBatchCreated?.Invoke(batch);
        return await RunControlledBatchDryRunAsync(batch.BatchOperationId, cancellationToken);
    }

    public async Task<BatchOperationDryRun> RunControlledBatchDryRunAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        var batch = await _repository.GetBatchOperationAsync(batchOperationId, cancellationToken) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (batch.State is BatchOperationState.Completed or BatchOperationState.PartiallyCompleted or BatchOperationState.Cancelled) throw new InvalidOperationException("Terminal batch cannot be dry-run again.");
        var items = await _repository.GetBatchItemsAsync(batchOperationId, cancellationToken);
        foreach (var item in items)
        {
            var dryRun = await RunDryRunAsync(item.OperationId, cancellationToken);
            var state = dryRun.Preflight.State == FileOperationState.Ready ? BatchItemState.Ready : BatchItemState.Blocked;
            await _repository.UpdateBatchItemAsync(item.BatchItemId, dryRun.Plan.State, state, dryRun.Preflight.State.ToString(), null, null, dryRun.Plan.LastErrorCode, dryRun.Preflight.State == FileOperationState.Ready ? null : dryRun.Preflight.Message, cancellationToken);
        }
        items = await _repository.GetBatchItemsAsync(batchOperationId, cancellationToken);
        var summary = SummarizeBatchDryRun(batch, items);
        var target = summary.BlockedCount == 0 ? BatchOperationState.DryRunCompleted : BatchOperationState.FailedBeforeExecution;
        batch = await _repository.TransitionBatchOperationAsync(batchOperationId, target, summary.BlockedCount == 0 ? "BATCH_DRY_RUN_READY" : "BATCH_DRY_RUN_BLOCKED", summary.WarningText, cancellationToken);
        await _repository.AddBatchOperationAuditAsync(new BatchOperationAuditEntry(Guid.NewGuid(), batchOperationId, null, DateTimeOffset.UtcNow, "BatchDryRunCompleted", JsonSerializer.Serialize(new { summary.ReadyCount, summary.BlockedCount, summary.ChangedCount, summary.UnavailableCount, summary.ConflictCount, summary.EstimatedBytes, FinalConfirmationEnabled = summary.BlockedCount == 0 })), cancellationToken);
        return summary with { Batch = batch, FinalConfirmationEnabled = false };
    }

    public async Task<BatchOperation> AcknowledgeControlledBatchDryRunAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        var batch = await _repository.GetBatchOperationAsync(batchOperationId, cancellationToken) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (batch.State != BatchOperationState.DryRunCompleted || batch.CancellationRequested) throw new InvalidOperationException("A ready, non-cancelled batch Dry Run is required before acknowledgement.");
        var items = await _repository.GetBatchItemsAsync(batchOperationId, cancellationToken);
        if (items.Any(item => item.State != BatchItemState.Ready)) throw new InvalidOperationException("Blocked batch items cannot be silently skipped for acknowledgement.");
        foreach (var item in items)
        {
            var acknowledged = await AcknowledgeProductionDryRunAsync(item.OperationId, cancellationToken);
            await _repository.UpdateBatchItemAsync(item.BatchItemId, acknowledged.State, BatchItemState.Ready, FileOperationState.Ready.ToString(), null, null, null, null, cancellationToken);
        }
        await _repository.AddBatchOperationAuditAsync(new BatchOperationAuditEntry(Guid.NewGuid(), batchOperationId, null, DateTimeOffset.UtcNow, "BatchDryRunAcknowledged", JsonSerializer.Serialize(new { batch.OperationType, batch.SelectedItemCount, FilesystemMutationAttempted = false })), cancellationToken);
        return batch;
    }

    public async Task<BatchOperation> ConfirmControlledBatchExecutionAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        var batch = await _repository.GetBatchOperationAsync(batchOperationId, cancellationToken) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (batch.State != BatchOperationState.DryRunCompleted || batch.CancellationRequested) throw new InvalidOperationException("A ready, non-cancelled Batch Dry Run is required before Final Confirmation.");
        if (batch.OperationType == FileOperationType.PermanentDelete) throw new InvalidOperationException("PermanentDelete batch is deferred.");
        var items = await _repository.GetBatchItemsAsync(batchOperationId, cancellationToken);
        if (items.Count != batch.SelectedItemCount || items.Any(item => item.State != BatchItemState.Ready)) throw new InvalidOperationException("Every frozen batch item must be Ready. Blocked items are never silently skipped.");
        foreach (var item in items)
        {
            var plan = await _repository.GetFileOperationPlanAsync(item.OperationId, cancellationToken) ?? throw new InvalidOperationException("Batch item plan disappeared before confirmation.");
            if (!plan.UserAcknowledged || plan.ConfirmedAt is null) throw new InvalidOperationException("Batch Dry Run acknowledgement is required before Final Confirmation.");
        }
        foreach (var item in items)
        {
            FileOperationPlan confirmed = batch.OperationType switch
            {
                FileOperationType.Recycle => await ConfirmProductionRecycleExecutionAsync(item.OperationId, cancellationToken),
                FileOperationType.Move when (await _repository.GetFileOperationPlanAsync(item.OperationId, cancellationToken)) is { ProductionCrossVolumeMoveCapability: true } => await ConfirmProductionCrossVolumeMoveExecutionAsync(item.OperationId, cancellationToken),
                FileOperationType.Move or FileOperationType.Rename => await ConfirmProductionSameVolumeExecutionAsync(item.OperationId, cancellationToken),
                _ => throw new InvalidOperationException("Unsupported controlled batch item type.")
            };
            if (confirmed.State != FileOperationState.FinalConfirmationRequired) throw new InvalidOperationException("Batch final confirmation was blocked by a per-item revalidation result.");
            await _repository.UpdateBatchItemAsync(item.BatchItemId, confirmed.State, BatchItemState.FinalConfirmed, FileOperationState.Ready.ToString(), null, null, null, null, cancellationToken);
        }
        batch = await _repository.TransitionBatchOperationAsync(batchOperationId, BatchOperationState.FinalConfirmationRequired, "BATCH_FINAL_CONFIRMATION_RECORDED", "All explicitly selected batch items were revalidated and independently final-confirmed. Execution remains sequential and cancellable between items.", cancellationToken);
        await _repository.AddBatchOperationAuditAsync(new BatchOperationAuditEntry(Guid.NewGuid(), batchOperationId, null, DateTimeOffset.UtcNow, "BatchFinalExecutionConfirmed", JsonSerializer.Serialize(new { batch.OperationType, batch.SelectedItemCount, batch.TotalSourceBytes, SequentialConcurrency = 1, PermanentDeleteBatch = "Deferred" })), cancellationToken);
        return batch;
    }

    public async Task<BatchOperation> ExecuteControlledBatchAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        var batch = await _repository.GetBatchOperationAsync(batchOperationId, cancellationToken) ?? throw new InvalidOperationException("Batch operation was not found.");
        if (batch.State != BatchOperationState.FinalConfirmationRequired || batch.CancellationRequested) throw new InvalidOperationException("Batch must be independently final-confirmed and not cancelled before execution.");
        batch = await _repository.TransitionBatchOperationAsync(batchOperationId, BatchOperationState.Executing, "BATCH_EXECUTION_STARTED", "Sequential controlled batch execution began.", CancellationToken.None);
        var items = await _repository.GetBatchItemsAsync(batchOperationId, CancellationToken.None);
        foreach (var item in items.Where(item => item.State == BatchItemState.FinalConfirmed).OrderBy(item => item.Sequence))
        {
            batch = await _repository.GetBatchOperationAsync(batchOperationId, CancellationToken.None) ?? throw new InvalidOperationException("Batch disappeared during execution.");
            if (batch.CancellationRequested || cancellationToken.IsCancellationRequested)
            {
                await CancelNotStartedBatchItemsAsync(batchOperationId, CancellationToken.None);
                return await _repository.TransitionBatchOperationAsync(batchOperationId, BatchOperationState.Cancelled, "BATCH_CANCELLED_BETWEEN_ITEMS", "Cancellation was observed before another filesystem operation began; remaining items were not started.", CancellationToken.None);
            }
            await _repository.UpdateBatchItemAsync(item.BatchItemId, item.PlanState, BatchItemState.Executing, item.RevalidationState, null, null, null, null, CancellationToken.None);
            try
            {
                var completed = await ExecuteBatchItemAsync(item.OperationId, batch.OperationType, cancellationToken);
                var itemState = IsBatchPlanCompleted(completed, batch.OperationType) ? BatchItemState.Completed : completed.State == FileOperationState.RecoveryRequired ? BatchItemState.RecoveryRequired : BatchItemState.Failed;
                await _repository.UpdateBatchItemAsync(item.BatchItemId, completed.State, itemState, completed.State.ToString(), LifecycleFor(completed), itemState == BatchItemState.RecoveryRequired ? "RecoveryRequired" : null, completed.LastErrorCode, completed.LastErrorMessage, CancellationToken.None);
                if (itemState != BatchItemState.Completed)
                {
                    await CancelNotStartedBatchItemsAsync(batchOperationId, CancellationToken.None);
                    return await _repository.TransitionBatchOperationAsync(batchOperationId, itemState == BatchItemState.RecoveryRequired ? BatchOperationState.RecoveryRequired : BatchOperationState.PartiallyCompleted, "BATCH_STOPPED_AFTER_ITEM_FAILURE", "A single item did not complete. Remaining items were preserved and not silently started.", CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                await _repository.UpdateBatchItemAsync(item.BatchItemId, FileOperationState.RecoveryRequired, BatchItemState.RecoveryRequired, null, null, "CancellationDuringCurrentItem", "BATCH_ITEM_CANCELLED_DURING_EXECUTION", "Cancellation was forwarded to the current single-file operation; recovery inspection is required.", CancellationToken.None);
                await CancelNotStartedBatchItemsAsync(batchOperationId, CancellationToken.None);
                return await _repository.TransitionBatchOperationAsync(batchOperationId, BatchOperationState.RecoveryRequired, "BATCH_CANCELLED_DURING_ITEM", "Current item requires observation-based recovery; no remaining item was started.", CancellationToken.None);
            }
        }
        return await _repository.TransitionBatchOperationAsync(batchOperationId, BatchOperationState.Completed, "BATCH_COMPLETED", "Every frozen item completed through its existing single-file safety gate.", CancellationToken.None);
    }

    public Task<BatchOperation> RequestControlledBatchCancellationAsync(Guid batchOperationId, CancellationToken cancellationToken) => _repository.RequestBatchCancellationAsync(batchOperationId, cancellationToken);

    public async Task<IReadOnlyList<BatchOperation>> RecoverInterruptedControlledBatchesAsync(CancellationToken cancellationToken)
    {
        var batches = await _repository.GetInterruptedBatchOperationsAsync(cancellationToken);
        var same = await RecoverInterruptedProductionSameVolumeOperationsAsync(cancellationToken);
        var cross = await RecoverInterruptedProductionCrossVolumeMoveOperationsAsync(cancellationToken);
        var recycle = await RecoverInterruptedProductionRecycleOperationsAsync(cancellationToken);
        var recovered = same.Concat(cross).Concat(recycle).ToDictionary(item => item.OperationId);
        var results = new List<BatchOperation>();
        foreach (var batch in batches)
        {
            var items = await _repository.GetBatchItemsAsync(batch.BatchOperationId, cancellationToken);
            foreach (var item in items.Where(item => item.State is BatchItemState.Executing or BatchItemState.RecoveryRequired))
            {
                if (recovered.TryGetValue(item.OperationId, out var recovery))
                    await _repository.UpdateBatchItemAsync(item.BatchItemId, recovery.State, recovery.State == FileOperationState.RecoveryRequired ? BatchItemState.RecoveryRequired : IsBatchPlanCompleted(recovery.State, batch.OperationType) ? BatchItemState.Completed : BatchItemState.Failed, null, null, recovery.State.ToString(), recovery.Code, recovery.Message, cancellationToken);
            }
            await CancelNotStartedBatchItemsAsync(batch.BatchOperationId, cancellationToken);
            var finalItems = await _repository.GetBatchItemsAsync(batch.BatchOperationId, cancellationToken);
            var aggregate = finalItems.Any(item => item.State == BatchItemState.RecoveryRequired) ? BatchOperationState.RecoveryRequired : finalItems.Any(item => item.State == BatchItemState.Completed) ? BatchOperationState.PartiallyCompleted : BatchOperationState.Cancelled;
            results.Add(await _repository.TransitionBatchOperationAsync(batch.BatchOperationId, aggregate, "BATCH_RESTART_OBSERVATION_COMPLETE", "Restart recovery observed individual plans only; no whole-batch replay or automatic resume occurred.", cancellationToken));
        }
        return results;
    }

    private async Task<FileOperationPlan> ExecuteBatchItemAsync(Guid operationId, FileOperationType type, CancellationToken cancellationToken)
    {
        var plan = await _repository.GetFileOperationPlanAsync(operationId, cancellationToken) ?? throw new InvalidOperationException("Batch item plan disappeared.");
        return type switch
        {
            FileOperationType.Recycle => await ExecuteProductionRecycleAsync(operationId, cancellationToken),
            FileOperationType.Move when plan.ProductionCrossVolumeMoveCapability => await ExecuteProductionCrossVolumeMoveAsync(operationId, cancellationToken),
            FileOperationType.Move or FileOperationType.Rename => await ExecuteProductionSameVolumeAsync(operationId, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported controlled batch execution type.")
        };
    }

    private async Task CancelNotStartedBatchItemsAsync(Guid batchOperationId, CancellationToken cancellationToken)
    {
        var remaining = await _repository.GetBatchItemsAsync(batchOperationId, cancellationToken);
        foreach (var item in remaining.Where(item => item.State is BatchItemState.Planned or BatchItemState.Ready or BatchItemState.FinalConfirmed or BatchItemState.NotStarted))
            await _repository.UpdateBatchItemAsync(item.BatchItemId, item.PlanState, BatchItemState.Cancelled, item.RevalidationState, item.LifecycleResult, item.RecoveryState, "BATCH_NOT_STARTED", "Batch item was not started after cancellation, interruption, or prior item failure.", cancellationToken);
    }

    private static bool IsBatchPlanCompleted(FileOperationPlan plan, FileOperationType type) => IsBatchPlanCompleted(plan.State, type);
    private static bool IsBatchPlanCompleted(FileOperationState state, FileOperationType type) => type switch { FileOperationType.Recycle => state == FileOperationState.Recycled, FileOperationType.Move or FileOperationType.Rename => state == FileOperationState.Completed, _ => false };
    private static string? LifecycleFor(FileOperationPlan plan) => plan.State == FileOperationState.Recycled ? "Recycled" : plan.State == FileOperationState.Completed ? "DbPublished" : null;

    private static BatchOperationDryRun SummarizeBatchDryRun(BatchOperation batch, IReadOnlyList<BatchItem> items)
    {
        var ready = items.Count(item => item.State == BatchItemState.Ready);
        var blocked = items.Count - ready;
        var changed = items.Count(item => item.RevalidationState is nameof(FileOperationState.SourceChanged) or nameof(FileOperationState.SourceMissing));
        var unavailable = items.Count(item => item.RevalidationState is nameof(FileOperationState.SourceUnavailable) or nameof(FileOperationState.AccessDenied));
        var conflict = items.Count(item => item.RevalidationState is nameof(FileOperationState.DestinationConflict) or nameof(FileOperationState.ValidationFailed));
        var warning = blocked == 0 ? "All explicit items passed Dry Run. Final Confirmation remains separate, and every item will revalidate immediately before its own mutation." : "One or more explicit items failed Dry Run. No item may be silently skipped or batch-executed until the selection is corrected and re-planned.";
        return new BatchOperationDryRun(batch, items, ready, blocked, changed, unavailable, conflict, 0, batch.TotalSourceBytes, false, warning);
    }
}
