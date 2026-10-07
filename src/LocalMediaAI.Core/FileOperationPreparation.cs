namespace LocalMediaAI.Core;

/// <summary>Transient optimistic snapshot for discarding only unexecuted preparation.
/// No new lifecycle: successful discard uses the existing Cancelled transitions.</summary>
public sealed record FileOperationPreparation(
    IReadOnlyList<FileOperationPlan> Plans,
    BatchOperation? Batch,
    IReadOnlyList<BatchItem> Items);
