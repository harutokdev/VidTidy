using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure.Tests;
public sealed partial class OperationTargetFoundationTests
{
    [Theory]
    [InlineData(0, "pilot-no-target-1080x680.png")]
    [InlineData(1, "pilot-target-1080x680.png")]
    [InlineData(2, "pilot-move-precheck-1080x680.png")]
    [InlineData(3, "pilot-blocked-1080x680.png")]
    [InlineData(4, "pilot-permanent-final-1080x680.png")]
    [InlineData(5, "pilot-result-1080x680.png")]
    [InlineData(6, "pilot-developer-on-1080x680.png")]
    [InlineData(7, "pilot-target-wide-1600x900.png")]
    public async Task VisualPilotRendersRequiredSafetyStatesWithoutChangingAuthority(int state, string imageName)
    {
        if (state != 0)
        {
            Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library"));
            await WaitAsync(() => _vm.OperationTargetVisual is not null);
        }
        switch (state)
        {
            case 2:
                _vm.OperationPlanningType = FileOperationType.Move;
                _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "moved.mp4");
                await CommandAsync(_vm.CreateOperationDryRunCommand);
                Assert.Equal(2, _vm.OperationCurrentStep);
                break;
            case 3:
                _vm.OperationPlanningType = FileOperationType.Move;
                _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "moved.mp4");
                await CommandAsync(_vm.CreateOperationDryRunCommand);
                _vm.OperationPlanningAcknowledgement = true;
                await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
                await File.WriteAllBytesAsync(_a.Path, new byte[155]);
                _vm.OperationPlanningFinalConfirmation = true;
                await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
                Assert.Equal(2, _vm.OperationCurrentStep);
                break;
            case 4:
                _vm.OperationPlanningType = FileOperationType.PermanentDelete;
                await _vm.OperationInputChangeTask;
                await CommandAsync(_vm.CreateOperationDryRunCommand);
                _vm.OperationProgressAcknowledgement = true;
                await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
                _vm.OperationPlanningFinalConfirmation = true;
                await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
                Assert.Equal(3, _vm.OperationCurrentStep);
                break;
            case 5:
                _vm.OperationPlanningType = FileOperationType.Rename;
                _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "renamed.mp4");
                await CommandAsync(_vm.CreateOperationDryRunCommand);
                _vm.OperationProgressAcknowledgement = true;
                await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
                _vm.OperationPlanningFinalConfirmation = true;
                await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
                await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
                Assert.Equal(4, _vm.OperationCurrentStep);
                Assert.Equal(_a.MediaFileId, _vm.OperationResult!.MediaFileId);
                break;
        }
        if (state == 6) { _vm.IsDeveloperMode = true; await _vm.DeveloperModeSaveTask; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { DataContext = _vm, Width = state == 7 ? 1600 : 1080, Height = state == 7 ? 900 : 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.ShowOperationWorkspaceForRecovery();
                window.Show(); window.UpdateLayout();
                var operation = (FileOperationWorkspace)window.FindName("ProgressiveOperationWorkspace");
                Assert.True(operation.IsVisible);
                Assert.True(operation.ActualWidth > 950);
                if (state == 0)
                {
                    Assert.Contains(DescendantsProgress(operation).OfType<TextBlock>(), text => text.IsVisible && text.Text == "操作対象を選択してください");
                    Assert.DoesNotContain(DescendantsProgress(operation).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("元ファイルの状態を確認できません", StringComparison.Ordinal));
                    Assert.False(_vm.CreateOperationDryRunCommand.CanExecute(null));
                }
                if (state is 0 or 1 or 7)
                {
                    var action = DescendantsProgress(operation).OfType<Button>().Single(button => button.IsVisible && Equals(button.Content, "実行前確認へ"));
                    var point = action.TransformToAncestor(operation).Transform(new Point(0, 0));
                    Assert.InRange(point.Y + action.ActualHeight, 1, operation.ActualHeight);
                    var selector = (ComboBox)operation.FindName("SingleOperationSelector");
                    var selectorPoint = selector.TransformToAncestor(operation).Transform(new Point(0, 0));
                    Assert.InRange(selectorPoint.Y + selector.ActualHeight, 1, operation.ActualHeight - 80);
                }
                if (state == 3)
                {
                    var retry = DescendantsProgress(operation).OfType<Button>().First(button => button.IsVisible && Equals(button.Content, "実行前確認をやり直す"));
                    var retryPoint = retry.TransformToAncestor(operation).Transform(new Point(0, 0));
                    Assert.InRange(retryPoint.Y + retry.ActualHeight, 1, operation.ActualHeight);
                }
                if (state == 7)
                {
                    var surface = (Border)operation.FindName("CurrentStepSurface");
                    Assert.True(surface.ActualWidth <= 1360);
                    var left = surface.TransformToAncestor(operation).Transform(new Point(0, 0)).X;
                    Assert.InRange(left, 80, 300);
                }
                if (state == 4)
                {
                    var destructive = (Button)operation.FindName("PermanentExecute");
                    Assert.True(destructive.IsVisible); Assert.False(destructive.IsDefault); Assert.False(destructive.IsKeyboardFocused);
                    Assert.Contains(DescendantsProgress(operation).OfType<TextBlock>(), text => text.IsVisible && text.Text.Contains("ごみ箱", StringComparison.Ordinal));
                }
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(ProgressRepositoryRoot(), "artifacts", "ui-file-operation"); Directory.CreateDirectory(directory);
                using var output = File.Create(Path.Combine(directory, imageName)); encoder.Save(output);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
    [Fact]
    public async Task ProgressiveStepsReflectCurrentPreparationNotReachedHistory()
    {
        await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library");
        Assert.Equal(1, _vm.OperationCurrentStep); Assert.Null(_vm.OperationPlanningType);
        Assert.False(_vm.CreateOperationDryRunCommand.CanExecute(null));
        _vm.OperationPlanningType = FileOperationType.Rename;
        _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "renamed.mp4");
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(2, _vm.OperationCurrentStep); Assert.Contains("完了しました", _vm.OperationPreflightMessage);
        Assert.False(_vm.CurrentOperationDryRun!.Plan.UserAcknowledged);
        _vm.OperationPlanningAcknowledgement = true; await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
        Assert.Equal(3, _vm.OperationCurrentStep); Assert.Null(_vm.CurrentOperationDryRun.Plan.FinalExecutionConfirmedAt);
        _vm.OperationPlanningFinalConfirmation = true; await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        Assert.Equal(3, _vm.OperationCurrentStep); Assert.Null(_vm.OperationResult);
        var old = _vm.CurrentOperationDryRun.Plan;
        _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "other.mp4"); await _vm.OperationInputChangeTask;
        Assert.Equal(1, _vm.OperationCurrentStep); Assert.False(_vm.OperationPlanningAcknowledgement); Assert.False(_vm.OperationPlanningFinalConfirmation);
        Assert.DoesNotContain("完了しました", _vm.OperationPreflightMessage);
        Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None))!.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressiveRevalidationBlockIsEarlierStepNotExecutionResult(bool execute)
    {
        await PrepareAsync(final: execute);
        if (!execute) { _vm.OperationPlanningAcknowledgement = true; await CommandAsync(_vm.AcknowledgeOperationDryRunCommand); }
        await File.WriteAllBytesAsync(_a.Path, new byte[155]);
        if (execute) await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
        else { _vm.OperationPlanningFinalConfirmation = true; await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand); }
        Assert.Equal(FileOperationState.SourceChanged, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.Equal(2, _vm.OperationCurrentStep); Assert.False(_vm.OperationShowConfirmation); Assert.Null(_vm.OperationResult);
        Assert.False(_vm.OperationProgressAcknowledgement);
        Assert.DoesNotContain("完了しました", _vm.OperationPreflightMessage);
        Assert.False(_vm.ExecuteProductionSameVolumeOperationCommand.CanExecute(null));
    }
    [Fact]
    public async Task ProgressiveTargetSelectorDelegatesSwitchAndNeverEditsBatchOrScope()
    {
        var old = await PrepareAsync(final: true);
        _vm.SelectedMedia = _a; await CommandAsync(_vm.AddSelectedToControlledBatchCommand);
        var scope = _vm.CurrentLibraryScope;
        _vm.OperationTargetSearch = "b.mp4";
        _vm.FindOperationTargetCommand.Execute(null);
        await WaitAsync(() => _vm.OperationTargetCandidates.Count == 1 && _vm.FindOperationTargetCommand.CanExecute(null));
        _vm.OperationTargetCandidate = _vm.OperationTargetCandidates.Single();
        _vm.ConfirmOperationPreparationSwitchAsync = () => Task.FromResult(false);
        _vm.ApplyOperationTargetCommand.Execute(null); await WaitAsync(() => _vm.ApplyOperationTargetCommand.CanExecute(null));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId); Assert.Equal(old, _vm.CurrentOperationDryRun!.Plan);
        _vm.ConfirmOperationPreparationSwitchAsync = () => Task.FromResult(true);
        _vm.ApplyOperationTargetCommand.Execute(null); await WaitAsync(() => _vm.OperationTarget?.MediaFileId == _b.MediaFileId && _vm.ApplyOperationTargetCommand.CanExecute(null));
        Assert.Null(_vm.OperationPlanningType); Assert.Equal(scope, _vm.CurrentLibraryScope);
        Assert.Equal(_a.MediaFileId, Assert.Single(_vm.ControlledBatchSelection).MediaFileId);
        Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None))!.State);
    }
    [Fact]
    public async Task ProgressiveModeIsPresentationOnlyAndBatchRetainsEveryItem()
    {
        await PrepareBatchAsync(); var batch = _vm.CurrentControlledBatchDryRun;
        _vm.ConfirmOperationPreparationSwitchAsync = () => Task.FromResult(true);
        await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Single");
        _vm.OperationPlanningType = FileOperationType.PermanentDelete; await _vm.OperationInputChangeTask;
        var single = _vm.OperationTarget;
        _vm.OperationBatchMode = true;
        Assert.DoesNotContain(_vm.BatchOperationChoices, c => c.Type == FileOperationType.PermanentDelete);
        Assert.Equal(FileOperationType.Rename, _vm.ControlledBatchType);
        Assert.Equal(2, _vm.ControlledBatchSelectionCount);
        Assert.Same(single, _vm.OperationTarget); Assert.Equal(FileOperationType.PermanentDelete, _vm.OperationPlanningType);
        await CommandAsync(_vm.CreateControlledBatchDryRunCommand);
        Assert.Equal(2, _vm.CurrentControlledBatchDryRun!.Items.Count);
        Assert.Equal(2, _vm.OperationCurrentStep);
        _vm.OperationBatchMode = false; Assert.Equal(1, _vm.OperationCurrentStep);
        Assert.Equal(batch!.Items.Select(i => i.MediaFileId), _vm.CurrentControlledBatchDryRun.Items.Select(i => i.MediaFileId));
    }
    [Fact]
    public async Task ProgressiveIncompleteRequiredChecksNeverBecomeAggregateSuccess()
    {
        await PrepareAsync(); var dry = _vm.CurrentOperationDryRun!;
        Assert.True(MainViewModel.HasRequiredPreflightChecks(dry));
        Assert.False(MainViewModel.HasRequiredPreflightChecks(dry with { Preflight = dry.Preflight with { StableIdentityMatches = false } }));
        Assert.False(MainViewModel.HasRequiredPreflightChecks(dry with { Preflight = dry.Preflight with { RequiredDestinationFreeBytes = null } }));
        Assert.False(MainViewModel.HasRequiredPreflightChecks(dry with { Preflight = dry.Preflight with { DestinationConflict = true } }));
        Assert.False(MainViewModel.HasRequiredPreflightChecks(dry with { Preflight = dry.Preflight with { State = FileOperationState.FreeSpaceUnknown } }));
        Assert.False(MainViewModel.HasRequiredPreflightChecks(dry with { Plan = dry.Plan with { ValidationStrength = SourceValidationStrength.StrongFullHash }, Preflight = dry.Preflight with { FullHashMatches = null } }));
    }
    [Fact]
    public async Task ProgressiveBlockedBatchKeepsWholeTargetSetAndCannotExecute()
    {
        await PrepareBatchAsync();
        var destination = _vm.ControlledBatchDestinationDirectory;
        await File.WriteAllBytesAsync(Path.Combine(destination, _a.FileName), new byte[22]);
        await CommandAsync(_vm.CreateControlledBatchDryRunCommand);
        _vm.OperationBatchMode = true;
        Assert.Equal(2, _vm.OperationCurrentStep); Assert.Equal(2, _vm.CurrentControlledBatchDryRun!.Items.Count);
        Assert.True(_vm.CurrentControlledBatchDryRun.BlockedCount > 0);
        Assert.DoesNotContain("完了しました", _vm.OperationPreflightMessage);
        Assert.False(_vm.ExecuteControlledBatchCommand.CanExecute(null)); Assert.Equal(2, _vm.ControlledBatchSelectionCount);
    }
    [Fact]
    public async Task ProgressiveResultUsesExecutedPlanAndLibraryUsesLogicalIdentity()
    {
        var plan = await PrepareAsync(final: true); _vm.SelectedMedia = _b;
        await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
        var result = _vm.OperationResult;
        Assert.NotNull(result); Assert.Equal(plan.OperationId, result.OperationId); Assert.Equal(_a.MediaFileId, result.MediaFileId);
        Assert.Equal(plan.SourcePath, result.Source); Assert.Equal(plan.DestinationPath, result.Destination);
        Assert.Equal(4, _vm.OperationCurrentStep); Assert.True(result.CanShowLibrary);
        var navigated = false; _vm.OperationLibraryRequested += (_, _) => navigated = true;
        _vm.ShowOperationResultInLibraryCommand.Execute(null); await WaitAsync(() => navigated);
        Assert.Equal(_a.MediaFileId, _vm.SelectedMedia!.MediaFileId); Assert.Equal(plan.DestinationPath, _vm.SelectedMedia.Path);
        await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B");
        Assert.Equal(1, _vm.OperationCurrentStep); Assert.Null(_vm.OperationResult);
        Assert.False(new OperationResultPresentation(plan.OperationId, plan.MediaFileId, FileOperationType.PermanentDelete, plan.SourcePath, null, FileOperationState.PermanentlyDeleted).CanShowLibrary);
    }
    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Rename)]
    public async Task ProgressiveMoveAndRenameUseExistingPreflight(FileOperationType type)
    {
        await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library");
        _vm.OperationPlanningType = type; _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "new.mp4");
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(type, _vm.CurrentOperationDryRun!.Plan.OperationType); Assert.Equal(2, _vm.OperationCurrentStep);
        Assert.True(_vm.OperationNeedsDestination); Assert.False(_vm.OperationIsPermanentDelete);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ProgressiveRealWindow1080By680AndPermanentEnterSafety(int state)
    {
        var finalReview = state is 3 or 4;
        if (state == 4)
        {
            await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library"); _vm.OperationPlanningType = FileOperationType.PermanentDelete; await _vm.OperationInputChangeTask;
            await CommandAsync(_vm.CreateOperationDryRunCommand); _vm.OperationProgressAcknowledgement = true; await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
            _vm.OperationPlanningFinalConfirmation = true; await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        }
        else if (finalReview) await PrepareAsync(final: true);
        else if (state == 2) { await PrepareAsync(final: true); await File.WriteAllBytesAsync(_a.Path, new byte[165]); await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand); }
        else { await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library"); if (state == 1) { _vm.OperationPlanningType = FileOperationType.PermanentDelete; await _vm.OperationInputChangeTask; } }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow { DataContext = _vm, Width = 1080, Height = 680, Left = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.ShowOperationWorkspaceForRecovery(); window.Show(); window.UpdateLayout();
                var control = DescendantsProgress(window).OfType<FileOperationWorkspace>().Single();
                Assert.True(control.IsVisible); Assert.True(control.ActualWidth > 950);
                var texts = DescendantsProgress(control).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains(_a.FileName, texts);
                if (state is 1 or 4) Assert.Contains(texts, t => t.Contains("元に戻せません", StringComparison.Ordinal));
                if (state == 0) { Assert.Null(_vm.OperationPlanningType); Assert.False(_vm.CreateOperationDryRunCommand.CanExecute(null)); }
                Assert.Contains(texts, t => t.Contains(finalReview ? "3 / 4" : state == 2 ? "2 / 4" : "1 / 4", StringComparison.Ordinal));
                var button = (Button)control.FindName("PermanentExecute"); Assert.False(button.IsDefault); Assert.False(button.IsKeyboardFocused);
                if (state == 3)
                {
                    var execute = DescendantsProgress(control).OfType<Button>().Single(b => b.IsVisible && b.Content?.ToString() == "移動／名前変更を実行");
                    var position = execute.TransformToAncestor(control).Transform(new Point(0, 0));
                    Assert.InRange(position.Y + execute.ActualHeight, 1, control.ActualHeight);
                }
                var source = PresentationSource.FromVisual(window)!;
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                button.RaiseEvent(key); Assert.True(key.Handled);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.Combine(ProgressRepositoryRoot(), "artifacts", "ui-file-operation"); Directory.CreateDirectory(directory);
                using var output = File.Create(Path.Combine(directory, state == 4 ? "operation-delete-final-1080x680.png" : finalReview ? "operation-final-1080x680.png" : state == 2 ? "operation-blocked-1080x680.png" : state == 0 ? "operation-handoff-1080x680.png" : "operation-1080x680.png")); encoder.Save(output); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (window is not null) { window.DataContext = null; window.UpdateLayout(); window.AuthorizeFinalClose(); } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); try { await done.Task; } finally { thread.Join(); }
    }
    private static IEnumerable<DependencyObject> DescendantsProgress(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in DescendantsProgress(child)) yield return item; }
    }
    private static string ProgressRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "LocalMediaAI.App"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
