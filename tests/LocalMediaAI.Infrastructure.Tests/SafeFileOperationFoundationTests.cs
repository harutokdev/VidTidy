using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class SafeFileOperationFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIOperationTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SafeFileOperationService _operations = null!;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _log = new JsonFileLogger(_paths);
        _repository = new SqliteLibraryRepository(_paths, _log);
        await _repository.InitializeAsync(CancellationToken.None);
        _operations = new SafeFileOperationService(_repository, _log);
    }

    public async Task DisposeAsync()
    {
        try
        {
            Dispose();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
            {
                try { Directory.Delete(_root, true); }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProductionDryRunAcknowledgementCreatesSeparateProposalIntentPlanAuditAndNeverMutatesSourceBeforeFinalConfirmation()
    {
        var fixture = await CreateFixtureAsync("production-dry-run");
        var source = await AddFixtureFileAsync(fixture.Root, "source.mp4", 12);
        var destination = Path.Combine(fixture.Root, "renamed.mp4");
        var before = await SnapshotAsync(source.Path);

        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, "exact:human-context", "ConfirmedDuplicate is context only", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        Assert.Equal(FileOperationState.Planned, plan.State);
        var dryRun = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dryRun.Plan.State);
        Assert.False(dryRun.ExecutionEnabled);
        Assert.Contains("Production", dryRun.WarningText, StringComparison.OrdinalIgnoreCase);

        var blocked = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, blocked.State);
        Assert.True(blocked.UserAcknowledged);
        Assert.Null(blocked.FinalExecutionConfirmedAt);
        Assert.Equal(before, await SnapshotAsync(source.Path));
        Assert.False(File.Exists(destination));
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "PlanCreated");
        Assert.Contains(audit, item => item.EventName == "Preflight");
        Assert.Contains(audit, item => item.EventName == "StateTransition");
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FixtureRenameRequiresExplicitAcknowledgementThenKeepsStableMediaFileId()
    {
        var fixture = await CreateFixtureAsync("fixture-rename");
        var source = await AddFixtureFileAsync(fixture.Root, "before.mp4", 21);
        var destination = Path.Combine(fixture.Root, "after.mp4");
        var plan = await PrepareFixturePlanAsync(source.Media, FileOperationType.Rename, destination, fixture.Capability);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None));
        Assert.True(File.Exists(source.Path));
        await _operations.AcknowledgeDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);
        var completed = await _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);

        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(destination));
        var current = Assert.Single((await _repository.QueryMediaAsync(source.Root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(source.Media.MediaFileId, current.MediaFileId);
        Assert.Equal(destination, current.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task FixtureMoveUsesExplicitDestinationAndDoesNotOverwrite()
    {
        var fixture = await CreateFixtureAsync("fixture-move");
        var source = await AddFixtureFileAsync(fixture.Root, "source.mp4", 32);
        var destinationDirectory = Path.Combine(fixture.Root, "organized");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "source.mp4");
        var plan = await PrepareFixturePlanAsync(source.Media, FileOperationType.Move, destination, fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);
        var completed = await _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);

        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(destination));
        Assert.Equal(source.Media.MediaFileId, Assert.Single((await _repository.QueryMediaAsync(source.Root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items).MediaFileId);
    }

    [Fact]
    public async Task SourceChangedAfterPlanBlocksFixtureExecutionWithoutDestinationMutation()
    {
        var fixture = await CreateFixtureAsync("source-changed");
        var source = await AddFixtureFileAsync(fixture.Root, "source.mp4", 43);
        var destination = Path.Combine(fixture.Root, "target.mp4");
        var plan = await PrepareFixturePlanAsync(source.Media, FileOperationType.Rename, destination, fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);
        await File.AppendAllBytesAsync(source.Path, [9, 8, 7]);

        var blocked = await _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);
        Assert.Equal(FileOperationState.SourceChanged, blocked.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SourceMissingAndDestinationConflictAreIndependentlyClassified()
    {
        var fixture = await CreateFixtureAsync("missing-conflict");
        var source = await AddFixtureFileAsync(fixture.Root, "missing.mp4", 51);
        var missingTarget = Path.Combine(fixture.Root, "missing-target.mp4");
        var missingPlan = await PrepareFixturePlanAsync(source.Media, FileOperationType.Rename, missingTarget, fixture.Capability);
        File.Delete(source.Path);
        await _operations.AcknowledgeDisposableFixtureAsync(missingPlan.OperationId, fixture.Capability, CancellationToken.None);
        Assert.Equal(FileOperationState.SourceMissing, (await _operations.ExecuteDisposableFixtureAsync(missingPlan.OperationId, fixture.Capability, CancellationToken.None)).State);

        var conflictSource = await AddFixtureFileAsync(fixture.Root, "conflict-source.mp4", 52);
        var conflictTarget = Path.Combine(fixture.Root, "conflict-target.mp4");
        await File.WriteAllBytesAsync(conflictTarget, Enumerable.Repeat((byte)99, 512).ToArray());
        var conflictPlan = await _operations.CreatePlanAsync(conflictSource.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.DisposableFixtureExecution, conflictTarget, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var conflictDry = await _operations.RunDryRunAsync(conflictPlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.DestinationConflict, conflictDry.Plan.State);
        Assert.True(File.Exists(conflictSource.Path));
        Assert.Equal(512, new FileInfo(conflictTarget).Length);
    }

    [Fact]
    public async Task CancellationBeforeExecutionLeavesAcknowledgedFixturePlanAndFilesystemUntouched()
    {
        var fixture = await CreateFixtureAsync("cancellation");
        var source = await AddFixtureFileAsync(fixture.Root, "source.mp4", 61);
        var destination = Path.Combine(fixture.Root, "target.mp4");
        var plan = await PrepareFixturePlanAsync(source.Media, FileOperationType.Rename, destination, fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(plan.OperationId, fixture.Capability, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _operations.ExecuteDisposableFixtureAsync(plan.OperationId, fixture.Capability, cancelled.Token));
        Assert.Equal(FileOperationState.ConfirmationRequired, (await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RestartBeforeExecutionPreservesPlanAndRestartAfterSimulatedPartialRecoversByFilesystemInspection()
    {
        var fixture = await CreateFixtureAsync("recovery");
        var before = await AddFixtureFileAsync(fixture.Root, "before.mp4", 71);
        var beforeTarget = Path.Combine(fixture.Root, "before-target.mp4");
        var beforePlan = await PrepareFixturePlanAsync(before.Media, FileOperationType.Rename, beforeTarget, fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(beforePlan.OperationId, fixture.Capability, CancellationToken.None);
        var restartedService = new SafeFileOperationService(_repository, _log);
        Assert.Empty(await restartedService.RecoverInterruptedOperationsAsync(fixture.Capability, CancellationToken.None));
        Assert.Equal(FileOperationState.ConfirmationRequired, (await _repository.GetFileOperationPlanAsync(beforePlan.OperationId, CancellationToken.None))!.State);

        var partial = await AddFixtureFileAsync(fixture.Root, "partial.mp4", 72);
        var partialTarget = Path.Combine(fixture.Root, "partial-target.mp4");
        var partialPlan = await PrepareFixturePlanAsync(partial.Media, FileOperationType.Rename, partialTarget, fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(partialPlan.OperationId, fixture.Capability, CancellationToken.None);
        await _repository.TransitionFileOperationPlanAsync(partialPlan.OperationId, FileOperationState.Executing, null, null, null, CancellationToken.None);
        File.Move(partial.Path, partialTarget, false); // disposable fixture fault-injection: filesystem effect before durable completion.

        var recovered = Assert.Single(await restartedService.RecoverInterruptedOperationsAsync(fixture.Capability, CancellationToken.None));
        Assert.Equal(FileOperationState.Completed, recovered.State);
        Assert.Equal("RECOVERY_DESTINATION_VERIFIED", recovered.Code);
        Assert.Equal(FileOperationState.Completed, (await _repository.GetFileOperationPlanAsync(partialPlan.OperationId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task MixedFixtureBatchRetainsPerItemResultAndReportsPartialCompletion()
    {
        var fixture = await CreateFixtureAsync("mixed-batch");
        var first = await AddFixtureFileAsync(fixture.Root, "first.mp4", 81);
        var second = await AddFixtureFileAsync(fixture.Root, "second.mp4", 82);
        var firstPlan = await PrepareFixturePlanAsync(first.Media, FileOperationType.Rename, Path.Combine(fixture.Root, "first-target.mp4"), fixture.Capability);
        var secondPlan = await PrepareFixturePlanAsync(second.Media, FileOperationType.Rename, Path.Combine(fixture.Root, "second-target.mp4"), fixture.Capability);
        await _operations.AcknowledgeDisposableFixtureAsync(firstPlan.OperationId, fixture.Capability, CancellationToken.None);
        await _operations.AcknowledgeDisposableFixtureAsync(secondPlan.OperationId, fixture.Capability, CancellationToken.None);
        await File.AppendAllBytesAsync(second.Path, [1]);

        var batch = await _operations.ExecuteDisposableFixtureBatchAsync([firstPlan.OperationId, secondPlan.OperationId], fixture.Capability, CancellationToken.None);
        Assert.Equal(FileOperationState.PartiallyCompleted, batch.AggregateState);
        Assert.Equal(1, batch.CompletedCount);
        Assert.Equal(1, batch.SkippedCount);
        Assert.Contains(batch.Items, item => item.OperationId == firstPlan.OperationId && item.State == FileOperationState.Completed);
        Assert.Contains(batch.Items, item => item.OperationId == secondPlan.OperationId && item.State == FileOperationState.SourceChanged);
    }

    [Fact]
    public async Task LockedSourceAndTypeSpecificPermanentDeleteDryRunRemainNonMutating()
    {
        var fixture = await CreateFixtureAsync("locked-and-delete");
        var source = await AddFixtureFileAsync(fixture.Root, "locked.mp4", 91);
        var target = Path.Combine(fixture.Root, "target.mp4");
        var lockedPlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.DisposableFixtureExecution, target, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        await using (var lockHandle = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var dry = await _operations.RunDryRunAsync(lockedPlan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.SourceUnavailable, dry.Plan.State);
        }

        var deletePlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, null, SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var deleteDry = await _operations.RunDryRunAsync(deletePlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, deleteDry.Plan.State);
        Assert.Equal("PREFLIGHT_PERMANENT_DELETE_READY", deleteDry.Preflight.Code);
        Assert.True(deleteDry.Plan.ProductionPermanentDeleteCapability);
        Assert.False(deleteDry.Plan.ProductionRecycleCapability);
        Assert.Equal(1, deleteDry.Plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, deleteDry.Plan.ValidationStrength);
        Assert.True(File.Exists(source.Path));
    }

    private async Task<(string Root, DisposableFixtureCapability Capability)> CreateFixtureAsync(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, FileOperationPolicy.FixtureMarkerFileName);
        await File.WriteAllTextAsync(marker, "disposable fixture only");
        return (root, new DisposableFixtureCapability(root, marker));
    }

    private async Task<(LibraryRoot Root, MediaFile Media, string Path)> AddFixtureFileAsync(string rootPath, string name, byte seed)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(seed, 4096).ToArray());
        var root = await _repository.AddOrGetLibraryRootAsync(rootPath, CancellationToken.None);
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var seen = 0;
        await foreach (var scanned in scanner.EnumerateAsync(root, CancellationToken.None))
        {
            await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
            seen++;
        }
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", seen, seen, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scanId, CancellationToken.None);
        var media = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, name, 0, 10, CancellationToken.None)).Items);
        return (root, (await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!, path);
    }

    private async Task<FileOperationPlan> PrepareFixturePlanAsync(MediaFile media, FileOperationType type, string destination, DisposableFixtureCapability capability)
    {
        var plan = await _operations.CreatePlanAsync(media.MediaFileId, type, FileOperationMode.DisposableFixtureExecution, destination, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.True(File.Exists(capability.MarkerPath));
        return dry.Plan;
    }

    private static async Task<string> SnapshotAsync(string path)
    {
        var info = new FileInfo(path);
        return $"{await FileHash.Sha256Async(path, CancellationToken.None)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }
}
