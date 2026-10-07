using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class JobLaneAcquisitionTests : IAsyncLifetime, IDisposable
{
    private const ProtectedSourceStageEligibility AllStages = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail |
        ProtectedSourceStageEligibility.VisualSampling | ProtectedSourceStageEligibility.NearDuplicateFingerprint;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIJobLaneTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SqliteJobStore _jobs = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _log = new JsonFileLogger(_paths);
        _repository = new SqliteLibraryRepository(_paths, _log);
        _jobs = new SqliteJobStore(_paths, _log);
        await _repository.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task SourceLaneOnlyTakesJobsOfItsOwnRoots()
    {
        var a = await CreateMediaAsync("drive-a", "a.mp4");
        var b = await CreateMediaAsync("drive-b", "b.mp4");
        await EnqueueLegacyMetadataAsync(b);
        await EnqueueLegacyMetadataAsync(a);

        var fromA = await AcquireAsync(new JobLaneFilter(JobLaneKind.Source, [a.LibraryRootId]));
        var fromB = await AcquireAsync(new JobLaneFilter(JobLaneKind.Source, [b.LibraryRootId]));

        Assert.Equal(a.MediaFileId, Payload(fromA).MediaFileId);
        Assert.Equal(b.MediaFileId, Payload(fromB).MediaFileId);
    }

    [Fact]
    public async Task EveryQuickPassRunsBeforeAnyDeepPassOnTheSameDrive()
    {
        var first = await CreateMediaAsync("drive-a", "first.mp4");
        var second = await CreateMediaAsync("drive-a", "second.mp4");
        await EnqueueLegacyMetadataAsync(first);
        await EnqueueLegacyMetadataAsync(second);
        var lane = new JobLaneFilter(JobLaneKind.Source, [first.LibraryRootId]);

        var order = new List<(Guid Media, bool Quick)>();
        for (var i = 0; i < 4; i++)
        {
            var job = await AcquireAsync(lane);
            order.Add((Payload(job).MediaFileId, JobLanes.IsQuickPass(Payload(job).EligibleStages)));
            await _jobs.CompleteAsync(job.JobId, JobState.Completed, "Test", CancellationToken.None);
        }

        Assert.Equal([true, true, false, false], order.Select(item => item.Quick));
        Assert.Equal([first.MediaFileId, second.MediaFileId, first.MediaFileId, second.MediaFileId], order.Select(item => item.Media));
        Assert.Null(await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, lane, CancellationToken.None));
    }

    [Fact]
    public async Task ComputeLaneNeverTakesSourceWorkAndSourceLaneNeverTakesComputeWork()
    {
        var media = await CreateMediaAsync("drive-a", "a.mp4");
        await EnqueueLegacyMetadataAsync(media);
        var embedding = await _jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(Guid.NewGuid()), 2, null, CancellationToken.None);

        var compute = await AcquireAsync(JobLaneFilter.Compute);
        Assert.Null(await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, JobLaneFilter.Compute, CancellationToken.None));
        var source = await AcquireAsync(new JobLaneFilter(JobLaneKind.Source, [media.LibraryRootId]));

        Assert.Equal(embedding.JobId, compute.JobId);
        Assert.Equal(JobType.ProtectedSourceOrchestration, source.JobType);
    }

    [Fact]
    public async Task ScanComesBeforeFileWorkOnItsDrive()
    {
        var media = await CreateMediaAsync("drive-a", "a.mp4");
        await EnqueueLegacyMetadataAsync(media);
        var scan = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(media.LibraryRootId, 1), 2, null, CancellationToken.None);

        var first = await AcquireAsync(new JobLaneFilter(JobLaneKind.Source, [media.LibraryRootId]));

        Assert.Equal(scan.JobId, first.JobId);
    }

    [Fact]
    public async Task UnattributedWorkGoesOnlyToTheLaneThatAcceptsIt()
    {
        var media = await CreateMediaAsync("drive-a", "a.mp4");
        var orphan = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(Guid.NewGuid(), Guid.NewGuid(), ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);

        Assert.Null(await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, new JobLaneFilter(JobLaneKind.Source, [media.LibraryRootId]), CancellationToken.None));
        var taken = await AcquireAsync(new JobLaneFilter(JobLaneKind.Source, [media.LibraryRootId], IncludeUnattributed: true));

        Assert.Equal(orphan.JobId, taken.JobId);
    }

    [Fact]
    public async Task WithoutALaneTheLegacyOldestFirstOrderIsKept()
    {
        var media = await CreateMediaAsync("drive-a", "a.mp4");
        var embedding = await _jobs.EnqueueAsync(JobType.VisualEmbedding, new VisualEmbeddingPayload(Guid.NewGuid()), 2, null, CancellationToken.None);
        await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(media.LibraryRootId, 1), 2, null, CancellationToken.None);

        var first = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);

        Assert.Equal(embedding.JobId, first!.JobId);
    }

    private async Task<Job> AcquireAsync(JobLaneFilter lane) =>
        await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, lane, CancellationToken.None)
        ?? throw new Xunit.Sdk.XunitException("Expected a job for the lane.");

    private static ProtectedSourceOrchestrationPayload Payload(Job job) => JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(job.PayloadJson)!;

    private Task EnqueueLegacyMetadataAsync(MediaFile media) =>
        _jobs.EnqueueAsync(JobType.Metadata, new MediaPayload(media.MediaFileId), 2, null, CancellationToken.None);

    private async Task<MediaFile> CreateMediaAsync(string folder, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "media", folder)).FullName;
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
        var root = await _repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        ReconcileResult? result = null;
        await foreach (var scanned in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
            if (string.Equals(scanned.FullPath, path, StringComparison.OrdinalIgnoreCase)) result = await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        return Assert.IsType<ReconcileResult>(result).MediaFile;
    }
}
