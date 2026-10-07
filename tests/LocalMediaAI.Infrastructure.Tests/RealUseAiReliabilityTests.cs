using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class RealUseAiReliabilityTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIRealUseReliability", Guid.NewGuid().ToString("N"));
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
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    public void Dispose() => _log?.Dispose();

    [Fact]
    public void PortableFlagKeepsStateCacheModelsAndRuntimeUnderAppRoot()
    {
        Assert.Equal(Path.Combine(_root, "data"), _paths.DataRoot);
        Assert.Equal(Path.Combine(_root, "cache"), _paths.CacheRoot);
        Assert.Equal(Path.Combine(_root, "models"), _paths.ModelsRoot);
        Assert.Equal(Path.Combine(_root, "runtime"), _paths.RuntimeRoot);
        Assert.Equal(Path.Combine(_root, "temp"), _paths.TempRoot);

        var worker = WorkerProcessOptions.Resolve(_paths, SemanticModelRegistry.ResolveDevelopmentCandidate());
        Assert.Equal(Path.Combine(_root, "runtime", "python-openclip", "python.exe"), worker.PythonExecutablePath);
        Assert.Equal(Path.Combine(_root, "runtime", "worker", "local_media_ai_worker.py"), worker.WorkerScriptPath);
    }

    [Fact]
    public async Task OrphanedVisualEmbeddingJobConvergesWithoutGenericFailure()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var queued = await jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(Guid.NewGuid()), 2, null, CancellationToken.None);
        using var pipeline = CreatePipeline(repository, jobs);

        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var completed = (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(value => value.JobId == queued.JobId);
        Assert.Equal(JobState.Partial, completed.State);
        Assert.Equal("OrphanedVisualSample", completed.Phase);
        Assert.Null(completed.LastErrorCode);
        Assert.Empty(await repository.GetCompletedVisualEmbeddingsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SupersededVisualSampleJobCannotPublishAgainstNewContent()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var mediaDirectory = Path.Combine(_root, "synthetic-media");
        Directory.CreateDirectory(mediaDirectory);
        var mediaPath = Path.Combine(mediaDirectory, "fixture.mp4");
        var libraryRoot = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var scan1 = await repository.StartScanAsync(libraryRoot.LibraryRootId, CancellationToken.None);
        var first = await repository.UpsertScannedFileAsync(
            new ScannedMediaFile(libraryRoot.LibraryRootId, mediaPath, PathPolicy.NormalizeForIdentity(mediaPath), "fixture.mp4", ".mp4", 100, now, now, "TEST-VOLUME", "TEST-FILE", "quick-v1"),
            scan1, CancellationToken.None);
        await repository.CompleteScanAsync(scan1, new ScanCompletion("Completed", 1, 1, 0, 0, 0, now), CancellationToken.None);

        var sample = new VisualSample(Guid.NewGuid(), first.MediaFile.MediaFileId, first.MediaFile.MediaContentId, "input-v1", 0, 0, 0,
            VisualSamplingPolicy.FixedIntervalV1.Version, "sample-cache-v1", "visual-samples/synthetic.jpg", "frame-sha", 10,
            VisualSampleStatus.Completed, null, null, now, now);
        await repository.ReplaceVisualSamplesAsync(first.MediaFile.MediaFileId, first.MediaFile.MediaContentId, sample.InputIdentityHash, sample.SamplingVersion, [sample], CancellationToken.None);

        var scan2 = await repository.StartScanAsync(libraryRoot.LibraryRootId, CancellationToken.None);
        var second = await repository.UpsertScannedFileAsync(
            new ScannedMediaFile(libraryRoot.LibraryRootId, mediaPath, PathPolicy.NormalizeForIdentity(mediaPath), "fixture.mp4", ".mp4", 101, now.AddSeconds(1), now.AddSeconds(1), "TEST-VOLUME", "TEST-FILE", "quick-v2"),
            scan2, CancellationToken.None);
        await repository.CompleteScanAsync(scan2, new ScanCompletion("Completed", 1, 0, 1, 0, 0, now.AddSeconds(1)), CancellationToken.None);
        Assert.Equal(first.MediaFile.MediaFileId, second.MediaFile.MediaFileId);
        Assert.NotEqual(first.MediaFile.MediaContentId, second.MediaFile.MediaContentId);

        var queued = await jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(sample.VisualSampleId), 2, null, CancellationToken.None);
        using var pipeline = CreatePipeline(repository, jobs);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var completed = (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(value => value.JobId == queued.JobId);
        Assert.Equal(JobState.Partial, completed.State);
        Assert.Equal("SupersededSourceVersion", completed.Phase);
        Assert.Null(completed.LastErrorCode);
        Assert.Empty(await repository.GetCompletedVisualEmbeddingsAsync(CancellationToken.None));
        Assert.True(await repository.IntegrityCheckAsync(CancellationToken.None));
    }

    private MediaPipeline CreatePipeline(SqliteLibraryRepository repository, SqliteJobStore jobs) =>
        new(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log);
}
