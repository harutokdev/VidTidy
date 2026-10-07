using System.Security.AccessControl;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// Phase 6.4 uses only test-generated files under a normal isolated C: LibraryRoot. Successful tests
/// deliberately leave files in the real Windows Recycle Bin; they never touch the Original Media corpus.
/// </summary>
public sealed class ProductionRecycleBinSafetyTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "LocalMediaAIProductionRecycleTests", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SafeFileOperationService _operations = null!;
    private bool _disposed;

    public ProductionRecycleBinSafetyTests()
    {
        _workspace = Path.Combine("C:\\Manus\\LocalMediaAI-Phase64-Isolated", _testId, "library");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_appRoot);
        await File.WriteAllTextAsync(Path.Combine(_appRoot, "portable.flag"), string.Empty);
        Directory.CreateDirectory(_workspace);
        _paths = AppPaths.Create(_appRoot);
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
            await DeleteEventuallyAsync(_appRoot);
            await DeleteEventuallyAsync(Path.Combine("C:\\Manus\\LocalMediaAI-Phase64-Isolated", _testId));
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
    public async Task RealWindowsRecycleBinE2EPreservesStableIdsLifecycleAndAudit()
    {
        var root = await AddRootAsync("real-shell-e2e");
        var source = await AddAndScanAsync(root, "recycle-real.mp4", 0x61);
        var plan = await PrepareRecyclePlanAsync(source.Media);

        Assert.True(plan.ProductionRecycleCapability);
        Assert.False(plan.ProductionSameVolumeCapability);
        Assert.False(plan.ProductionCrossVolumeMoveCapability);
        Assert.Equal(SourceValidationStrength.StrongFullHash, plan.ValidationStrength);
        Assert.False(string.IsNullOrWhiteSpace(plan.SourceFullSha256));
        Assert.False(FileOperationPolicy.CanExecute(plan, false, out var beforeCode, out _));
        Assert.Equal("DRY_RUN_ACKNOWLEDGEMENT_REQUIRED", beforeCode);
        Assert.True(File.Exists(source.Path));

        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var ackCode, out _));
        Assert.Equal("FINAL_EXECUTION_CONFIRMATION_REQUIRED", ackCode);
        var final = await _operations.ConfirmProductionRecycleExecutionAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.FinalConfirmationRequired, final.State);
        Assert.True(FileOperationPolicy.CanExecute(final, false, out var finalCode, out _));
        Assert.Equal("PRODUCTION_RECYCLE_EXECUTION_ALLOWED", finalCode);

        var recycled = await _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.Recycled, recycled.State);
        Assert.False(File.Exists(source.Path));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(source.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(source.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(PresenceState.Recycled, current.PresenceState);
        var lifecycle = await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(lifecycle);
        Assert.Equal(plan.OperationId, lifecycle!.RecycleOperationId);
        Assert.Equal("Recycled", lifecycle.LifecycleState);
        Assert.False(lifecycle.SourceAvailable);
        Assert.Equal(source.Path, lifecycle.LastKnownSourcePath);
        Assert.Equal(plan.SourceFullSha256, lifecycle.LastKnownFullSha256);
        var listed = await _repository.GetMediaListItemAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(listed);
        Assert.Equal(PresenceState.Recycled, listed!.PresenceState);
        Assert.Equal(source.Path, listed.Path);
        Assert.Empty((await _repository.QueryMediaAsync(root.LibraryRootId, null, 0, 20, CancellationToken.None)).Items);
        Assert.Null(await _repository.FindLibraryMediaOffsetAsync(source.Media.MediaFileId, root.LibraryRootId, null, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "recycled row is not an operation target", SourceValidationStrength.StrongFullHash, CancellationToken.None));
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "FinalRecycleExecutionConfirmed");
        Assert.Contains(audit, item => item.EventName == "RecycleLifecycleDbPublished");
        Assert.DoesNotContain(audit, item => item.EventName.Contains("PermanentDelete", StringComparison.OrdinalIgnoreCase));
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RealWindowsRestoreOfIsolatedRecycledItemReconcilesLifecycleWithoutCreatingNewMediaId()
    {
        var root = await AddRootAsync("external-restore");
        var source = await AddAndScanAsync(root, "restore-" + _testId + ".mp4", 0x71);
        var plan = await ConfirmRecyclePlanAsync(source.Media);
        var recycled = await _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Recycled, recycled.State);
        Assert.False(File.Exists(source.Path));

        await RestoreExactlyOneIsolatedRecycleBinItemAsync(source.Path);
        await RescanAsync(root);

        Assert.True(File.Exists(source.Path));
        var restored = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(source.Media.MediaFileId, restored!.MediaFileId);
        Assert.Equal(source.Media.MediaContentId, restored.MediaContentId);
        Assert.Equal(PresenceState.Present, restored.PresenceState);
        var lifecycle = await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(lifecycle);
        Assert.Equal("RestoredExternally", lifecycle!.LifecycleState);
        Assert.True(lifecycle.SourceAvailable);
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "ExternalRestoreReconciled");
    }

    [Fact]
    public async Task DryRunAcknowledgementDoesNotRecycleAndFinalConfirmationRemainsRequired()
    {
        var root = await AddRootAsync("dry-run-gate");
        var source = await AddAndScanAsync(root, "dryrun.mp4", 0x62);
        var plan = await PrepareRecyclePlanAsync(source.Media);

        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ConfirmationRequired, acknowledged.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var code, out _));
        Assert.Equal("FINAL_EXECUTION_CONFIRMATION_REQUIRED", code);
    }

    [Fact]
    public async Task SourceChangedAfterFinalConfirmationBlocksRecycleWithoutShellMutation()
    {
        var root = await AddRootAsync("source-changed");
        var source = await AddAndScanAsync(root, "changed.mp4", 0x63);
        var plan = await ConfirmRecyclePlanAsync(source.Media);
        await File.AppendAllBytesAsync(source.Path, [0xAB]);

        var blocked = await _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceChanged, blocked.State);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task RecycleUnavailableDoesNotFallbackToPermanentDeleteAndPreservesSource()
    {
        var root = await AddRootAsync("unavailable");
        var source = await AddAndScanAsync(root, "unavailable.mp4", 0x64);
        var unavailable = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new StubRecycleBin(WindowsRecycleBinResult.Failure("RECYCLE_UNAVAILABLE_TEST", "test-only unavailable shell")));
        var plan = await ConfirmRecyclePlanAsync(source.Media, unavailable);

        var failed = await unavailable.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecycleUnavailable, failed.State);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Equal(PresenceState.Present, current!.PresenceState);
    }

    [Fact]
    public async Task ShellSuccessWithSourceStillPresentRequiresRecoveryAndDoesNotInferRecycled()
    {
        var root = await AddRootAsync("still-present");
        var source = await AddAndScanAsync(root, "still-present.mp4", 0x65);
        var misleading = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new StubRecycleBin(WindowsRecycleBinResult.Success(aborted: false)));
        var plan = await ConfirmRecyclePlanAsync(source.Media, misleading);

        var recovery = await misleading.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, recovery.State);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        var restart = Assert.Single(await new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin()).RecoverInterruptedProductionRecycleOperationsAsync(CancellationToken.None));
        Assert.Equal(FileOperationState.RecoveryRequired, restart.State);
        Assert.Equal("RECOVERY_RECYCLE_NO_EFFECT_OBSERVED", restart.Code);
    }

    [Fact]
    public async Task RealRecycleBeforeDbPublishFailureRecoversLifecycleWithoutSecondShellCall()
    {
        var root = await AddRootAsync("db-failure-recovery");
        var source = await AddAndScanAsync(root, "db-failure.mp4", 0x66);
        var failing = new SafeFileOperationService(_repository, _log, beforeProductionRecycleLifecyclePublish: (_, _) => throw new InvalidOperationException("test-only lifecycle publish failure"));
        var plan = await ConfirmRecyclePlanAsync(source.Media, failing);

        var recoveryRequired = await failing.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, recoveryRequired.State);
        Assert.False(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        var restarted = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin());
        var recovery = Assert.Single(await restarted.RecoverInterruptedProductionRecycleOperationsAsync(CancellationToken.None));
        Assert.Equal(FileOperationState.Recycled, recovery.State);
        var lifecycle = await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(lifecycle);
        Assert.Equal(plan.OperationId, lifecycle!.RecycleOperationId);
    }

    [Fact]
    public async Task RepeatedExecuteDoesNotAttemptSecondRecycle()
    {
        var root = await AddRootAsync("idempotency");
        var source = await AddAndScanAsync(root, "idempotency.mp4", 0x67);
        var plan = await ConfirmRecyclePlanAsync(source.Media);
        var recycled = await _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None));

        Assert.Equal(FileOperationState.Recycled, recycled.State);
        Assert.False(File.Exists(source.Path));
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Single(audit, item => item.EventName == "RecycleLifecycleDbPublished");
    }

    [Fact]
    public async Task PermanentDeleteIsTypeSpecificAndRecycleIsNotGenericDelete()
    {
        var root = await AddRootAsync("permanent-type-specific");
        var source = await AddAndScanAsync(root, "permanent-type-specific.mp4", 0x68);
        var permanent = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, "isolated capability boundary; Recycle and PermanentDelete remain distinct operation types", SourceValidationStrength.StrongFullHash, CancellationToken.None);

        var dry = await _operations.RunDryRunAsync(permanent.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.Equal("PREFLIGHT_PERMANENT_DELETE_READY", dry.Preflight.Code);
        Assert.True(dry.Plan.ProductionPermanentDeleteCapability);
        Assert.False(dry.Plan.ProductionRecycleCapability);
        Assert.Equal(1, dry.Plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, dry.Plan.ValidationStrength);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task RealRecyclePreservesManualHumanTagAndReviewHistory()
    {
        var root = await AddRootAsync("manual-history");
        var source = await AddAndScanAsync(root, "manual-history.mp4", 0x69);
        var tag = await _repository.AddManualMediaTagAsync(source.Media.MediaFileId, "keep-context", "human note must survive recycle", CancellationToken.None);
        var before = await _repository.GetManualMediaTagsAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Contains(before, item => item.ManualMediaTagId == tag.ManualMediaTagId && item.Note == "human note must survive recycle");
        var plan = await ConfirmRecyclePlanAsync(source.Media);

        var recycled = await _operations.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.Recycled, recycled.State);
        var after = await _repository.GetManualMediaTagsAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Contains(after, item => item.ManualMediaTagId == tag.ManualMediaTagId && item.Note == "human note must survive recycle");
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "RecycleLifecycleDbPublished");
    }

    [Fact]
    public async Task ShellOperationFailureDoesNotDeleteOrPublishRecycleLifecycle()
    {
        var root = await AddRootAsync("shell-failure");
        var source = await AddAndScanAsync(root, "shell-failure.mp4", 0x6A);
        var failing = new SafeFileOperationService(_repository, _log, windowsRecycleBin: new StubRecycleBin(WindowsRecycleBinResult.Failure("RECYCLE_SHELL_TEST_FAILURE", "test-only shell failure")));
        var plan = await ConfirmRecyclePlanAsync(source.Media, failing);

        var failed = await failing.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecycleFailed, failed.State);
        Assert.True(File.Exists(source.Path));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task SourceMissingBeforeDryRunBlocksRecycleWithoutShellMutation()
    {
        var root = await AddRootAsync("source-missing");
        var source = await AddAndScanAsync(root, "source-missing.mp4", 0x6B);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "isolated source-missing test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
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
    public async Task SourceLockBlocksRecyclePreflightWithoutShellMutation()
    {
        var root = await AddRootAsync("source-lock");
        var source = await AddAndScanAsync(root, "source-lock.mp4", 0x6C);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "isolated lock test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        await using var lockStream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.None);

        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceUnavailable, dry.Plan.State);
        Assert.Equal("SOURCE_READ_UNAVAILABLE", dry.Preflight.Code);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task ActualNtfsAclReadDenyBlocksRecyclePreflightAndPreservesSource()
    {
        var root = await AddRootAsync("acl-read-deny");
        var source = await AddAndScanAsync(root, "acl-read-deny.mp4", 0x6D);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "isolated NTFS ACL deny test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var originalAcl = AddFileReadDeny(source.Path);
        try
        {
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.AccessDenied, dry.Plan.State);
            Assert.Equal("MEDIA_ACCESS_DENIED", dry.Preflight.Code);
            Assert.True(File.Exists(source.Path));
            Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
        }
        finally
        {
            new FileInfo(source.Path).SetAccessControl(originalAcl);
        }
    }

    [Fact]
    public async Task ReparsePointEscapeAfterPlanBlocksRecycleAndPreservesExternalTarget()
    {
        var root = await AddRootAsync("reparse-escape");
        var linkedDirectory = Path.Combine(root.ConfiguredPath, "linked");
        Directory.CreateDirectory(linkedDirectory);
        var source = await AddAndScanAsync(root, Path.Combine("linked", "reparse-escape.mp4"), 0x6E);
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "isolated reparse escape test", SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var outsideDirectory = Path.Combine(_workspace, "outside-reparse", "target-directory");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideDirectory)!);
        Directory.Move(linkedDirectory, outsideDirectory);
        await CreateDirectoryJunctionAsync(linkedDirectory, outsideDirectory);

        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ValidationFailed, dry.Plan.State);
        Assert.Equal("SOURCE_REPARSE_ANCESTOR", dry.Preflight.Code);
        Assert.True(File.Exists(Path.Combine(outsideDirectory, "reparse-escape.mp4")));
        Assert.Null(await _repository.GetMediaRecycleLifecycleAsync(source.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task IsolatedRealRecyclePerformanceCapturesPlanHashShellObservationDbAndRestartInspection()
    {
        var performancePath = Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_PHASE64_PERFORMANCE_PATH");
        var root = await AddRootAsync("performance");
        var path = Path.Combine(root.ConfiguredPath, "performance.mp4");
        var bytes = new byte[4 * 1024 * 1024];
        Array.Fill(bytes, (byte)0x7A);
        await File.WriteAllBytesAsync(path, bytes);
        await RescanAsync(root);
        var listed = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, "performance.mp4", 0, 10, CancellationToken.None)).Items);
        var media = (await _repository.GetMediaFileAsync(listed.MediaFileId, CancellationToken.None))!;
        ProductionRecycleExecutionTiming? timing = null;
        var measured = new SafeFileOperationService(_repository, _log, productionRecycleTimingObserver: value => timing = value);
        var planTimer = System.Diagnostics.Stopwatch.StartNew();
        var plan = await PrepareRecyclePlanAsync(media, measured);
        planTimer.Stop();
        await measured.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await measured.ConfirmProductionRecycleExecutionAsync(plan.OperationId, CancellationToken.None);
        var recycled = await measured.ExecuteProductionRecycleAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Recycled, recycled.State);
        Assert.NotNull(timing);
        var recoveryTimer = System.Diagnostics.Stopwatch.StartNew();
        var recovery = await new SafeFileOperationService(_repository, _log, windowsRecycleBin: new ThrowingRecycleBin()).RecoverInterruptedProductionRecycleOperationsAsync(CancellationToken.None);
        recoveryTimer.Stop();
        Assert.Empty(recovery);
        if (!string.IsNullOrWhiteSpace(performancePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(performancePath)!);
            var report = new
            {
                SchemaVersion = 1,
                TestName = "phase64-isolated-real-windows-recycle-performance",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Scope = "One 4 MiB test-generated isolated C: file. This is not a cold-cache or sustained HDD benchmark.",
                SourceSizeBytes = new FileInfo(path).Exists ? (long?)new FileInfo(path).Length : 4L * 1024L * 1024L,
                PlanAndFullSha256Milliseconds = planTimer.ElapsedMilliseconds,
                timing!.FinalRevalidationMilliseconds,
                timing.RecycleShellMilliseconds,
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
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(seed, 4096).ToArray());
        await RescanAsync(root);
        var media = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, name, 0, 10, CancellationToken.None)).Items);
        return ((await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!, path);
    }

    private async Task<FileOperationPlan> PrepareRecyclePlanAsync(MediaFile media, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await service.CreatePlanAsync(media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "Explicit isolated media-list selection; review, AI, and preferred state are not filesystem authorization.", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await service.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.True(dry.Plan.ProductionRecycleCapability);
        Assert.False(dry.Plan.ProductionSameVolumeCapability);
        Assert.False(dry.Plan.ProductionCrossVolumeMoveCapability);
        Assert.Equal(SourceValidationStrength.StrongFullHash, dry.Plan.ValidationStrength);
        Assert.False(string.IsNullOrWhiteSpace(dry.Plan.SourceFullSha256));
        Assert.True(dry.Preflight.FullHashMatches);
        return dry.Plan;
    }

    private async Task<FileOperationPlan> ConfirmRecyclePlanAsync(MediaFile media, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await PrepareRecyclePlanAsync(media, service);
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

    private static async Task RestoreExactlyOneIsolatedRecycleBinItemAsync(string expectedSourcePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Recycle Bin restore test requires Windows.");
        var expectedDirectory = Path.GetDirectoryName(expectedSourcePath)!;
        var expectedName = Path.GetFileName(expectedSourcePath);
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application COM class is unavailable.");
        var shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Shell.Application activation failed.");
        try
        {
            var recycleBin = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [10], null, System.Globalization.CultureInfo.InvariantCulture, null) ?? throw new InvalidOperationException("Recycle Bin shell namespace is unavailable.");
            var items = recycleBin.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, recycleBin, null, null, System.Globalization.CultureInfo.InvariantCulture, null) as System.Collections.IEnumerable ?? throw new InvalidOperationException("Recycle Bin items enumeration is unavailable.");
            object? match = null;
            foreach (var item in items)
            {
                if (item is null) continue;
                var itemType = item.GetType();
                var name = itemType.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null, null, System.Globalization.CultureInfo.InvariantCulture, null) as string;
                var deletedFrom = itemType.InvokeMember("ExtendedProperty", System.Reflection.BindingFlags.InvokeMethod, null, item, ["System.Recycle.DeletedFrom"], null, System.Globalization.CultureInfo.InvariantCulture, null) as string;
                if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) && string.Equals(deletedFrom, expectedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    if (match is not null) throw new Xunit.Sdk.XunitException("More than one Recycle Bin item matched the isolated test source; refusing to restore.");
                    match = item;
                }
            }
            if (match is null) throw new Xunit.Sdk.XunitException("The exact isolated test-generated Recycle Bin item was not found; restore was not attempted.");
            match.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, match, ["undelete"], null, System.Globalization.CultureInfo.InvariantCulture, null);
            for (var attempt = 0; attempt < 30 && !File.Exists(expectedSourcePath); attempt++) await Task.Delay(200);
            Assert.True(File.Exists(expectedSourcePath), "Windows Shell Restore did not re-create the exact isolated source path within 6 seconds.");
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static async Task CreateDirectoryJunctionAsync(string linkPath, string targetPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
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
            => throw new Xunit.Sdk.XunitException("Recovery must never invoke the Windows Recycle Bin adapter.");
    }
}
