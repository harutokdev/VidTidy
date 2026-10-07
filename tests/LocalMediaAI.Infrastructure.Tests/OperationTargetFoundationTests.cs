using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class OperationTargetFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LocalMediaAIOperationTargetTests", Guid.NewGuid().ToString("N"));
    private JsonFileLogger _log = null!;
    private AppPaths _paths = null!;
    private SqliteLibraryRepository _library = null!;
    private SafeFileOperationService _operations = null!;
    private MainViewModel _vm = null!;
    private LibraryRoot _root = null!;
    private MediaListItem _a = null!;
    private MediaListItem _b = null!;
    private Func<FileOperationPlan, Task>? _pauseExecute;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_directory); _paths.EnsureDirectories();
        _log = new JsonFileLogger(_paths);
        _library = new SqliteLibraryRepository(_paths, _log);
        await _library.InitializeAsync(CancellationToken.None);
        var mediaDirectory = Path.Combine(_directory, "media"); Directory.CreateDirectory(mediaDirectory);
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "a.mp4"), new byte[120]);
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "b.mp4"), new byte[130]);
        _root = await _library.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        await ScanAsync();
        _operations = new SafeFileOperationService(_library, _log, beforeProductionDbPublish: (plan, _) => _pauseExecute?.Invoke(plan) ?? Task.CompletedTask);
        var jobs = new SqliteJobStore(_paths, _log);
        var worker = new WorkerProcessManager(_paths, _log);
        var pipeline = new MediaPipeline(_library, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log, worker: worker, paths: _paths);
        _vm = new MainViewModel(_library, jobs, pipeline, worker, new SearchDouble(), _log, fileOperations: _operations);
        _vm.RefreshCommand.Execute(null);
        await WaitAsync(() => _vm.RefreshCommand.CanExecute(null));
        _a = _vm.MediaItems.Single(m => m.FileName == "a.mp4"); _b = _vm.MediaItems.Single(m => m.FileName == "b.mp4");
    }

    [Fact]
    public async Task SelectionIsNotAuthorityAndHandoffRequiresExplicitOperationChoice()
    {
        _vm.SelectedMedia = _a;
        Assert.Null(_vm.OperationTarget);
        _vm.HandoffLibraryMediaCommand.Execute(null);
        await WaitAsync(() => _vm.OperationTarget is not null && _vm.HandoffLibraryMediaCommand.CanExecute(null));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Null(_vm.OperationPlanningType);
        Assert.False(_vm.CreateOperationDryRunCommand.CanExecute(null));
        _vm.CreateOperationDryRunCommand.Execute(null);
        _vm.OperationPlanningAcknowledgement = true; _vm.OperationPlanningFinalConfirmation = true;
        Assert.Null(_vm.CurrentOperationDryRun);
        Assert.False(_vm.OperationPlanningAcknowledgement); Assert.False(_vm.OperationPlanningFinalConfirmation);
        _vm.SelectedMedia = _b;
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget.MediaFileId);
        _vm.OperationPlanningType = FileOperationType.Rename;
        _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "a-renamed.mp4");
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(_a.MediaFileId, _vm.CurrentOperationDryRun!.Plan.MediaFileId);
    }

    [Fact]
    public async Task ReviewHandsOffSpecificMediaWithoutChangingHumanDecision()
    {
        await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "Library");
        var review = Review(_a.MediaFileId, _b.MediaFileId);
        _vm.SelectedUnifiedReview = review;
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        _vm.HandoffReviewSecondaryMediaCommand.Execute(null);
        await WaitAsync(() => _vm.OperationTarget?.MediaFileId == _b.MediaFileId);
        Assert.Null(_vm.OperationPlanningType);
        Assert.Equal("Preferred", review.HumanDecision);
        Assert.Equal(review.ReviewItemId, _vm.OperationTarget!.ReviewItemId);
        _vm.SelectedUnifiedReview = Review(_a.MediaFileId, null);
        Assert.Equal(_b.MediaFileId, _vm.OperationTarget.MediaFileId);
        Assert.False(_vm.HandoffReviewSecondaryMediaCommand.CanExecute(null));
        _vm.HandoffReviewPrimaryMediaCommand.Execute(null);
        await WaitAsync(() => _vm.OperationTarget?.MediaFileId == _a.MediaFileId);
    }

    [Fact]
    public async Task ScopeAndLibrarySelectionClearingPreserveTargetAndPreparation()
    {
        var plan = await PrepareAsync(final: true);
        _vm.SelectedMedia = _b;
        _vm.CurrentLibraryScope = new(_root.LibraryRootId, _root.DisplayName);
        await _vm.ScopeRefreshTask;
        _vm.CurrentLibraryScope = LibraryScopePresentation.All;
        await _vm.ScopeRefreshTask;
        _vm.SelectedMedia = null; _vm.SelectedRoot = null;
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Equal(plan, _vm.CurrentOperationDryRun!.Plan);
        Assert.True(_vm.OperationPlanningFinalConfirmation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSwitchNeedsApprovalAndCancelsBeforeCommitting(bool approve)
    {
        var old = await PrepareAsync(final: true);
        var oldPresentation = _vm.CurrentOperationDryRun;
        var confirmationCalls = 0;
        _vm.ConfirmOperationPreparationSwitchAsync = () => { confirmationCalls++; Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId); return Task.FromResult(approve); };
        var applied = await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "Library B");
        Assert.Equal(approve, applied); Assert.Equal(1, confirmationCalls);
        var stored = await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None);
        if (!approve)
        {
            Assert.Equal(old, stored); Assert.Same(oldPresentation, _vm.CurrentOperationDryRun);
            Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId); Assert.True(_vm.OperationPlanningFinalConfirmation);
        }
        else
        {
            Assert.Equal(FileOperationState.Cancelled, stored!.State);
            Assert.Equal(_b.MediaFileId, _vm.OperationTarget!.MediaFileId);
            Assert.Null(_vm.CurrentOperationDryRun); Assert.Null(_vm.OperationPlanningType);
            Assert.Empty(_vm.OperationDestinationPath); Assert.False(_vm.OperationPlanningFinalConfirmation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionSameVolumeAsync(old.OperationId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SwitchConfirmationCannotCancelNewPlanOrChangedDraft()
    {
        var old = await PrepareAsync();
        var entered = new TaskCompletionSource(); var answer = new TaskCompletionSource<bool>();
        _vm.ConfirmOperationPreparationSwitchAsync = () => { entered.SetResult(); return answer.Task; };
        var switching = _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B");
        await entered.Task;
        _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "another.mp4");
        await _vm.OperationInputChangeTask;
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        var replacement = _vm.CurrentOperationDryRun!.Plan;
        Assert.NotEqual(old.OperationId, replacement.OperationId);
        answer.SetResult(true);
        Assert.False(await switching);
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Equal(replacement, await _library.GetFileOperationPlanAsync(replacement.OperationId, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchRejectsExecuteTransitionOrDurableCancelFailureWithoutPartialReset(bool cancelFailure)
    {
        var old = await PrepareAsync(final: true);
        var oldDryRun = _vm.CurrentOperationDryRun; var destination = _vm.OperationDestinationPath;
        _vm.ConfirmOperationPreparationSwitchAsync = async () =>
        {
            if (cancelFailure) await SqlAsync("CREATE TRIGGER reject_discard BEFORE UPDATE OF state ON file_operation_plan WHEN NEW.state='Cancelled' BEGIN SELECT RAISE(ABORT,'injected cancel failure'); END;");
            else await _library.TransitionFileOperationPlanAsync(old.OperationId, FileOperationState.Executing, null, null, null, CancellationToken.None);
            return true;
        };
        Assert.False(await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B"));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId); Assert.Same(oldDryRun, _vm.CurrentOperationDryRun);
        Assert.Equal(destination, _vm.OperationDestinationPath); Assert.True(_vm.OperationPlanningFinalConfirmation);
        Assert.Equal(cancelFailure ? old.State : FileOperationState.Executing, (await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData("type", false)] [InlineData("destination", false)]
    [InlineData("type", true)] [InlineData("destination", true)]
    public async Task DraftChangeCancelsDurablePlanBeforeCommitAndRejectsExecutingRace(string input, bool executing)
    {
        var old = await PrepareAsync(final: true); var oldDestination = _vm.OperationDestinationPath;
        if (executing) await _library.TransitionFileOperationPlanAsync(old.OperationId, FileOperationState.Executing, null, null, null, CancellationToken.None);
        if (input == "type") _vm.OperationPlanningType = FileOperationType.Recycle;
        else _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "new-name.mp4");
        await _vm.OperationInputChangeTask;
        var stored = await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None);
        if (executing)
        {
            Assert.Equal(FileOperationState.Executing, stored!.State);
            Assert.Equal(FileOperationType.Rename, _vm.OperationPlanningType); Assert.Equal(oldDestination, _vm.OperationDestinationPath);
            Assert.True(_vm.OperationPlanningFinalConfirmation); Assert.Equal(old, _vm.CurrentOperationDryRun!.Plan);
        }
        else
        {
            Assert.Equal(FileOperationState.Cancelled, stored!.State); Assert.Null(_vm.CurrentOperationDryRun);
            Assert.False(_vm.OperationPlanningAcknowledgement); Assert.False(_vm.OperationPlanningFinalConfirmation);
        }
    }

    [Fact]
    public async Task ExecutingContextAndAsyncCompletionStayWithCapturedTarget()
    {
        var old = await PrepareAsync(final: true);
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        _pauseExecute = async plan => { Assert.Equal(old.OperationId, plan.OperationId); entered.SetResult(); await release.Task; };
        _vm.ExecuteProductionSameVolumeOperationCommand.Execute(null);
        await entered.Task;
        Assert.False(_vm.OperationEditorsEnabled);
        _vm.SelectedMedia = _b;
        Assert.False(await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B"));
        var destination = _vm.OperationDestinationPath;
        _vm.OperationDestinationPath = "not applied"; await _vm.OperationInputChangeTask;
        Assert.Equal(destination, _vm.OperationDestinationPath);
        release.SetResult(); await WaitAsync(() => _vm.OperationEditorsEnabled);
        Assert.Equal(old.OperationId, _vm.CurrentOperationDryRun!.Plan.OperationId);
        Assert.Equal(FileOperationState.Completed, _vm.CurrentOperationDryRun.Plan.State);
        Assert.True(await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B after completion"));
        Assert.Null(_vm.CurrentOperationDryRun); Assert.Null(_vm.OperationPlanningType);
        Assert.Equal(_b.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Equal(FileOperationState.Completed, (await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreparedBatchSwitchCancelsAllItemPlansOrPreservesExecutingBatch(bool executing)
    {
        await PrepareBatchAsync();
        var old = _vm.CurrentControlledBatchDryRun!;
        _vm.ConfirmOperationPreparationSwitchAsync = async () =>
        {
            if (executing) await _library.TransitionBatchOperationAsync(old.Batch.BatchOperationId, BatchOperationState.Executing, null, null, CancellationToken.None);
            return true;
        };
        Assert.Equal(!executing, await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "single"));
        var batch = await _library.GetBatchOperationAsync(old.Batch.BatchOperationId, CancellationToken.None);
        Assert.Equal(executing ? BatchOperationState.Executing : BatchOperationState.Cancelled, batch!.State);
        foreach (var item in old.Items)
            Assert.Equal(executing ? item.PlanState : FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(item.OperationId, CancellationToken.None))!.State);
        Assert.Equal(2, _vm.ControlledBatchSelection.Count);
        if (executing) { Assert.Null(_vm.OperationTarget); Assert.Same(old, _vm.CurrentControlledBatchDryRun); }
        else { Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId); Assert.Null(_vm.CurrentControlledBatchDryRun); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BatchTargetSetChangeInvalidatesOrRejectsExecuteRace(bool executing)
    {
        await PrepareBatchAsync(); var old = _vm.CurrentControlledBatchDryRun!;
        if (executing) await _library.TransitionBatchOperationAsync(old.Batch.BatchOperationId, BatchOperationState.Executing, null, null, CancellationToken.None);
        await CommandAsync(_vm.ClearControlledBatchCommand);
        Assert.Equal(executing ? 2 : 0, _vm.ControlledBatchSelection.Count);
        Assert.Equal(executing ? BatchOperationState.Executing : BatchOperationState.Cancelled, (await _library.GetBatchOperationAsync(old.Batch.BatchOperationId, CancellationToken.None))!.State);
        if (executing) Assert.Same(old, _vm.CurrentControlledBatchDryRun);
        else { Assert.Null(_vm.CurrentControlledBatchDryRun); Assert.False(_vm.ControlledBatchFinalConfirmation); }
    }

    [Fact]
    public async Task PlanningResolvesReconciledCurrentPathByLogicalId()
    {
        await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "A");
        var moved = Path.Combine(_root.ConfiguredPath, "external-renamed.mp4"); File.Move(_a.Path, moved);
        await ScanAsync();
        var current = await _library.GetMediaFileAsync(_a.MediaFileId, CancellationToken.None);
        Assert.Equal(moved, current!.Path);
        _vm.OperationPlanningType = FileOperationType.Rename;
        _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "final.mp4");
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(moved, _vm.CurrentOperationDryRun!.Plan.SourcePath);
        Assert.Equal(_a.MediaFileId, _vm.CurrentOperationDryRun.Plan.MediaFileId);
    }

    private async Task<FileOperationPlan> PrepareAsync(bool final = false)
    {
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "A"));
        _vm.OperationPlanningType = FileOperationType.Rename; _vm.OperationDestinationPath = Path.Combine(_root.ConfiguredPath, "renamed.mp4");
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(FileOperationState.ConfirmationRequired, _vm.CurrentOperationDryRun!.Plan.State);
        if (final)
        {
            _vm.OperationPlanningAcknowledgement = true; await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
            _vm.OperationPlanningFinalConfirmation = true; await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
            Assert.Equal(FileOperationState.FinalConfirmationRequired, _vm.CurrentOperationDryRun.Plan.State);
        }
        return _vm.CurrentOperationDryRun.Plan;
    }

    [Fact]
    public async Task CombinedSingleAndBatchCancelFailureRollsBackEveryPlanBeforeTargetCommit()
    {
        var single = await PrepareAsync(final: true); await PrepareBatchAsync();
        var batch = _vm.CurrentControlledBatchDryRun!;
        await SqlAsync("CREATE TRIGGER reject_batch_discard BEFORE UPDATE OF state ON batch_operation WHEN NEW.state='Cancelled' BEGIN SELECT RAISE(ABORT,'batch cancel failure'); END;");
        _vm.ConfirmOperationPreparationSwitchAsync = () => Task.FromResult(true);
        Assert.False(await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B"));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Equal(single, await _library.GetFileOperationPlanAsync(single.OperationId, CancellationToken.None));
        Assert.Same(batch, _vm.CurrentControlledBatchDryRun);
        foreach (var item in batch.Items) Assert.Equal(item.PlanState, (await _library.GetFileOperationPlanAsync(item.OperationId, CancellationToken.None))!.State);
        Assert.True(_vm.OperationPlanningFinalConfirmation);
    }

    [Fact]
    public async Task BatchDraftInputChangeRejectsExecutingContext()
    {
        await PrepareBatchAsync(); var batch = _vm.CurrentControlledBatchDryRun!;
        await _library.TransitionBatchOperationAsync(batch.Batch.BatchOperationId, BatchOperationState.Executing, null, null, CancellationToken.None);
        _vm.ControlledBatchType = FileOperationType.Recycle; await _vm.OperationInputChangeTask;
        _vm.ControlledBatchDestinationDirectory = "not applied"; await _vm.OperationInputChangeTask;
        Assert.Equal(FileOperationType.Rename, _vm.ControlledBatchType);
        Assert.EndsWith("destination", _vm.ControlledBatchDestinationDirectory, StringComparison.Ordinal);
        Assert.Same(batch, _vm.CurrentControlledBatchDryRun);
    }

    [Fact]
    public async Task PartialBatchCreationFailureRetainsIdsForLaterGuardedDiscard()
    {
        await SqlAsync("CREATE TRIGGER reject_second_plan BEFORE INSERT ON file_operation_plan WHEN NEW.source_path LIKE '%b.mp4' BEGIN SELECT RAISE(ABORT,'second plan failure'); END;");
        _vm.SelectedMedia = _a; await CommandAsync(_vm.AddSelectedToControlledBatchCommand);
        _vm.SelectedMedia = _b; await CommandAsync(_vm.AddSelectedToControlledBatchCommand);
        _vm.ControlledBatchType = FileOperationType.Rename;
        _vm.ControlledBatchDestinationDirectory = Path.Combine(_root.ConfiguredPath, "destination");
        Directory.CreateDirectory(_vm.ControlledBatchDestinationDirectory);
        await CommandAsync(_vm.CreateControlledBatchDryRunCommand);
        Assert.Null(_vm.CurrentControlledBatchDryRun);
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT operation_id FROM file_operation_plan;";
        var id = Guid.Parse((string)(await command.ExecuteScalarAsync())!);
        var calls = 0; _vm.ConfirmOperationPreparationSwitchAsync = () => { calls++; return Task.FromResult(true); };
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "single"));
        Assert.Equal(1, calls); Assert.Equal(FileOperationState.Cancelled, (await _library.GetFileOperationPlanAsync(id, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ActualCancelAndExecuteCompeteWithoutCancellingExecutedFilesystemEffects()
    {
        for (var i = 0; i < 6; i++)
        {
            var media = (await _library.GetMediaFileAsync(_a.MediaFileId, CancellationToken.None))!;
            var destination = Path.Combine(_root.ConfiguredPath, $"race-{i}.mp4");
            var plan = await _operations.CreatePlanAsync(media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, null, "race test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
            await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
            plan = await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
            var start = new TaskCompletionSource();
            var cancel = Task.Run(async () => { await start.Task; return await _library.TryCancelFileOperationPreparationAsync(new([plan], null, []), CancellationToken.None); });
            var execute = Task.Run(async () => { await start.Task; try { await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None); } catch (InvalidOperationException) { } });
            start.SetResult(); await Task.WhenAll(cancel, execute);
            var stored = (await _library.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!;
            Assert.True(stored.State is FileOperationState.Cancelled or FileOperationState.Completed);
            Assert.Equal(stored.State == FileOperationState.Cancelled, await cancel);
            Assert.Equal(stored.State == FileOperationState.Completed, File.Exists(destination));
            Assert.Equal(stored.State == FileOperationState.Cancelled, File.Exists(media.Path));
        }
    }

    [Fact]
    public async Task ActualExecutingBatchRejectsTargetSetEditAndHandoff()
    {
        await PrepareBatchAsync();
        _vm.ControlledBatchAcknowledgement = true; await CommandAsync(_vm.AcknowledgeControlledBatchDryRunCommand);
        _vm.ControlledBatchFinalConfirmation = true; await CommandAsync(_vm.ConfirmControlledBatchFinalExecutionCommand);
        var old = _vm.CurrentControlledBatchDryRun!;
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        _pauseExecute = async _ => { entered.TrySetResult(); await release.Task; };
        _vm.ExecuteControlledBatchCommand.Execute(null); await entered.Task;
        Assert.False(_vm.ClearControlledBatchCommand.CanExecute(null));
        Assert.False(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "single"));
        Assert.Equal(2, _vm.ControlledBatchSelection.Count); Assert.Null(_vm.OperationTarget);
        release.SetResult(); await WaitAsync(() => _vm.OperationEditorsEnabled);
        Assert.Equal(old.Batch.BatchOperationId, _vm.CurrentControlledBatchDryRun!.Batch.BatchOperationId);
        Assert.Equal(BatchOperationState.Completed, _vm.CurrentControlledBatchDryRun.Batch.State);
    }
    private async Task PrepareBatchAsync()
    {
        _vm.SelectedMedia = _a; await CommandAsync(_vm.AddSelectedToControlledBatchCommand);
        _vm.SelectedMedia = _b; await CommandAsync(_vm.AddSelectedToControlledBatchCommand);
        _vm.ControlledBatchType = FileOperationType.Rename; _vm.ControlledBatchDestinationDirectory = Path.Combine(_root.ConfiguredPath, "destination");
        Directory.CreateDirectory(_vm.ControlledBatchDestinationDirectory);
        await CommandAsync(_vm.CreateControlledBatchDryRunCommand);
        Assert.NotNull(_vm.CurrentControlledBatchDryRun);
    }

    [Fact]
    public async Task SuccessfulRenameImmediatelyRefreshesCurrentLibraryProjection()
    {
        var plan = await PrepareAsync(final: true);
        await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
        Assert.Equal(FileOperationState.Completed, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.False(File.Exists(_a.Path));
        Assert.True(File.Exists(plan.DestinationPath));
        Assert.Equal(_a.MediaFileId, Assert.Single(_vm.MediaItems, item => item.Path == plan.DestinationPath).MediaFileId);
        Assert.DoesNotContain(_vm.MediaItems, item => item.Path == _a.Path);
    }

    [Fact]
    public async Task SuccessfulMoveWithinRootImmediatelyUpdatesCurrentLibraryLocation()
    {
        var destinationDirectory = Path.Combine(_root.ConfiguredPath, "organized");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "a.mp4");
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "A"));
        _vm.OperationPlanningType = FileOperationType.Move;
        _vm.OperationDestinationPath = destination;
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        _vm.OperationPlanningAcknowledgement = true;
        await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
        _vm.OperationPlanningFinalConfirmation = true;
        await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
        Assert.Equal(FileOperationState.Completed, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.False(File.Exists(_a.Path));
        Assert.True(File.Exists(destination));
        Assert.Equal(_a.MediaFileId, Assert.Single(_vm.MediaItems, item => item.Path == destination).MediaFileId);
        Assert.DoesNotContain(_vm.MediaItems, item => item.Path == _a.Path);
    }

    [Fact]
    public async Task SuccessfulRecycleImmediatelyRemovesCurrentLibraryRow()
    {
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "A"));
        _vm.OperationPlanningType = FileOperationType.Recycle;
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(FileOperationState.ConfirmationRequired, _vm.CurrentOperationDryRun!.Plan.State);
        _vm.OperationPlanningAcknowledgement = true;
        await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
        _vm.OperationPlanningFinalConfirmation = true;
        await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        await CommandAsync(_vm.ExecuteProductionRecycleOperationCommand);
        Assert.Equal(FileOperationState.Recycled, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.False(File.Exists(_a.Path));
        Assert.DoesNotContain(_vm.MediaItems, item => item.MediaFileId == _a.MediaFileId);
        Assert.Equal(_b.MediaFileId, Assert.Single(_vm.MediaItems).MediaFileId);
    }

    [Fact]
    public async Task SuccessfulPermanentDeleteImmediatelyRemovesCurrentLibraryRow()
    {
        Assert.True(await _vm.HandoffMediaToOperationAsync(_a.MediaFileId, "A"));
        _vm.OperationPlanningType = FileOperationType.PermanentDelete;
        await CommandAsync(_vm.CreateOperationDryRunCommand);
        Assert.Equal(FileOperationState.ConfirmationRequired, _vm.CurrentOperationDryRun!.Plan.State);
        _vm.OperationPlanningAcknowledgement = true;
        await CommandAsync(_vm.AcknowledgeOperationDryRunCommand);
        _vm.OperationPlanningFinalConfirmation = true;
        await CommandAsync(_vm.ConfirmOperationFinalExecutionCommand);
        await CommandAsync(_vm.ExecuteProductionPermanentDeleteOperationCommand);
        Assert.Equal(FileOperationState.PermanentlyDeleted, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.False(File.Exists(_a.Path));
        Assert.DoesNotContain(_vm.MediaItems, item => item.MediaFileId == _a.MediaFileId);
        Assert.Equal(_b.MediaFileId, Assert.Single(_vm.MediaItems).MediaFileId);
    }

    [Fact]
    public async Task SamePathReplacementIsRejectedByExistingFilesystemIdentityValidation()
    {
        var old = await PrepareAsync(final: true);
        File.Move(_a.Path, Path.Combine(_root.ConfiguredPath, "original-held.mp4"));
        await File.WriteAllBytesAsync(_a.Path, new byte[120]);
        File.SetLastWriteTimeUtc(_a.Path, old.SourceLastWriteTimeUtc.UtcDateTime);
        await CommandAsync(_vm.ExecuteProductionSameVolumeOperationCommand);
        Assert.Equal(FileOperationState.SourceChanged, _vm.CurrentOperationDryRun!.Plan.State);
        Assert.True(File.Exists(_a.Path)); Assert.False(File.Exists(old.DestinationPath));
    }

    [Fact]
    public async Task DurableStateChangeDuringConfirmationRejectsEvenWithUnchangedUiSnapshot()
    {
        var old = await PrepareAsync(final: true);
        _vm.ConfirmOperationPreparationSwitchAsync = async () =>
        {
            await _library.TransitionFileOperationPlanAsync(old.OperationId, FileOperationState.FinalConfirmationRequired, null, null, null, CancellationToken.None);
            return true;
        };
        Assert.False(await _vm.HandoffMediaToOperationAsync(_b.MediaFileId, "B"));
        Assert.Equal(_a.MediaFileId, _vm.OperationTarget!.MediaFileId);
        Assert.Equal(FileOperationState.FinalConfirmationRequired, (await _library.GetFileOperationPlanAsync(old.OperationId, CancellationToken.None))!.State);
        Assert.Equal(old, _vm.CurrentOperationDryRun!.Plan); Assert.True(_vm.OperationPlanningFinalConfirmation);
    }
    private async Task CommandAsync(AsyncCommand command)
    {
        Assert.True(command.CanExecute(null)); command.Execute(null);
        await WaitAsync(() => _vm.OperationEditorsEnabled);
    }
    private async Task ScanAsync()
    {
        var scan = await _library.StartScanAsync(_root.LibraryRootId, CancellationToken.None); var count = 0;
        await foreach (var file in new FileSystemMediaScanner().EnumerateAsync(_root, CancellationToken.None)) { await _library.UpsertScannedFileAsync(file, scan, CancellationToken.None); count++; }
        await _library.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
    }
    private async Task SqlAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static UnifiedReviewRow Review(Guid primary, Guid? secondary) => new(new UnifiedReviewItem("review-test", UnifiedReviewType.ContentSimilar, "edge", Guid.NewGuid(), primary, Guid.NewGuid(), secondary, Guid.NewGuid(),
        $"{primary},{secondary}", "contents", ReviewEvidenceState.Current, "Present", "Preferred", "keep", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "test", "embedding", "test", "0.9", "test", null, null, 120, secondary is null ? 1 : 2, "test"));
    private static async Task WaitAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Operation integration did not settle."); await Task.Delay(10); }
    }
    private sealed class SearchDouble : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }
    public void Dispose() { _vm?.Dispose(); _log?.Dispose(); GC.SuppressFinalize(this); }
    public async Task DisposeAsync()
    {
        Dispose(); SqliteConnection.ClearAllPools();
        for (var i = 0; Directory.Exists(_directory) && i < 8; i++)
        { try { Directory.Delete(_directory, true); } catch (IOException) { await Task.Delay(100); } catch (UnauthorizedAccessException) { await Task.Delay(100); } }
    }
}
