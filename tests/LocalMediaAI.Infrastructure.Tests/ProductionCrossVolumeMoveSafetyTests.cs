using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// Phase 6.3 uses normal test-generated library roots on two physically distinct Windows volumes.
/// It never references the user Original Media corpus and it deliberately does not create a fixture marker.
/// </summary>
public sealed class ProductionCrossVolumeMoveSafetyTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly string _appRoot = Path.Combine(Path.GetTempPath(), "LocalMediaAIProductionCrossVolumeTests", Guid.NewGuid().ToString("N"));
    private readonly string _sourceWorkspace;
    private readonly string _destinationWorkspace;
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SafeFileOperationService _operations = null!;
    private bool _disposed;

    public ProductionCrossVolumeMoveSafetyTests()
    {
        _sourceWorkspace = Path.Combine("E:\\Manus\\LocalMediaAI-Phase63-Isolated", _testId, "source");
        _destinationWorkspace = Path.Combine("C:\\Manus\\LocalMediaAI-Phase63-Isolated", _testId, "destination");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_appRoot);
        await File.WriteAllTextAsync(Path.Combine(_appRoot, "portable.flag"), string.Empty);
        Directory.CreateDirectory(_sourceWorkspace);
        Directory.CreateDirectory(_destinationWorkspace);
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
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryEventuallyAsync(_appRoot);
            await DeleteDirectoryEventuallyAsync(Path.Combine("E:\\Manus\\LocalMediaAI-Phase63-Isolated", _testId));
            await DeleteDirectoryEventuallyAsync(Path.Combine("C:\\Manus\\LocalMediaAI-Phase63-Isolated", _testId));
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
    public async Task RealDifferentVolumeCrossVolumeMoveCopiesVerifiesRemovesSourceAndPublishesStableIds()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("success", 0x31);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);

        Assert.True(plan.ProductionCrossVolumeMoveCapability);
        Assert.False(plan.ProductionSameVolumeCapability);
        Assert.Equal(FileOperationType.Move, plan.OperationType);
        Assert.Equal(SourceValidationStrength.StrongFullHash, plan.ValidationStrength);
        Assert.NotNull(plan.SourceFullSha256);
        Assert.NotNull(plan.DestinationLibraryRootId);
        Assert.NotNull(plan.DestinationVolumeIdentity);
        Assert.NotNull(plan.TemporaryDestinationPath);
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
        Assert.NotEqual(plan.SourceVolumeIdentity, plan.DestinationVolumeIdentity);
        Assert.False(FileOperationPolicy.CanExecute(plan, false, out var beforeCode, out _));
        Assert.Equal("DRY_RUN_ACKNOWLEDGEMENT_REQUIRED", beforeCode);

        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, acknowledged.State);
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var acknowledgementCode, out _));
        Assert.Equal("FINAL_EXECUTION_CONFIRMATION_REQUIRED", acknowledgementCode);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));

        var confirmed = await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.FinalConfirmationRequired, confirmed.State);
        Assert.Equal(CrossVolumeMoveStage.PreflightReady, confirmed.CrossVolumeMoveStage);
        Assert.NotNull(confirmed.FinalExecutionConfirmedAt);
        Assert.True(FileOperationPolicy.CanExecute(confirmed, false, out var confirmedCode, out _));
        Assert.Equal("PRODUCTION_CROSS_VOLUME_MOVE_EXECUTION_ALLOWED", confirmedCode);

        var completed = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.Equal(CrossVolumeMoveStage.DbPublished, completed.CrossVolumeMoveStage);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
        Assert.Equal(await FileHash.Sha256Async(fixture.DestinationPath, CancellationToken.None), plan.SourceFullSha256);

        Assert.NotEqual(plan.SourceVolumeIdentity, plan.DestinationVolumeIdentity);
        Assert.Equal("C:\\", Path.GetPathRoot(fixture.DestinationPath));
        Assert.Equal("E:\\", Path.GetPathRoot(fixture.SourcePath));
        var current = await _repository.GetMediaFileAsync(fixture.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(fixture.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(fixture.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(plan.DestinationLibraryRootId, current.LibraryRootId);
        Assert.Equal(PathPolicy.NormalizeForIdentity(fixture.DestinationPath), current.NormalizedPath);
        Assert.Equal(plan.SourceFullSha256, current.FullSha256);
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "FinalCrossVolumeMoveExecutionConfirmed");
        Assert.Contains(audit, item => item.EventName == "CrossVolumeDbPublished");
        Assert.Contains(audit, item => item.EventName == "ExecutionRecorded");
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TemporaryHashMismatchPreservesSourceAndTempForRecoveryInspection()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("temp-hash-mismatch", 0x41);
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: async (plan, stage, callbackCancellationToken) =>
        {
            if (stage == CrossVolumeMoveStage.TempCopied)
                await File.AppendAllTextAsync(plan.TemporaryDestinationPath!, "tamper", callbackCancellationToken);
        });
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        var result = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, result.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(plan.TemporaryDestinationPath));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.NotEqual(plan.SourceFullSha256, await FileHash.Sha256Async(plan.TemporaryDestinationPath!, CancellationToken.None));
    }

    [Fact]
    public async Task SourceChangedAfterVerifiedCopyPreservesBothSourceAndFinalDestinationWithoutRemoval()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("source-change-after-copy", 0x51);
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: async (plan, stage, callbackCancellationToken) =>
        {
            if (stage == CrossVolumeMoveStage.FinalPublished)
                await File.AppendAllTextAsync(plan.SourcePath, "changed-after-copy", callbackCancellationToken);
        });
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        var result = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceChangedAfterCopy, result.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
        Assert.Equal(plan.SourceFullSha256, await FileHash.Sha256Async(fixture.DestinationPath, CancellationToken.None));
        Assert.NotEqual(plan.SourceFullSha256, await FileHash.Sha256Async(fixture.SourcePath, CancellationToken.None));
    }

    [Fact]
    public async Task DbPublishFailureAfterSourceRemovalRecoversOnlyByVerifyingFinalDestination()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("db-recovery", 0x61);
        var injected = new SafeFileOperationService(_repository, _log, beforeProductionDbPublish: (_, _) => Task.FromException(new IOException("injected db publish failure")));
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, injected);
        await injected.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await injected.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        var failed = await injected.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.RecoveryRequired, failed.State);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));

        var recovered = await new SafeFileOperationService(_repository, _log).RecoverInterruptedProductionCrossVolumeMoveOperationsAsync(CancellationToken.None);
        var result = Assert.Single(recovered);
        Assert.Equal(FileOperationState.Completed, result.State);
        var current = await _repository.GetMediaFileAsync(fixture.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(fixture.Media.MediaContentId, current!.MediaContentId);
        Assert.Equal(PathPolicy.NormalizeForIdentity(fixture.DestinationPath), current.NormalizedPath);
    }

    [Fact]
    public async Task ExistingTemporaryArtifactBlocksDryRunWithoutOverwriteOrCopy()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("temp-conflict", 0x71);
        var rawPlan = await _operations.CreatePlanAsync(fixture.Media.MediaFileId, FileOperationType.Move, FileOperationMode.ProductionDryRun, fixture.DestinationPath, null, "isolated production-like cross-volume test; no human decision grants authorization", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        Assert.True(rawPlan.ProductionCrossVolumeMoveCapability);
        await File.WriteAllTextAsync(rawPlan.TemporaryDestinationPath!, "unrelated operation artifact");

        var dry = await _operations.RunDryRunAsync(rawPlan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.DestinationConflict, dry.Plan.State);
        Assert.Equal("TEMP_DESTINATION_CONFLICT", dry.Preflight.Code);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(rawPlan.TemporaryDestinationPath));
        Assert.False(File.Exists(fixture.DestinationPath));
    }

    [Fact]
    public async Task CrossVolumeMoveRequiresFinalConfirmationAndRemainsTypeSpecificWhenRecycleExists()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("confirmation-and-negative", 0x81);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None));
        var stillBlocked = await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None);
        Assert.NotNull(stillBlocked);
        Assert.Equal(FileOperationState.ConfirmationRequired, stillBlocked!.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));

        var recycle = await _operations.CreatePlanAsync(fixture.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, "review context is not filesystem authority", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var recycleDry = await _operations.RunDryRunAsync(recycle.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, recycleDry.Plan.State);
        Assert.True(recycleDry.Plan.ProductionRecycleCapability);
        Assert.False(recycleDry.Plan.ProductionCrossVolumeMoveCapability);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionCrossVolumeMoveAsync(recycle.OperationId, CancellationToken.None));
    }

    [Fact]
    public async Task SourceModifiedBeforeCopyBlocksCrossVolumeExecutionBeforeAnyDestinationArtifact()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("source-changed-before-copy", 0x85);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        await File.AppendAllTextAsync(fixture.SourcePath, "changed-before-copy");

        var result = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceChanged, result.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
    }

    [Fact]
    public async Task SourceMissingBeforeCopyBlocksCrossVolumeExecutionBeforeAnyDestinationArtifact()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("source-missing-before-copy", 0x86);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        File.Delete(fixture.SourcePath);

        var result = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceMissing, result.State);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
    }

    [Fact]
    public async Task LockedSourceBlocksCrossVolumePreflightWithoutCopyOrRemoval()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("locked-source", 0x87);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        await using var sourceLock = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceUnavailable, result.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
    }

    [Fact]
    public async Task CopyInterruptionPreservesSourceAndDoesNotTreatPartialStateAsCompleted()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("copy-interruption", 0x88);
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: (_, stage, _) =>
            stage == CrossVolumeMoveStage.Copying ? Task.FromException(new OperationCanceledException("injected copy interruption")) : Task.CompletedTask);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None));
        var persisted = await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.RecoveryRequired, persisted!.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
    }

    [Fact]
    public async Task FinalDestinationConflictAfterVerifiedTemporaryCopyNeverOverwrites()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("final-conflict-after-copy", 0x89);
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: async (plan, stage, cancellationToken) =>
        {
            if (stage == CrossVolumeMoveStage.TempHashVerified)
                await File.WriteAllTextAsync(plan.DestinationPath!, "competing destination", cancellationToken);
        });
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        var result = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.DestinationConflict, result.State);
        Assert.Equal(CrossVolumeMoveStage.TempHashVerified, result.CrossVolumeMoveStage);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(plan.TemporaryDestinationPath));
        Assert.Equal("competing destination", await File.ReadAllTextAsync(fixture.DestinationPath));
    }

    [Fact]
    public async Task RestartAfterFinalPublishPreservesVerifiedSourceAndDestinationWithoutAutomaticRemoval()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("restart-final-published", 0x8A);
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: (_, stage, _) =>
            stage == CrossVolumeMoveStage.FinalPublished ? Task.FromException(new IOException("injected crash after final publish")) : Task.CompletedTask);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);

        var interrupted = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.RecoveryRequired, interrupted.State);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));

        var recovered = await new SafeFileOperationService(_repository, _log).RecoverInterruptedProductionCrossVolumeMoveOperationsAsync(CancellationToken.None);
        var result = Assert.Single(recovered);
        Assert.Equal(FileOperationState.SourceChangedAfterCopy, result.State);
        Assert.Equal("RECOVERY_SOURCE_AND_FINAL_PRESERVED", result.Code);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));
    }

    [Fact]
    public async Task CompletedCrossVolumeOperationCannotBeExecutedTwiceOrCreateDuplicateDestination()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("idempotent-completed", 0x8B);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        var completed = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None));
        var current = await _repository.GetMediaFileAsync(fixture.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(fixture.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(fixture.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(fixture.DestinationPath));
        Assert.Single(Directory.EnumerateFiles(fixture.DestinationRoot.ConfiguredPath, "*.mp4", SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(plan.TemporaryDestinationPath));
    }

    [Fact]
    public async Task IsolatedDifferentVolumePerformanceMeasurementCapturesRequiredTransactionStages()
    {
        var performancePath = Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_PHASE63_PERFORMANCE_PATH");
        var measurements = new List<object>();
        foreach (var scenario in new[] { (Name: "small-4MiB", SizeBytes: 4 * 1024 * 1024, Seed: (byte)0xC1), (Name: "medium-32MiB", SizeBytes: 32 * 1024 * 1024, Seed: (byte)0xD1) })
        {
            var fixture = await CreateCrossVolumeFixtureAsync($"performance-{scenario.Name}", scenario.Seed, scenario.SizeBytes);
            var timings = new List<ProductionCrossVolumeMoveExecutionTiming>();
            var service = new SafeFileOperationService(_repository, _log, crossVolumeTimingObserver: timings.Add);
            var planTimer = Stopwatch.StartNew();
            var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
            planTimer.Stop();
            await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
            await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
            var completed = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
            var timing = Assert.Single(timings);
            Assert.Equal(FileOperationState.Completed, completed.State);
            Assert.False(File.Exists(fixture.SourcePath));
            Assert.True(File.Exists(fixture.DestinationPath));
            var recoveryTimer = Stopwatch.StartNew();
            var recovery = await new SafeFileOperationService(_repository, _log).RecoverInterruptedProductionCrossVolumeMoveOperationsAsync(CancellationToken.None);
            recoveryTimer.Stop();
            Assert.Empty(recovery);
            var copySeconds = Math.Max(0.001, timing.CopyMilliseconds / 1000d);
            measurements.Add(new
            {
                scenario.Name,
                SourcePathRoot = Path.GetPathRoot(fixture.SourcePath),
                DestinationPathRoot = Path.GetPathRoot(fixture.DestinationPath),
                SourceVolumeIdentity = plan.SourceVolumeIdentity,
                DestinationVolumeIdentity = plan.DestinationVolumeIdentity,
                SourceSizeBytes = scenario.SizeBytes,
                PlanAndFullHashMilliseconds = planTimer.ElapsedMilliseconds,
                timing.CopyMilliseconds,
                CopyThroughputMiBPerSecond = Math.Round(scenario.SizeBytes / 1024d / 1024d / copySeconds, 3),
                timing.TempHashVerificationMilliseconds,
                timing.FinalPublishMilliseconds,
                timing.SourceRemovalMilliseconds,
                timing.DbPublishMilliseconds,
                timing.TotalMilliseconds,
                RestartRecoveryInspectionMilliseconds = recoveryTimer.ElapsedMilliseconds,
                RecoveryObservationCount = recovery.Count
            });
        }
        if (!string.IsNullOrWhiteSpace(performancePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(performancePath)!);
            await File.WriteAllTextAsync(performancePath, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                TestName = "phase63-isolated-real-different-volume-performance",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                SourceVolumeRoot = "E:\\",
                DestinationVolumeRoot = "C:\\",
                Measurements = measurements,
                Scope = "Isolated production-like files only; this is not a cold-cache sustained HDD benchmark."
            }, IndentedJsonOptions));
        }
    }

    [Fact]
    public async Task ActualNtfsAclSourceReadDenyBlocksCrossVolumePreflightAndPreservesSource()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("acl-source-read-deny", 0x91);
        var plan = await _operations.CreatePlanAsync(fixture.Media.MediaFileId, FileOperationType.Move, FileOperationMode.ProductionDryRun, fixture.DestinationPath, null, "isolated ACL verification; no human decision grants authorization", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var deny = AddFileDenyRule(fixture.SourcePath, FileSystemRights.ReadData);
        try
        {
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.AccessDenied, dry.Plan.State);
            Assert.Equal("MEDIA_ACCESS_DENIED", dry.Preflight.Code);
            Assert.True(File.Exists(fixture.SourcePath));
            Assert.False(File.Exists(fixture.DestinationPath));
        }
        finally
        {
            RemoveFileDenyRule(fixture.SourcePath, deny);
        }
    }

    [Fact]
    public async Task ActualNtfsAclDestinationWriteDenyBlocksCopyAndPreservesSource()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("acl-destination-write-deny", 0xA1);
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        var deny = AddDirectoryDenyRule(fixture.DestinationRoot.ConfiguredPath, FileSystemRights.CreateFiles);
        try
        {
            var result = await _operations.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.RecoveryRequired, result.State);
            Assert.True(File.Exists(fixture.SourcePath));
            Assert.False(File.Exists(fixture.DestinationPath));
            Assert.False(File.Exists(plan.TemporaryDestinationPath));
        }
        finally
        {
            RemoveDirectoryDenyRule(fixture.DestinationRoot.ConfiguredPath, deny);
        }
    }

    [Fact]
    public async Task ActualNtfsAclSourceRemovalDenyPreservesVerifiedDestinationAndSource()
    {
        var fixture = await CreateCrossVolumeFixtureAsync("acl-source-removal-deny", 0xB1);
        FileSystemAccessRule? deny = null;
        var service = new SafeFileOperationService(_repository, _log, beforeCrossVolumeStage: (plan, stage, _) =>
        {
            if (stage == CrossVolumeMoveStage.SourceRemovalPending)
                deny = AddFileDenyRule(plan.SourcePath, FileSystemRights.Delete);
            return Task.CompletedTask;
        });
        var plan = await PrepareCrossVolumePlanAsync(fixture.Media, fixture.DestinationPath, service);
        await service.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await service.ConfirmProductionCrossVolumeMoveExecutionAsync(plan.OperationId, CancellationToken.None);
        try
        {
            var result = await service.ExecuteProductionCrossVolumeMoveAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.SourceRemovalFailed, result.State);
            Assert.Equal(CrossVolumeMoveStage.FinalPublished, result.CrossVolumeMoveStage);
            Assert.True(File.Exists(fixture.SourcePath));
            Assert.True(File.Exists(fixture.DestinationPath));
            Assert.False(File.Exists(plan.TemporaryDestinationPath));
            Assert.Equal(plan.SourceFullSha256, await FileHash.Sha256Async(fixture.DestinationPath, CancellationToken.None));
        }
        finally
        {
            if (deny is not null) RemoveFileDenyRule(fixture.SourcePath, deny);
        }
    }

    private static FileSystemAccessRule AddFileDenyRule(string path, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(Environment.UserName, rights, AccessControlType.Deny);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return rule;
    }

    private static void RemoveFileDenyRule(string path, FileSystemAccessRule rule)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(security);
    }

    private static FileSystemAccessRule AddDirectoryDenyRule(string path, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(Environment.UserName, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return rule;
    }

    private static void RemoveDirectoryDenyRule(string path, FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(security);
    }

    private async Task<(LibraryRoot SourceRoot, LibraryRoot DestinationRoot, MediaFile Media, string SourcePath, string DestinationPath)> CreateCrossVolumeFixtureAsync(string name, byte seed, int sizeBytes = 512 * 1024)
    {
        var sourceRootPath = Path.Combine(_sourceWorkspace, name);
        var destinationRootPath = Path.Combine(_destinationWorkspace, name);
        Directory.CreateDirectory(sourceRootPath);
        Directory.CreateDirectory(destinationRootPath);
        Assert.Equal("E:\\", Path.GetPathRoot(sourceRootPath));
        Assert.Equal("C:\\", Path.GetPathRoot(destinationRootPath));

        var sourceRoot = await _repository.AddOrGetLibraryRootAsync(sourceRootPath, CancellationToken.None);
        var destinationRoot = await _repository.AddOrGetLibraryRootAsync(destinationRootPath, CancellationToken.None);
        var sourcePath = Path.Combine(sourceRootPath, "source.mp4");
        var destinationPath = Path.Combine(destinationRootPath, "moved.mp4");
        var content = new byte[sizeBytes];
        Array.Fill(content, seed);
        await File.WriteAllBytesAsync(sourcePath, content);
        await RescanAsync(sourceRoot);
        var media = Assert.Single((await _repository.QueryMediaAsync(sourceRoot.LibraryRootId, "source.mp4", 0, 10, CancellationToken.None)).Items);
        var persisted = await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None);
        Assert.NotNull(persisted);
        return (sourceRoot, destinationRoot, persisted!, sourcePath, destinationPath);
    }

    private async Task<FileOperationPlan> PrepareCrossVolumePlanAsync(MediaFile media, string destinationPath, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await service.CreatePlanAsync(media.MediaFileId, FileOperationType.Move, FileOperationMode.ProductionDryRun, destinationPath, null, "isolated production-like cross-volume test; human review is not filesystem authority", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        Assert.True(plan.ProductionCrossVolumeMoveCapability);
        Assert.False(plan.ProductionSameVolumeCapability);
        var dry = await service.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.Equal(SourceValidationStrength.StrongFullHash, dry.Plan.ValidationStrength);
        Assert.True(dry.Preflight.CrossVolumeVerified);
        Assert.False(dry.Preflight.SameVolumeVerified);
        Assert.True(dry.Preflight.TemporaryDestinationAvailable);
        Assert.NotNull(dry.Preflight.RequiredDestinationFreeBytes);
        Assert.True(dry.Preflight.DestinationFreeBytes >= dry.Preflight.RequiredDestinationFreeBytes);
        var persisted = await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(dry.Preflight.DestinationFreeBytes, persisted!.DestinationFreeSpaceSnapshotBytes);
        return dry.Plan;
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

    private static async Task DeleteDirectoryEventuallyAsync(string path)
    {
        for (var attempt = 0; Directory.Exists(path) && attempt < 8; attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}
