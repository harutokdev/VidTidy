using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>
/// Phase 6.2 tests create normal LibraryRoot records in a fresh disposable workspace. They intentionally
/// do not create the Phase 6.1 fixture marker, and never use the user Original Media corpus.
/// </summary>
public sealed class ProductionSameVolumeFileOperationTests : IAsyncLifetime, IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions PerformanceJsonOptions = new() { WriteIndented = true };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIProductionSameVolumeTests", Guid.NewGuid().ToString("N"));
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
    public async Task ProductionRenameUsesDryRunAndDistinctFinalConfirmationThenPublishesStableIds()
    {
        var fixture = await CreateProductionLikeFixtureAsync("rename");
        var source = await AddAndScanAsync(fixture.Root, "before.mp4", 11);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "after.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);

        Assert.True(plan.ProductionSameVolumeCapability);
        Assert.False(FileOperationPolicy.CanExecute(plan, false, out var beforeCode, out _));
        Assert.Equal("DRY_RUN_ACKNOWLEDGEMENT_REQUIRED", beforeCode);
        var acknowledged = await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, acknowledged.State);
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var acknowledgementCode, out _));
        Assert.Equal("FINAL_EXECUTION_CONFIRMATION_REQUIRED", acknowledgementCode);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));

        var final = await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.FinalConfirmationRequired, final.State);
        Assert.NotNull(final.FinalExecutionConfirmedAt);
        Assert.True(FileOperationPolicy.CanExecute(final, false, out var finalCode, out _));
        Assert.Equal("PRODUCTION_SAME_VOLUME_EXECUTION_ALLOWED", finalCode);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));

        var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(destination));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(source.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(source.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(PathPolicy.NormalizeForIdentity(destination), current.NormalizedPath);
        var visible = await _repository.QueryMediaAsync(fixture.Root.LibraryRootId, null, 0, 20, CancellationToken.None);
        Assert.Single(visible.Items);
        Assert.Equal(source.Media.MediaFileId, visible.Items[0].MediaFileId);
        Assert.Equal(destination, visible.Items[0].Path);
        Assert.DoesNotContain(visible.Items, item => item.Path == source.Path);
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "FinalExecutionConfirmed");
        Assert.Contains(audit, item => item.EventName == "ExecutionRecorded");
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProductionMoveUsesSameVolumeDestinationAndRetainsStableMediaFileId()
    {
        var fixture = await CreateProductionLikeFixtureAsync("move");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 22);
        var folder = Path.Combine(fixture.Root.ConfiguredPath, "organized");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "source.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Move, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);

        var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(destination));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(source.Media.MediaFileId, current!.MediaFileId);
        Assert.Equal(source.Media.MediaContentId, current.MediaContentId);
        Assert.Equal(destination, current.Path);
        var visible = await _repository.QueryMediaAsync(fixture.Root.LibraryRootId, null, 0, 20, CancellationToken.None);
        Assert.Single(visible.Items);
        Assert.Equal(destination, visible.Items[0].Path);
        Assert.DoesNotContain(visible.Items, item => item.Path == source.Path);
    }

    [Fact]
    public async Task MoveBetweenActiveRootsChangesCurrentLibraryScopeMembership()
    {
        var fixture = await CreateProductionLikeFixtureAsync("between-roots");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 26);
        var otherDirectory = Path.Combine(_root, "between-roots-destination");
        Directory.CreateDirectory(otherDirectory);
        var otherRoot = await _repository.AddOrGetLibraryRootAsync(otherDirectory, CancellationToken.None);
        var destination = Path.Combine(otherDirectory, "source.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Move, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);

        var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.Empty((await _repository.QueryMediaAsync(fixture.Root.LibraryRootId, null, 0, 20, CancellationToken.None)).Items);
        var moved = Assert.Single((await _repository.QueryMediaAsync(otherRoot.LibraryRootId, null, 0, 20, CancellationToken.None)).Items);
        Assert.Equal(source.Media.MediaFileId, moved.MediaFileId);
        Assert.Equal(destination, moved.Path);
        var movedMedia = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Equal(source.Media.MediaContentId, movedMedia!.MediaContentId);
        Assert.Equal(otherRoot.LibraryRootId, movedMedia.LibraryRootId);
        var audit = await _repository.GetFileOperationAuditAsync(plan.OperationId, CancellationToken.None);
        Assert.Contains(audit, item => item.EventName == "SameVolumeCrossRootDbPublished");
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MoveBetweenRootsIsBlockedWithoutMutationWhenDestinationRootIsUnregisteredAfterPlanning()
    {
        var fixture = await CreateProductionLikeFixtureAsync("between-roots-unregistered");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 27);
        var otherDirectory = Path.Combine(_root, "between-roots-unregistered-destination");
        Directory.CreateDirectory(otherDirectory);
        var otherRoot = await _repository.AddOrGetLibraryRootAsync(otherDirectory, CancellationToken.None);
        var destination = Path.Combine(otherDirectory, "source.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Move, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        await _repository.UnregisterRootAsync(otherRoot.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);

        var blocked = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.ValidationFailed, blocked.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
        var current = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.Equal(fixture.Root.LibraryRootId, current!.LibraryRootId);
        Assert.Equal(source.Path, current.Path);
    }

    [Fact]
    public async Task SourceChangeAfterFinalConfirmationBlocksProductionExecutionWithoutMutation()
    {
        var fixture = await CreateProductionLikeFixtureAsync("source-change");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 33);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        await File.AppendAllBytesAsync(source.Path, [4, 5, 6]);

        var blocked = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceChanged, blocked.State);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
        Assert.Equal(source.Media.MediaFileId, Assert.Single((await _repository.QueryMediaAsync(fixture.Root.LibraryRootId, null, 0, 20, CancellationToken.None)).Items).MediaFileId);
    }

    [Fact]
    public async Task DestinationConflictAfterFinalConfirmationBlocksOverwriteAndKeepsBothFiles()
    {
        var fixture = await CreateProductionLikeFixtureAsync("destination-conflict");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 44);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        await File.WriteAllBytesAsync(destination, Enumerable.Repeat((byte)99, 512).ToArray());

        var blocked = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.DestinationConflict, blocked.State);
        Assert.True(File.Exists(source.Path));
        Assert.Equal(512, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task ExecuteIsIdempotentAndSecondInvocationDoesNotMutateCompletedPlan()
    {
        var fixture = await CreateProductionLikeFixtureAsync("idempotency");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 55);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);
        var snapshot = await SnapshotAsync(destination);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None));

        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.False(File.Exists(source.Path));
        Assert.Equal(snapshot, await SnapshotAsync(destination));
    }

    [Fact]
    public async Task FilesystemSuccessBeforeDbPublishFailureIsRecoveryRequiredAndRestartPublishesVerifiedDestination()
    {
        var fixture = await CreateProductionLikeFixtureAsync("db-publish-failure");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 66);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var failing = new SafeFileOperationService(_repository, _log, (_, _) => throw new InvalidOperationException("test-only DB publish boundary failure"));
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination, failing);
        await failing.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await failing.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);

        var recoveryRequired = await failing.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.RecoveryRequired, recoveryRequired.State);
        Assert.False(File.Exists(source.Path));
        Assert.True(File.Exists(destination));
        var preRecovery = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(preRecovery);
        Assert.Equal(source.Path, preRecovery!.Path);

        var restarted = new SafeFileOperationService(_repository, _log);
        var recovery = Assert.Single(await restarted.RecoverInterruptedProductionSameVolumeOperationsAsync(CancellationToken.None));

        Assert.Equal(FileOperationState.Completed, recovery.State);
        var published = await _repository.GetMediaFileAsync(source.Media.MediaFileId, CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal(destination, published!.Path);
        Assert.Equal(source.Media.MediaContentId, published.MediaContentId);
    }

    [Fact]
    public async Task PermanentDeleteDryRunAndInvalidDestinationRemainNonMutating()
    {
        var fixture = await CreateProductionLikeFixtureAsync("negative-operations");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 77);
        var before = await SnapshotAsync(source.Path);
        var deletePlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, null, SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var deleteDry = await _operations.RunDryRunAsync(deletePlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, deleteDry.Plan.State);
        Assert.Equal("PREFLIGHT_PERMANENT_DELETE_READY", deleteDry.Preflight.Code);
        Assert.True(deleteDry.Plan.ProductionPermanentDeleteCapability);
        Assert.False(deleteDry.Plan.ProductionRecycleCapability);
        Assert.Equal(1, deleteDry.Plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, deleteDry.Plan.ValidationStrength);

        var outsideRoot = Path.Combine(_root, "outside-authorized-library", "target.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideRoot)!);
        var outsidePlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, outsideRoot, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var outsideDry = await _operations.RunDryRunAsync(outsidePlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ValidationFailed, outsideDry.Plan.State);
        Assert.Equal("INVALID_DESTINATION", outsideDry.Preflight.Code);
        Assert.Equal(before, await SnapshotAsync(source.Path));
        Assert.False(File.Exists(outsideRoot));
    }

    [Fact]
    public async Task ReservedFilenameAndCaseOnlyRenameAreRejectedBeforeFinalConfirmation()
    {
        var fixture = await CreateProductionLikeFixtureAsync("filename-rules");
        var source = await AddAndScanAsync(fixture.Root, "Source.mp4", 88);
        var reservedPlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, Path.Combine(fixture.Root.ConfiguredPath, "CON.mp4"), null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var reserved = await _operations.RunDryRunAsync(reservedPlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ValidationFailed, reserved.Plan.State);
        Assert.Equal("RESERVED_WINDOWS_FILENAME", reserved.Preflight.Code);

        var caseOnlyPlan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, Path.Combine(fixture.Root.ConfiguredPath, "source.mp4"), null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var caseOnly = await _operations.RunDryRunAsync(caseOnlyPlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ValidationFailed, caseOnly.Plan.State);
        Assert.Equal("SOURCE_DESTINATION_EQUAL_OR_CASE_ONLY", caseOnly.Preflight.Code);
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task RestartBeforeFinalConfirmationPreservesPlanAndCannotInferExecution()
    {
        var fixture = await CreateProductionLikeFixtureAsync("restart-before-final");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 99);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);

        var restarted = new SafeFileOperationService(_repository, _log);
        Assert.Empty(await restarted.RecoverInterruptedProductionSameVolumeOperationsAsync(CancellationToken.None));
        var preserved = await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None);
        Assert.NotNull(preserved);
        Assert.Equal(FileOperationState.ConfirmationRequired, preserved!.State);
        Assert.True(preserved.UserAcknowledged);
        Assert.Null(preserved.FinalExecutionConfirmedAt);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task SourceMissingAfterFinalConfirmationBlocksExecutionWithoutRecreation()
    {
        var fixture = await CreateProductionLikeFixtureAsync("source-missing");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 101);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        File.Delete(source.Path);

        var blocked = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);

        Assert.Equal(FileOperationState.SourceMissing, blocked.State);
        Assert.False(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task LockedSourceIsClassifiedBeforeFinalConfirmationWithoutFilesystemMutation()
    {
        var fixture = await CreateProductionLikeFixtureAsync("locked-source");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 102);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        await using (var handle = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.SourceUnavailable, dry.Plan.State);
            Assert.Equal("SOURCE_READ_UNAVAILABLE", dry.Preflight.Code);
        }
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ReadOnlySourceSameVolumeRenameCompletesWithDestinationIdentityVerification()
    {
        var fixture = await CreateProductionLikeFixtureAsync("read-only-source");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 103);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        File.SetAttributes(source.Path, File.GetAttributes(source.Path) | FileAttributes.ReadOnly);
        try
        {
            var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
            await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
            await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
            var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.Completed, completed.State);
            Assert.False(File.Exists(source.Path));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(source.Path)) File.SetAttributes(source.Path, FileAttributes.Normal);
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task LongPathNearPracticalBoundaryRemainsExplicitAndSameVolume()
    {
        var fixture = await CreateProductionLikeFixtureAsync("long-path");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 104);
        var destinationDirectory = fixture.Root.ConfiguredPath;
        while (Path.Combine(destinationDirectory, "renamed.mp4").Length < 245)
        {
            destinationDirectory = Path.Combine(destinationDirectory, "segment0123456789");
            Directory.CreateDirectory(destinationDirectory);
        }
        var destination = Path.Combine(destinationDirectory, "renamed.mp4");
        Assert.True(destination.Length is >= 245 and < 260);
        var plan = await PrepareProductionPlanAsync(source.Media, FileOperationType.Rename, destination);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        await _operations.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
        var completed = await _operations.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task JunctionEscapeIsRejectedByDestinationReparsePointGuard()
    {
        var fixture = await CreateProductionLikeFixtureAsync("junction-escape");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 105);
        var outside = Path.Combine(_root, "outside-authorized-target");
        var junction = Path.Combine(fixture.Root.ConfiguredPath, "junction-outside");
        Directory.CreateDirectory(outside);
        using (var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true } })
        {
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add("mklink");
            process.StartInfo.ArgumentList.Add("/J");
            process.StartInfo.ArgumentList.Add(junction);
            process.StartInfo.ArgumentList.Add(outside);
            Assert.True(process.Start());
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, Path.Combine(junction, "target.mp4"), null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ValidationFailed, dry.Plan.State);
        Assert.Equal("DESTINATION_REPARSE_POINT", dry.Preflight.Code);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(Path.Combine(outside, "target.mp4")));
    }

    [Fact]
    public async Task HumanReviewContextCannotBypassFinalConfirmationOrExecute()
    {
        var fixture = await CreateProductionLikeFixtureAsync("human-context");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 106);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, "exact:human-context", "ConfirmedDuplicate and Preferred are audit context only, never filesystem authorization.", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.False(FileOperationPolicy.CanExecute(dry.Plan, false, out var beforeAckCode, out _));
        Assert.Equal("DRY_RUN_ACKNOWLEDGEMENT_REQUIRED", beforeAckCode);
        await _operations.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
        var acknowledged = (await _repository.GetFileOperationPlanAsync(plan.OperationId, CancellationToken.None))!;
        Assert.False(FileOperationPolicy.CanExecute(acknowledged, false, out var beforeFinalCode, out _));
        Assert.Equal("FINAL_EXECUTION_CONFIRMATION_REQUIRED", beforeFinalCode);
        Assert.True(File.Exists(source.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RecycleAndPermanentDeleteRequireSeparateFinalConfirmationAndCrossVolumeOutsideRootIsRejected()
    {
        var fixture = await CreateProductionLikeFixtureAsync("type-specific-cross-volume");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 107);
        var recycle = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Recycle, FileOperationMode.ProductionDryRun, null, null, null, SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var recycleDry = await _operations.RunDryRunAsync(recycle.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, recycleDry.Plan.State);
        Assert.True(recycleDry.Plan.ProductionRecycleCapability);
        var permanentDelete = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.PermanentDelete, FileOperationMode.ProductionDryRun, null, null, null, SourceValidationStrength.StrongFullHash, CancellationToken.None);
        var permanentDeleteDry = await _operations.RunDryRunAsync(permanentDelete.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, permanentDeleteDry.Plan.State);
        Assert.Equal("PREFLIGHT_PERMANENT_DELETE_READY", permanentDeleteDry.Preflight.Code);
        Assert.True(permanentDeleteDry.Plan.ProductionPermanentDeleteCapability);
        Assert.False(permanentDeleteDry.Plan.ProductionRecycleCapability);
        Assert.Equal(1, permanentDeleteDry.Plan.SelectedMediaCount);
        Assert.Equal(SourceValidationStrength.StrongFullHash, permanentDeleteDry.Plan.ValidationStrength);
        var crossVolumeRoot = Path.GetPathRoot(fixture.Root.ConfiguredPath)!.StartsWith("E:", StringComparison.OrdinalIgnoreCase) ? "C:\\" : "E:\\";
        if (Directory.Exists(crossVolumeRoot))
        {
            var destination = Path.Combine(crossVolumeRoot, "LocalMediaAIPhase62CrossVolume", "target.mp4");
            var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.ValidationFailed, dry.Plan.State);
            Assert.Equal("INVALID_DESTINATION", dry.Preflight.Code);
        }
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task ActualNtfsAclDenyIsClassifiedAsAccessDeniedWithoutMutation()
    {
        var fixture = await CreateProductionLikeFixtureAsync("actual-acl-deny");
        var source = await AddAndScanAsync(fixture.Root, "source.mp4", 108);
        var destination = Path.Combine(fixture.Root.ConfiguredPath, "target.mp4");
        var plan = await _operations.CreatePlanAsync(source.Media.MediaFileId, FileOperationType.Rename, FileOperationMode.ProductionDryRun, destination, null, null, SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        try
        {
            await RunIcaclsAsync(source.Path, "/deny", $"{currentUser}:(R)");
            var dry = await _operations.RunDryRunAsync(plan.OperationId, CancellationToken.None);
            Assert.Equal(FileOperationState.AccessDenied, dry.Plan.State);
            Assert.Equal("MEDIA_ACCESS_DENIED", dry.Preflight.Code);
            Assert.True(File.Exists(source.Path));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(source.Path)) await RunIcaclsAsync(source.Path, "/remove:d", currentUser);
        }
    }

    [Fact]
    public async Task IsolatedProductionLikePerformanceRecordsPlanFinalRevalidationMoveRenameDbPublishAndRecovery()
    {
        var trials = new List<object>();
        foreach (var operationType in new[] { FileOperationType.Rename, FileOperationType.Move })
        {
            for (var trial = 1; trial <= 3; trial++)
            {
                var fixture = await CreateProductionLikeFixtureAsync($"performance-{operationType}-{trial}");
                var source = await AddAndScanAsync(fixture.Root, "source.mp4", (byte)(120 + trial));
                var destinationDirectory = operationType == FileOperationType.Move ? Path.Combine(fixture.Root.ConfiguredPath, "moved") : fixture.Root.ConfiguredPath;
                Directory.CreateDirectory(destinationDirectory);
                var destination = Path.Combine(destinationDirectory, $"target-{trial}.mp4");
                var timing = new List<ProductionSameVolumeExecutionTiming>();
                var timedService = new SafeFileOperationService(_repository, _log, productionTimingObserver: timing.Add);
                var planTimer = System.Diagnostics.Stopwatch.StartNew();
                var plan = await timedService.CreatePlanAsync(source.Media.MediaFileId, operationType, FileOperationMode.ProductionDryRun, destination, null, "Isolated performance measurement; this context is not authorization.", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
                var dry = await timedService.RunDryRunAsync(plan.OperationId, CancellationToken.None);
                planTimer.Stop();
                Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
                await timedService.AcknowledgeProductionDryRunAsync(plan.OperationId, CancellationToken.None);
                var finalTimer = System.Diagnostics.Stopwatch.StartNew();
                await timedService.ConfirmProductionSameVolumeExecutionAsync(plan.OperationId, CancellationToken.None);
                finalTimer.Stop();
                var completed = await timedService.ExecuteProductionSameVolumeAsync(plan.OperationId, CancellationToken.None);
                Assert.Equal(FileOperationState.Completed, completed.State);
                var execution = Assert.Single(timing);
                trials.Add(new
                {
                    Trial = trial,
                    OperationType = operationType.ToString(),
                    PlanAndDryRunMilliseconds = planTimer.ElapsedMilliseconds,
                    FinalRevalidationMilliseconds = finalTimer.ElapsedMilliseconds,
                    execution.FileSystemMoveMilliseconds,
                    execution.DestinationVerificationMilliseconds,
                    execution.DbPublishMilliseconds,
                    execution.TotalMilliseconds
                });
            }
        }

        var recoveryFixture = await CreateProductionLikeFixtureAsync("performance-recovery");
        var recoverySource = await AddAndScanAsync(recoveryFixture.Root, "source.mp4", 130);
        var recoveryDestination = Path.Combine(recoveryFixture.Root.ConfiguredPath, "target.mp4");
        var failing = new SafeFileOperationService(_repository, _log, (_, _) => throw new InvalidOperationException("test-only recovery timing injection"));
        var recoveryPlan = await PrepareProductionPlanAsync(recoverySource.Media, FileOperationType.Rename, recoveryDestination, failing);
        await failing.AcknowledgeProductionDryRunAsync(recoveryPlan.OperationId, CancellationToken.None);
        await failing.ConfirmProductionSameVolumeExecutionAsync(recoveryPlan.OperationId, CancellationToken.None);
        var recoveryRequired = await failing.ExecuteProductionSameVolumeAsync(recoveryPlan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.RecoveryRequired, recoveryRequired.State);
        var recoveryTimer = System.Diagnostics.Stopwatch.StartNew();
        var recovered = Assert.Single(await new SafeFileOperationService(_repository, _log).RecoverInterruptedProductionSameVolumeOperationsAsync(CancellationToken.None));
        recoveryTimer.Stop();
        Assert.Equal(FileOperationState.Completed, recovered.State);

        var report = new
        {
            SchemaVersion = 1,
            Environment = "isolated production-like library on the current Windows filesystem; not a cold-cache sustained-HDD benchmark.",
            Trials = trials,
            RecoveryInspectionMilliseconds = recoveryTimer.ElapsedMilliseconds,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
        var outputPath = Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_PHASE62_PERFORMANCE_PATH");
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, System.Text.Json.JsonSerializer.Serialize(report, PerformanceJsonOptions));
        }
    }

    [Fact]
    public async Task SchemaV8ToV13MigrationAddsSingleItemAndControlledBatchOperationFields()
    {
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS retention_cleanup_item; DROP TABLE IF EXISTS retention_state; DROP TABLE IF EXISTS library_directory; ALTER TABLE library_scan DROP COLUMN registration_revision; ALTER TABLE media_file DROP COLUMN cleanup_committed_at; ALTER TABLE library_root DROP COLUMN registration_revision; ALTER TABLE library_root DROP COLUMN unregistered_at; ALTER TABLE library_root DROP COLUMN root_identity; ALTER TABLE library_root DROP COLUMN root_kind; ALTER TABLE library_root DROP COLUMN registration_state; DELETE FROM schema_migration WHERE version=14;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "DROP INDEX IF EXISTS ix_batch_operation_audit_batch_time; DROP TABLE IF EXISTS batch_operation_audit; DROP INDEX IF EXISTS ix_batch_item_batch_state; DROP TABLE IF EXISTS batch_item; DROP INDEX IF EXISTS ix_batch_operation_state_time; DROP TABLE IF EXISTS batch_operation; DROP INDEX IF EXISTS ix_media_permanent_delete_lifecycle_state_time; DROP TABLE IF EXISTS media_permanent_delete_lifecycle; DROP INDEX IF EXISTS ix_file_operation_plan_permanent_delete_state; ALTER TABLE file_operation_plan DROP COLUMN production_permanent_delete_capability; DROP INDEX ix_media_recycle_lifecycle_state_time; DROP TABLE media_recycle_lifecycle; DROP INDEX ix_file_operation_plan_recycle_state; ALTER TABLE file_operation_plan DROP COLUMN production_recycle_capability; DROP INDEX ix_file_operation_plan_cross_volume_stage; ALTER TABLE file_operation_plan DROP COLUMN cross_volume_move_stage; ALTER TABLE file_operation_plan DROP COLUMN production_cross_volume_move_capability; ALTER TABLE file_operation_plan DROP COLUMN destination_free_space_snapshot_bytes; ALTER TABLE file_operation_plan DROP COLUMN temporary_destination_normalized_path; ALTER TABLE file_operation_plan DROP COLUMN temporary_destination_path; ALTER TABLE file_operation_plan DROP COLUMN destination_volume_identity; ALTER TABLE file_operation_plan DROP COLUMN destination_root_path; ALTER TABLE file_operation_plan DROP COLUMN destination_library_root_id; ALTER TABLE file_operation_preflight DROP COLUMN required_destination_free_bytes; ALTER TABLE file_operation_preflight DROP COLUMN temporary_destination_available; ALTER TABLE file_operation_preflight DROP COLUMN cross_volume_verified; ALTER TABLE file_operation_plan DROP COLUMN final_execution_confirmed_at; ALTER TABLE file_operation_plan DROP COLUMN production_same_volume_capability; ALTER TABLE file_operation_preflight DROP COLUMN same_volume_verified; DELETE FROM schema_migration WHERE version IN (9,10,11,12,13);";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var migrated = new SqliteLibraryRepository(_paths, _log);
        await migrated.InitializeAsync(CancellationToken.None);
        await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        await verification.OpenAsync();
        await using var pragma = verification.CreateCommand();
        pragma.CommandText = "SELECT name FROM pragma_table_info('file_operation_plan') WHERE name IN ('final_execution_confirmed_at','production_same_volume_capability','destination_library_root_id','destination_root_path','destination_volume_identity','temporary_destination_path','temporary_destination_normalized_path','destination_free_space_snapshot_bytes','production_cross_volume_move_capability','cross_volume_move_stage','production_recycle_capability','production_permanent_delete_capability') UNION ALL SELECT name FROM pragma_table_info('file_operation_preflight') WHERE name IN ('same_volume_verified','cross_volume_verified','temporary_destination_available','required_destination_free_bytes') ORDER BY name;";
        await using var reader = await pragma.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(["cross_volume_move_stage", "cross_volume_verified", "destination_free_space_snapshot_bytes", "destination_library_root_id", "destination_root_path", "destination_volume_identity", "final_execution_confirmed_at", "production_cross_volume_move_capability", "production_permanent_delete_capability", "production_recycle_capability", "production_same_volume_capability", "required_destination_free_bytes", "same_volume_verified", "temporary_destination_available", "temporary_destination_normalized_path", "temporary_destination_path"], names);
        await using var batchTables = verification.CreateCommand();
        batchTables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('batch_operation','batch_item','batch_operation_audit') ORDER BY name;";
        await using var batchReader = await batchTables.ExecuteReaderAsync();
        var batchNames = new List<string>();
        while (await batchReader.ReadAsync()) batchNames.Add(batchReader.GetString(0));
        Assert.Equal(["batch_item", "batch_operation", "batch_operation_audit"], batchNames);
        Assert.True(await migrated.IntegrityCheckAsync(CancellationToken.None));
    }

    private async Task<(LibraryRoot Root, string Workspace)> CreateProductionLikeFixtureAsync(string name)
    {
        var workspace = Path.Combine(_root, "production-like", name);
        Directory.CreateDirectory(workspace);
        var root = await _repository.AddOrGetLibraryRootAsync(workspace, CancellationToken.None);
        return (root, workspace);
    }

    private async Task<(MediaFile Media, string Path)> AddAndScanAsync(LibraryRoot root, string name, byte seed)
    {
        var path = Path.Combine(root.ConfiguredPath, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(seed, 4096).ToArray());
        await RescanAsync(root);
        var media = Assert.Single((await _repository.QueryMediaAsync(root.LibraryRootId, name, 0, 10, CancellationToken.None)).Items);
        return ((await _repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!, path);
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

    private async Task<FileOperationPlan> PrepareProductionPlanAsync(MediaFile media, FileOperationType type, string destination, SafeFileOperationService? service = null)
    {
        service ??= _operations;
        var plan = await service.CreatePlanAsync(media.MediaFileId, type, FileOperationMode.ProductionDryRun, destination, null, "Isolated production-like test library; no human decision grants authorization.", SourceValidationStrength.StatAndStableIdentity, CancellationToken.None);
        var dry = await service.RunDryRunAsync(plan.OperationId, CancellationToken.None);
        Assert.Equal(FileOperationState.ConfirmationRequired, dry.Plan.State);
        Assert.True(dry.Plan.ProductionSameVolumeCapability);
        Assert.Equal(SourceValidationStrength.StrongFullHash, dry.Plan.ValidationStrength);
        Assert.False(string.IsNullOrWhiteSpace(dry.Plan.SourceFullSha256));
        Assert.True(dry.Preflight.SameVolumeVerified);
        return dry.Plan;
    }

    private static async Task RunIcaclsAsync(params string[] arguments)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        await process.WaitForExitAsync();
        var error = await process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, $"icacls failed with {process.ExitCode}: {output} {error}");
    }

    private static async Task<string> SnapshotAsync(string path)
    {
        var info = new FileInfo(path);
        return $"{await FileHash.Sha256Async(path, CancellationToken.None)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }
}
