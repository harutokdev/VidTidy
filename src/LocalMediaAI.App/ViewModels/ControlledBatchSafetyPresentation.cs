using System.IO;
using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

public sealed record ControlledBatchItemPresentation(
    Guid MediaFileId,
    Guid? BatchItemId,
    Guid? OperationId,
    int Sequence,
    string Target,
    string Source,
    string Destination,
    string StateLabel,
    string AttentionLabel,
    bool RequiresAttention,
    bool IsCompleted,
    bool IsNotStarted,
    bool IsCancelled,
    string TechnicalDetail)
{
    public string AutomationName => $"{Target}; {StateLabel}; {AttentionLabel}";
    public string BlockingReason { get; init; } = string.Empty;

    public static ControlledBatchItemPresentation FromSelection(MediaListItem item, int sequence, FileOperationType operationType, string destinationDirectory)
    {
        var destination = operationType is FileOperationType.Move or FileOperationType.Rename && !string.IsNullOrWhiteSpace(destinationDirectory)
            ? Path.Combine(destinationDirectory, item.FileName)
            : L.T("この操作では使用しません");
        return new(item.MediaFileId, null, null, sequence, item.FileName, item.Path, destination,
            L.T("確認リスト — ドライラン未実行"), L.T("ドライランが必要"), false, false, true, false,
            L.T("永続的な一括操作項目はまだ作成されていません。現在の明示確認リストだけでは実行を承認しません。"));
    }

    public static ControlledBatchItemPresentation FromBatchItem(BatchItem item)
    {
        var notStarted = item.State == BatchItemState.NotStarted || item.State == BatchItemState.Cancelled && string.Equals(item.ErrorCode, "BATCH_NOT_STARTED", StringComparison.Ordinal);
        var stateLabel = item.State switch
        {
            BatchItemState.Ready => L.T("準備完了 — ドライラン確認可能"),
            BatchItemState.Blocked => L.T("停止中 — 最終確認不可"),
            BatchItemState.FinalConfirmed => L.T("最終確認済み — 実行待ち"),
            BatchItemState.Executing => L.T("実行中"),
            BatchItemState.Completed => L.T("完了済み — 元には戻りません"),
            BatchItemState.Failed => L.T("要確認 — 項目の処理に失敗"),
            BatchItemState.RecoveryRequired => L.T("要確認 — 観察・回復情報を確認"),
            BatchItemState.Cancelled when notStarted => L.T("未開始 — 中止後に実行されていません"),
            BatchItemState.Cancelled => L.T("中止済み — 現在実行されていません"),
            BatchItemState.NotStarted => L.T("未開始"),
            _ => item.State.ToString()
        };
        var attention = item.State switch
        {
            BatchItemState.Blocked => L.T("停止中の項目を解消して新しい一括操作計画が必要"),
            BatchItemState.Failed => L.T("既存の監査記録・詳細を確認。自動再試行なし"),
            BatchItemState.RecoveryRequired => L.T("既存の観察・回復情報を確認。自動再開なし"),
            BatchItemState.Completed => L.T("対応不要。既に反映済みで元には戻りません"),
            BatchItemState.Cancelled when notStarted => L.T("対応不要。実行されていない項目"),
            _ => L.T("追加操作なし")
        };
        var detail = string.Join(Environment.NewLine,
        [
            $"BatchItemId: {item.BatchItemId:D}",
            $"OperationId: {item.OperationId:D}",
            $"Sequence: {item.Sequence + 1}",
            $"Durable item state: {item.State}",
            $"Plan state: {item.PlanState}",
            $"Revalidation: {item.RevalidationState ?? "not recorded"}",
            $"Lifecycle result: {item.LifecycleResult ?? "none"}",
            $"Recovery observation: {item.RecoveryState ?? "none"}",
            $"Error: {item.ErrorCode ?? "none"} / {item.ErrorMessage ?? "none"}",
            $"Updated: {item.UpdatedAt:O}"
        ]);
        return new(item.MediaFileId, item.BatchItemId, item.OperationId, item.Sequence, Path.GetFileName(item.SourcePath), item.SourcePath,
            item.DestinationPath ?? L.T("この操作では使用しません"), stateLabel, attention,
            item.State is BatchItemState.Blocked or BatchItemState.Failed or BatchItemState.RecoveryRequired,
            item.State == BatchItemState.Completed, notStarted, item.State == BatchItemState.Cancelled, detail)
        { BlockingReason = item.State == BatchItemState.Blocked ? MainViewModel.OperationBlockingMessage(item.PlanState) : string.Empty };
    }
}

