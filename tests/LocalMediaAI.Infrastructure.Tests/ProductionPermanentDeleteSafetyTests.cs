using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// Phase 6.5 uses only test-generated normal files under an isolated C: NTFS library.
/// Tests never enumerate, scan, mutate, or use the Original Media corpus. PermanentDelete is
/// deliberately exercised only through its type-specific, single-file production capability.
/// </summary>
public sealed class ProductionPermanentDeleteSafetyTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "LocalMediaAIProductionPermanentDeleteTests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SafeFileOperationService _operations = null!;
    private bool _disposed;

    public ProductionPermanentDeleteSafetyTests()
    {
        _workspace = Path.Combine("C:\\Manus\\LocalMediaAI-Phase65-Isolated", _testId, "library");
    }

    public async Task InitializeAsync()
    {
        Assert.True(OperatingSystem.IsWindows(), "Phase 6.5 production permanent-delete safety tests require Windows.");
        Directory.CreateDirectory(_appRoot);
        await File.WriteAllTextAsync(Path.Combine(_appRoot, "portable.flag"), string.Empty);
        Directory.CreateDirectory(_workspace);
        _paths = AppPaths.Create(_appRoot);
        _log = new JsonFileLogger(_paths);
        _repository = new SqliteLibraryRepository(_paths, _log);
        await _repository.InitializeAsync(CancellationToken.None);
        _operations = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin());
    }

    public async Task DisposeAsync()
    {
        try
        {
            Dispose();
            await DeleteEventuallyAsync(_appRoot);
            await DeleteEventuallyAsync(Path.Combine("C:\\Manus\\LocalMediaAI-Phase65-Isolated", _testId));
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
    public async Task RealNtfsPermanentDeleteE2EPreservesStableIdsHistoryAndNeverUsesRecycleBin()
    {
        var root = await AddRootAsync("real-ntfs-e2e");
        var source = await AddAndScanAsync(root, "permanent-real.mp4", 0x81);
        var tag = await _repository.AddManualMediaTagAsync(source.Media.MediaFileId, "keep-context", "human context must survive permanent lifecycle", CancellationToken.None);
        var plan = await PreparePermanentDeletePlanAsync(source.Media);

        Assert.True(plan.ProductionPermanentDeleteCapability);
        Assert.False(plan.ProductionSameVolumeCapability);
        Assert.False(plan.ProductionCrossVolumeMoveCapability);
        Assert.False(plan.ProductionRecycleCapability);
        Assert.Equal(1, plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, plan.ValidationStrength);
        Assert.False(string.IsNullOrWhiteSpace(plan.SourceFullSha256));
        Assert.False(FileOperationPolicy.CanExecute(plan, false, out var beforeCode, out _));
        Assert.Equal("DRY_RUN_ACKNOWLEDGEMENT_REQUIRED", beforeCode);
        Assert.True(File.Exists(source.Path));

        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, acknowledged.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var ackCode, out _));
        Assert.Equal("IRREVERSIBLE_FINAL_CONFIRMATION_REQUIRED", ackCode);

        var final = await _operations.ConfirmProductionPermanentDeleteExecutionAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.FinalConfirmationRequired, final.State);
        Assert.True(FileOperationPolicy.CanExecute(final, false, out var finalCode, out _));
        Assert.Equal("PRODUCTION_PERMANENT_DELETE_EXECUTION_ALLOWED", finalCode);

        var deleted = await _operations.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.PermanentlyDeleted, deleted.State);
        Assert.False(File.Exists(source.Path));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(source.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(source.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(PresenceState.PermanentlyDeleted, current.PresenceState);
        var lifecycle = await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(lifecycle);
        Assert.Equal(plan.OperationId, lifecycle!.PermanentDeleteOperationId);
        Assert.Equal("PermanentlyDeleted", lifecycle.LifecycleState);
        Assert.False(lifecycle.SourceAvailable);
        Assert.Equal(source.Path, lifecycle.LastKnownSourcePath);
        Assert.Equal(plan.SourceFullSha256, lifecycle.LastKnownFullSha256);
        var listed = await _repository.GetMediaListItemAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(listed);
        Assert.Equal(PresenceState.PermanentlyDeleted, listed!.PresenceState);
        Assert.Equal(source.Path, listed.Path); // list projection preserves the last-known source for human audit while media_file is unavailable.
        Assert.Empty((await _repository.QueryMediaAsync(root.LibraryRootId, null, 0, 20, CancellationToken.None)).Items);
        Assert.Null(await _repository.FindLibraryMediaOffsetAsync(source.Media.MediaFileId, root.LibraryRootId, null, null, false, CancellationToken.None));
        var tags = await _repository.GetManualMediaTagsAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Contains(tags, item => item.ManualMediaTagId == tag.ManualMediaTagId && item.Note == "human context must survive permanent lifecycle");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "deleted row must not become an operation target", SourceValidationStrength.StrongFullHash, CancellationToken.None));
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "FinalPermanentDeleteExecutionConfirmed");
        Assert.Contains(audit, item => item.EventName == "FinalPermanentDeleteConfirmationPreflightReady");
        Assert.Contains(audit, item => item.EventName == "PermanentDeleteLifecycleDbPublished");
        Assert.DoesNotContain(audit, item => item.EventName.Contains("Recycle", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DryRunAcknowledgementDoesNotDeleteAndIndependentFinalConfirmationRemainsRequired()
    {
        var root = await AddRootAsync("dry-run-gate");
        var source = await AddAndScanAsync(root, "dryrun.mp4", 0x82);
        var plan = await PreparePermanentDeletePlanAsync(source.Media);

        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ConfirmationRequired, acknowledged.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var code, out _));
        Assert.Equal("IRREVERSIBLE_FINAL_CONFIRMATION_REQUIRED", code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None));
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task SourceChangedAfterFinalConfirmationBlocksPermanentDeleteAndPreservesSource()
    {
        var root = await AddRootAsync("source-changed");
        var source = await AddAndScanAsync(root, "changed.mp4", 0x83);
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media);
        await File.AppendAllBytesAsync(source.Path, [0xAB]);

        var blocked = await _operations.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceChanged, blocked.State);
        Assert.Equal("SOURCE_CHANGED", blocked.LastErrorCode);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task PermanentDeleteAdapterFailurePreservesSourceAndDoesNotPublishLifecycle()
    {
        var root = await AddRootAsync("adapter-failure");
        var source = await AddAndScanAsync(root, "adapter-failure.mp4", 0x84);
        var adapter = new StubPermanentDelete(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_TEST_FAILURE", "test-only delete adapter failure"));
        var failing = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: adapter);
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media, failing);

        var failed = await failing.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.PermanentDeleteFailed, failed.State);
        Assert.Equal("PERMANENT_DELETE_TEST_FAILURE", failed.LastErrorCode);
        Assert.Equal(1, adapter.Calls);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        Assert.Equal(PresenceState.Present, (await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None))!.PresenceState);
    }

    [Fact]
    public async Task SuccessResultWithSourceStillPresentIsPermanentDeleteFailureWithoutSecondDeleteOrLifecyclePublish()
    {
        var root = await AddRootAsync("still-present");
        var source = await AddAndScanAsync(root, "still-present.mp4", 0x85);
        var adapter = new StubPermanentDelete(WindowsPermanentDeleteResult.Success());
        var service = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: adapter);
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media, service);

        var failed = await service.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.PermanentDeleteFailed, failed.State);
        Assert.Equal("PERMANENT_DELETE_API_SUCCESS_SOURCE_STILL_PRESENT", failed.LastErrorCode);
        Assert.Equal(1, adapter.Calls);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task LifecyclePublishFailureRecoversAfterRestartWithoutSecondDeleteCall()
    {
        var root = await AddRootAsync("db-failure-recovery");
        var source = await AddAndScanAsync(root, "db-failure.mp4", 0x86);
        var counting = new CountingPermanentDelete();
        var failing = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: counting, beforeProductionPermanentDeleteLifecyclePublish: (_, _) => throw new InvalidOperationException("test-only lifecycle publish failure"));
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media, failing);

        var recoveryRequired = await failing.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, recoveryRequired.State);
        Assert.Equal(1, counting.Calls);
        Assert.False(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));

        var noRetry = new ThrowingPermanentDelete();
        var restarted = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: noRetry);
        var recovery = Assert.Single(await restarted.RecoverInterruptedProductionPermanentDeleteOperationsAsync(CancellationToken.None));

        Assert.Equal(FileOperationState.PermanentlyDeleted, recovery.State);
        Assert.Equal("RECOVERY_PERMANENT_DELETE_SOURCE_ABSENT_LIFECYCLE_PUBLISHED", recovery.Code);
        Assert.Equal(0, noRetry.Calls);
        var lifecycle = await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(lifecycle);
        Assert.Equal(plan.OperationId, lifecycle!.PermanentDeleteOperationId);
    }

    [Fact]
    public async Task RestartRecoveryWithSourcePresentObservesNoEffectAndNeverRetriesDelete()
    {
        var root = await AddRootAsync("restart-source-present");
        var source = await AddAndScanAsync(root, "restart-source-present.mp4", 0x87);
        var interrupted = new ThrowingDuringPermanentDelete();
        var service = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: interrupted);
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media, service);

        var recoveryRequired = await service.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, recoveryRequired.State);
        Assert.Equal(1, interrupted.Calls);
        Assert.True(File.Exists(source.Path));

        var noRetry = new ThrowingPermanentDelete();
        var restarted = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: noRetry);
        var recovery = Assert.Single(await restarted.RecoverInterruptedProductionPermanentDeleteOperationsAsync(CancellationToken.None));

        Assert.Equal(FileOperationState.RecoveryRequired, recovery.State);
        Assert.Equal("RECOVERY_PERMANENT_DELETE_NO_EFFECT_OBSERVED", recovery.Code);
        Assert.Equal(0, noRetry.Calls);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedExecuteCannotDeleteTwiceOrPublishPermanentLifecycleTwice()
    {
        var root = await AddRootAsync("idempotency");
        var source = await AddAndScanAsync(root, "idempotency.mp4", 0x88);
        var counting = new CountingPermanentDelete();
        var service = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: counting);
        var plan = await ConfirmPermanentDeletePlanAsync(source.Media, service);

        var deleted = await service.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None));

        Assert.Equal(FileOperationState.PermanentlyDeleted, deleted.State);
        Assert.False(File.Exists(source.Path));
        Assert.Equal(1, counting.Calls);
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Single(audit, item => item.EventName == "PermanentDeleteLifecycleDbPublished");
    }

    [Fact]
    public async Task RecycleUnavailableNeverTriggersPermanentDeleteFallback()
    {
        var root = await AddRootAsync("recycle-no-fallback");
        var source = await AddAndScanAsync(root, "recycle-no-fallback.mp4", 0x89);
        var permanent = new ThrowingPermanentDelete();
        var service = new SafeFileOperationService(
            _repository,
            _log,
            windowsRecycleBin: new StubRecycleBin(WindowsRecycleBinResult.Failure("RECYCLE_UNAVAILABLE_TEST", "test-only unavailable recycle shell")),
            windowsPermanentDelete: permanent);
        var recyclePlan = await ConfirmRecyclePlanAsync(source.Media, service);

        var failed = await service.ExecuteProductionRecycleAsync(recyclePlan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecycleUnavailable, failed.State);
        Assert.Equal(0, permanent.Calls);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task SourceMissingBeforeDryRunBlocksPermanentDeleteWithoutFilesystemMutation()
    {
        var root = await AddRootAsync("source-missing");
        var source = await AddAndScanAsync(root, "source-missing.mp4", 0x8A);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "isolated missing source test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var movedAside = source.Path + ".held";
        File.Move(source.Path, movedAside);
        try
        {
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.SourceMissing, dry.Plan.State);
            Assert.Equal("SOURCE_MISSING", dry.Preflight.Code);
            Assert.True(File.Exists(movedAside));
        }
        finally
        {
            if (File.Exists(movedAside)) File.Move(movedAside, source.Path);
        }
    }

    [Fact]
    public async Task SourceLockBlocksPermanentDeletePreflightWithoutFilesystemMutation()
    {
        var root = await AddRootAsync("source-lock");
        var source = await AddAndScanAsync(root, "source-lock.mp4", 0x8B);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "isolated source lock test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        await using var lockStream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.None);

        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceUnavailable, dry.Plan.State);
        Assert.Equal("SOURCE_READ_UNAVAILABLE", dry.Preflight.Code);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task ActualNtfsAclReadDenyBlocksPermanentDeletePreflightAndPreservesSource()
    {
        var root = await AddRootAsync("acl-read-deny");
        var source = await AddAndScanAsync(root, "acl-read-deny.mp4", 0x8C);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "isolated NTFS ACL read deny test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var originalAcl = AddFileReadDeny(source.Path);
        try
        {
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.AccessDenied, dry.Plan.State);
            Assert.Equal("MEDIA_ACCESS_DENIED", dry.Preflight.Code);
            Assert.True(File.Exists(source.Path));
            Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        }
        finally
        {
            new FileInfo(source.Path).SetAccessControl(originalAcl);
        }
    }

    [Fact]
    public async Task ReparsePointAncestorEscapeAfterPlanBlocksPermanentDeleteAndPreservesExternalTarget()
    {
        var root = await AddRootAsync("reparse-escape");
        var linkedDirectory = Path.Combine(root.ConfiguredPath, "linked");
        Directory.CreateDirectory(linkedDirectory);
        var source = await AddAndScanAsync(root, Path.Combine("linked", "reparse-escape.mp4"), 0x8D);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "isolated reparse escape test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var outsideDirectory = Path.Combine(_workspace, "outside-reparse", "target-directory");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideDirectory)!);
        Directory.Move(linkedDirectory, outsideDirectory);
        await CreateDirectoryJunctionAsync(linkedDirectory, outsideDirectory);

        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ValidationFailed, dry.Plan.State);
        Assert.Equal("SOURCE_REPARSE_ANCESTOR", dry.Preflight.Code);
        Assert.True(File.Exists(Path.Combine(outsideDirectory, "reparse-escape.mp4")));
        Assert.Null(await _repository.GetMediaPermanentDeleteLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task DedicatedAdapterRejectsDirectoriesAndHasNoRecursiveDeletePath()
    {
        var directory = Path.Combine(_workspace, "adapter-directory-rejected");
        var child = Path.Combine(directory, "must-survive.mp4");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(child, [0x91, 0x92]);
        var adapter = new WindowsPermanentDeleteFileSystem();

        var result = await adapter.PermanentlyDeleteSingleFileAsync(directory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("PERMANENT_DELETE_DIRECTORY_REJECTED", result.ErrorCode);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(child));
    }

    [Fact]
    public async Task IsolatedRealPermanentDeletePerformanceCapturesPlanHashFinalDeleteObservationDbAndRestartInspection()
    {
        var performancePath = Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_PHASE65_PERFORMANCE_PATH");
        var root = await AddRootAsync("performance");
        var path = Path.Combine(root.ConfiguredPath, "performance.mp4");
        var bytes = new byte[4 * 1024 * 1024];
        Array.Fill(bytes, (byte)0x8E);
        await File.WriteAllBytesAsync(path, bytes);
        await RescanAsync(root);
        var listed = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, "performance.mp4", 0, 10, CancellationToken.None)).Items);
        var media = (await _repository.GetMediaFileAsync(listed.MediaFileId, CancellationToken.None))!;
        ProductionPermanentDeleteExecutionTiming? timing = null;
        var measured = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), productionPermanentDeleteTimingObserver: value => timing = value);
        var planTimer = Stopwatch.StartNew();
        var plan = await PreparePermanentDeletePlanAsync(media, measured);
        planTimer.Stop();
        await measured.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await measured.ConfirmProductionPermanentDeleteExecutionAsync(plan.OperationId, CancellationToken.None);
        var deleted = await measured.ExecuteProductionPermanentDeleteAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.PermanentlyDeleted, deleted.State);
        Assert.NotNull(timing);
        var recoveryTimer = Stopwatch.StartNew();
        var recovery = await new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin(), windowsPermanentDelete: new ThrowingPermanentDelete()).RecoverInterruptedProductionPermanentDeleteOperationsAsync(CancellationToken.None);
        recoveryTimer.Stop();
        Assert.Empty(recovery);
        if (!string.IsNullOrWhiteSpace(performancePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(performancePath)!);
            var report = new
            {
                SchemaVersion = 1,
                TestName = "phase65-isolated-real-ntfs-permanent-delete-performance",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Scope = "One 4 MiB test-generated isolated C: NTFS file. This is not a cold-cache or sustained HDD benchmark.",
                SourceSizeBytes = 4L * 1024L * 1024L,
                PlanAndFullSha256Milliseconds = planTimer.ElapsedMilliseconds,
                timing!.FinalRevalidationMilliseconds,
                timing.DeleteApiMilliseconds,
                timing.SourceObservationMilliseconds,
                timing.DbLifecyclePublishMilliseconds,
                timing.TotalMilliseconds,
                RestartRecoveryInspectionMilliseconds = recoveryTimer.ElapsedMilliseconds,
                RestartRecoveryObservationCount = recovery.Count
            };
            await File.WriteAllTextAsync(performancePath, JsonSerializer.Serialize(report, IndentedJsonOptions));
        }
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(seed, 4096).ToArray());
        await RescanAsync(root);
        var media = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, Path.GetFileName(name), 0, 10, CancellationToken.None)).Items);
        return ((await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!, path);
    }

    private async Task<FileOperationPlan> PreparePermanentDeletePlanAsync(MediaFile media, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await service.CreatePlanAsync(media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "Explicit isolated media-list selection; review, AI, and preferred state are not filesystem authorization.", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await service.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.True(dry.Plan.ProductionPermanentDeleteCapability);
        Assert.False(dry.Plan.ProductionSameVolumeCapability);
        Assert.False(dry.Plan.ProductionCrossVolumeMoveCapability);
        Assert.False(dry.Plan.ProductionRecycleCapability);
        Assert.Equal(1, dry.Plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, dry.Plan.ValidationStrength);
        Assert.False(string.IsNullOrWhiteSpace(dry.Plan.SourceFullSha256));
        Assert.True(dry.Preflight.FullHashMatches);
        return dry.Plan;
    }

    private async Task<FileOperationPlan> ConfirmPermanentDeletePlanAsync(MediaFile media, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await PreparePermanentDeletePlanAsync(media, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        return await service.ConfirmProductionPermanentDeleteExecutionAsync(plan.OperationId, CancellationToken.None);
    }

    private static async Task<FileOperationPlan> ConfirmRecyclePlanAsync(MediaFile media, SafeFileOperationService service)
    {
        var plan = await service.CreatePlanAsync(media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "Recycle no-fallback proof uses an explicit isolated media-list selection.", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var dry = await service.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        return await service.ConfirmProductionRecycleExecutionAsync(plan.OperationId, CancellationToken.None);
    }

    private async Task RescanAsync(LibraryRoot root)
    {
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var seen = 0;
        var added = 0;
        var updated = 0;
        await foreach (var scanned in scanner.EnumerateAsync(root, CancellationToken.None))
        {
            var result = await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
            seen++;
            if (result.IsNew) added++; else updated++;
        }
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", seen, added, updated, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scanId, CancellationToken.None);
    }

    private static async Task CreateDirectoryJunctionAsync(string linkPath, string targetPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(linkPath);
        process.StartInfo.ArgumentList.Add(targetPath);
        Assert.True(process.Start());
        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, $"mklink /J failed with {process.ExitCode}: {output} {error}");
    }

    private static FileSecurity AddFileReadDeny(string path)
    {
        var info = new FileInfo(path);
        var original = info.GetAccessControl();
        var updated = info.GetAccessControl();
        updated.AddAccessRule(new FileSystemAccessRule(Environment.UserName, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(updated);
        return original;
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

    private sealed class StubRecycleBin(WindowsRecycleBinResult result) : IWindowsRecycleBin
    {
        public Task<WindowsRecycleBinResult> RecycleAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingRecycleBin : IWindowsRecycleBin
    {
        public Task<WindowsRecycleBinResult> RecycleAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("PermanentDelete must never invoke the Windows Recycle Bin adapter.");
    }

    private sealed class StubPermanentDelete(WindowsPermanentDeleteResult result) : IWindowsPermanentDelete
    {
        public int Calls { get; private set; }

        public Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class CountingPermanentDelete : IWindowsPermanentDelete
    {
        public int Calls { get; private set; }

        public Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
        {
            Calls++;
            File.Delete(fullyQualifiedSourcePath);
            return Task.FromResult(WindowsPermanentDeleteResult.Success());
        }
    }

    private sealed class ThrowingDuringPermanentDelete : IWindowsPermanentDelete
    {
        public int Calls { get; private set; }

        public Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("test-only adapter interruption before delete");
        }
    }

    private sealed class ThrowingPermanentDelete : IWindowsPermanentDelete
    {
        public int Calls { get; private set; }

        public Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
        {
            Calls++;
            throw new Xunit.Sdk.XunitException("Recovery must never invoke the PermanentDelete adapter.");
        }
    }
}
