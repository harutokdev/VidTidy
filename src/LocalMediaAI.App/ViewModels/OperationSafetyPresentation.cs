using LocalMediaAI.Core;

namespace LocalMediaAI.App.ViewModels;

/// <summary>
/// Presentation-only projection of the existing Phase 6 single-file operation state.
/// It does not authorize, persist, or transition an operation.
/// </summary>
public sealed record OperationSafetyInput(
    FileOperationType? OperationType,
    int SelectedCount,
    string Source,
    string? Destination,
    bool HasPlan,
    FileOperationState? PlanState,
    bool UserAcknowledged,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? FinalExecutionConfirmedAt,
    bool ExecutionEnabled,
    string ValidationState,
    string MajorWarning,
    string Recoverability,
    bool ProductionSameVolumeCapability,
    bool ProductionCrossVolumeMoveCapability,
    bool ProductionRecycleCapability,
    bool ProductionPermanentDeleteCapability);

public sealed record OperationSafetyPresentation(
    string CurrentStage,
    string RequiredNextAction,
    string TargetStepStatus,
    string DryRunStepStatus,
    string AcknowledgementStepStatus,
    string FinalConfirmationStepStatus,
    string ExecutionStepStatus,
    string OperationLabel,
    string Consequence,
    string Source,
    string Destination,
    string Recoverability,
    string MajorWarning,
    string FinalReviewSummary,
    bool IsMoveOrRename,
    bool IsRecycle,
    bool IsPermanentDelete)
{
    public static OperationSafetyPresentation Evaluate(OperationSafetyInput input)
    {
        var acknowledged = input.UserAcknowledged && input.AcknowledgedAt is not null;
        var finalConfirmed = input.FinalExecutionConfirmedAt is not null;
        var terminal = input.PlanState is
            FileOperationState.Completed or
            FileOperationState.Recycled or
            FileOperationState.PermanentlyDeleted or
            FileOperationState.Cancelled or
            FileOperationState.RecycleFailed or
            FileOperationState.PermanentDeleteFailed or
            FileOperationState.SourceRemovalFailed or
            FileOperationState.RecoveryRequired or
            FileOperationState.CannotRollback;

        var operationLabel = input.OperationType switch
        {
            null => L.T("操作の種類: 未選択"),
            FileOperationType.Move => input.ProductionCrossVolumeMoveCapability ? L.T("別ドライブへの移動") : L.T("移動"),
            FileOperationType.Rename => L.T("名前変更"),
            FileOperationType.Recycle => L.T("Windowsのごみ箱へ移動"),
            FileOperationType.PermanentDelete => L.T("完全削除（元に戻せません）"),
            _ => input.OperationType.ToString()!
        };
        var consequence = input.OperationType switch
        {
            FileOperationType.Move => L.T("指定した移動先へ1件を移動します。既存ファイルへの上書きは禁止です。"),
            FileOperationType.Rename => L.T("指定した移動先へ1件の名前を変更します。既存ファイルへの上書きは禁止です。"),
            FileOperationType.Recycle => L.T("Windowsのごみ箱へ1件を移動します。VidTidyに復元・元に戻す機能はなく、完全削除への切り替えもありません。"),
            FileOperationType.PermanentDelete => L.T("Windowsのごみ箱を使用せず1件を不可逆に削除します。復元・元に戻す・ごみ箱への切り替えはありません。"),
            null => L.T("操作の種類を明示的に選択するまで、ドライランや実行には進めません。"),
            _ => L.T("既存の単一ファイル操作規則を使用します。")
        };

        string currentStage;
        string nextAction;
        if (!input.HasPlan)
        {
            currentStage = L.T("1 / 5 — 対象と操作");
            nextAction = input.SelectedCount != 1 ? L.T("ライブラリまたはレビューで「ファイルを整理」を実行して対象を渡してください。") :
                input.OperationType is null ? L.T("操作の種類を選択してください。未選択のままドライランや実行には進めません。") : L.T("対象、操作種別、移動先を確認してドライランを作成してください。");
        }
        else if (terminal)
        {
            currentStage = L.T("5 / 5 — 実行 / 結果");
            nextAction = L.T("結果と既存の監査記録を確認してください。自動再試行、ロールバック、復元は行いません。");
        }
        else if (finalConfirmed)
        {
            currentStage = L.T("5 / 5 — 実行 / 結果");
            nextAction = input.ExecutionEnabled ? L.T("最終確認の内容と一致する操作だけを実行できます。") : L.T("実行条件と停止理由を確認してください。");
        }
        else if (acknowledged)
        {
            currentStage = L.T("4 / 5 — 最終確認");
            nextAction = L.T("同じ確認欄で対象、結果、警告を確認し、最終確認を別に記録してください。");
        }
        else
        {
            currentStage = L.T("3 / 5 — 内容確認");
            nextAction = L.T("ドライランの対象、検証結果、警告、回復可能性を確認し、確認済みとして記録してください。");
        }

        var targetStatus = input.SelectedCount == 1 ? L.T("完了 — 明示選択1件") : L.T("要対応 — Present mediaを1件選択");
        var dryRunStatus = !input.HasPlan
            ? L.T("未実行 — ファイル操作は未承認")
            : terminal
                ? L.F("完了 — 永続状態: {0}", input.PlanState)
                : L.F("完了 — 検証結果: {0}", input.ValidationState);
        var acknowledgementStatus = acknowledged
            ? L.F("記録済み — {0:O}", input.AcknowledgedAt)
            : input.HasPlan ? L.T("未記録 — ドライランの確認が必要") : L.T("利用不可 — ドライランが必要");
        var finalStatus = finalConfirmed
            ? L.F("記録済み — {0:O}", input.FinalExecutionConfirmedAt)
            : acknowledged ? L.T("未記録 — 独立した最終確認が必要") : L.T("利用不可 — ドライランの確認記録が必要");
        var executionStatus = terminal
            ? L.F("結果 — {0}", input.PlanState)
            : input.ExecutionEnabled ? L.T("実行可能 — 選択した操作だけ") : L.T("実行不可 — 安全確認が未完了または停止中");
        var destination = input.OperationType is FileOperationType.Move or FileOperationType.Rename
            ? string.IsNullOrWhiteSpace(input.Destination) ? L.T("未指定（ドライランを作成できません）") : input.Destination
            : L.T("この操作では使用しません");
        var finalReview = string.Join(Environment.NewLine,
        [
            L.F("対象件数: {0:N0}", input.SelectedCount),
            L.F("操作: {0}", operationLabel),
            L.F("元の場所: {0}", input.Source),
            L.F("移動先: {0}", destination),
            L.F("結果: {0}", consequence),
            L.F("回復可能性: {0}", input.Recoverability),
            L.F("重要な警告: {0}", input.MajorWarning),
            L.T("上書き: 禁止")
        ]);

        return new OperationSafetyPresentation(
            currentStage,
            nextAction,
            targetStatus,
            dryRunStatus,
            acknowledgementStatus,
            finalStatus,
            executionStatus,
            operationLabel,
            consequence,
            input.Source,
            destination,
            input.Recoverability,
            input.MajorWarning,
            finalReview,
            input.OperationType is FileOperationType.Move or FileOperationType.Rename,
            input.OperationType == FileOperationType.Recycle,
            input.OperationType == FileOperationType.PermanentDelete);
    }
}
