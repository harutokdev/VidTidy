using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class InfrastructureTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAITests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _fixtureDisposed;

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
        // Windows may retain an SQLite WAL handle after a test process has completed its assertions.
        // Cleanup must never change the pass/fail result of the behavior under test.
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
        if (_fixtureDisposed) return;
        _fixtureDisposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MigrationAndRescanKeepOneStableMediaFile()
    {
        var mediaDir = Path.Combine(_root, "media"); Directory.CreateDirectory(mediaDir);
        var source = Path.Combine(mediaDir, "fixture.mp4"); await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
        var root = await repository.AddOrGetLibraryRootAsync(mediaDir, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None)) await repository.UpsertScannedFileAsync(file, scanId, CancellationToken.None);
        await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scanId, CancellationToken.None);
        var first = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.Single(first.Items);
        var mediaId = first.Items[0].MediaFileId;
        var secondScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None)) await repository.UpsertScannedFileAsync(file, secondScan, CancellationToken.None);
        await repository.CompleteScanAsync(secondScan, new ScanCompletion("Completed", 1, 0, 1, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, secondScan, CancellationToken.None);
        var second = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.Single(second.Items);
        Assert.Equal(mediaId, second.Items[0].MediaFileId);
    }

    [Fact]
    public async Task SameVolumeRenameKeepsStableMediaFileId()
    {
        if (!OperatingSystem.IsWindows()) return;

        var mediaDir = Path.Combine(_root, "rename-media");
        Directory.CreateDirectory(mediaDir);
        var originalPath = Path.Combine(mediaDir, "before.mp4");
        await File.WriteAllBytesAsync(originalPath, Enumerable.Repeat((byte)17, 96_000).ToArray());
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDir, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();

        var firstScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None))
            await repository.UpsertScannedFileAsync(file, firstScan, CancellationToken.None);
        await repository.CompleteScanAsync(firstScan, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        var initial = Assert.Single((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);

        var renamedPath = Path.Combine(mediaDir, "after.mp4");
        File.Move(originalPath, renamedPath);
        var secondScan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None))
            await repository.UpsertScannedFileAsync(file, secondScan, CancellationToken.None);
        await repository.CompleteScanAsync(secondScan, new ScanCompletion("Completed", 1, 0, 1, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, secondScan, CancellationToken.None);

        var renamed = Assert.Single((await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(initial.MediaFileId, renamed.MediaFileId);
        Assert.Equal(renamedPath, renamed.Path);
    }

    [Fact]
    public async Task CommitFaultRollsBackWithoutDuplicateRowsOrIntegrityDamage()
    {
        var mediaDir = Path.Combine(_root, "commit-fault-media");
        Directory.CreateDirectory(mediaDir);
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDir, CancellationToken.None);
        var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var scanned = new ScannedMediaFile(root.LibraryRootId, Path.Combine(mediaDir, "fault.mp4"), PathPolicy.NormalizeForIdentity(Path.Combine(mediaDir, "fault.mp4")), "fault.mp4", ".mp4", 42, now, now, null, null, "fault-fingerprint");

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using (var createTrigger = connection.CreateCommand())
        {
            createTrigger.CommandText = "CREATE TRIGGER phase2_commit_fault BEFORE INSERT ON media_file BEGIN SELECT RAISE(ABORT, 'phase2-commit-fault'); END;";
            await createTrigger.ExecuteNonQueryAsync();
        }
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None));
        }
        finally
        {
            await using var dropTrigger = connection.CreateCommand();
            dropTrigger.CommandText = "DROP TRIGGER IF EXISTS phase2_commit_fault;";
            await dropTrigger.ExecuteNonQueryAsync();
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT (SELECT COUNT(*) FROM media_file), (SELECT COUNT(*) FROM media_content);";
            await using var reader = await countCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));

        await repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedRunningJobIsRecoveredAsRetryWaiting()
    {
        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var queued = await jobs.EnqueueAsync(JobType.Scan, new ScanPayload(Guid.NewGuid()), 2, null, CancellationToken.None);
        var running = await jobs.TryAcquireNextAsync(CancellationToken.None);
        Assert.NotNull(running);
        await jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        var recovered = (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(x => x.JobId == queued.JobId);
        Assert.Equal(JobState.RetryWaiting, recovered.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovered.Reason);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QuickFingerprintIsDeterministicAndNotAnExactHashClaim()
    {
        var path = Path.Combine(_root, "sample.mp4"); await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)7, 200_000).ToArray());
        var first = await FileSystemMediaScanner.QuickFingerprintAsync(path, new FileInfo(path).Length, CancellationToken.None);
        var second = await FileSystemMediaScanner.QuickFingerprintAsync(path, new FileInfo(path).Length, CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.NotEqual(await FileHash.Sha256Async(path, CancellationToken.None), first);
    }

    [Fact]
    public async Task MissingFolderRootUsesFolderAbsenceInsteadOfDriveOffline()
    {
        var online = Path.Combine(_root, "online"); Directory.CreateDirectory(online);
        var source = Path.Combine(online, "keep.mp4"); await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var repository = new SqliteLibraryRepository(_paths, _log); await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(online, CancellationToken.None);
        var scanner = new FileSystemMediaScanner(); var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None)) await repository.UpsertScannedFileAsync(file, scanId, CancellationToken.None);
        await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Directory.Move(online, online + "-offline");
        var pipeline = new MediaPipeline(repository, jobs, scanner, new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        var result = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 10, CancellationToken.None);
        Assert.Empty(result.Items); // historical absence is not a current Library row
        Assert.Equal(RootAvailability.Online, (await repository.GetLibraryRootAsync(root.LibraryRootId, CancellationToken.None))!.Availability);
    }
}


