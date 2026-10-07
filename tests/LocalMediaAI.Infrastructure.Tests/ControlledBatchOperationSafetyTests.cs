using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ControlledBatchOperationSafetyTests : IAsyncLifetime, IDisposable
{
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "LocalMediaAIControlledBatchTests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SafeFileOperationService _operations = null!;
    private bool _disposed;

    public ControlledBatchOperationSafetyTests() => _workspace = Path.Combine("C:\\Manus\\LocalMediaAI-Phase66-Isolated", _testId, "library");

    public async Task InitializeAsync()
    {
        Assert.True(OperatingSystem.IsWindows(), "Phase 6.6 controlled-batch safety tests require Windows isolated NTFS paths.");
        Directory.CreateDirectory(_appRoot);
        await File.WriteAllTextAsync(Path.Combine(_appRoot, "portable.flag"), string.Empty);
        Directory.CreateDirectory(_workspace);
        _paths = AppPaths.Create(_appRoot);
        _log = new JsonFileLogger(_paths);
        _repository = new SqliteLibraryRepository(_paths, _log);
        await _repository.InitializeAsync(CancellationToken.None);
        _operations = new SafeFileOperationService(_repository, _log);
    }

    [Fact]
    public async Task ExplicitTwoItemSameVolumeMoveRunsSequentiallyAndRetainsPerItemPlanAudit()
    {
        var root = await AddRootAsync("same-volume");
        var a = await AddAndScanAsync(root, "a.mp4", 0x41);
        var b = await AddAndScanAsync(root, "b.mp4", 0x42);
        var destinationDirectory = Path.Combine(root.ConfiguredPath, "moved");
        Directory.CreateDirectory(destinationDirectory);

        var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Move,
        [
            new ControlledBatchItemRequest(a.Media.MediaFileId, Path.Combine(destinationDirectory, "a-moved.mp4"), null, "explicit isolated item A"),
            new ControlledBatchItemRequest(b.Media.MediaFileId, Path.Combine(destinationDirectory, "b-moved.mp4"), null, "explicit isolated item B")
        ], CancellationToken.None);

        Assert.Equal(2, dry.Batch.SelectedItemCount);
        Assert.Equal(2, dry.ReadyCount);
        Assert.Equal(0, dry.BlockedCount);
        Assert.False(dry.FinalConfirmationEnabled); // Dry Run acknowledgement remains independently required.
        Assert.True(File.Exists(a.Path));
        Assert.True(File.Exists(b.Path));

        await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        var final = await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.Equal(BatchOperationState.FinalConfirmationRequired, final.State);
        var completed = await _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        Assert.Equal(BatchOperationState.Completed, completed.State);
        Assert.False(File.Exists(a.Path));
        Assert.False(File.Exists(b.Path));
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "a-moved.mp4")));
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "b-moved.mp4")));
        var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.All(items, item => Assert.Equal(BatchItemState.Completed, item.State));
        var audits = await _repository.GetBatchOperationAuditAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.Contains(audits, audit => audit.EventName == "BatchDryRunAcknowledged");
        Assert.Contains(audits, audit => audit.EventName == "BatchFinalExecutionConfirmed");
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RealDifferentVolumeBatchMoveUsesPerItemCopyHashPublishTransaction()
    {
        var unique = Guid.NewGuid().ToString("N");
        var sourceDirectory = Path.Combine("E:\\Manus\\LocalMediaAI-Phase66-Isolated", unique, "source");
        var destinationDirectory = Path.Combine("C:\\Manus\\LocalMediaAI-Phase66-Isolated", unique, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            Assert.Equal("E:\\", Path.GetPathRoot(sourceDirectory));
            Assert.Equal("C:\\", Path.GetPathRoot(destinationDirectory));
            var sourceRoot = await _repository.AddOrGetLibraryRootAsync(sourceDirectory, CancellationToken.None);
            await _repository.AddOrGetLibraryRootAsync(destinationDirectory, CancellationToken.None);
            var aPath = Path.Combine(sourceDirectory, "a.mp4");
            var bPath = Path.Combine(sourceDirectory, "b.mp4");
            await File.WriteAllBytesAsync(aPath, Enumerable.Repeat((byte)0x48, 65536).ToArray());
            await File.WriteAllBytesAsync(bPath, Enumerable.Repeat((byte)0x49, 65536).ToArray());
            await RescanRootAsync(sourceRoot);
            var media = (await _repository.QueryMediaAsync(sourceRoot.LibraryRootId, null, 0, 10, CancellationToken.None)).Items;
            var a = Assert.Single(media, item => item.FileName == "a.mp4");
            var b = Assert.Single(media, item => item.FileName == "b.mp4");
            var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Move,
            [new ControlledBatchItemRequest(a.MediaFileId, Path.Combine(destinationDirectory, "a.mp4")), new ControlledBatchItemRequest(b.MediaFileId, Path.Combine(destinationDirectory, "b.mp4"))], CancellationToken.None);
            await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
            await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);
            var completed = await _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None);
            Assert.Equal(BatchOperationState.Completed, completed.State);
            Assert.False(File.Exists(aPath)); Assert.False(File.Exists(bPath));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "a.mp4"))); Assert.True(File.Exists(Path.Combine(destinationDirectory, "b.mp4")));
            var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
            Assert.All(items, item => Assert.Equal(BatchItemState.Completed, item.State));
            Assert.All(items, item => Assert.Equal(FileOperationState.Completed, item.PlanState));
        }
        finally
        {
            await DeleteEventuallyAsync(Path.Combine("E:\\Manus\\LocalMediaAI-Phase66-Isolated", unique));
            await DeleteEventuallyAsync(Path.Combine("C:\\Manus\\LocalMediaAI-Phase66-Isolated", unique));
        }
    }

    [Fact]
    public async Task RealWindowsRecycleBatchUsesPerItemRecycleLifecycleAndNeverPermanentDeleteFallback()
    {
        var root = await AddRootAsync("recycle-e2e");
        var a = await AddAndScanAsync(root, "a-recycle.mp4", 0x46);
        var b = await AddAndScanAsync(root, "b-recycle.mp4", 0x47);
        var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Recycle,
        [new ControlledBatchItemRequest(a.Media.MediaFileId, null), new ControlledBatchItemRequest(b.Media.MediaFileId, null)], CancellationToken.None);
        await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        var completed = await _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        Assert.Equal(BatchOperationState.Completed, completed.State);
        Assert.False(File.Exists(a.Path));
        Assert.False(File.Exists(b.Path));
        Assert.Equal(PresenceState.Recycled, (await _repository.GetMediaFileAsync(a.Media.MediaFileId, CancellationToken.None))!.PresenceState);
        Assert.Equal(PresenceState.Recycled, (await _repository.GetMediaFileAsync(b.Media.MediaFileId, CancellationToken.None))!.PresenceState);
        Assert.NotNull(await _repository.GetMediaRecycleLifecycleAsync(a.Media.MediaFileId, CancellationToken.None));
        Assert.NotNull(await _repository.GetMediaRecycleLifecycleAsync(b.Media.MediaFileId, CancellationToken.None));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(a.Media.MediaFileId, CancellationToken.None));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(b.Media.MediaFileId, CancellationToken.None));
        var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.All(items, item => Assert.Equal(BatchItemState.Completed, item.State));
    }

    [Fact]
    public async Task PermanentDeleteBatchAndOverLimitSelectionAreRejectedBeforeAnyPlanOrMutation()
    {
        var root = await AddRootAsync("rejections");
        var source = await AddAndScanAsync(root, "single.mp4", 0x43);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.CreateControlledBatchAsync(FileOperationType.PermanentDelete, [new ControlledBatchItemRequest(source.Media.MediaFileId, null)], CancellationToken.None));
        var overLimit = Enumerable.Range(0, FileOperationPolicy.ControlledBatchMaxItems + 1).Select(index => new ControlledBatchItemRequest(Guid.NewGuid(), null)).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _operations.CreateControlledBatchAsync(FileOperationType.Recycle, overLimit, CancellationToken.None));
        Assert.True(File.Exists(source.Path));
        Assert.Equal(PresenceState.Present, (await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None))!.PresenceState);
    }

    [Fact]
    public async Task ChangedSecondItemStopsSequentialBatchWithoutSilentSkipOrThirdMutation()
    {
        var root = await AddRootAsync("partial-failure");
        var a = await AddAndScanAsync(root, "a.mp4", 0x51);
        var b = await AddAndScanAsync(root, "b.mp4", 0x52);
        var c = await AddAndScanAsync(root, "c.mp4", 0x53);
        var destinationDirectory = Path.Combine(root.ConfiguredPath, "moved");
        Directory.CreateDirectory(destinationDirectory);
        var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Move,
        [new ControlledBatchItemRequest(a.Media.MediaFileId, Path.Combine(destinationDirectory, "a.mp4")), new ControlledBatchItemRequest(b.Media.MediaFileId, Path.Combine(destinationDirectory, "b.mp4")), new ControlledBatchItemRequest(c.Media.MediaFileId, Path.Combine(destinationDirectory, "c.mp4"))], CancellationToken.None);
        await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await File.AppendAllBytesAsync(b.Path, [0x99]);

        var result = await _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        Assert.Equal(BatchOperationState.PartiallyCompleted, result.State);
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "a.mp4")));
        Assert.True(File.Exists(b.Path));
        Assert.True(File.Exists(c.Path));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "c.mp4")));
        var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.Equal(BatchItemState.Completed, items.Single(item => item.MediaFileId == a.Media.MediaFileId).State);
        Assert.Equal(BatchItemState.Failed, items.Single(item => item.MediaFileId == b.Media.MediaFileId).State);
        Assert.Equal(BatchItemState.Cancelled, items.Single(item => item.MediaFileId == c.Media.MediaFileId).State);
    }

    [Fact]
    public async Task DbPublishFailureStopsRemainingItemAndRestartRecoversWithoutBatchReplay()
    {
        var root = await AddRootAsync("db-publish-recovery");
        var a = await AddAndScanAsync(root, "a.mp4", 0x58);
        var b = await AddAndScanAsync(root, "b.mp4", 0x59);
        var destinationDirectory = Path.Combine(root.ConfiguredPath, "moved");
        Directory.CreateDirectory(destinationDirectory);
        var failing = new SafeFileOperationService(_repository, _log, (_, _) => throw new InvalidOperationException("test-only DB publish failure"));
        var dry = await failing.CreateControlledBatchAsync(FileOperationType.Move,
        [new ControlledBatchItemRequest(a.Media.MediaFileId, Path.Combine(destinationDirectory, "a.mp4")), new ControlledBatchItemRequest(b.Media.MediaFileId, Path.Combine(destinationDirectory, "b.mp4"))], CancellationToken.None);
        await failing.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await failing.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        var interrupted = await failing.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        Assert.Equal(BatchOperationState.RecoveryRequired, interrupted.State);
        Assert.False(File.Exists(a.Path));
        Assert.True(File.Exists(Path.Combine(destinationDirectory, "a.mp4")));
        Assert.True(File.Exists(b.Path));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "b.mp4")));
        var recovered = await new SafeFileOperationService(_repository, _log).RecoverInterruptedControlledBatchesAsync(CancellationToken.None);
        Assert.Equal(BatchOperationState.PartiallyCompleted, Assert.Single(recovered).State);
        Assert.Equal(Path.Combine(destinationDirectory, "a.mp4"), (await _repository.GetMediaFileAsync(a.Media.MediaFileId, CancellationToken.None))!.Path);
        Assert.True(File.Exists(b.Path));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "b.mp4")));
    }

    [Fact]
    public async Task RestartRecoveryDoesNotReplayFinalConfirmedNotStartedBatchItems()
    {
        var root = await AddRootAsync("restart-no-replay");
        var a = await AddAndScanAsync(root, "a.mp4", 0x61);
        var b = await AddAndScanAsync(root, "b.mp4", 0x62);
        var destinationDirectory = Path.Combine(root.ConfiguredPath, "moved");
        Directory.CreateDirectory(destinationDirectory);
        var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Move,
        [new ControlledBatchItemRequest(a.Media.MediaFileId, Path.Combine(destinationDirectory, "a.mp4")), new ControlledBatchItemRequest(b.Media.MediaFileId, Path.Combine(destinationDirectory, "b.mp4"))], CancellationToken.None);
        await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await _repository.TransitionBatchOperationAsync(dry.Batch.BatchOperationId, BatchOperationState.Executing, "TEST_INTERRUPTED_BEFORE_FIRST_ITEM", "test-only durable interrupted batch", CancellationToken.None);

        var recovered = await new SafeFileOperationService(_repository, _log).RecoverInterruptedControlledBatchesAsync(CancellationToken.None);

        Assert.Equal(BatchOperationState.Cancelled, Assert.Single(recovered).State);
        Assert.True(File.Exists(a.Path));
        Assert.True(File.Exists(b.Path));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "a.mp4")));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "b.mp4")));
        var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.All(items, item => Assert.Equal(BatchItemState.Cancelled, item.State));
        var audit = await _repository.GetBatchOperationAuditAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "BatchStateTransition" && item.DetailJson!.Contains("BATCH_RESTART_OBSERVATION_COMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationRequestBeforeExecutionPreservesSourcesAndDoesNotStartItems()
    {
        var root = await AddRootAsync("cancel-before-execution");
        var a = await AddAndScanAsync(root, "a.mp4", 0x44);
        var b = await AddAndScanAsync(root, "b.mp4", 0x45);
        var destinationDirectory = Path.Combine(root.ConfiguredPath, "moved");
        Directory.CreateDirectory(destinationDirectory);
        var dry = await _operations.CreateControlledBatchAsync(FileOperationType.Move,
        [new ControlledBatchItemRequest(a.Media.MediaFileId, Path.Combine(destinationDirectory, "a.mp4")), new ControlledBatchItemRequest(b.Media.MediaFileId, Path.Combine(destinationDirectory, "b.mp4"))], CancellationToken.None);
        await _operations.AcknowledgeControlledBatchDryRunAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        await _operations.ConfirmControlledBatchExecutionAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        var requested = await _operations.RequestControlledBatchCancellationAsync(dry.Batch.BatchOperationId, CancellationToken.None);

        Assert.True(requested.CancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteControlledBatchAsync(dry.Batch.BatchOperationId, CancellationToken.None));
        Assert.True(File.Exists(a.Path));
        Assert.True(File.Exists(b.Path));
        var items = await _repository.GetBatchItemsAsync(dry.Batch.BatchOperationId, CancellationToken.None);
        Assert.All(items, item => Assert.Equal(BatchItemState.FinalConfirmed, item.State));
    }

    private async Task<LibraryRoot> AddRootAsync(string scenario)
    {
        var path = Path.Combine(_workspace, scenario);
        Directory.CreateDirectory(path);
        return await _repository.AddOrGetLibraryRootAsync(path, CancellationToken.None);
    }

    private async Task<(MediaFile Media, string Path)> AddAndScanAsync(LibraryRoot root, string name, byte seed)
    {
        var path = Path.Combine(root.ConfiguredPath, name);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(seed, 4096).ToArray());
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var seen = 0;
        var added = 0;
        var updated = 0;
        await foreach (var scanned in scanner.EnumerateAsync(root, CancellationToken.None))
        {
            var outcome = await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
            seen++;
            if (outcome.IsNew) added++; else updated++;
        }
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", seen, added, updated, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scanId, CancellationToken.None);
        var media = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, Path.GetFileName(name), 0, 10, CancellationToken.None)).Items);
        return ((await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!, path);
    }

    private async Task RescanRootAsync(LibraryRoot root)
    {
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var scanner = new FileSystemMediaScanner(); var seen = 0; var added = 0; var updated = 0;
        await foreach (var scanned in scanner.EnumerateAsync(root, CancellationToken.None)) { var outcome = await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None); seen++; if (outcome.IsNew) added++; else updated++; }
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", seen, added, updated, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scanId, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Dispose();
        await DeleteEventuallyAsync(_appRoot);
        await DeleteEventuallyAsync(Path.Combine("C:\\Manus\\LocalMediaAI-Phase66-Isolated", _testId));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task DeleteEventuallyAsync(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 8; attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}
