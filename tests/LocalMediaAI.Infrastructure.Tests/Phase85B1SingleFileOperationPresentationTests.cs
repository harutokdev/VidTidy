using System.Xml.Linq;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase85B1SingleFileOperationPresentationTests
{
    [Fact]
    public void ExistingDurableStateProjectsToFiveDistinctSafetyStages()
    {
        var initial = OperationSafetyPresentation.Evaluate(Input());
        Assert.StartsWith("1 / 5", initial.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("ドライラン", initial.RequiredNextAction, StringComparison.Ordinal);

        var dryRun = OperationSafetyPresentation.Evaluate(Input(hasPlan: true, planState: FileOperationState.ConfirmationRequired));
        Assert.StartsWith("3 / 5", dryRun.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("確認済みとして記録", dryRun.RequiredNextAction, StringComparison.Ordinal);

        var acknowledged = OperationSafetyPresentation.Evaluate(Input(
            hasPlan: true,
            planState: FileOperationState.ConfirmationRequired,
            acknowledged: true,
            acknowledgedAt: new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)));
        Assert.StartsWith("4 / 5", acknowledged.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("最終確認", acknowledged.RequiredNextAction, StringComparison.Ordinal);

        var confirmed = OperationSafetyPresentation.Evaluate(Input(
            hasPlan: true,
            planState: FileOperationState.FinalConfirmationRequired,
            acknowledged: true,
            acknowledgedAt: new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
            finalAt: new DateTimeOffset(2026, 8, 29, 12, 1, 0, TimeSpan.Zero),
            executionEnabled: true));
        Assert.StartsWith("5 / 5", confirmed.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("選択した操作だけ", confirmed.ExecutionStepStatus, StringComparison.Ordinal);

        var result = OperationSafetyPresentation.Evaluate(Input(
            hasPlan: true,
            planState: FileOperationState.Recycled,
            acknowledged: true,
            acknowledgedAt: new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero),
            finalAt: new DateTimeOffset(2026, 8, 29, 12, 1, 0, TimeSpan.Zero)));
        Assert.Contains("結果", result.CurrentStage, StringComparison.Ordinal);
        Assert.Contains("Recycled", result.ExecutionStepStatus, StringComparison.Ordinal);
        Assert.Contains("自動再試行、ロールバック、復元は行いません", result.RequiredNextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleAndPermanentDeleteRemainDistinctWithoutInventedRecovery()
    {
        var recycle = OperationSafetyPresentation.Evaluate(Input(FileOperationType.Recycle));
        Assert.True(recycle.IsRecycle);
        Assert.False(recycle.IsPermanentDelete);
        Assert.Contains("Windowsのごみ箱へ移動", recycle.OperationLabel, StringComparison.Ordinal);
        Assert.Contains("VidTidyに復元・元に戻す機能はなく", recycle.Consequence, StringComparison.Ordinal);
        Assert.Contains("完全削除への切り替えもありません", recycle.Consequence, StringComparison.Ordinal);

        var permanent = OperationSafetyPresentation.Evaluate(Input(FileOperationType.PermanentDelete));
        Assert.True(permanent.IsPermanentDelete);
        Assert.False(permanent.IsRecycle);
        Assert.Contains("完全削除", permanent.OperationLabel, StringComparison.Ordinal);
        Assert.Contains("不可逆", permanent.Consequence, StringComparison.Ordinal);
        Assert.Contains("復元・元に戻す・ごみ箱への切り替えはありません", permanent.Consequence, StringComparison.Ordinal);
        Assert.Contains("対象件数: 1", permanent.FinalReviewSummary, StringComparison.Ordinal);
        Assert.Contains("上書き: 禁止", permanent.FinalReviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void XamlKeepsMandatoryReviewAndExistingAutomationContractsWhileDisclosingDiagnostics()
    {
        var document = XDocument.Load(MainWindowPath());
        var source = File.ReadAllText(MainWindowPath());
        var finalReview = ByAutomationId(document, "OperationPlanningFinalReviewContext");
        var technical = ByAutomationId(document, "OperationPlanningTechnicalDetail");

        Assert.Contains(finalReview.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == "OperationPlanningFinalReviewSummary");
        Assert.Contains(finalReview.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == "OperationPlanningFinalConfirmation");
        Assert.Contains(finalReview.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == "OperationPlanningRecordFinalConfirmation");
        Assert.Equal("False", (string?)technical.Attribute("IsExpanded"));

        foreach (var id in new[]
        {
            "OperationPlanningPanel", "OperationPlanningType", "OperationPlanningCreateDryRun",
            "OperationPlanningAcknowledgement", "OperationPlanningAcknowledgeDryRun",
            "OperationPlanningFinalConfirmation", "OperationPlanningRecordFinalConfirmation",
            "OperationPlanningProductionExecute", "OperationPlanningCrossVolumeExecute",
            "OperationPlanningRecycleExecute", "OperationPlanningPermanentDeleteExecute",
            "OperationPlanningResultAudit", "ControlledBatchOperationPanel"
        })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", source, StringComparison.Ordinal);

        Assert.Contains("Windows側で回復可能な場合がありますが、VidTidyに復元 / 元に戻す機能はありません", source, StringComparison.Ordinal);
        Assert.Contains("完全削除 — 元に戻せません", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Retry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Restore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding Undo", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingInvalidationAndPermanentDeleteCardinalityRemainGrounded()
    {
        var viewModel = File.ReadAllText(ViewModelPath());

        Assert.Contains("RequestOperationInputChange(() => _operationPlanningType = value", viewModel, StringComparison.Ordinal);
        Assert.Contains("RequestOperationInputChange(() => _operationDestinationPath = value", viewModel, StringComparison.Ordinal);
        Assert.Contains("var target = OperationTarget;", viewModel, StringComparison.Ordinal);
        Assert.Contains("OperationPlanningAcknowledgement = false;", viewModel, StringComparison.Ordinal);
        Assert.Contains("OperationPlanningFinalConfirmation = false;", viewModel, StringComparison.Ordinal);
        Assert.Contains("recreateSingle: true", viewModel, StringComparison.Ordinal);
        Assert.Contains("OperationPlanningType != FileOperationType.PermanentDelete || OperationPlanningSelectedCount == 1", viewModel, StringComparison.Ordinal);
        Assert.Contains("dry.Plan.ProductionPermanentDeleteCapability && dry.Plan.SelectedMediaCount == 1", viewModel, StringComparison.Ordinal);
        Assert.Contains("ControlledBatchTypes { get; } = [FileOperationType.Move, FileOperationType.Rename, FileOperationType.Recycle]", viewModel, StringComparison.Ordinal);
    }

    private static OperationSafetyInput Input(
        FileOperationType type = FileOperationType.Move,
        bool hasPlan = false,
        FileOperationState? planState = null,
        bool acknowledged = false,
        DateTimeOffset? acknowledgedAt = null,
        DateTimeOffset? finalAt = null,
        bool executionEnabled = false)
        => new(
            type,
            1,
            @"E:\fixture\source.mp4",
            type is FileOperationType.Move or FileOperationType.Rename ? @"E:\fixture\destination.mp4" : null,
            hasPlan,
            planState,
            acknowledged,
            acknowledgedAt,
            finalAt,
            executionEnabled,
            planState?.ToString() ?? "Not planned",
            "explicit warning",
            type == FileOperationType.PermanentDelete ? "Irreversible" : "ConditionallyReversible",
            type is FileOperationType.Move or FileOperationType.Rename,
            false,
            type == FileOperationType.Recycle,
            type == FileOperationType.PermanentDelete);

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
