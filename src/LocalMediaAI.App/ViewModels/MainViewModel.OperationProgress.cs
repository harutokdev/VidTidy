using System.Collections.ObjectModel;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

public sealed record OperationChoice(FileOperationType Type, string Label);
public sealed record OperationResultPresentation(Guid OperationId, Guid MediaFileId, FileOperationType Type, string Source, string? Destination, FileOperationState State)
{
    public string Summary => L.F("{0} — {1}\n{2}\n元の場所: {3}", System.IO.Path.GetFileName(Source), MainViewModel.OperationName(Type), MainViewModel.OperationOutcome(State), Source) + (Destination is null ? "" : L.F("\n変更先: {0}", Destination));
    public bool CanShowLibrary => State == FileOperationState.Completed && Type is FileOperationType.Move or FileOperationType.Rename;
}

public sealed partial class MainViewModel
{
    private bool _operationBatchMode;
    private bool _operationPresentationUnverified;
    private bool _batchPresentationUnverified;
    private string _operationProgressFeedback = string.Empty;
    private OperationResultPresentation? _operationResult;
    public bool OperationBatchMode { get => _operationBatchMode; set { if (Set(ref _operationBatchMode, value)) NotifyOperationProgress(); } }
    public OperationResultPresentation? OperationResult => _operationResult;
    public bool OperationForegroundBusy => Volatile.Read(ref _operationActionBusy) != 0;
    public IReadOnlyList<OperationChoice> SingleOperationChoices { get; } = Enum.GetValues<FileOperationType>().Select(t => new OperationChoice(t, OperationName(t))).ToArray();
    public IReadOnlyList<OperationChoice> BatchOperationChoices => SingleOperationChoices.Where(c => c.Type != FileOperationType.PermanentDelete).ToArray();
    public static string OperationName(FileOperationType? type) => type switch { FileOperationType.Move => L.T("移動"), FileOperationType.Rename => L.T("名前変更"), FileOperationType.Recycle => L.T("ごみ箱へ移動"), FileOperationType.PermanentDelete => L.T("完全に削除"), _ => L.T("未選択") };
    public static string OperationOutcome(FileOperationState state) => state switch { FileOperationState.Completed => L.T("完了しました"), FileOperationState.Recycled => L.T("ごみ箱へ移動しました"), FileOperationState.PermanentlyDeleted => L.T("完全に削除しました。元に戻せません"), _ => L.T("操作は正常完了していません。元ファイル・変更先と技術情報を確認してください。自動再実行はしません。") };
    public static bool HasRequiredPreflightChecks(FileOperationDryRun dry)
    {
        var p = dry.Preflight; var plan = dry.Plan;
        if (p.OperationId != plan.OperationId || p.State != FileOperationState.Ready || !p.SourceExists || !p.StableIdentityMatches || !p.SizeMatches || !p.MtimeMatches || !p.QuickFingerprintMatches || p.FullHashMatches is false) return false;
        if (plan.ValidationStrength == SourceValidationStrength.StrongFullHash && p.FullHashMatches != true) return false;
        if (plan.OperationType is FileOperationType.Move or FileOperationType.Rename && (!p.DestinationAvailable || p.DestinationConflict || p.DestinationFreeBytes is null || p.RequiredDestinationFreeBytes is null || p.DestinationFreeBytes < p.RequiredDestinationFreeBytes)) return false;
        if (plan.ProductionSameVolumeCapability && !p.SameVolumeVerified) return false;
        if (plan.ProductionCrossVolumeMoveCapability && (!p.CrossVolumeVerified || p.TemporaryDestinationAvailable != true || p.FullHashMatches != true)) return false;
        return plan.ProductionSameVolumeCapability || plan.ProductionCrossVolumeMoveCapability || plan.ProductionRecycleCapability || plan.ProductionPermanentDeleteCapability;
    }
    private bool SingleChecksValid => !_operationPresentationUnverified && CurrentOperationDryRun is { } dry && HasRequiredPreflightChecks(dry) && dry.Plan.State is FileOperationState.ConfirmationRequired or FileOperationState.FinalConfirmationRequired;
    public bool OperationProgressAcknowledgement
    {
        get => SingleChecksValid && OperationPlanningAcknowledgement;
        set { if (SingleChecksValid) OperationPlanningAcknowledgement = value; Raise(); }
    }
    public bool BatchProgressAcknowledgement
    {
        get => !_batchPresentationUnverified && CurrentControlledBatchDryRun is { Batch.State: BatchOperationState.DryRunCompleted, BlockedCount: 0 } && ControlledBatchAcknowledgement;
        set { if (!_batchPresentationUnverified && CurrentControlledBatchDryRun is { Batch.State: BatchOperationState.DryRunCompleted, BlockedCount: 0 }) ControlledBatchAcknowledgement = value; Raise(); }
    }
    public int OperationCurrentStep => OperationBatchMode
        ? CurrentControlledBatchDryRun is not { } batch ? 1 : _batchPresentationUnverified ? 2 : batch.Batch.StartedAt is not null && batch.Batch.State != BatchOperationState.Executing ? 4 : batch.Batch.State == BatchOperationState.FinalConfirmationRequired ? 3 : batch.Batch.State == BatchOperationState.DryRunCompleted && batch.BlockedCount == 0 && _controlledBatchAcknowledgementRecorded ? 3 : 2
        : OperationResult is { } result && CurrentOperationDryRun?.Plan.OperationId == result.OperationId && OperationTarget?.MediaFileId == result.MediaFileId ? 4 : CurrentOperationDryRun is null ? 1 : SingleChecksValid && CurrentOperationDryRun.Plan.UserAcknowledged && CurrentOperationDryRun.Plan.ConfirmedAt is not null ? 3 : 2;
    public string OperationCurrentStepLabel => $"{OperationCurrentStep} / 4 — " + (OperationCurrentStep switch { 1 => L.T("操作対象と操作"), 2 => L.T("実行前確認"), 3 => L.T("最終確認"), _ => L.T("結果") });
    public bool OperationShowPreparation => OperationCurrentStep < 4;
    public bool OperationExpandTargetEditor => OperationCurrentStep == 1;
    public bool OperationShowPreflight => OperationCurrentStep == 2;
    public bool OperationNeedsPreflightRetry => OperationShowPreflight && (OperationBatchMode ? CurrentControlledBatchDryRun is not { Batch.State: BatchOperationState.DryRunCompleted, BlockedCount: 0 } : !SingleChecksValid);
    public bool OperationShowConfirmation => OperationCurrentStep == 3;
    public bool OperationShowResult => OperationCurrentStep == 4;
    public string OperationCurrentLabel => OperationName(OperationBatchMode ? CurrentControlledBatchDryRun?.Batch.OperationType ?? ControlledBatchType : CurrentOperationDryRun?.Plan.OperationType ?? OperationPlanningType);
    public string OperationTargetLabel => OperationBatchMode ? L.F("{0:N0} 件の明示的な操作対象", ControlledBatchSelectionCount) : OperationTarget?.Media.FileName ?? L.T("操作対象は未選択です");
    public bool OperationHasTarget => !OperationBatchMode && OperationTarget is not null;
    public bool OperationSingleMode => !OperationBatchMode;
    public bool OperationShowTargetEmpty => !OperationBatchMode && OperationTarget is null;
    public bool OperationHasSafetyPreparation => CurrentOperationDryRun is not null || CurrentControlledBatchDryRun is not null;
    private MediaListItem? _operationTargetVisual;
    public MediaListItem? OperationTargetVisual => _operationTargetVisual;
    public string OperationTargetFacts => OperationTarget is not { } target ? string.Empty :
        L.F("種類: {0}   ·   サイズ: {1:N1} MB", target.Media.Extension.TrimStart('.').ToUpperInvariant(), target.Media.SizeBytes / (1024d * 1024d)) +
        (OperationTargetVisual?.Metadata?.DurationSeconds is { } seconds ? L.F("   ·   再生時間: {0:hh\\\\:mm\\\\:ss}", TimeSpan.FromSeconds(seconds)) : string.Empty);
    public string OperationTargetAvailability => OperationTarget is null ? string.Empty : OperationTarget.Media.PresenceState == PresenceState.Present ? L.T("元ファイル: 利用可能（実行前に再確認します）") : L.T("元ファイルの状態を確認できません。実行前確認が必要です。");
    private async Task LoadOperationTargetVisualAsync(Guid mediaFileId)
    {
        try
        {
            var item = await _library.GetMediaListItemAsync(mediaFileId, _lifetime.Token);
            if (OperationTarget?.MediaFileId != mediaFileId) return;
            _operationTargetVisual = item;
            Raise(nameof(OperationTargetVisual)); Raise(nameof(OperationTargetFacts));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("Warning", "OperationTargetVisualUnavailable", new { mediaFileId }, ex); }
    }
    public bool OperationNeedsDestination => OperationPlanningType is FileOperationType.Move or FileOperationType.Rename;
    public bool BatchNeedsDestination => ControlledBatchType is FileOperationType.Move or FileOperationType.Rename;
    public bool OperationIsPermanentDelete => !OperationBatchMode && OperationPlanningType == FileOperationType.PermanentDelete;
    public bool OperationShowExecute => OperationShowConfirmation && !OperationForegroundBusy;
    private string BatchPreflightMessage => _batchPresentationUnverified ? L.T("一括操作の現在状態を確認できません。以前の確認は有効として扱いません。") : CurrentControlledBatchDryRun is not { } batch ? L.T("実行前確認は未実行です。") :
        batch.Batch.State == BatchOperationState.FinalConfirmationRequired && batch.Batch.FinalExecutionConfirmedAt is not null && batch.Items.Count == batch.Batch.SelectedItemCount && batch.Items.All(i => i.State == BatchItemState.FinalConfirmed) ? L.T("全対象の最終確認を記録しました。実行直前にも再検証します。") :
        batch.Batch.State == BatchOperationState.DryRunCompleted && batch.BlockedCount == 0 && batch.Items.Count == batch.Batch.SelectedItemCount && batch.Items.All(i => i.State == BatchItemState.Ready) ? L.T("全対象の実行前確認が完了しました。まだファイルは変更していません。") :
        L.F("{0:N0} 件中 {1:N0} 件確認済み / {2:N0} 件要確認。現在の全対象の条件を確認してください。停止項目は除外しません。", batch.Batch.SelectedItemCount, batch.Items.Count(i => i.State is BatchItemState.Ready or BatchItemState.FinalConfirmed), batch.Items.Count(i => i.State is BatchItemState.Blocked or BatchItemState.Failed or BatchItemState.RecoveryRequired));
    public string OperationPreflightMessage => OperationBatchMode ? BatchPreflightMessage
        : SingleChecksValid ? L.T("必要な実行前確認が完了しました。まだファイルは変更していません。実行直前にも再検証します。") : CurrentOperationDryRun is not { } dry ? L.T("操作対象と操作を指定して実行前確認を行ってください。") : _operationPresentationUnverified ? L.T("安全確認の現在状態を確認できません。以前の確認は有効として扱いません。新しい実行前確認が必要です。") : OperationBlockingMessage(dry.Plan.State);
    public string OperationCheckFacts => !OperationBatchMode && SingleChecksValid ? L.T("✓ 対象ファイルの同一性・サイズ・更新時刻・内容の識別情報を確認しました。") + (OperationNeedsDestination ? L.T("\n✓ 変更先を使用できます。同名ファイルはなく、必要な空き容量を確認しました。") : "") : string.Empty;
    public static string OperationBlockingMessage(FileOperationState state) => state switch
    {
        FileOperationState.SourceMissing => L.T("元ファイルを確認できません。登録フォルダーとファイルの場所を確認し、新しい実行前確認を行ってください。"),
        FileOperationState.SourceChanged => L.T("元ファイルが計画時から変更されています。対象を確認し、新しい実行前確認を行ってください。"),
        FileOperationState.DestinationConflict => L.T("変更先に同名ファイルまたは一時ファイルがあります。上書きはしません。変更先を確認してください。"),
        FileOperationState.InsufficientSpace => L.T("変更先の空き容量が不足しています。変更先を確認してください。"),
        FileOperationState.AccessDenied => L.T("必要なアクセス権を確認できません。ファイル・フォルダーへのアクセスを確認してください。"),
        FileOperationState.FreeSpaceUnknown or FileOperationState.SourceUnavailable => L.T("安全に状態を確認できません。接続・変更先を確認してください。"),
        FileOperationState.RecycleUnavailable => L.T("ごみ箱へ移動する条件を確認できません。完全削除への自動切替はしません。"),
        _ => L.T("実行に必要な条件が揃っていません。対象・変更先と技術情報を確認し、新しい実行前確認を行ってください。")
    };
    public string OperationConfirmationSummary => OperationBatchMode ? L.F("操作: {0}\n{1:N0} 件を順番に実行します。完了項目を元には戻しません。停止項目を無断で除外しません。", OperationCurrentLabel, ControlledBatchSelectionCount) : L.F("{0}\n操作: {1}\n元の場所: {2}\n変更先: {3}\n上書きは禁止です。", OperationTargetLabel, OperationCurrentLabel, CurrentOperationDryRun?.Plan.SourcePath, CurrentOperationDryRun?.Plan.DestinationPath ?? L.T("この操作では使用しません"));
    public string OperationConsequence => OperationIsPermanentDelete ? L.T("完全に削除 — 元に戻せません。ごみ箱は使用しません。アプリに復元機能はありません。") : OperationPlanningType == FileOperationType.Recycle && !OperationBatchMode ? L.T("Windowsのごみ箱へ移動します。完全削除ではありません。アプリに復元機能はありません。") : L.T("既存ファイルを上書きしません。実行直前の再検証で状態が変わっていれば停止します。");
    public string OperationSafetyWarning => CurrentOperationDryRun is { Plan.ProductionCrossVolumeMoveCapability: true } && !OperationBatchMode
        ? L.T("別ドライブへの移動では、一時ファイルと変更先の内容検証後に元ファイルを再確認して削除します。途中停止時に自動再実行・元に戻す・元ファイル削除は行いません。")
        : OperationBatchMode ? L.T("全対象を順番に実行します。失敗・中止後の残りは自動再開しません。完了項目を元には戻しません。") : OperationConsequence;
    public string OperationFinalConfirmationText => CurrentOperationDryRun is not { } dry ? L.T("新しい実行前確認が必要です。") :
        L.F("表示された元ファイルの完全パス・ファイル名・サイズ（{0:N0} bytes）・対象1件・操作・変更先・警告を最終確認しました。", dry.Plan.SourceSizeBytes) +
        (dry.Plan.ProductionPermanentDeleteCapability ? L.T(" SHA-256による内容再検証、ごみ箱を使用しない完全削除、元に戻せずアプリに通常の復元機能がないことを確認しました。") :
         dry.Plan.ProductionRecycleCapability ? L.T(" SHA-256による内容再検証とWindowsのごみ箱への移動を確認しました。完全削除ではなく、アプリに復元機能はありません。") :
         dry.Plan.ProductionCrossVolumeMoveCapability ? L.T(" 別ドライブへの移動、一時ファイル・変更先のSHA-256検証後の元ファイル削除を確認しました。") : L.T(" 同じドライブ内の移動／名前変更を確認しました。実行はこの1件だけです。"));
    public string OperationLocalMessage => OperationForegroundBusy ? (_operationExecuting ? L.T("ファイル操作を処理しています。対象・入力は変更できません。") : L.T("実行前の安全確認を処理しています。")) : _operationProgressFeedback.Length > 0 ? _operationProgressFeedback : OperationPlanningType is not null && OperationHandoffFeedback == L.T("対象をファイル操作へ渡しました。操作の種類は未選択です。") ? string.Empty : OperationHandoffFeedback;
    public ObservableCollection<MediaListItem> OperationTargetCandidates { get; } = [];
    public string OperationTargetSearch { get; set; } = string.Empty;
    private MediaListItem? _operationTargetCandidate;
    public MediaListItem? OperationTargetCandidate { get => _operationTargetCandidate; set { if (Set(ref _operationTargetCandidate, value)) _applyOperationTargetCommand?.RaiseCanExecuteChanged(); } }
    private AsyncCommand? _findOperationTargetCommand, _applyOperationTargetCommand, _showOperationResultCommand;
    public AsyncCommand FindOperationTargetCommand => _findOperationTargetCommand ??= new(async () =>
    {
        try
        {
            var result = await _library.QueryMediaAsync(null, OperationTargetSearch, 0, 30, _lifetime.Token);
            OperationTargetCandidates.Clear(); foreach (var item in result.Items) OperationTargetCandidates.Add(item);
            OperationTargetCandidate = null;
        }
        catch (Exception ex) { _operationProgressFeedback = L.T("対象を検索できませんでした。同じ検索操作から再試行できます。"); NotifyOperationProgress(); _log.Write("Warning", "OperationTargetSearchFailed", new { }, ex); }
    }, () => OperationEditorsEnabled);
    public AsyncCommand ApplyOperationTargetCommand => _applyOperationTargetCommand ??= new(async () =>
    {
        if (OperationTargetCandidate is { } item) await HandoffMediaToOperationAsync(item.MediaFileId, L.T("ファイル操作の対象選択"));
    }, () => OperationEditorsEnabled && OperationTargetCandidate is not null);
    public AsyncCommand ShowOperationResultInLibraryCommand => _showOperationResultCommand ??= new(async () =>
    {
        var result = OperationResult;
        if (result?.CanShowLibrary != true) return;
        var media = await _library.GetMediaFileAsync(result.MediaFileId, _lifetime.Token);
        if (media?.PresenceState != PresenceState.Present || result != OperationResult) return;
        if (CurrentLibraryScope.LibraryRootId is { } root && root != media.LibraryRootId) { OperationHandoffFeedback = L.T("現在の表示範囲外です。ライブラリで表示範囲を変更して確認してください。"); return; }
        SearchText = media.FileName; CurrentPage = 0; await SearchAsync();
        if (result != OperationResult) return;
        SelectedMedia = MediaItems.FirstOrDefault(m => m.MediaFileId == result.MediaFileId);
        if (SelectedMedia is not null) OperationLibraryRequested?.Invoke(this, EventArgs.Empty);
        else { OperationHandoffFeedback = L.T("現在のライブラリの絞り込みでは対象を表示できません。フィルターを確認してください。"); NotifyOperationProgress(); }
    }, () => OperationResult?.CanShowLibrary == true && OperationEditorsEnabled);
    public event EventHandler? OperationLibraryRequested;
    private void NotifyOperationProgress()
    {
        foreach (var property in new[] { nameof(OperationResult), nameof(OperationCurrentStep), nameof(OperationCurrentStepLabel), nameof(OperationShowPreparation), nameof(OperationExpandTargetEditor), nameof(OperationShowPreflight), nameof(OperationNeedsPreflightRetry), nameof(OperationShowConfirmation), nameof(OperationShowResult), nameof(OperationCurrentLabel), nameof(OperationTargetLabel), nameof(OperationHasTarget), nameof(OperationSingleMode), nameof(OperationShowTargetEmpty), nameof(OperationHasSafetyPreparation), nameof(OperationTargetFacts), nameof(OperationTargetAvailability), nameof(OperationNeedsDestination), nameof(BatchNeedsDestination), nameof(OperationIsPermanentDelete), nameof(OperationShowExecute), nameof(OperationPreflightMessage), nameof(OperationCheckFacts), nameof(OperationProgressAcknowledgement), nameof(BatchProgressAcknowledgement), nameof(OperationConfirmationSummary), nameof(OperationConsequence), nameof(OperationSafetyWarning), nameof(OperationFinalConfirmationText), nameof(OperationLocalMessage), nameof(OperationForegroundBusy) }) Raise(property);
        _findOperationTargetCommand?.RaiseCanExecuteChanged(); _applyOperationTargetCommand?.RaiseCanExecuteChanged(); _showOperationResultCommand?.RaiseCanExecuteChanged();
    }
    private async Task RefreshOperationProgressAfterActionAsync(PreparationContext captured, bool executing, bool batchAction)
    {
        if (batchAction && captured.Batch is { } originalBatch && CurrentControlledBatchDryRun?.Batch.BatchOperationId == originalBatch.Batch.BatchOperationId)
        {
            try
            {
                var batch = await _library.GetBatchOperationAsync(originalBatch.Batch.BatchOperationId, CancellationToken.None);
                if (batch is not null && CurrentControlledBatchDryRun?.Batch.BatchOperationId == batch.BatchOperationId) await RefreshCurrentControlledBatchAsync(batch, CancellationToken.None);
            }
            catch { _batchPresentationUnverified = true; _operationProgressFeedback = L.T("一括操作の現在状態を確認できません。以前の確認を再利用せず、技術情報を確認してください。"); }
        }
        if (batchAction) { NotifyOperationProgress(); return; }
        if (captured.Single is not { } original) return;
        var id = original.Plan.OperationId;
        if (CurrentOperationDryRun?.Plan.OperationId != id || OperationTarget?.MediaFileId != captured.Target?.MediaFileId) return;
        try
        {
            var plan = await _library.GetFileOperationPlanAsync(id, CancellationToken.None);
            if (plan is null || CurrentOperationDryRun?.Plan.OperationId != id || OperationTarget?.MediaFileId != captured.Target?.MediaFileId) return;
            CurrentOperationDryRun = CurrentOperationDryRun with { Plan = plan };
            if (plan == original.Plan && !executing) _operationProgressFeedback = L.T("確認の記録を完了できませんでした。対象・入力と技術情報を確認して、同じ確認操作から再試行してください。");
            else if (plan.State is FileOperationState.ConfirmationRequired or FileOperationState.FinalConfirmationRequired) _operationProgressFeedback = string.Empty;
            if (executing && plan.State is FileOperationState.Completed or FileOperationState.Recycled or FileOperationState.PermanentlyDeleted or FileOperationState.RecycleFailed or FileOperationState.PermanentDeleteFailed or FileOperationState.SourceRemovalFailed or FileOperationState.SourceChangedAfterCopy or FileOperationState.RecoveryRequired or FileOperationState.CannotRollback or FileOperationState.PartiallyCompleted)
                _operationResult = new(id, plan.MediaFileId, original.Plan.OperationType, original.Plan.SourcePath, original.Plan.DestinationPath, plan.State);
        }
        catch { _operationPresentationUnverified = true; }
        NotifyOperationProgress();
    }
}