public sealed class VisualSemanticPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAITests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _disposed;

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
    public async Task SchemaV2PersistsVisualSamplesEmbeddingAndModelProvenanceWithoutCrossIdentityReuse()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));

        var mediaDirectory = Path.Combine(_root, "visual-media");
        Directory.CreateDirectory(mediaDirectory);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var mediaPath = Path.Combine(mediaDirectory, "sample.mp4");
        var scanned = new ScannedMediaFile(root.LibraryRootId, mediaPath, PathPolicy.NormalizeForIdentity(mediaPath), "sample.mp4", ".mp4", 1024, now, now, null, null, "quick-identity-v1");
        var reconciled = await repository.UpsertScannedFileAsync(scanned, scan, CancellationToken.None);
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", 1, 1, 0, 0, 0, now), CancellationToken.None);

        var policy = VisualSamplingPolicy.FixedIntervalV1;
        Assert.Equal([0d, 10d, 20d], policy.CreateTimestamps(21d));
        Assert.Equal([0d], policy.CreateTimestamps(null));
        var inputIdentity = "quick-identity-v1";
        var sample = new VisualSample(
            Guid.NewGuid(), reconciled.MediaFile.MediaFileId, reconciled.MediaFile.MediaContentId, inputIdentity, 0, 0d, 0d, policy.Version,
            AnalysisKey.ArtifactCacheKey(inputIdentity, "visual-frame", policy.Version, "ffmpeg-frame-v1", AnalysisKey.ConfigHash(new { timestamp = 0d })),
            "visual-samples/frame-0.jpg", "frame-sha", 42, VisualSampleStatus.Completed, null, null, now, now);
        await repository.ReplaceVisualSamplesAsync(reconciled.MediaFile.MediaFileId, reconciled.MediaFile.MediaContentId, inputIdentity, policy.Version, [sample], CancellationToken.None);

        var currentSamples = await repository.GetVisualSamplesAsync(reconciled.MediaFile.MediaFileId, inputIdentity, policy.Version, CancellationToken.None);
        Assert.Equal(sample, Assert.Single(currentSamples));
        Assert.Empty(await repository.GetVisualSamplesAsync(reconciled.MediaFile.MediaFileId, "different-input-identity", policy.Version, CancellationToken.None));

        var provenance = new ModelProvenance("google/siglip2-base-patch16-224", "75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2", "siglip2-processor-v1", "manifest-sha", "Apache-2.0", false, now);
        await repository.UpsertModelProvenanceAsync(provenance, CancellationToken.None);
        var embeddingCacheKey = AnalysisKey.VisualEmbeddingCacheKey(inputIdentity, sample.PresentationTimestampSeconds, provenance.ModelRevision, provenance.ProcessorVersion, policy.Version, "float32");
        var embedding = new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, inputIdentity, embeddingCacheKey, provenance.ModelName, provenance.ModelRevision, provenance.ProcessorVersion, policy.Version, "float32", 3, true, [0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0], VisualEmbeddingStatus.Completed, null, null, now, now);
        await repository.UpsertVisualEmbeddingAsync(embedding, CancellationToken.None);

        var reused = await repository.FindCompletedVisualEmbeddingAsync(embeddingCacheKey, CancellationToken.None);
        Assert.NotNull(reused);
        Assert.Equal(embedding.VectorFloat32, reused.VectorFloat32);
        var completed = await repository.GetCompletedVisualEmbeddingsAsync(CancellationToken.None);
        var result = Assert.Single(completed);
        Assert.Equal(embedding.VisualEmbeddingId, result.Embedding.VisualEmbeddingId);
        Assert.Equal(sample.VisualSampleId, result.Sample.VisualSampleId);
        Assert.Equal(reconciled.MediaFile.MediaFileId, result.Media.MediaFileId);
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public void VisualSamplingPolicyV2IsBoundedAndCacheSeparated()
    {
        var baseline = VisualSamplingPolicy.FixedIntervalV1;
        var candidate = VisualSamplingPolicy.FixedIntervalV2;

        Assert.Equal("fixed-interval-v1", baseline.Version);
        Assert.Equal("fixed-interval-v2", candidate.Version);
        Assert.Equal(10d, baseline.IntervalSeconds);
        Assert.Equal(5d, candidate.IntervalSeconds);
        Assert.Equal(12, baseline.MaximumSamples);
        Assert.Equal(24, candidate.MaximumSamples);
        Assert.Equal(0.25d, candidate.EndMarginSeconds);
        Assert.Equal([0d, 5d, 10d, 15d, 20d], candidate.CreateTimestamps(21d));
        Assert.Equal([0d, 5d, 10d], candidate.CreateTimestamps(15.02d));
        Assert.Equal(24, candidate.CreateTimestamps(240d).Count);

        var baselineKey = AnalysisKey.VisualEmbeddingCacheKey("input", 10d, 10d, "model", "revision", "adapter", "processor", baseline.Version, "float32", 512);
        var candidateKey = AnalysisKey.VisualEmbeddingCacheKey("input", 10d, 10d, "model", "revision", "adapter", "processor", candidate.Version, "float32", 512);
        Assert.NotEqual(baselineKey, candidateKey);
    }
}


public sealed class ResourceAdmissionPersistenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIResourceAdmission", Guid.NewGuid().ToString("N"));
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
        Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
        await Task.CompletedTask;
    }

    public void Dispose() => _log?.Dispose();

    [Fact]
    public async Task ResourceDeferralKeepsAttemptAndSchedulesDurableRetry()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var queued = await jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(Guid.NewGuid()), 2, null, CancellationToken.None);
        var running = await jobs.TryAcquireNextAsync(CancellationToken.None);
        Assert.NotNull(running);
        Assert.Equal(1, running!.Attempt);

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(1);
        await jobs.DeferForResourceAsync(queued.JobId, JobReason.ResourceVram, "RESOURCE_VRAM_UNAVAILABLE", "VRAMを確認できないか不足しています。", notBefore, CancellationToken.None);

        var deferred = (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(job => job.JobId == queued.JobId);
        Assert.Equal(JobState.RetryWaiting, deferred.State);
        Assert.Equal(JobReason.ResourceVram, deferred.Reason);
        Assert.Equal(1, deferred.Attempt);
        Assert.Equal("RESOURCE_VRAM_UNAVAILABLE", deferred.LastErrorCode);
        Assert.NotNull(deferred.NotBefore);
        Assert.True(deferred.NotBefore >= notBefore.AddSeconds(-1));
        Assert.Contains(await jobs.GetEventsAsync(queued.JobId, CancellationToken.None), item => item.EventName == "ResourceDeferred");
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }
}
