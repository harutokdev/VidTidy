using System.Xml.Linq;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase85B2ControlledBatchPresentationTests
{
    [Fact]
    public void AggregateFinalConfirmationRequiresEveryFrozenItemReadyAndRecordedAcknowledgement()
    {
        var readyItems = Enumerable.Range(0, 2).Select(ReadyItem).ToArray();

        var beforeAcknowledgement = ControlledBatchSafetyPresentation.Evaluate(Input(2, BatchOperationState.DryRunCompleted, false, readyItems));
        Assert.False(beforeAcknowledgement.CanOfferFinalConfirmation);
        Assert.StartsWith("3 / 6", beforeAcknowledgement.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("確認が未記録", beforeAcknowledgement.FinalConfirmationDisabledReason, StringComparison.Ordinal);

        var ready = ControlledBatchSafetyPresentation.Evaluate(Input(2, BatchOperationState.DryRunCompleted, true, readyItems));
        Assert.True(ready.CanOfferFinalConfirmation);
        Assert.StartsWith("4 / 6", ready.CurrentStage, StringComparison.Ordinal);
        Assert.Equal(2, ready.ReadyCount);
        Assert.Contains("実行方法: 1件ずつ逐次実行", ready.FinalReviewSummary, StringComparison.Ordinal);
        Assert.Contains("完了済み項目は元に戻りません", ready.FinalReviewSummary, StringComparison.Ordinal);

        var blocked = ControlledBatchSafetyPresentation.Evaluate(Input(2, BatchOperationState.DryRunCompleted, true,
        [readyItems[0], Item(1, "Blocked — Final Confirmation不可", requiresAttention: true)]));
        Assert.False(blocked.CanOfferFinalConfirmation);
        Assert.StartsWith("2 / 6", blocked.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("停止中または要確認", blocked.FinalConfirmationDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxTwentyFiveAndPermanentDeleteBatchBoundariesAreExplicit()
    {
        var twentyFive = Enumerable.Range(0, FileOperationPolicy.ControlledBatchMaxItems).Select(ReadyItem).ToArray();
        var allowed = ControlledBatchSafetyPresentation.Evaluate(Input(25, BatchOperationState.DryRunCompleted, true, twentyFive));
        Assert.True(allowed.CanOfferFinalConfirmation);

        var overLimit = ControlledBatchSafetyPresentation.Evaluate(Input(26, BatchOperationState.DryRunCompleted, true,
            Enumerable.Range(0, 26).Select(ReadyItem).ToArray()));
        Assert.False(overLimit.CanOfferFinalConfirmation);
        Assert.Contains("最大25件", overLimit.FinalConfirmationDisabledReason, StringComparison.Ordinal);

        var permanentDelete = ControlledBatchSafetyPresentation.Evaluate(Input(1, BatchOperationState.DryRunCompleted, true,
            [ReadyItem(0)], FileOperationType.PermanentDelete));
        Assert.Contains("利用不可", permanentDelete.OperationLabel, StringComparison.Ordinal);
        Assert.Contains("完全削除の一括操作: 対象外 / 利用不可", permanentDelete.FinalReviewSummary, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(ViewModelPath());
        Assert.Contains("ControlledBatchTypes { get; } = [FileOperationType.Move, FileOperationType.Rename, FileOperationType.Recycle]", viewModel, StringComparison.Ordinal);
        Assert.Contains("ControlledBatchSelection.Count is > 0 and <= FileOperationPolicy.ControlledBatchMaxItems", viewModel, StringComparison.Ordinal);
        Assert.Contains("ControlledBatchFinalConfirmation && ControlledBatchFinalConfirmationEnabled", viewModel, StringComparison.Ordinal);
        Assert.Contains("AddSelectedToControlledBatchCommand.RaiseCanExecuteChanged();", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationAndPartiallyCompletedRemainItemSpecificWithoutRecoveryActions()
    {
        var items = new[]
        {
            Item(0, "完了済み — rollbackされません", isCompleted: true),
            Item(1, "未開始 — cancellation後に実行されていません", isNotStarted: true, isCancelled: true),
            Item(2, "要確認 — observation / recovery detail", requiresAttention: true)
        };
        var presentation = ControlledBatchSafetyPresentation.Evaluate(Input(3, BatchOperationState.PartiallyCompleted, false, items, cancellationRequested: true));

        Assert.StartsWith("6 / 6", presentation.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("一部完了", presentation.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("完了=1", presentation.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("未開始=1", presentation.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("要確認・未解決=1", presentation.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("完了済み項目は元に戻りません", presentation.CancellationConsequence, StringComparison.Ordinal);
        Assert.Contains("次の項目を開始せず", presentation.CancellationConsequence, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue Remaining", presentation.RequiredNextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void XamlKeepsExplicitTargetReviewAndGroundedBatchCommandsOnly()
    {
        var document = XDocument.Load(MainWindowPath());
        var source = File.ReadAllText(MainWindowPath());
        var finalReview = ByAutomationId(document, "ControlledBatchFinalReviewContext");

        foreach (var id in new[]
        {
            "ControlledBatchOperationPanel", "ControlledBatchStageStatus", "ControlledBatchSafetySummary",
            "ControlledBatchItemStates", "ControlledBatchAggregateReadiness", "ControlledBatchFinalConfirmationDisabledReason",
            "ControlledBatchFinalTargetReview", "ControlledBatchFinalConfirmation", "ControlledBatchRecordFinalConfirmation",
            "ControlledBatchSequentialSemantics", "ControlledBatchCancellationConsequence", "ControlledBatchCancel",
            "ControlledBatchResultSummary", "ControlledBatchSelectedItemDetail", "ControlledBatchSelectedItemAudit",
            "ControlledBatchPermanentDeleteDeferred", "OperationPlanningPanel"
        })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", source, StringComparison.Ordinal);

        Assert.Contains(finalReview.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == "ControlledBatchFinalTargetReview");
        Assert.Contains(finalReview.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == "ControlledBatchFinalConfirmation");
        Assert.Contains("IsEnabled=\"{Binding ControlledBatchFinalConfirmationEnabled}\"", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ControlledBatchReviewItems}\"", source, StringComparison.Ordinal);
        Assert.Contains("中止を要求 — 完了項目は戻りません", source, StringComparison.Ordinal);
        Assert.Contains("完全削除の一括操作: 対象外 / 利用不可", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Retry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Resume", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Rollback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Undo", source, StringComparison.Ordinal);
    }

    private static ControlledBatchSafetyInput Input(
        int count,
        BatchOperationState? state,
        bool acknowledged,
        IReadOnlyList<ControlledBatchItemPresentation> items,
        FileOperationType operationType = FileOperationType.Recycle,
        bool cancellationRequested = false)
        => new(operationType, count, state, acknowledged, cancellationRequested, items, "Blocked items are not skipped.");

    private static ControlledBatchItemPresentation ReadyItem(int index)
        => Item(index, "Ready — Dry Run確認可能");

    private static ControlledBatchItemPresentation Item(
        int index,
        string state,
        bool requiresAttention = false,
        bool isCompleted = false,
        bool isNotStarted = false,
        bool isCancelled = false)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), index, $"target-{index}.mp4", $@"E:\fixture\target-{index}.mp4",
            "このoperationでは使用しません", state, requiresAttention ? "要確認" : "追加actionなし", requiresAttention,
            isCompleted, isNotStarted, isCancelled, "existing durable item detail");

    private static XElement ByAutomationId(XDocument document, string id)
        => document.Descendants().Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);

    private static string MainWindowPath() => Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "MainWindow.xaml");
    private static string ViewModelPath() => Path.Combine(RepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LocalMediaAI.sln from test base directory.");
    }
}
