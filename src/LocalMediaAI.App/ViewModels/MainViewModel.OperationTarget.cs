using System.Windows;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

/// <summary>Display snapshot only; planning resolves MediaFileId again from the repository.</summary>
public sealed record OperationTargetPresentation(MediaFile Media, string SourceContext, string? ReviewItemId)
{
    public Guid MediaFileId => Media.MediaFileId;
}

public sealed partial class MainViewModel
{
    private OperationTargetPresentation? _operationTarget;
    private int _operationActionBusy;
    private long _operationDraftVersion;
    private Guid? _unpresentedOperationPlanId;
    private readonly List<Guid> _unpresentedBatchPlanIds = [];
    private Guid? _unpresentedBatchId;
    private bool _operationExecuting;
    private string _operationHandoffFeedback = string.Empty;
    public OperationTargetPresentation? OperationTarget => _operationTarget;
    public string OperationHandoffFeedback
    {
        get => _operationHandoffFeedback;
        private set { if (Set(ref _operationHandoffFeedback, value)) { _operationProgressFeedback = string.Empty; Raise(nameof(OperationLocalMessage)); } }
    }
    public bool OperationEditorsEnabled => Volatile.Read(ref _operationActionBusy) == 0 && !_operationExecuting && !IsBusy &&
        CurrentOperationDryRun?.Plan.State is not (FileOperationState.Executing or FileOperationState.Copying or FileOperationState.CopyVerified or FileOperationState.FinalDestinationPublished) &&
        CurrentControlledBatchDryRun?.Batch.State != BatchOperationState.Executing;
    public Task OperationInputChangeTask { get; private set; } = Task.CompletedTask;
    public Func<Task<bool>> ConfirmOperationPreparationSwitchAsync { get; set; } = () => Task.FromResult(
        MessageBox.Show(L.T("現在の操作準備を破棄して、このファイルへ切り替えますか？\nはい: 切り替える / いいえ: 現在の操作を続ける"), L.T("ファイル操作対象の切替"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    public Func<Task<bool>> ConfirmOperationPreparationExitAsync { get; set; } = () => Task.FromResult(
        MessageBox.Show(L.T("未実行の操作準備を破棄して戻りますか？"), L.T("ファイル操作から戻る"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    public event EventHandler? OperationHandoffApplied;

    private sealed record PreparationContext(long Version, OperationTargetPresentation? Target, FileOperationDryRun? Single, Guid? UnpresentedPlanId,
        BatchOperationDryRun? Batch, bool Acknowledged, bool FinalConfirmed, bool BatchAcknowledged, bool BatchFinalConfirmed, bool BatchRecorded, Guid? UnpresentedBatchId, string UnpresentedBatchPlans);
    private PreparationContext CapturePreparationContext() => new(_operationDraftVersion, OperationTarget, CurrentOperationDryRun, _unpresentedOperationPlanId,
        CurrentControlledBatchDryRun, OperationPlanningAcknowledgement, OperationPlanningFinalConfirmation, ControlledBatchAcknowledgement, ControlledBatchFinalConfirmation, _controlledBatchAcknowledgementRecorded, _unpresentedBatchId, string.Join(',', _unpresentedBatchPlanIds));
    private static bool IsUnexecutedPlan(FileOperationPlan plan) => plan.State is
        FileOperationState.Proposed or FileOperationState.Planned or FileOperationState.Validating or FileOperationState.Ready or FileOperationState.ConfirmationRequired or FileOperationState.FinalConfirmationRequired or
        FileOperationState.SourceChanged or FileOperationState.SourceMissing or FileOperationState.SourceUnavailable or FileOperationState.AccessDenied or FileOperationState.DestinationConflict or FileOperationState.InsufficientSpace or
        FileOperationState.FreeSpaceUnknown or FileOperationState.ValidationFailed or FileOperationState.ProductionExecutionBlocked or FileOperationState.Unsupported;
    private static bool IsUnexecutedBatch(BatchOperation batch) => batch.StartedAt is null && batch.State is
        BatchOperationState.Planned or BatchOperationState.DryRunCompleted or BatchOperationState.FinalConfirmationRequired or BatchOperationState.FailedBeforeExecution;
    private bool HasPreparation => _unpresentedBatchPlanIds.Count != 0 || _unpresentedBatchId is not null || CurrentOperationDryRun is { } single && IsUnexecutedPlan(single.Plan) || _unpresentedOperationPlanId is not null ||
        CurrentControlledBatchDryRun is { } batch && IsUnexecutedBatch(batch.Batch) ||
        CurrentOperationDryRun is null && (OperationPlanningAcknowledgement || OperationPlanningFinalConfirmation) ||
        CurrentControlledBatchDryRun is null && (ControlledBatchAcknowledgement || ControlledBatchFinalConfirmation || _controlledBatchAcknowledgementRecorded);

    public async Task<bool> HandoffMediaToOperationAsync(Guid mediaFileId, string sourceContext, string? reviewItemId = null)
    {
        if (!OperationEditorsEnabled) { OperationHandoffFeedback = OperationPlanningStatus = L.T("現在のファイル操作が完了してから変更してください。"); return false; }
        var context = CapturePreparationContext();
        try
        {
            var media = await _library.GetMediaFileAsync(mediaFileId, _lifetime.Token);
            if (media is null) { OperationHandoffFeedback = OperationPlanningStatus = L.T("対象Mediaを確認できませんでした。"); return false; }
            var expected = await ReadPreparationAsync(context, includeSingle: true, includeBatch: true);
            if (context != CapturePreparationContext() || !OperationEditorsEnabled) return RejectStaleSwitch();
            if (HasPreparation && !await ConfirmOperationPreparationSwitchAsync()) { OperationHandoffFeedback = L.T("現在の操作準備を保持しました。切替は適用していません。"); return false; }
            if (Interlocked.CompareExchange(ref _operationActionBusy, 1, 0) != 0) return RejectStaleSwitch();
            NotifyOperationEditors();
            try
            {
                if (context != CapturePreparationContext() || _operationExecuting) return RejectStaleSwitch();
                if (!await _library.TryCancelFileOperationPreparationAsync(expected, _lifetime.Token)) return RejectStaleSwitch();
                // Commit only after the entire old preparation (including item plans) was cancelled.
                _operationTarget = new(media, sourceContext, reviewItemId);
                _operationTargetVisual = null;
                Raise(nameof(OperationTargetVisual));
                _ = LoadOperationTargetVisualAsync(mediaFileId);
                OperationBatchMode = false;
                _operationPlanningType = null;
                _operationDestinationPath = string.Empty;
                _unpresentedOperationPlanId = null;
                ResetSinglePreparation(L.T("対象を受け取りました。操作の種類を選択してください。"));
                if (context.Batch is not null || context.UnpresentedBatchId is not null || context.UnpresentedBatchPlans.Length != 0 || context.BatchAcknowledged || context.BatchFinalConfirmed || context.BatchRecorded)
                    InvalidateControlledBatch(L.T("未実行Batch準備を破棄しました。明示Batch対象一覧は保持されます。"));
                _operationDraftVersion++;
                OperationHandoffFeedback = L.T("対象をファイル操作へ渡しました。操作の種類は未選択です。");
                Raise(nameof(OperationTarget)); Raise(nameof(OperationPlanningType)); Raise(nameof(OperationDestinationPath));
                Raise(nameof(OperationPlanningSelectedSource)); Raise(nameof(OperationPlanningSelectedCount)); Raise(nameof(OperationPlanningSummary)); Raise(nameof(OperationSafety));
                OperationHandoffApplied?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { Interlocked.Exchange(ref _operationActionBusy, 0); NotifyOperationEditors(); }
        }
        catch (Exception ex)
        {
            OperationHandoffFeedback = OperationPlanningStatus = L.T("操作準備を破棄できないため、対象を切り替えませんでした。現在の操作を確認してください。");
            _log.Write("Warning", "OperationHandoffFailed", new { mediaFileId }, ex);
            return false;
        }
    }

    public async Task<bool> TryLeaveOperationWorkspaceAsync()
    {
        if (!OperationEditorsEnabled) { OperationHandoffFeedback = L.T("現在のファイル操作が完了してから戻ってください。"); return false; }
        var context = CapturePreparationContext();
        try
        {
            if (HasPreparation && !await ConfirmOperationPreparationExitAsync()) return false;
            if (Interlocked.CompareExchange(ref _operationActionBusy, 1, 0) != 0)
            { OperationHandoffFeedback = L.T("操作の状態が変化したため、現在の画面を維持しました。"); return false; }
            NotifyOperationEditors();
            try
            {
                if (context != CapturePreparationContext() || _operationExecuting) return RejectStaleSwitch();
                var expected = await ReadPreparationAsync(context, includeSingle: true, includeBatch: true);
                if (context != CapturePreparationContext() || !await _library.TryCancelFileOperationPreparationAsync(expected, _lifetime.Token)) return RejectStaleSwitch();
                if (HasPreparation)
                {
                    _unpresentedOperationPlanId = null;
                    ResetSinglePreparation(L.T("未実行の操作準備を破棄しました。"));
                    InvalidateControlledBatch(L.T("未実行の一括操作準備を破棄しました。明示した対象一覧は保持されます。"));
                    _operationDraftVersion++;
                    Raise(nameof(OperationSafety));
                }
                return true;
            }
            finally { Interlocked.Exchange(ref _operationActionBusy, 0); NotifyOperationEditors(); }
        }
        catch (Exception ex)
        {
            OperationHandoffFeedback = L.T("操作準備を破棄できないため、この画面を維持しました。");
            _log.Write("Warning", "OperationWorkspaceExitRejected", new { }, ex);
            return false;
        }
    }

    private bool RejectStaleSwitch()
    {
        OperationHandoffFeedback = OperationPlanningStatus = L.T("操作準備または実行stateが変化したため、切替を適用しませんでした。現在の操作を確認してください。");
        return false;
    }

    private async Task<FileOperationPreparation> ReadPreparationAsync(PreparationContext context, bool includeSingle, bool includeBatch)
    {
        var plans = new List<FileOperationPlan>();
        if (includeSingle && (context.Single?.Plan.OperationId ?? context.UnpresentedPlanId) is { } operationId)
        {
            var plan = await _library.GetFileOperationPlanAsync(operationId, _lifetime.Token) ?? throw new InvalidOperationException("Preparation plan missing.");
            if (context.Single is null || IsUnexecutedPlan(context.Single.Plan)) plans.Add(plan);
            else if (plan != context.Single.Plan) throw new InvalidOperationException("Result context changed.");
        }
        BatchOperation? batch = null;
        IReadOnlyList<BatchItem> items = [];
        if (includeBatch && (context.Batch?.Batch.BatchOperationId ?? context.UnpresentedBatchId) is { } batchId)
        {
            batch = await _library.GetBatchOperationAsync(batchId, _lifetime.Token) ?? throw new InvalidOperationException("Preparation batch missing.");
            if (context.Batch is { } batchContext && !IsUnexecutedBatch(batchContext.Batch))
            {
                if (batch != batchContext.Batch) throw new InvalidOperationException("Batch result context changed.");
                return new(plans, null, []);
            }
            items = await _library.GetBatchItemsAsync(batch.BatchOperationId, _lifetime.Token);
            foreach (var item in items)
                plans.Add(await _library.GetFileOperationPlanAsync(item.OperationId, _lifetime.Token) ?? throw new InvalidOperationException("Batch preparation plan missing."));
        }
        if (includeBatch && context.UnpresentedBatchPlans.Length != 0)
            foreach (var id in context.UnpresentedBatchPlans.Split(',').Select(Guid.Parse))
                if (!plans.Any(p => p.OperationId == id)) plans.Add(await _library.GetFileOperationPlanAsync(id, _lifetime.Token) ?? throw new InvalidOperationException("Partial batch plan missing."));
        return new(plans, batch, items);
    }

    private void RequestOperationInputChange(Action commit, string property, bool batch)
    {
        OperationInputChangeTask = ChangeOperationInputAsync(commit, property, batch);
    }
    private async Task ChangeOperationInputAsync(Action commit, string property, bool batch)
    {
        if (!OperationEditorsEnabled || Interlocked.CompareExchange(ref _operationActionBusy, 1, 0) != 0)
        { OperationPlanningStatus = L.T("現在の操作が完了してから入力を変更してください。"); Raise(property); return; }
        NotifyOperationEditors();
        try
        {
            var context = CapturePreparationContext();
            if (batch ? context.Batch is not null || context.UnpresentedBatchId is not null || context.UnpresentedBatchPlans.Length != 0 : context.Single is not null || context.UnpresentedPlanId is not null)
            {
                var expected = await ReadPreparationAsync(context, includeSingle: !batch, includeBatch: batch);
                if (context != CapturePreparationContext() || !await _library.TryCancelFileOperationPreparationAsync(expected, _lifetime.Token)) { RejectStaleSwitch(); return; }
            }
            commit();
            if (batch) { InvalidateControlledBatch(L.T("入力が変更されたため、新しいBatch Dry Runが必要です。")); RefreshControlledBatchReviewItems(); }
            else { _unpresentedOperationPlanId = null; ResetSinglePreparation(L.T("入力が変更されたため、新しいDry Runが必要です。")); }
            _operationDraftVersion++;
            Raise(nameof(OperationPlanningSummary)); Raise(nameof(OperationSafety));
        }
        catch (Exception ex)
        { OperationPlanningStatus = L.T("未実行準備を破棄できないため、入力を変更しませんでした。"); _log.Write("Warning", "OperationInputChangeRejected", new { property }, ex); }
        finally { Raise(property); Interlocked.Exchange(ref _operationActionBusy, 0); NotifyOperationEditors(); }
    }

    private void ResetSinglePreparation(string status)
    {
        _operationResult = null; _operationPresentationUnverified = false; _operationProgressFeedback = string.Empty;
        _operationPlanningAcknowledgement = false; _operationPlanningFinalConfirmation = false;
        Raise(nameof(OperationPlanningAcknowledgement)); Raise(nameof(OperationPlanningFinalConfirmation));
        CurrentOperationDryRun = null; OperationPlanningStatus = status;
    }
    private void NotifyOperationEditors()
    {
        NotifyOperationProgress();
        Raise(nameof(OperationEditorsEnabled)); Raise(nameof(OperationPlanningFinalConfirmationEnabled));
        Raise(nameof(ControlledBatchFinalConfirmationEnabled));
        RaiseOperationPlanningCommands(); RaiseControlledBatchCommands();
        HandoffLibraryMediaCommand?.RaiseCanExecuteChanged(); HandoffReviewPrimaryMediaCommand?.RaiseCanExecuteChanged(); HandoffReviewSecondaryMediaCommand?.RaiseCanExecuteChanged();
        HandoffExactMemberCommand?.RaiseCanExecuteChanged(); HandoffNearMemberCommand?.RaiseCanExecuteChanged(); HandoffContentMemberCommand?.RaiseCanExecuteChanged(); HandoffSuggestedTagMediaCommand?.RaiseCanExecuteChanged();
        foreach (var evidence in ReviewEvidence) evidence.Handoff.RaiseCanExecuteChanged();
    }

    private async Task RunOperationActionAsync(Func<Task> action, bool executing = false, bool recreateSingle = false, bool recreateBatch = false, bool batchAction = false)
    {
        if (!OperationEditorsEnabled || Interlocked.CompareExchange(ref _operationActionBusy, 1, 0) != 0) return;
        _operationExecuting = executing;
        var presentationContext = CapturePreparationContext();
        NotifyOperationEditors();
        try
        {
            if (recreateSingle || recreateBatch)
            {
                var context = CapturePreparationContext();
                var expected = await ReadPreparationAsync(context, recreateSingle, recreateBatch);
                if (context != CapturePreparationContext() || !await _library.TryCancelFileOperationPreparationAsync(expected, _lifetime.Token)) { RejectStaleSwitch(); return; }
                if (recreateSingle) { _unpresentedOperationPlanId = null; ResetSinglePreparation(L.T("新しいDry Runを準備しています。")); }
                if (recreateBatch) InvalidateControlledBatch(L.T("新しいBatch Dry Runを準備しています。"));
            }
            await action();
        }
        catch (Exception ex) { OperationPlanningStatus = L.T("操作準備を変更できませんでした。現在のcontextは保持されます。"); _log.Write("Warning", "OperationPreparationActionFailed", new { executing }, ex); }
        finally { await RefreshOperationProgressAfterActionAsync(presentationContext, executing, batchAction); _operationExecuting = false; Interlocked.Exchange(ref _operationActionBusy, 0); NotifyOperationEditors(); }
    }
}
