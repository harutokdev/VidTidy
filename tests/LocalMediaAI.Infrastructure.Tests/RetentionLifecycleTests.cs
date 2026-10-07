using System.Diagnostics;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class RetentionLifecycleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIRetentionTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(50); }
            catch (UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }

    [Fact]
    public async Task DefaultsAndFiniteUnlimitedSettingsAreDurableAndDoNotCleanupOnSave()
    {
        var repository = await CreateRepositoryAsync();
        Assert.Equal(RetentionSettings.Defaults, await repository.GetRetentionSettingsAsync(CancellationToken.None));
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(null, 90, 730), CancellationToken.None);
        var reopened = new SqliteLibraryRepository(_paths, _log);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal(new RetentionSettings(null, 90, 730), await reopened.GetRetentionSettingsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.SaveRetentionSettingsAsync(new RetentionSettings(0, 1, 1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reopened.SaveRetentionSettingsAsync(new RetentionSettings(RetentionSettings.MaximumSafeDays + 1, 1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task SamePathReplacementNeverInheritsFileIdentityOrManualMetadata()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("replacement");
        var path = Path.Combine(mediaRoot, "same.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var at = DateTimeOffset.UtcNow;
        var firstScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var old = await repository.UpsertScannedFileAsync(ScannedFile(root, path, "V", "OLD", 10, at), firstScan, CancellationToken.None);
        await repository.AddManualMediaTagAsync(old.MediaFile.MediaFileId, "keep-old-only", null, CancellationToken.None);
        await CompleteAsync(repository, root, firstScan, [ScannedDir(mediaRoot, "V", "ROOT")], at);

        var secondScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var replacement = await repository.UpsertScannedFileAsync(ScannedFile(root, path, "V", "NEW", 10, at), secondScan, CancellationToken.None);
        await CompleteAsync(repository, root, secondScan, [ScannedDir(mediaRoot, "V", "ROOT")], at.AddMinutes(1));

        Assert.NotEqual(old.MediaFile.MediaFileId, replacement.MediaFile.MediaFileId);
        Assert.Empty(await repository.GetManualMediaTagsAsync(replacement.MediaFile.MediaFileId, CancellationToken.None));
        Assert.Single(await repository.GetManualMediaTagsAsync(old.MediaFile.MediaFileId, CancellationToken.None));
        Assert.Contains(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.TargetType == RetentionTargetType.File && value.TargetId == old.MediaFile.MediaFileId.ToString("D"));
    }

    [Fact]
    public async Task StableIdentityRenameAndMoveAcrossRegisteredRootsPreservesFileAndContent()
    {
        var repository = await CreateRepositoryAsync();
        var rootA = await repository.AddOrGetLibraryRootAsync(NewDirectory("root-a"), CancellationToken.None);
        var rootB = await repository.AddOrGetLibraryRootAsync(NewDirectory("root-b"), CancellationToken.None);
        var at = DateTimeOffset.UtcNow;
        var scanA = await repository.StartScanAsync(rootA.LibraryRootId, CancellationToken.None);
        var original = await repository.UpsertScannedFileAsync(ScannedFile(rootA, Path.Combine(rootA.ConfiguredPath, "a.mp4"), "V", "FILE", 12, at), scanA, CancellationToken.None);
        await repository.CompleteScanAsync(scanA, new ScanCompletion("Completed", 1, 1, 0, 0, 0, at), CancellationToken.None);
        var scanB = await repository.StartScanAsync(rootB.LibraryRootId, CancellationToken.None);
        var moved = await repository.UpsertScannedFileAsync(ScannedFile(rootB, Path.Combine(rootB.ConfiguredPath, "renamed.mp4"), "V", "FILE", 12, at), scanB, CancellationToken.None);
        Assert.Equal(original.MediaFile.MediaFileId, moved.MediaFile.MediaFileId);
        Assert.Equal(original.MediaFile.MediaContentId, moved.MediaFile.MediaContentId);
        Assert.Equal(rootB.LibraryRootId, moved.MediaFile.LibraryRootId);
        Assert.Empty(await repository.GetRetentionRecordsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FolderCauseSuppressesChildTimersAndUnknownResetsElapsedTime()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("folders");
        var child = Directory.CreateDirectory(Path.Combine(mediaRoot, "child")).FullName;
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var firstAt = DateTimeOffset.UtcNow.AddDays(-40);
        var scan1 = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await repository.UpsertScannedFileAsync(ScannedFile(root, Path.Combine(child, "a.mp4"), "V", "F", 1, firstAt), scan1, CancellationToken.None);
        await CompleteAsync(repository, root, scan1, [ScannedDir(mediaRoot, "V", "R"), ScannedDir(child, "V", "D")], firstAt);

        Directory.Delete(child, true);
        var missingAt = DateTimeOffset.UtcNow.AddDays(-29);
        var scan2 = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await CompleteAsync(repository, root, scan2, [ScannedDir(mediaRoot, "V", "R")], missingAt);
        var folderRetention = Assert.Single((await repository.GetRetentionRecordsAsync(CancellationToken.None)).Where(value => value.Cause == RetentionCause.FolderMissing));
        Assert.DoesNotContain(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.FileMissing);

        await repository.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.Unknown, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        Assert.Empty((await repository.GetRetentionRecordsAsync(CancellationToken.None)).Where(value => value.State == RetentionLifecycleState.Retained));
        var restartedAt = DateTimeOffset.UtcNow;
        var scan3 = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await CompleteAsync(repository, root, scan3, [ScannedDir(mediaRoot, "V", "R")], restartedAt);
        var restarted = Assert.Single((await repository.GetRetentionRecordsAsync(CancellationToken.None)).Where(value => value.Cause == RetentionCause.FolderMissing));
        Assert.NotEqual(folderRetention.AbsenceStartedAt, restarted.AbsenceStartedAt);
        Assert.Equal(restartedAt, restarted.AbsenceStartedAt);
    }

    [Fact]
    public async Task CandidateRacesUseLatestSettingUnknownAndReappearance()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("races");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var holding = Path.Combine(_root, "holding.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var media = await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-50));
        File.Move(source, holding);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-40));
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(1, 60, 365), CancellationToken.None);

        repository.BeforeFinalCleanupEligibilityForTests = async (_, ct) => await repository.SaveRetentionSettingsAsync(new RetentionSettings(90, 60, 365), ct);
        var extended = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, extended.Committed);
        Assert.Contains(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.TargetId == media.MediaFileId.ToString("D") && value.State == RetentionLifecycleState.Retained);

        await repository.SaveRetentionSettingsAsync(new RetentionSettings(1, 60, 365), CancellationToken.None);
        repository.BeforeFinalCleanupEligibilityForTests = (_, _) => { File.Move(holding, source); return Task.CompletedTask; };
        var rescued = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, rescued.Rescued);
        Assert.Equal(PresenceState.Present, (await repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!.PresenceState);
        Assert.DoesNotContain(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.TargetId == media.MediaFileId.ToString("D"));

        File.Move(source, holding);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-2));
        repository.BeforeFinalCleanupEligibilityForTests = async (_, ct) => await repository.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.Unknown, DateTimeOffset.UtcNow, ct);
        var deferred = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, deferred.Committed);
        Assert.DoesNotContain(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.State == RetentionLifecycleState.Retained);
    }

    [Fact]
    public async Task ExplicitUnregisterResetsCauseCancelsQueuedWorkAndIdentityGatesReregister()
    {
        var repository = await CreateRepositoryAsync();
        var jobs = new SqliteJobStore(_paths, _log);
        var mediaRoot = NewDirectory("unregister");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var media = await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-20));
        var queued = await jobs.EnqueueAsync(JobType.Metadata, new MediaPayload(media.MediaFileId), 2, null, CancellationToken.None);
        var at = DateTimeOffset.UtcNow;
        await repository.UnregisterRootAsync(root.LibraryRootId, at, CancellationToken.None);
        var retained = Assert.Single((await repository.GetRetentionRecordsAsync(CancellationToken.None)).Where(value => value.Cause == RetentionCause.ExplicitUnregister));
        Assert.Equal(at, retained.AbsenceStartedAt);
        Assert.Equal(JobState.Cancelled, Assert.Single((await jobs.GetRecentAsync(20, CancellationToken.None)).Where(value => value.JobId == queued.JobId)).State);
        var lateFollowUp = await jobs.EnqueueForActiveMediaAsync(JobType.Thumbnail, new MediaPayload(media.MediaFileId), media.MediaFileId, 2, null, unique: false, CancellationToken.None);
        Assert.Null(lateFollowUp);
        Assert.DoesNotContain(await jobs.GetRecentAsync(20, CancellationToken.None), value => value.JobType == JobType.Thumbnail);
        Assert.Empty(await repository.GetLibraryRootsAsync(CancellationToken.None));

        var reregistered = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        Assert.Equal(root.LibraryRootId, reregistered.LibraryRootId);
        Assert.Equal(RootRegistrationState.Active, reregistered.RegistrationState);
        Assert.DoesNotContain(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.ExplicitUnregister && value.State == RetentionLifecycleState.Retained);
    }

    [Fact]
    public async Task NestedRootKeepsMediaActiveWhenParentRootIsUnregisteredAndCleaned()
    {
        var repository = await CreateRepositoryAsync();
        var parentPath = NewDirectory("nested-parent");
        var nestedPath = Directory.CreateDirectory(Path.Combine(parentPath, "nested")).FullName;
        var source = Path.Combine(nestedPath, "a.mp4");
        await File.WriteAllBytesAsync(source, [1, 2]);
        var parent = await repository.AddOrGetLibraryRootAsync(parentPath, CancellationToken.None);
        var nested = await repository.AddOrGetLibraryRootAsync(nestedPath, CancellationToken.None);
        var media = await ScanRealAsync(repository, parent, DateTimeOffset.UtcNow.AddDays(-10));
        var fromNested = await ScanRealAsync(repository, nested, DateTimeOffset.UtcNow.AddDays(-9));
        Assert.Equal(media.MediaFileId, fromNested.MediaFileId);
        Assert.Equal(nested.LibraryRootId, fromNested.LibraryRootId);

        await repository.UnregisterRootAsync(parent.LibraryRootId, DateTimeOffset.UtcNow.AddDays(-2), CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(30, 1, 365), CancellationToken.None);
        var cleanup = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, cleanup.Committed);
        var active = (await repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!;
        Assert.Equal(nested.LibraryRootId, active.LibraryRootId);
        Assert.Equal(PresenceState.Present, active.PresenceState);
        Assert.NotNull(active.FileIdentity);
    }

    [Fact]
    public async Task CandidateThenReregisterCannotCommitOldUnregisterCleanup()
    {
        var repository = await CreateRepositoryAsync();
        var path = NewDirectory("reregister-race");
        var root = await repository.AddOrGetLibraryRootAsync(path, CancellationToken.None);
        await repository.UnregisterRootAsync(root.LibraryRootId, DateTimeOffset.UtcNow.AddDays(-2), CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(30, 1, 365), CancellationToken.None);
        repository.BeforeFinalCleanupEligibilityForTests = async (record, ct) => { await repository.AddOrGetLibraryRootAsync(path, ct); };
        var result = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, result.Committed);
        Assert.Equal(RootRegistrationState.Active, (await repository.GetLibraryRootAsync(root.LibraryRootId, CancellationToken.None))!.RegistrationState);
    }

    [Fact]
    public async Task ScanFromPriorRegistrationCannotPublishAfterUnregisterReregister()
    {
        var repository = await CreateRepositoryAsync();
        var path = NewDirectory("stale-scan");
        var mediaPath = Path.Combine(path, "a.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(path, CancellationToken.None);
        var staleScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await repository.UnregisterRootAsync(root.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);
        var active = await repository.AddOrGetLibraryRootAsync(path, CancellationToken.None);
        Assert.True(active.RegistrationRevision > root.RegistrationRevision);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertScannedFileAsync(ScannedFile(active, mediaPath, "V", "F", 1, DateTimeOffset.UtcNow), staleScan, CancellationToken.None));
        Assert.Empty((await repository.QueryMediaAsync(active.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task PostCommitArtifactFailureLeavesDurableRetryableCleanupMarker()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("recovery");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1, 2]);
        var holding = Path.Combine(_root, "recovery-holding.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var media = await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-5));
        await repository.AddManualMediaTagAsync(media.MediaFileId, "durable-until-commit", null, CancellationToken.None);
        var analysis = await repository.StartAnalysisAsync(media.MediaContentId, "Thumbnail", "test", "1", "{}", "cfg", "input", "1", CancellationToken.None);
        await repository.CompleteAnalysisAsync(analysis.AnalysisId, AnalysisStatus.Completed, CancellationToken.None);
        var relative = "thumbnails/locked.bin";
        var artifactPath = Path.Combine(_paths.CacheRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllBytesAsync(artifactPath, [9]);
        await repository.AddArtifactAsync(new Artifact(Guid.NewGuid(), analysis.AnalysisId, "Thumbnail", relative, "sha", 1, "application/octet-stream", ArtifactDurability.RebuildableCache, DateTimeOffset.UtcNow), CancellationToken.None);
        File.Move(source, holding);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-2));
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(1, 60, 365), CancellationToken.None);

        using (var locked = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal(1, first.Committed);
            Assert.Contains(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.State == RetentionLifecycleState.CleanupCommitted);
            Assert.Single(await repository.GetManualMediaTagsAsync(media.MediaFileId, CancellationToken.None));
        }
        var retry = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, retry.Completed);
        Assert.False(File.Exists(artifactPath));
        Assert.Empty(await repository.GetManualMediaTagsAsync(media.MediaFileId, CancellationToken.None));
        Assert.Empty((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HigherDriveCauseAndRecoveryDiscardTheOldFileTimer()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("cause-priority");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        var holding = Path.Combine(_root, "cause-holding.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var media = await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-40));
        File.Move(source, holding);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-29));
        Assert.Contains(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.FileMissing);

        var driveAt = DateTimeOffset.UtcNow.AddDays(-1);
        await repository.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.DriveOffline, driveAt, CancellationToken.None);
        var drive = Assert.Single(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.DriveOffline);
        Assert.Equal(driveAt, drive.AbsenceStartedAt);
        Assert.DoesNotContain(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.FileMissing);
        Assert.Equal(PresenceState.OfflineRoot, (await repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!.PresenceState);

        await repository.RecordRootObservationAsync(root.LibraryRootId, RootObservationKind.Online, DateTimeOffset.UtcNow, CancellationToken.None);
        var restartedAt = DateTimeOffset.UtcNow;
        await ScanRealAsync(repository, root, restartedAt);
        var restarted = Assert.Single(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.FileMissing);
        Assert.Equal(restartedAt, restarted.AbsenceStartedAt);
    }

    [Fact]
    public async Task FiniteUnlimitedTransitionsAlwaysUseOriginalAbsenceTimestamp()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("unlimited");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        var holding = Path.Combine(_root, "unlimited-holding.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-100));
        File.Move(source, holding);
        var absentAt = DateTimeOffset.UtcNow.AddDays(-40);
        await ScanRealAsync(repository, root, absentAt);

        await repository.SaveRetentionSettingsAsync(new RetentionSettings(null, 60, 365), CancellationToken.None);
        Assert.Equal(0, (await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Candidates);
        Assert.Equal(absentAt, Assert.Single(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.Cause == RetentionCause.FileMissing).AbsenceStartedAt);
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(30, 60, 365), CancellationToken.None);
        var finite = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, finite.Committed);
    }

    [Fact]
    public async Task ReownedChildIsExcludedAtSubtreeCommit()
    {
        var repository = await CreateRepositoryAsync();
        var rootAPath = NewDirectory("ownership-a");
        var missingFolder = Directory.CreateDirectory(Path.Combine(rootAPath, "folder")).FullName;
        var source = Path.Combine(missingFolder, "a.mp4");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var rootBPath = NewDirectory("ownership-b");
        var destination = Path.Combine(rootBPath, "a.mp4");
        var rootA = await repository.AddOrGetLibraryRootAsync(rootAPath, CancellationToken.None);
        var rootB = await repository.AddOrGetLibraryRootAsync(rootBPath, CancellationToken.None);
        var media = await ScanRealAsync(repository, rootA, DateTimeOffset.UtcNow.AddDays(-100));
        File.Move(source, destination);
        Directory.Delete(missingFolder);
        await ScanRealAsync(repository, rootA, DateTimeOffset.UtcNow.AddDays(-70));
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(30, 1, 365), CancellationToken.None);
        repository.BeforeFinalCleanupEligibilityForTests = async (_, _) => { await ScanRealAsync(repository, rootB, DateTimeOffset.UtcNow); };

        var result = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(1, result.Committed);
        var current = (await repository.GetMediaFileAsync(media.MediaFileId, CancellationToken.None))!;
        Assert.Equal(rootB.LibraryRootId, current.LibraryRootId);
        Assert.Equal(PresenceState.Present, current.PresenceState);
        Assert.NotNull(current.FileIdentity);
    }

    [Fact]
    public async Task FailureBeforePointOfNoReturnLeavesMetadataRescuable()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("precommit");
        var source = Path.Combine(mediaRoot, "a.mp4");
        await File.WriteAllBytesAsync(source, [1]);
        var holding = Path.Combine(_root, "precommit-holding.mp4");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var media = await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-5));
        await repository.AddManualMediaTagAsync(media.MediaFileId, "must-survive", null, CancellationToken.None);
        File.Move(source, holding);
        await ScanRealAsync(repository, root, DateTimeOffset.UtcNow.AddDays(-2));
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(1, 60, 365), CancellationToken.None);
        repository.BeforeFinalCleanupEligibilityForTests = (_, _) => throw new IOException("simulated crash before final eligibility");

        await Assert.ThrowsAsync<IOException>(() => repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Single(await repository.GetManualMediaTagsAsync(media.MediaFileId, CancellationToken.None));
        Assert.Contains(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.State == RetentionLifecycleState.Retained);
    }

    [Fact]
    public async Task MigrationFailureRollsBackAndRetryCompletesWithoutHalfSchema()
    {
        var repository = await CreateRepositoryAsync();
        var legacyRootPath = NewDirectory("legacy-unregister");
        var sentinelPath = Path.Combine(legacyRootPath, "must-not-be-touched.mp4");
        var sentinelBytes = new byte[] { 1, 4, 9, 16 };
        await File.WriteAllBytesAsync(sentinelPath, sentinelBytes);
        var legacyRoot = await repository.AddOrGetLibraryRootAsync(legacyRootPath, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE retention_cleanup_item; DROP TABLE retention_state; DROP TABLE library_directory; ALTER TABLE library_scan DROP COLUMN registration_revision; ALTER TABLE media_file DROP COLUMN cleanup_committed_at; ALTER TABLE library_root DROP COLUMN registration_revision; ALTER TABLE library_root DROP COLUMN unregistered_at; ALTER TABLE library_root DROP COLUMN root_identity; ALTER TABLE library_root DROP COLUMN root_kind; ALTER TABLE library_root DROP COLUMN registration_state; DELETE FROM schema_migration WHERE version=14;";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var retry = new SqliteLibraryRepository(_paths, _log);
        retry.MigrationAppliedBeforeCommitForTests = _ => throw new IOException("simulated interruption before migration commit");
        await Assert.ThrowsAsync<IOException>(() => retry.InitializeAsync(CancellationToken.None));
        await using (var verification = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await verification.OpenAsync();
            await using var command = verification.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM pragma_table_info('library_root') WHERE name='registration_state';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        SqliteConnection.ClearAllPools();
        retry.MigrationAppliedBeforeCommitForTests = null;
        await retry.InitializeAsync(CancellationToken.None);
        Assert.Equal(RetentionSettings.Defaults, await retry.GetRetentionSettingsAsync(CancellationToken.None));
        Assert.True(await retry.IntegrityCheckAsync(CancellationToken.None));

        var migratedRoot = (await retry.GetLibraryRootAsync(legacyRoot.LibraryRootId, CancellationToken.None))!;
        Assert.Equal(RootRegistrationState.Active, migratedRoot.RegistrationState);
        Assert.Null(migratedRoot.RootIdentity);
        Assert.Equal(0, migratedRoot.RegistrationRevision);

        var unregisteredAt = DateTimeOffset.UtcNow;
        await retry.UnregisterRootAsync(migratedRoot.LibraryRootId, unregisteredAt, CancellationToken.None);
        Assert.DoesNotContain(await retry.GetLibraryRootsAsync(CancellationToken.None), value => value.LibraryRootId == migratedRoot.LibraryRootId);
        var retained = Assert.Single(await retry.GetRetentionRecordsAsync(CancellationToken.None), value => value.TargetId == migratedRoot.LibraryRootId.ToString("D"));
        Assert.Equal(RetentionCause.ExplicitUnregister, retained.Cause);
        Assert.Equal(unregisteredAt, retained.AbsenceStartedAt);
        Assert.Equal(sentinelBytes, await File.ReadAllBytesAsync(sentinelPath));

        var registeredAgain = await retry.AddOrGetLibraryRootAsync(legacyRootPath, CancellationToken.None);
        Assert.Equal(RootRegistrationState.Active, registeredAgain.RegistrationState);
        Assert.NotEqual(migratedRoot.LibraryRootId, registeredAgain.LibraryRootId);
    }

    [Theory]
    [InlineData(RootObservationKind.RootFolderMissing)]
    [InlineData(RootObservationKind.DriveOffline)]
    public async Task ExplicitUnregisterOverridesExistingAuthoritativeRootAbsence(RootObservationKind observation)
    {
        var repository = await CreateRepositoryAsync();
        var rootPath = NewDirectory($"unregister-{observation}");
        var sentinelPath = Path.Combine(rootPath, "must-not-be-touched.mp4");
        await File.WriteAllBytesAsync(sentinelPath, [2, 3, 5, 7]);
        var root = await repository.AddOrGetLibraryRootAsync(rootPath, CancellationToken.None);
        await repository.RecordRootObservationAsync(root.LibraryRootId, observation, DateTimeOffset.UtcNow.AddDays(-20), CancellationToken.None);

        var unregisteredAt = DateTimeOffset.UtcNow;
        await repository.UnregisterRootAsync(root.LibraryRootId, unregisteredAt, CancellationToken.None);

        var retained = Assert.Single(await repository.GetRetentionRecordsAsync(CancellationToken.None), value => value.TargetId == root.LibraryRootId.ToString("D"));
        Assert.Equal(RetentionCause.ExplicitUnregister, retained.Cause);
        Assert.Equal(unregisteredAt, retained.AbsenceStartedAt);
        Assert.True(File.Exists(sentinelPath));
    }

    [Fact]
    public async Task RepresentativeSubtreeCleanupIsLinearEnoughForOneThousandRows()
    {
        var repository = await CreateRepositoryAsync();
        var mediaRoot = NewDirectory("performance");
        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var at = DateTimeOffset.UtcNow.AddDays(-100);
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var files = Enumerable.Range(0, 1000).Select(i => ScannedFile(root, Path.Combine(mediaRoot, $"f{i}.mp4"), "V", $"F{i}", i + 1, at)).ToArray();
        await repository.ReconcileScannedFilesAsync(files, scan, CancellationToken.None);
        await CompleteAsync(repository, root, scan, [ScannedDir(mediaRoot, "V", "R")], at);
        await repository.UnregisterRootAsync(root.LibraryRootId, at, CancellationToken.None);
        await repository.SaveRetentionSettingsAsync(new RetentionSettings(30, 60, 1), CancellationToken.None);
        var timer = Stopwatch.StartNew();
        var result = await repository.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        timer.Stop();
        Assert.Equal(1, result.Committed);
        Assert.Equal(1, result.Completed);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"1000-row subtree cleanup took {timer.Elapsed}.");
    }

    private async Task<SqliteLibraryRepository> CreateRepositoryAsync()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    private static ScannedMediaFile ScannedFile(LibraryRoot root, string path, string volume, string identity, long size, DateTimeOffset at) => new(root.LibraryRootId, path, PathPolicy.NormalizeForIdentity(path), Path.GetFileName(path), ".mp4", size, at, at, volume, identity, $"q-{identity}-{size}");
    private static ScannedDirectory ScannedDir(string path, string volume, string identity) => new(path, PathPolicy.NormalizeForIdentity(path), volume, identity);

    private static async Task CompleteAsync(SqliteLibraryRepository repository, LibraryRoot root, Guid scan, IReadOnlyList<ScannedDirectory> directories, DateTimeOffset at)
    {
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", 0, 0, 0, 0, 0, at), CancellationToken.None);
        await repository.ReconcileCompletedScanAbsenceAsync(root.LibraryRootId, scan, new ScanAuthoritySnapshot(directories, [], directories.FirstOrDefault()?.VolumeIdentity, directories.FirstOrDefault()?.DirectoryIdentity, true), at, CancellationToken.None);
    }

    private async Task<MediaFile> ScanRealAsync(SqliteLibraryRepository repository, LibraryRoot root, DateTimeOffset at)
    {
        var scanner = new FileSystemMediaScanner();
        var observations = new ScanObservationCollector();
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var results = new List<ReconcileResult>();
        await foreach (var file in scanner.EnumerateAsync(root, observations, CancellationToken.None)) results.Add(await repository.UpsertScannedFileAsync(file, scan, CancellationToken.None));
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", results.Count, results.Count(value => value.IsNew), results.Count(value => !value.IsNew), 0, 0, at), CancellationToken.None);
        var probe = scanner.Probe(root);
        await repository.ReconcileCompletedScanAbsenceAsync(root.LibraryRootId, scan, observations.Snapshot(probe.VolumeIdentity, probe.RootDirectoryIdentity, true), at, CancellationToken.None);
        if (results.Count > 0) return results[0].MediaFile;
        // The Library list shows current media only, so a file the scan found gone is read straight from the catalog.
        var ids = new List<Guid>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT media_file_id FROM media_file WHERE library_root_id=$root;";
            command.Parameters.AddWithValue("$root", root.LibraryRootId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return (await repository.GetMediaFileAsync(Assert.Single(ids), CancellationToken.None))!;
    }
}