public sealed record ControlledBatchSafetyInput(
    FileOperationType OperationType,
    int SelectionCount,
    BatchOperationState? BatchState,
    bool AcknowledgementRecorded,
    bool CancellationRequested,
    IReadOnlyList<ControlledBatchItemPresentation> Items,
    string WarningText);

public sealed record ControlledBatchSafetyPresentation(
    string CurrentStage,
    string RequiredNextAction,
    string OperationLabel,
    string Consequence,
    string AggregateReadiness,
    string FinalConfirmationDisabledReason,
    string FinalReviewSummary,
    string ResultSummary,
    string CancellationConsequence,
    int ReadyCount,
    int BlockedCount,
    int CompletedCount,
    int NotStartedCount,
    int AttentionCount,
    bool CanOfferFinalConfirmation)
{
    public static ControlledBatchSafetyPresentation Evaluate(ControlledBatchSafetyInput input)
    {
        var ready = input.Items.Count(item =>
            item.StateLabel.StartsWith(L.T("準備完了"), StringComparison.Ordinal) ||
            item.StateLabel.StartsWith(L.T("最終確認済み"), StringComparison.Ordinal) ||
            item.StateLabel.StartsWith("Ready", StringComparison.Ordinal) ||
            item.StateLabel.StartsWith("Final Confirmed", StringComparison.Ordinal));
        var blocked = input.Items.Count(item =>
            item.StateLabel.StartsWith(L.T("停止中"), StringComparison.Ordinal) ||
            item.StateLabel.StartsWith("Blocked", StringComparison.Ordinal));
        var completed = input.Items.Count(item => item.IsCompleted);
        var notStarted = input.Items.Count(item => item.IsNotStarted);
        var attention = input.Items.Count(item => item.RequiresAttention);
        var allReady = input.SelectionCount is > 0 and <= FileOperationPolicy.ControlledBatchMaxItems && input.Items.Count == input.SelectionCount && blocked == 0 && attention == 0 && ready == input.SelectionCount;
        var canFinalConfirm = input.BatchState == BatchOperationState.DryRunCompleted && allReady && input.AcknowledgementRecorded && !input.CancellationRequested;
        var operationLabel = input.OperationType switch
        {
            FileOperationType.Move => L.T("一括移動"),
            FileOperationType.Rename => L.T("一括名前変更"),
            FileOperationType.Recycle => L.T("一括操作 — Windowsのごみ箱へ移動"),
            FileOperationType.PermanentDelete => L.T("利用不可 — 完全削除の一括操作は対象外"),
            _ => input.OperationType.ToString()
        };
        var consequence = input.OperationType switch
        {
            FileOperationType.Move => L.T("明示確認した各項目を1件ずつ移動します。上書きは禁止です。"),
            FileOperationType.Rename => L.T("明示確認した各項目の名前を1件ずつ変更します。上書きは禁止です。"),
            FileOperationType.Recycle => L.T("明示確認した各項目を1件ずつWindowsのごみ箱へ移動します。完全削除への切り替えはありません。"),
            _ => L.T("この操作種別は一括操作で実行できません。")
        };
        var cancellation = L.T("中止は永続的な要求です。完了済み項目は元に戻りません。要求確認後は次の項目を開始せず、未開始項目は未実行のまま残り、実行中の項目は既存の観察・回復規則に従います。");

        string currentStage;
        string nextAction;
        if (input.BatchState is BatchOperationState.Completed or BatchOperationState.PartiallyCompleted or BatchOperationState.Cancelled or BatchOperationState.RecoveryRequired)
        {
            currentStage = L.T("6 / 6 — 結果 / 監査記録");
            nextAction = L.T("全体結果と項目ごとの状態・監査記録を確認してください。再試行、再開、ロールバックはありません。");
        }
        else if (input.BatchState is BatchOperationState.FinalConfirmationRequired or BatchOperationState.Executing)
        {
            currentStage = L.T("5 / 6 — 逐次実行");
            nextAction = input.BatchState == BatchOperationState.Executing ? L.T("現在の項目の完了または中止状態の確認を待ってください。") : L.T("最終確認済みの対象を再確認して逐次実行を選択できます。");
        }
        else if (canFinalConfirm)
        {
            currentStage = L.T("4 / 6 — 一括最終確認");
            nextAction = L.T("同じ確認欄で全対象と中止した場合の結果を確認し、最終確認を記録してください。");
        }
        else if (input.BatchState == BatchOperationState.DryRunCompleted && allReady)
        {
            currentStage = L.T("3 / 6 — 一括内容確認");
            nextAction = L.T("全項目のドライランを確認し、一括操作の確認記録を別に残してください。");
        }
        else if (input.BatchState is BatchOperationState.DryRunCompleted or BatchOperationState.FailedBeforeExecution or BatchOperationState.Planned)
        {
            currentStage = L.T("2 / 6 — 項目準備 / ドライラン");
            nextAction = blocked > 0 || attention > 0 ? L.T("停止中の項目を確認してください。項目を無断で除外せず、最終確認は利用できません。") : L.T("各項目の準備状態を確認してください。");
        }
        else
        {
            currentStage = L.T("1 / 6 — 一括対象確認");
            nextAction = input.SelectionCount == 0 ? L.T("利用可能なメディアを明示確認リストへ追加してください。") : L.T("対象、単一の操作種別、移動先を確認して一括ドライランを作成してください。");
        }

        var readiness = L.F("準備完了 {0:N0} / 停止中 {1:N0} / 要確認 {2:N0} / 合計 {3:N0}", ready, blocked, attention, input.SelectionCount);
        var disabledReason = canFinalConfirm
            ? L.T("利用可能 — 全固定項目が準備完了、確認記録済み、中止未要求")
            : input.SelectionCount == 0 ? L.T("利用不可 — 明示対象がありません")
            : input.SelectionCount > FileOperationPolicy.ControlledBatchMaxItems ? L.T("利用不可 — 最大25件を超えています")
            : input.BatchState != BatchOperationState.DryRunCompleted ? L.F("利用不可 — 一括操作の状態は{0}です", input.BatchState?.ToString() ?? L.T("未計画"))
            : blocked > 0 || attention > 0 ? L.F("利用不可 — 停止中または要確認の項目が{0:N0}件あります", blocked + attention)
            : !input.AcknowledgementRecorded ? L.T("利用不可 — 一括ドライランの確認が未記録です")
            : input.CancellationRequested ? L.T("利用不可 — 中止要求済みです")
            : L.T("利用不可 — 全固定項目の準備完了を確認できません");
        var finalReview = string.Join(Environment.NewLine,
        [
            L.T("操作範囲: 一括操作"),
            L.F("合計項目: {0:N0} / {1:N0}", input.SelectionCount, FileOperationPolicy.ControlledBatchMaxItems),
            L.F("操作種別: {0}", operationLabel),
            L.F("全体の準備状態: {0}", readiness),
            L.T("実行方法: 1件ずつ逐次実行"),
            L.F("結果: {0}", consequence),
            L.F("中止時: {0}", cancellation),
            L.F("重要な警告: {0}", input.WarningText),
            L.T("完全削除の一括操作: 対象外 / 利用不可")
        ]);
        var resultPrefix = input.BatchState == BatchOperationState.PartiallyCompleted
            ? L.T("一部完了 — 完了済みと未開始・要確認が混在しています。完了・失敗・中止のいずれかへ丸めません。")
            : input.BatchState switch
            {
                BatchOperationState.Completed => L.T("完了 — 全固定項目が完了しました。"),
                BatchOperationState.Cancelled => L.T("中止済み — 中止後の項目状態を確認してください。"),
                BatchOperationState.RecoveryRequired => L.T("回復状態の確認が必要 — 自動再開せず項目詳細を確認してください。"),
                BatchOperationState.FailedBeforeExecution => L.T("実行前に停止 — ファイル操作は開始されていません。"),
                _ => L.T("結果未確定")
            };
        var result = L.F("{0} 完了={1:N0}; 未開始={2:N0}; 要確認・未解決={3:N0}; 合計={4:N0}。完了済み項目は元に戻りません。", resultPrefix, completed, notStarted, attention, input.SelectionCount);

        return new(currentStage, nextAction, operationLabel, consequence, readiness, disabledReason, finalReview, result, cancellation,
            ready, blocked, completed, notStarted, attention, canFinalConfirm);
    }
}
