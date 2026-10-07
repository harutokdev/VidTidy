using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class CurrentStateProjectionQueryTests : IAsyncLifetime, IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), "LocalMediaAICurrentProjectionTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_fixtureRoot);
        File.WriteAllText(Path.Combine(_fixtureRoot, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_fixtureRoot);
        _log = new JsonFileLogger(_paths);
        await new SqliteLibraryRepository(_paths, _log).InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_fixtureRoot) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_fixtureRoot, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ActiveQueryIsIndependentFromRecentHistoryLimit()
    {
        var jobs = new SqliteJobStore(_paths, _log);
        var oldActive = await jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(Guid.NewGuid()), 3, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        for (var index = 0; index < 60; index++)
        {
            await jobs.EnqueueAsync(JobType.TagSuggestion, new MediaPayload(Guid.NewGuid()), 1, null, CancellationToken.None);
            var acquired = await jobs.TryAcquireNextAsync(CancellationToken.None);
            Assert.NotNull(acquired);
            await jobs.CompleteAsync(acquired.JobId, JobState.Completed, "Completed", CancellationToken.None);
        }

        var recent = await jobs.GetRecentAsync(50, CancellationToken.None);
        var active = await jobs.GetActiveAsync(CancellationToken.None);

        Assert.DoesNotContain(recent, job => job.JobId == oldActive.JobId);
        Assert.Contains(active, job => job.JobId == oldActive.JobId);
    }

    [Fact]
    public async Task EmbeddingCompletenessUsesCurrentContentAndCurrentModelProfile()
    {
        var mediaDirectory = Path.Combine(_fixtureRoot, "media");
        Directory.CreateDirectory(mediaDirectory);
        var sourcePath = Path.Combine(mediaDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        var repository = new SqliteLibraryRepository(_paths, _log);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        var media = await ScanSingleAsync(repository, root);
        var model = SemanticModelProfile.DevelopmentProductionCandidate;
        var execution = new SemanticExecutionProfile("cuda", model.DefaultDtype);
        var oldSample = CreateSample(media);
        await repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, oldSample.InputIdentityHash, oldSample.SamplingVersion, [oldSample], CancellationToken.None);
        await repository.UpsertVisualEmbeddingAsync(CreateEmbedding(oldSample, model, execution, VisualEmbeddingStatus.Failed, "old-failure"), CancellationToken.None);

        var missingOld = await repository.GetCurrentSemanticEmbeddingCompletenessAsync(model, execution, null, CancellationToken.None);
        Assert.Equal(new SemanticEmbeddingCompleteness(1, 0, 1), missingOld);

        await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 4100).Select(index => (byte)((index + 17) % 251)).ToArray());
        media = await ScanSingleAsync(repository, root);
        var staleExcluded = await repository.GetCurrentSemanticEmbeddingCompletenessAsync(model, execution, null, CancellationToken.None);
        Assert.Equal(new SemanticEmbeddingCompleteness(0, 0, 0), staleExcluded);

        var currentSample = CreateSample(media);
        await repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, currentSample.InputIdentityHash, currentSample.SamplingVersion, [currentSample], CancellationToken.None);
        var currentMissing = await repository.GetCurrentSemanticEmbeddingCompletenessAsync(model, execution, null, CancellationToken.None);
        Assert.Equal(new SemanticEmbeddingCompleteness(1, 0, 1), currentMissing);

        await repository.UpsertVisualEmbeddingAsync(CreateEmbedding(currentSample, model, execution, VisualEmbeddingStatus.Completed, "current-complete"), CancellationToken.None);
        var recovered = await repository.GetCurrentSemanticEmbeddingCompletenessAsync(model, execution, null, CancellationToken.None);
        Assert.Equal(new SemanticEmbeddingCompleteness(1, 1, 0), recovered);
    }

    [Fact]
    public async Task LatestScanPublicationDefinesCurrentImpactAndCompletedRecoveryClearsIt()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        var directory = Path.Combine(_fixtureRoot, "scan-currentness");
        Directory.CreateDirectory(directory);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var interrupted = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await repository.CompleteScanAsync(interrupted, new ScanCompletion("Interrupted", 2, 1, 0, 0, 1, DateTimeOffset.UtcNow.AddSeconds(-1)), CancellationToken.None);

        var incomplete = Assert.Single(await repository.GetCurrentRootScanCurrentnessAsync(CancellationToken.None));
        Assert.Equal(root.LibraryRootId, incomplete.LibraryRootId);
        Assert.Equal(root.RegistrationRevision, incomplete.RegistrationRevision);
        Assert.Equal("Interrupted", incomplete.State);

        var recovered = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await repository.CompleteScanAsync(recovered, new ScanCompletion("Completed", 2, 0, 1, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);

        var complete = Assert.Single(await repository.GetCurrentRootScanCurrentnessAsync(CancellationToken.None));
        Assert.Equal("Completed", complete.State);
        Assert.Equal(0, complete.ErrorCount);
    }

    [Fact]
    public async Task ScanCurrentnessDoesNotCrossRegistrationLifecycle()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        var directory = Path.Combine(_fixtureRoot, "scan-lifecycle");
        Directory.CreateDirectory(directory);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var interrupted = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        await repository.CompleteScanAsync(interrupted, new ScanCompletion("Interrupted", 0, 0, 0, 0, 1, DateTimeOffset.UtcNow), CancellationToken.None);

        await repository.UnregisterRootAsync(root.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);
        var registeredAgain = await repository.ReregisterRootAsync(directory, CancellationToken.None);
        Assert.Equal(root.LibraryRootId, registeredAgain.LibraryRootId);
        Assert.True(registeredAgain.RegistrationRevision > root.RegistrationRevision);
        Assert.Empty(await repository.GetCurrentRootScanCurrentnessAsync(CancellationToken.None));
    }

    private static async Task<MediaFile> ScanSingleAsync(SqliteLibraryRepository repository, LibraryRoot root)
    {
        var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        ReconcileResult? result = null;
        await foreach (var file in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
            result = await repository.UpsertScannedFileAsync(file, scanId, CancellationToken.None);
        await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.NotNull(result);
        return result.MediaFile;
    }

    [Fact]
    public async Task SemanticRowsUseSpecificRootAndAllActiveRootsBeforeRanking()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        var model = SemanticModelProfile.DevelopmentProductionCandidate;
        var execution = new SemanticExecutionProfile("cuda", model.DefaultDtype);
        var roots = new List<LibraryRoot>();
        for (var index = 0; index < 2; index++)
        {
            var directory = Path.Combine(_fixtureRoot, $"semantic-scope-{index}");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "sample.mp4"), new byte[100 + index]);
            var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
            roots.Add(root);
            var media = await ScanSingleAsync(repository, root);
            var sample = CreateSample(media);
            await repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, sample.InputIdentityHash, sample.SamplingVersion, [sample], CancellationToken.None);
            await repository.UpsertVisualEmbeddingAsync(CreateEmbedding(sample, model, execution, VisualEmbeddingStatus.Completed, $"root-{index}"), CancellationToken.None);
        }
        Assert.Equal(2, (await repository.GetCurrentSemanticSearchRowsAsync(model, execution, null, CancellationToken.None)).Count);
        var specific = Assert.Single(await repository.GetCurrentSemanticSearchRowsAsync(model, execution, roots[0].LibraryRootId, CancellationToken.None));
        Assert.Equal(roots[0].LibraryRootId, specific.LibraryRootId);
        await repository.UnregisterRootAsync(roots[1].LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Single(await repository.GetCurrentSemanticSearchRowsAsync(model, execution, null, CancellationToken.None));
    }

    private static VisualSample CreateSample(MediaFile media)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, $"content:{media.MediaContentId:D}", 0, 0, 0,
            VisualSamplingPolicy.FixedIntervalV1.Version, $"sample:{media.MediaContentId:D}", "frames/sample.jpg", "frame-sha", 100,
            VisualSampleStatus.Completed, null, null, now, now);
    }

    private static VisualEmbedding CreateEmbedding(VisualSample sample, SemanticModelProfile model, SemanticExecutionProfile execution, VisualEmbeddingStatus status, string marker)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, $"embedding:{marker}:{sample.VisualSampleId:D}", model.ModelId,
            model.ModelRevision, model.ProcessorVersion, sample.SamplingVersion, execution.Dtype, model.Dimension, true,
            status == VisualEmbeddingStatus.Completed ? new byte[model.Dimension * sizeof(float)] : null, status,
            status == VisualEmbeddingStatus.Failed ? "HISTORICAL_FAILURE" : null, null, now, now);
    }
}
