using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ProtectedSourcePublicationCutoverTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIProtectedPublicationTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _repository = null!;
    private SqliteJobStore _jobs = null!;
    private bool _disposed;

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
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SuccessfulGuardedPublicationIsAtomicAndLeaseSurvivesCommit()
    {
        var media = await CreateScannedByteMediaAsync("publish-success.mp4");
        var job = await AcquireProtectedJobAsync(media, ProtectedSourceStageEligibility.Metadata);
        var session = await ProcessingSourceSession.AcquireAsync(_paths, media.MediaFileId, media.MediaContentId, media.Path, _log, CancellationToken.None);
        var metadata = CreateMetadata(media, "guarded-current");
        var prepared = await PrepareSyntheticArtifactAsync(session, media);
        var bundle = CreateBundle(session, metadata, prepared);
        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();

        var result = await _repository.GuardedPublishProtectedSourceAsync(session, job.JobId, bundle, CancellationToken.None);

        Assert.Equal(GuardedPublicationDisposition.Published, result.Disposition);
        Assert.True(session.IsLeaseHeld);
        AssertWriteBlocked(media.Path);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM artifact WHERE analysis_id=$id;", ("$id", prepared.Analysis.AnalysisId.ToString("D"))));
        Assert.Equal("Completed", await TextAsync("SELECT state FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));
        Assert.Equal("GuardedPublished", await TextAsync("SELECT phase FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));

        session.CompletePublicationBoundary(ProcessingPublicationResolution.Published);
        await session.DisposeAsync();
        using var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task VisualEmbeddingRecoveryUsesCanonicalPublishedIdsAndIsIdempotent()
    {
        var media = await CreateScannedByteMediaAsync("canonical-visual-sample.mp4");
        var canonical = CreateSample(media, "canonical") with
        {
            FrameRelativePath = "visual-samples/canonical.jpg",
            FrameSha256 = "canonical-frame",
            FrameByteSize = 10
        };
        await _repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, canonical.InputIdentityHash, canonical.SamplingVersion, [canonical], CancellationToken.None);

        var publicationJob = await AcquireProtectedJobAsync(media, ProtectedSourceStageEligibility.VisualSampling);
        var session = await ProcessingSourceSession.AcquireAsync(_paths, media.MediaFileId, media.MediaContentId, media.Path, _log, CancellationToken.None);
        var generatedCollision = canonical with { VisualSampleId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow };
        var generatedNew = CreateSample(media, "new-cache-key") with
        {
            SampleIndex = 1,
            FrameRelativePath = "visual-samples/new.jpg",
            FrameSha256 = "new-frame",
            FrameByteSize = 11
        };
        var visualSampleDirectory = Path.Combine(_paths.CacheRoot, "visual-samples");
        Directory.CreateDirectory(visualSampleDirectory);
        await File.WriteAllBytesAsync(Path.Combine(visualSampleDirectory, "canonical.jpg"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(visualSampleDirectory, "new.jpg"), new byte[11]);
        var bundle = new ProtectedSourcePublicationBundle(session.SessionId, media.MediaFileId, media.MediaContentId, null, [generatedCollision, generatedNew], []);
        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();

        var publication = await _repository.GuardedPublishProtectedSourceAsync(session, publicationJob.JobId, bundle, CancellationToken.None);
        Assert.Equal(GuardedPublicationDisposition.Published, publication.Disposition);
        session.CompletePublicationBoundary(ProcessingPublicationResolution.Published);
        await session.DisposeAsync();

        Assert.NotNull(await _repository.GetVisualSampleRecordAsync(canonical.VisualSampleId, CancellationToken.None));
        Assert.Null(await _repository.GetVisualSampleRecordAsync(generatedCollision.VisualSampleId, CancellationToken.None));
        Assert.NotNull(await _repository.GetVisualSampleRecordAsync(generatedNew.VisualSampleId, CancellationToken.None));

        using var pipeline = new MediaPipeline(_repository, _jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log);
        await pipeline.EnqueueMissingVisualEmbeddingJobsAsync(null, media.MediaFileId, CancellationToken.None);
        await pipeline.EnqueueMissingVisualEmbeddingJobsAsync(null, media.MediaFileId, CancellationToken.None);

        var embeddingJobs = (await _jobs.GetRecentAsync(20, CancellationToken.None)).Where(value => value.JobType == JobType.VisualEmbedding).ToArray();
        Assert.Equal(2, embeddingJobs.Length);
        var payloadIds = embeddingJobs.Select(value => JsonSerializer.Deserialize<VisualEmbeddingPayload>(value.PayloadJson)!.VisualSampleId).ToHashSet();
        Assert.Contains(canonical.VisualSampleId, payloadIds);
        Assert.Contains(generatedNew.VisualSampleId, payloadIds);
        Assert.DoesNotContain(generatedCollision.VisualSampleId, payloadIds);

        var model = SemanticModelRegistry.ResolveDevelopmentCandidate();
        var execution = SemanticExecutionProfile.Resolve(model);
        await _repository.UpsertVisualEmbeddingAsync(CreateEmbedding(canonical, model, execution, "canonical-embedding"), CancellationToken.None);
        await _repository.UpsertVisualEmbeddingAsync(CreateEmbedding(generatedNew, model, execution, "new-embedding"), CancellationToken.None);
        await pipeline.EnqueueMissingVisualEmbeddingJobsAsync(null, media.MediaFileId, CancellationToken.None);
        Assert.Equal(2, (await _jobs.GetRecentAsync(20, CancellationToken.None)).Count(value => value.JobType == JobType.VisualEmbedding));
        Assert.Equal(2, (await _repository.GetCurrentSemanticSearchRowsAsync(model, execution, null, CancellationToken.None)).Count);

        var staleCandidate = CreateSample(media, "stale-missing-embedding") with
        {
            SampleIndex = 2,
            FrameRelativePath = "visual-samples/stale.jpg"
        };
        await _repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, staleCandidate.InputIdentityHash, staleCandidate.SamplingVersion, [staleCandidate], CancellationToken.None);
        Assert.Contains((await _repository.GetMissingVisualEmbeddingCandidatesAsync(model, execution, null, media.MediaFileId, CancellationToken.None)),
            value => value.Sample.VisualSampleId == staleCandidate.VisualSampleId);

        var changed = await ReconcileAsChangedAsync(media, "canonical-content-changed");
        Assert.NotEqual(media.MediaContentId, changed.MediaContentId);
        Assert.Empty(await _repository.GetMissingVisualEmbeddingCandidatesAsync(model, execution, null, media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task ContentTransitionRejectsEntireBundleAndFinalizesSupersededJob()
    {
        var mediaA = await CreateScannedByteMediaAsync("publish-stale.mp4");
        var job = await AcquireProtectedJobAsync(mediaA, ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.VisualSampling);
        var session = await ProcessingSourceSession.AcquireAsync(_paths, mediaA.MediaFileId, mediaA.MediaContentId, mediaA.Path, _log, CancellationToken.None);
        var sample = CreateSample(mediaA, "stale-sample");
        var bundle = new ProtectedSourcePublicationBundle(session.SessionId, mediaA.MediaFileId, mediaA.MediaContentId, CreateMetadata(mediaA, "stale"), [sample], []);
        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();
        var mediaB = await ReconcileAsChangedAsync(mediaA, "changed-before-publication");

        var result = await _repository.GuardedPublishProtectedSourceAsync(session, job.JobId, bundle, CancellationToken.None);

        Assert.NotEqual(mediaA.MediaContentId, mediaB.MediaContentId);
        Assert.Equal(GuardedPublicationDisposition.Superseded, result.Disposition);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", mediaA.MediaFileId.ToString("D"))));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM visual_sample WHERE cache_key=$cache;", ("$cache", sample.CacheKey)));
        Assert.Equal("Partial", await TextAsync("SELECT state FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));
        Assert.Equal("SupersededSourceVersion", await TextAsync("SELECT phase FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));
        Assert.True(session.IsLeaseHeld);
        session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ReconciliationInvalidatesFileKeyedMetadataForNewContentGeneration()
    {
        var mediaA = await CreateScannedByteMediaAsync("metadata-transition.mp4");
        await _repository.UpsertMetadataAsync(CreateMetadata(mediaA, "legacy-a"), CancellationToken.None);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", mediaA.MediaFileId.ToString("D"))));

        var mediaB = await ReconcileAsChangedAsync(mediaA, "metadata-b");

        Assert.NotEqual(mediaA.MediaContentId, mediaB.MediaContentId);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", mediaA.MediaFileId.ToString("D"))));
        var presented = await _repository.GetMediaListItemAsync(mediaA.MediaFileId, CancellationToken.None);
        Assert.NotNull(presented);
        Assert.Null(presented.Metadata);
    }

    [Fact]
    public async Task SemanticSearchExcludesOldEmbeddingAndIncludesCurrentEmbedding()
    {
        var mediaA = await CreateScannedByteMediaAsync("semantic-current.mp4");
        var sampleA = CreateSample(mediaA, "semantic-a");
        await _repository.ReplaceVisualSamplesAsync(mediaA.MediaFileId, mediaA.MediaContentId, sampleA.InputIdentityHash, sampleA.SamplingVersion, [sampleA], CancellationToken.None);
        var model = SemanticModelRegistry.ResolveDevelopmentCandidate();
        var execution = SemanticExecutionProfile.Resolve(model);
        await _repository.UpsertVisualEmbeddingAsync(CreateEmbedding(sampleA, model, execution, "embedding-a"), CancellationToken.None);
        var mediaB = await ReconcileAsChangedAsync(mediaA, "semantic-b");

        Assert.Empty(await _repository.GetCurrentSemanticSearchRowsAsync(model, execution, null, CancellationToken.None));
        Assert.Null(await _repository.GetVisualSampleAsync(sampleA.VisualSampleId, CancellationToken.None));
        Assert.Empty(await _repository.GetCompletedVisualEmbeddingsAsync(CancellationToken.None));

        var sampleB = CreateSample(mediaB, "semantic-b");
        await _repository.ReplaceVisualSamplesAsync(mediaB.MediaFileId, mediaB.MediaContentId, sampleB.InputIdentityHash, sampleB.SamplingVersion, [sampleB], CancellationToken.None);
        await _repository.UpsertVisualEmbeddingAsync(CreateEmbedding(sampleB, model, execution, "embedding-b"), CancellationToken.None);
        var rows = await _repository.GetCurrentSemanticSearchRowsAsync(model, execution, null, CancellationToken.None);
        Assert.Equal(sampleB.VisualSampleId, Assert.Single(rows).VisualSampleId);
        Assert.Equal(2, await ScalarAsync("SELECT COUNT(1) FROM visual_embedding;"));
    }

    [Fact]
    public async Task PublicationFailureRollsBackAllCurrentVisibilityAndDoesNotCompleteJob()
    {
        var media = await CreateScannedByteMediaAsync("publish-rollback.mp4");
        var job = await AcquireProtectedJobAsync(media, ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.VisualSampling);
        var session = await ProcessingSourceSession.AcquireAsync(_paths, media.MediaFileId, media.MediaContentId, media.Path, _log, CancellationToken.None);
        var sample = CreateSample(media, "rollback-sample");
        var bundle = new ProtectedSourcePublicationBundle(session.SessionId, media.MediaFileId, media.MediaContentId, CreateMetadata(media, "rollback"), [sample], []);
        session.CompleteSourceStaging();
        session.EnterDerivedOrPublicationPhase();
        await ExecuteSqlAsync("CREATE TRIGGER synthetic_guarded_failure BEFORE INSERT ON visual_sample BEGIN SELECT RAISE(ABORT,'synthetic-guarded-failure'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => _repository.GuardedPublishProtectedSourceAsync(session, job.JobId, bundle, CancellationToken.None));

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM visual_sample WHERE cache_key=$cache;", ("$cache", sample.CacheKey)));
        Assert.Equal("Running", await TextAsync("SELECT state FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));
        await ExecuteSqlAsync("DROP TRIGGER synthetic_guarded_failure;");
        session.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LegacyPendingAndRecoveredRetryWorkTransitionsIdempotentlyWithEligibilityPreserved()
    {
        var media = await CreateScannedByteMediaAsync("legacy-transition.mp4");
        var metadata = await _jobs.EnqueueAsync(JobType.Metadata, new MediaPayload(media.MediaFileId), 2, null, CancellationToken.None);
        var fullHash = await _jobs.EnqueueAsync(JobType.FullHash, new FullHashPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), 3, null, CancellationToken.None);
        var running = await _jobs.TryAcquireNextAsync(CancellationToken.None);
        Assert.Equal(metadata.JobId, running!.JobId);
        await _jobs.RecoverInterruptedJobsAsync(CancellationToken.None);

        var first = await _jobs.TransitionLegacySourceJobsAsync(CancellationToken.None);
        var second = await _jobs.TransitionLegacySourceJobsAsync(CancellationToken.None);
        var jobs = await _jobs.GetRecentAsync(20, CancellationToken.None);
        // One file's work is split into a quick pass (metadata + thumbnail) and a deep pass; together they keep every stage.
        var passes = jobs.Where(value => value.JobType == JobType.ProtectedSourceOrchestration)
            .Select(value => JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(value.PayloadJson)!.EligibleStages).ToArray();
        var payload = new { EligibleStages = passes.Aggregate(ProtectedSourceStageEligibility.None, (all, stages) => all | stages) };

        Assert.Equal(2, first.SupersededLegacyJobs);
        Assert.Equal(2, first.ProtectedJobsCreated);
        Assert.Equal(0, second.SupersededLegacyJobs);
        Assert.Equal(0, second.ProtectedJobsCreated);
        Assert.Equal(2, passes.Length);
        Assert.Single(passes, LocalMediaAI.Core.Processing.JobLanes.IsQuickPass);
        Assert.True(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.Metadata));
        Assert.True(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.Thumbnail));
        Assert.True(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.VisualSampling));
        Assert.True(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.NearDuplicateFingerprint));
        Assert.True(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.FullHash));
        Assert.All(jobs.Where(value => value.JobId == metadata.JobId || value.JobId == fullHash.JobId), value =>
        {
            Assert.Equal(JobState.Cancelled, value.State);
            Assert.Equal("SupersededByProtectedOrchestration", value.Phase);
        });
    }

    [Fact]
    public async Task ProtectedDispatcherStructurallyTransitionsLateLegacyWorkAndNeverReturnsIt()
    {
        var media = await CreateScannedByteMediaAsync("dispatcher-block.mp4");
        var legacy = await _jobs.EnqueueAsync(JobType.Thumbnail, new MediaPayload(media.MediaFileId), 2, null, CancellationToken.None);

        var acquired = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);

        Assert.NotNull(acquired);
        Assert.Equal(JobType.ProtectedSourceOrchestration, acquired.JobType);
        Assert.True(ProtectedPipelineDispatchPolicy.CanUseLegacyUnprotectedExecutor(JobType.Thumbnail, ProtectedPipelineDispatchMode.LegacyPreActivation));
        Assert.False(ProtectedPipelineDispatchPolicy.CanUseLegacyUnprotectedExecutor(JobType.Thumbnail, ProtectedPipelineDispatchMode.ProtectedActivated));
        ProtectedPipelineDispatchPolicy.EnsureProtectedDispatch(acquired);
        Assert.Throws<InvalidOperationException>(() => ProtectedPipelineDispatchPolicy.EnsureProtectedDispatch(legacy));
        var persistedLegacy = Assert.Single(await _jobs.GetRecentAsync(20, CancellationToken.None), value => value.JobId == legacy.JobId);
        Assert.Equal(JobState.Cancelled, persistedLegacy.State);
    }

    [Fact]
    public async Task LateLegacyStageIsNotLostByMergingIntoAlreadyRunningProtectedAttempt()
    {
        var media = await CreateScannedByteMediaAsync("late-stage.mp4");
        await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var running = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.NotNull(running);
        await _jobs.EnqueueUniqueAsync(JobType.FullHash,
            new FullHashPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), 3, null, CancellationToken.None);

        var transition = await _jobs.TransitionLegacySourceJobsAsync(CancellationToken.None);
        var protectedJobs = (await _jobs.GetRecentAsync(20, CancellationToken.None)).Where(value => value.JobType == JobType.ProtectedSourceOrchestration).ToArray();

        Assert.Equal(1, transition.ProtectedJobsCreated);
        Assert.Equal(2, protectedJobs.Length);
        var runningPayload = JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(Assert.Single(protectedJobs, value => value.State == JobState.Running).PayloadJson)!;
        var pendingPayload = JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(Assert.Single(protectedJobs, value => value.State == JobState.Pending).PayloadJson)!;
        Assert.Equal(ProtectedSourceStageEligibility.Metadata, runningPayload.EligibleStages);
        Assert.Equal(ProtectedSourceStageEligibility.FullHash, pendingPayload.EligibleStages);
    }

    [Fact]
    public async Task CandidateCrashBoundaryRetriesWithFreshSessionAndPublishesOnlyOnce()
    {
        var media = await CreateScannedByteMediaAsync("candidate-retry.mp4");
        var queued = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var firstJob = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.Equal(queued.JobId, firstJob!.JobId);
        var orchestrator = new PreActivationProtectedSourceOrchestrator(_paths, _repository, _jobs, _log);
        Guid firstSession = Guid.Empty;

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(firstJob, media, (session, _) =>
        {
            firstSession = session.SessionId;
            session.CreateUnpublishedArtifact("crash/unpublished.tmp");
            throw new InvalidOperationException("synthetic pre-publication failure");
        }, CancellationToken.None));

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.Equal("RetryWaiting", await TextAsync("SELECT state FROM job WHERE job_id=$id;", ("$id", queued.JobId.ToString("D"))));
        await ExecuteSqlAsync("UPDATE job SET not_before=NULL WHERE job_id=$id;", ("$id", queued.JobId.ToString("D")));
        var retryJob = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Guid retrySession = Guid.Empty;
        var result = await orchestrator.ExecuteAsync(retryJob!, media, async (session, cancellationToken) =>
        {
            retrySession = session.SessionId;
            await session.ExecuteSourceStageAsync("read-proof", async (source, token) =>
            {
                await using var stream = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[16];
                return await stream.ReadAsync(buffer, token);
            }, cancellationToken);
            return new ProtectedSourcePublicationBundle(session.SessionId, media.MediaFileId, media.MediaContentId, CreateMetadata(media, "retry-current"), [], []);
        }, CancellationToken.None);

        Assert.NotEqual(firstSession, retrySession);
        Assert.Equal(GuardedPublicationDisposition.Published, result.Disposition);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM job_event WHERE job_id=$id AND event_name='ProtectedPublicationCommitted';", ("$id", queued.JobId.ToString("D"))));
    }

    [Fact]
    public async Task CandidateLeaseAcquisitionFailureFailsClosedAndSchedulesBoundedRetry()
    {
        var media = await CreateScannedByteMediaAsync("candidate-conflicting-writer.mp4");
        var queued = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var running = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.Equal(queued.JobId, running!.JobId);
        var orchestrator = new PreActivationProtectedSourceOrchestrator(_paths, _repository, _jobs, _log);
        await using var writer = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        await Assert.ThrowsAsync<ProcessingSourceLeaseUnavailableException>(() => orchestrator.ExecuteAsync(
            running,
            media,
            (_, _) => throw new InvalidOperationException("Preparation must not run without the authoritative lease."),
            CancellationToken.None));

        var persisted = await _jobs.GetJobAsync(queued.JobId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(JobState.RetryWaiting, persisted.State);
        Assert.Equal("PROTECTED_SOURCE_ATTEMPT_FAILED", persisted.LastErrorCode);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
    }

    [Fact]
    public async Task CandidateIntegrationSmokeUsesGeneratedMediaAndKeepsFullHashOptional()
    {
        var media = await CreateScannedVideoMediaAsync();
        var queued = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var job = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.Equal(queued.JobId, job!.JobId);
        var payload = JsonSerializer.Deserialize<ProtectedSourceOrchestrationPayload>(job.PayloadJson)!;
        Assert.Equal(ProtectedSourceStageEligibility.Metadata, payload.EligibleStages);
        Assert.False(payload.EligibleStages.HasFlag(ProtectedSourceStageEligibility.FullHash));
        var adapter = new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30));
        var orchestrator = new PreActivationProtectedSourceOrchestrator(_paths, _repository, _jobs, _log);

        var result = await orchestrator.ExecuteAsync(job, media, async (session, cancellationToken) =>
        {
            var probe = await session.ExecuteSourceStageAsync("ffprobe", (source, token) => adapter.ProbeAsync(source, media, token), cancellationToken);
            Assert.True(session.IsLeaseHeld);
            AssertWriteBlocked(media.Path);
            return new ProtectedSourcePublicationBundle(session.SessionId, media.MediaFileId, media.MediaContentId, probe.Metadata, [], []);
        }, CancellationToken.None);

        Assert.Equal(GuardedPublicationDisposition.Published, result.Disposition);
        Assert.Equal("Completed", await TextAsync("SELECT state FROM job WHERE job_id=$id;", ("$id", job.JobId.ToString("D"))));
        using var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ActivatedProductionPipelineTransitionsLegacyMetadataAndPublishesUnderOneSession()
    {
        var media = await CreateScannedVideoMediaAsync();
        var legacy = await _jobs.EnqueueAsync(JobType.Metadata, new MediaPayload(media.MediaFileId), 2, null, CancellationToken.None);
        Guid observedSession = Guid.Empty;
        var observedUnpublished = 0;
        var probes = new List<(Guid Session, long Metadata, long Samples)>();
        using var pipeline = new MediaPipeline(_repository, _jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30)), _log,
            resourceAdmission: new AlwaysAdmitResourcePolicy(), paths: _paths,
            protectedPublicationProbe: async (session, _) =>
            {
                observedSession = session.SessionId;
                observedUnpublished = session.UnpublishedArtifacts.Count;
                Assert.True(session.IsLeaseHeld);
                AssertWriteBlocked(media.Path);
                probes.Add((session.SessionId,
                    await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))),
                    await ScalarAsync("SELECT COUNT(1) FROM visual_sample WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D")))));
            });

        Assert.Equal(ProtectedPipelineDispatchMode.ProtectedActivated, MediaPipeline.DispatchMode);
        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));
        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));

        Assert.NotEqual(Guid.Empty, observedSession);
        Assert.True(observedUnpublished > 0);
        // Quick pass publishes metadata + thumbnail; the deep pass then publishes samples under its own session.
        Assert.Equal(2, probes.Count);
        Assert.NotEqual(probes[0].Session, probes[1].Session);
        Assert.Equal((0L, 0L), (probes[0].Metadata, probes[0].Samples));
        Assert.Equal((1L, 0L), (probes[1].Metadata, probes[1].Samples));
        var jobs = await _jobs.GetRecentAsync(30, CancellationToken.None);
        Assert.Equal(JobState.Cancelled, Assert.Single(jobs, value => value.JobId == legacy.JobId).State);
        var protectedJobs = jobs.Where(value => value.JobType == JobType.ProtectedSourceOrchestration).ToArray();
        Assert.Equal(2, protectedJobs.Length);
        Assert.All(protectedJobs, value =>
        {
            Assert.Equal(JobState.Completed, value.State);
            Assert.Equal("GuardedPublished", value.Phase);
        });
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.True(await ScalarAsync("SELECT COUNT(1) FROM visual_sample WHERE media_file_id=$id AND media_content_id=$content;",
            ("$id", media.MediaFileId.ToString("D")), ("$content", media.MediaContentId.ToString("D"))) > 0);
        Assert.Equal(NearDuplicateVerificationProfile.HighPrecisionV1.RequiredSampleCount,
            await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_frame WHERE media_file_id=$id AND media_content_id=$content;",
                ("$id", media.MediaFileId.ToString("D")), ("$content", media.MediaContentId.ToString("D"))));
        using var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ActivatedProductionPipelineRecoversLegacyFullHashIntoProtectedExecution()
    {
        var media = await CreateScannedByteMediaAsync("activated-full-hash.mp4");
        var legacy = await _jobs.EnqueueUniqueAsync(JobType.FullHash,
            new FullHashPayload(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc), 3, null, CancellationToken.None);
        Assert.Equal(legacy.JobId, (await _jobs.TryAcquireNextAsync(CancellationToken.None))!.JobId);
        await _jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        Guid sessionId = Guid.Empty;
        using var pipeline = new MediaPipeline(_repository, _jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log), _log, paths: _paths,
            resourceAdmission: new AlwaysAdmitResourcePolicy(),
            protectedPublicationProbe: (session, _) =>
            {
                sessionId = session.SessionId;
                Assert.True(session.IsLeaseHeld);
                AssertWriteBlocked(media.Path);
                return Task.CompletedTask;
            });

        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));

        Assert.NotEqual(Guid.Empty, sessionId);
        var jobs = await _jobs.GetRecentAsync(20, CancellationToken.None);
        Assert.Equal(JobState.Cancelled, Assert.Single(jobs, value => value.JobId == legacy.JobId).State);
        Assert.Equal(JobState.Completed, Assert.Single(jobs, value => value.JobType == JobType.ProtectedSourceOrchestration).State);
        Assert.NotNull(await _repository.GetCurrentFullHashEvidenceAsync(media.MediaFileId, media.MediaContentId, "SHA-256", "full-sha256-v1", CancellationToken.None));
        using var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task ActivatedProductionPipelineRejectsStalePublicationAndRetriesWithFreshSession()
    {
        var media = await CreateScannedVideoMediaAsync();
        var queued = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var sessions = new List<Guid>();
        var failFirst = true;
        using var pipeline = new MediaPipeline(_repository, _jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30)), _log, paths: _paths,
            protectedPublicationProbe: (session, _) =>
            {
                sessions.Add(session.SessionId);
                if (failFirst)
                {
                    failFirst = false;
                    throw new InvalidOperationException("synthetic activated pre-publication interruption");
                }
                return Task.CompletedTask;
            });

        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(JobState.RetryWaiting, (await _jobs.GetJobAsync(queued.JobId, CancellationToken.None))!.State);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        await ExecuteSqlAsync("UPDATE job SET not_before=NULL WHERE job_id=$id;", ("$id", queued.JobId.ToString("D")));
        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0], sessions[1]);
        Assert.Equal(JobState.Completed, (await _jobs.GetJobAsync(queued.JobId, CancellationToken.None))!.State);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", media.MediaFileId.ToString("D"))));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM job_event WHERE job_id=$id AND event_name='ProtectedPublicationCommitted';", ("$id", queued.JobId.ToString("D"))));
    }

    [Fact]
    public async Task ActivatedProductionPipelineRejectsAWhenDatabaseTransitionsToBAtPublicationBoundary()
    {
        var mediaA = await CreateScannedVideoMediaAsync();
        await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(mediaA.MediaFileId, mediaA.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        MediaFile? mediaB = null;
        using var pipeline = new MediaPipeline(_repository, _jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30)), _log, paths: _paths,
            protectedPublicationProbe: async (session, _) =>
            {
                Assert.True(session.IsLeaseHeld);
                mediaB = await ReconcileAsChangedAsync(mediaA, "synthetic-b-" + Guid.NewGuid().ToString("N"));
            });

        Assert.True(await pipeline.ProcessNextAsync(CancellationToken.None));

        Assert.NotNull(mediaB);
        Assert.NotEqual(mediaA.MediaContentId, mediaB.MediaContentId);
        var protectedJob = Assert.Single(await _jobs.GetRecentAsync(20, CancellationToken.None), value => value.JobType == JobType.ProtectedSourceOrchestration);
        Assert.Equal(JobState.Partial, protectedJob.State);
        Assert.Equal("SupersededSourceVersion", protectedJob.Phase);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(1) FROM media_metadata WHERE media_file_id=$id;", ("$id", mediaA.MediaFileId.ToString("D"))));
    }

    [Fact]
    public void GuardedTransactionApiCannotAcceptMediaWorkOrArtifactGenerationCallbacks()
    {
        var method = typeof(SqliteLibraryRepository).GetMethod(nameof(SqliteLibraryRepository.GuardedPublishProtectedSourceAsync));
        Assert.NotNull(method);
        Assert.DoesNotContain(method.GetParameters(), parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(FfmpegAdapter));
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(ProtectedSourcePublicationBundle));
    }

    [Fact]
    public async Task SafeStopFromIdleLinearizesWithoutMutatingJobsAndRestartDefaultsEnabled()
    {
        var queued = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(Guid.NewGuid()), 2, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var retry = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(Guid.NewGuid()), 3, null, CancellationToken.None);
        Assert.Equal(retry.JobId, (await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None))!.JobId);
        await _jobs.FailOrRetryAsync(retry.JobId, "SYNTHETIC_RETRY", "Synthetic isolated retry state.", true, CancellationToken.None);
        var retryBeforeStop = await _jobs.GetJobAsync(retry.JobId, CancellationToken.None);
        using var pipeline = CreateSafeStopPipeline();
        var states = new List<ProcessingRunState>();
        pipeline.RunStateChanged += (_, args) => states.Add(args.State);

        await Task.WhenAll(
            pipeline.RequestStopAndDrainAsync(CancellationToken.None),
            pipeline.RequestStopAndDrainAsync(CancellationToken.None));

        Assert.Equal(ProcessingRunState.UserStopped, pipeline.RunState);
        Assert.Contains(ProcessingRunState.StopRequested, states);
        Assert.Contains(ProcessingRunState.UserStopped, states);
        var preserved = await _jobs.GetJobAsync(queued.JobId, CancellationToken.None);
        Assert.Equal(JobState.Pending, preserved!.State);
        Assert.Equal(queued.NotBefore, preserved.NotBefore);
        var retryAfterStop = await _jobs.GetJobAsync(retry.JobId, CancellationToken.None);
        Assert.Equal(JobState.RetryWaiting, retryAfterStop!.State);
        Assert.Equal(retryBeforeStop!.Attempt, retryAfterStop.Attempt);
        Assert.Equal(retryBeforeStop.NotBefore, retryAfterStop.NotBefore);
        await pipeline.RequestRunAsync(CancellationToken.None);
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(queued.JobId, CancellationToken.None))!.State);

        var offlineRescanRootPath = Path.Combine(_root, "offline-rescan-while-stopped");
        Directory.CreateDirectory(offlineRescanRootPath);
        var offlineRescanRoot = await _repository.AddOrGetLibraryRootAsync(offlineRescanRootPath, CancellationToken.None);
        var offlineRescanJobId = await pipeline.EnqueueScanAsync(offlineRescanRoot.LibraryRootId, CancellationToken.None);
        await pipeline.RequestRunAsync(CancellationToken.None);
        Assert.Equal(ProcessingRunState.UserStopped, pipeline.RunState);
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(offlineRescanJobId, CancellationToken.None))!.State);

        using var freshRuntime = CreateSafeStopPipeline();
        Assert.Equal(ProcessingRunState.EnabledIdle, freshRuntime.RunState);
    }

    [Fact]
    public async Task AcquisitionRaceAllowsInsideAttemptButBlocksWaitingSiblingAfterLinearization()
    {
        var firstRootPath = Path.Combine(_root, "race-root-a");
        var secondRootPath = Path.Combine(_root, "race-root-b");
        Directory.CreateDirectory(firstRootPath);
        Directory.CreateDirectory(secondRootPath);
        var firstRoot = await _repository.AddOrGetLibraryRootAsync(firstRootPath, CancellationToken.None);
        var secondRoot = await _repository.AddOrGetLibraryRootAsync(secondRootPath, CancellationToken.None);
        var firstJob = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(firstRoot.LibraryRootId), 2, null, CancellationToken.None);
        var secondJob = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(secondRoot.LibraryRootId), 2, null, CancellationToken.None);
        var acquisitionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcquisition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        using var pipeline = CreateSafeStopPipeline(beforeAcquisition: async _ =>
        {
            if (Interlocked.Increment(ref probeCalls) != 1) return;
            acquisitionEntered.TrySetResult(true);
            await releaseAcquisition.Task;
        });

        var acquisitionA = pipeline.ProcessNextAsync(CancellationToken.None);
        await acquisitionEntered.Task;
        var acquisitionB = pipeline.ProcessNextAsync(CancellationToken.None);
        var stop = pipeline.RequestStopAndDrainAsync(CancellationToken.None);
        Assert.Equal(ProcessingRunState.StopRequested, pipeline.RunState);
        releaseAcquisition.TrySetResult(true);

        Assert.True(await acquisitionA);
        Assert.False(await acquisitionB);
        await stop;
        Assert.Equal(ProcessingRunState.UserStopped, pipeline.RunState);
        var first = await _jobs.GetJobAsync(firstJob.JobId, CancellationToken.None);
        var second = await _jobs.GetJobAsync(secondJob.JobId, CancellationToken.None);
        Assert.Equal(JobState.Completed, first!.State);
        Assert.Equal(JobState.Pending, second!.State);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task ActiveProtectedWorkDrainsThenExplicitStartProcessesRetainedQueue()
    {
        var media = await CreateScannedVideoMediaAsync();
        var protectedJob = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var retainedRootPath = Path.Combine(_root, "retained-root");
        Directory.CreateDirectory(retainedRootPath);
        var retainedRoot = await _repository.AddOrGetLibraryRootAsync(retainedRootPath, CancellationToken.None);
        var publicationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainingObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessingSourceSession? observedSession = null;
        using var pipeline = CreateSafeStopPipeline(publicationProbe: async (session, _) =>
        {
            observedSession = session;
            publicationEntered.TrySetResult(true);
            await releasePublication.Task;
        });
        pipeline.RunStateChanged += (_, args) =>
        {
            if (args.State == ProcessingRunState.Draining) drainingObserved.TrySetResult(true);
        };

        var run = pipeline.RequestRunAsync(CancellationToken.None);
        await publicationEntered.Task;
        // Queued while the drive's only source lane is busy: it must wait, survive the stop, and run after Start.
        var retainedJob = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(retainedRoot.LibraryRootId), 2, null, CancellationToken.None);
        Assert.NotNull(observedSession);
        Assert.True(observedSession!.IsLeaseHeld);
        AssertWriteBlocked(media.Path);

        var stop = pipeline.RequestStopAndDrainAsync(CancellationToken.None);
        await drainingObserved.Task;
        Assert.False(stop.IsCompleted);
        Assert.False(await pipeline.StartProcessingAsync(CancellationToken.None));
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(retainedJob.JobId, CancellationToken.None))!.State);
        Assert.True(observedSession.IsLeaseHeld);

        releasePublication.TrySetResult(true);
        await Task.WhenAll(run, stop);
        Assert.Equal(ProcessingRunState.UserStopped, pipeline.RunState);
        Assert.Equal(JobState.Completed, (await _jobs.GetJobAsync(protectedJob.JobId, CancellationToken.None))!.State);
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(retainedJob.JobId, CancellationToken.None))!.State);
        using (var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }

        var automaticTrigger = pipeline.RequestRunAsync(CancellationToken.None);
        await automaticTrigger;
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(retainedJob.JobId, CancellationToken.None))!.State);

        Assert.True(await pipeline.StartProcessingAsync(CancellationToken.None));
        Assert.Equal(ProcessingRunState.EnabledIdle, pipeline.RunState);
        Assert.Equal(JobState.Completed, (await _jobs.GetJobAsync(retainedJob.JobId, CancellationToken.None))!.State);
        Assert.True(await _repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedStartUsesOneCoordinatorAndCannotDuplicateAcquisition()
    {
        var rootPath = Path.Combine(_root, "start-idempotency-root");
        Directory.CreateDirectory(rootPath);
        var libraryRoot = await _repository.AddOrGetLibraryRootAsync(rootPath, CancellationToken.None);
        var queued = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(libraryRoot.LibraryRootId), 2, null, CancellationToken.None);
        var acquisitionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcquisition = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        using var pipeline = CreateSafeStopPipeline(beforeAcquisition: async _ =>
        {
            if (Interlocked.Increment(ref probeCalls) != 1) return;
            acquisitionEntered.TrySetResult(true);
            await releaseAcquisition.Task;
        });
        await pipeline.RequestStopAndDrainAsync(CancellationToken.None);

        var firstStart = pipeline.StartProcessingAsync(CancellationToken.None);
        await acquisitionEntered.Task;
        var duplicateStart = pipeline.StartProcessingAsync(CancellationToken.None);
        Assert.False(await duplicateStart);
        releaseAcquisition.TrySetResult(true);

        Assert.True(await firstStart);
        Assert.Equal(ProcessingRunState.EnabledIdle, pipeline.RunState);
        Assert.Equal(JobState.Completed, (await _jobs.GetJobAsync(queued.JobId, CancellationToken.None))!.State);
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(1) FROM job_event WHERE job_id=$id AND event_name='Acquired';", ("$id", queued.JobId.ToString("D"))));
    }

    [Fact]
    public async Task TerminalShutdownFromIdleIsSharedPermanentAndPreservesQueuedWork()
    {
        var rootPath = Path.Combine(_root, "shutdown-idle-root");
        Directory.CreateDirectory(rootPath);
        var libraryRoot = await _repository.AddOrGetLibraryRootAsync(rootPath, CancellationToken.None);
        var queued = await _jobs.EnqueueAsync(JobType.Scan, new ScanPayload(libraryRoot.LibraryRootId), 2, null, CancellationToken.None);
        using var pipeline = CreateSafeStopPipeline();

        var first = pipeline.RequestShutdownAndDrainAsync();
        var repeated = pipeline.RequestShutdownAndDrainAsync();

        Assert.Same(first, repeated);
        await first;
        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.False(await pipeline.StartProcessingAsync(CancellationToken.None));
        await pipeline.RequestRunAsync(CancellationToken.None);
        Assert.Equal(JobState.Pending, (await _jobs.GetJobAsync(queued.JobId, CancellationToken.None))!.State);
        pipeline.Dispose();
        pipeline.Dispose();

        using var freshRuntime = CreateSafeStopPipeline();
        Assert.Equal(ProcessingRunState.EnabledIdle, freshRuntime.RunState);
    }

    [Fact]
    public async Task TerminalShutdownFromUserStoppedDoesNotReopenOrPersistStopState()
    {
        using var pipeline = CreateSafeStopPipeline();
        await pipeline.RequestStopAndDrainAsync(CancellationToken.None);
        Assert.Equal(ProcessingRunState.UserStopped, pipeline.RunState);

        await pipeline.RequestShutdownAndDrainAsync();

        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.False(await pipeline.StartProcessingAsync(CancellationToken.None));
        using var freshRuntime = CreateSafeStopPipeline();
        Assert.Equal(ProcessingRunState.EnabledIdle, freshRuntime.RunState);
    }

    [Fact]
    public async Task TerminalShutdownDrainsProtectedPublicationBeforeLeaseRelease()
    {
        var media = await CreateScannedVideoMediaAsync();
        var protectedJob = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var publicationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessingSourceSession? observedSession = null;
        using var pipeline = CreateSafeStopPipeline(publicationProbe: async (session, _) =>
        {
            observedSession = session;
            publicationEntered.TrySetResult(true);
            await releasePublication.Task;
        });

        var run = pipeline.RequestRunAsync(CancellationToken.None);
        await publicationEntered.Task;
        var shutdown = pipeline.RequestShutdownAndDrainAsync();

        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.False(shutdown.IsCompleted);
        Assert.False(await pipeline.StartProcessingAsync(CancellationToken.None));
        Assert.True(observedSession!.IsLeaseHeld);
        AssertWriteBlocked(media.Path);

        releasePublication.TrySetResult(true);
        await Task.WhenAll(run, shutdown);

        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.Equal(JobState.Completed, (await _jobs.GetJobAsync(protectedJob.JobId, CancellationToken.None))!.State);
        using var writable = new FileStream(media.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    [Fact]
    public async Task TerminalShutdownPromotesExistingSafeStopDrainWithoutParallelDrain()
    {
        var media = await CreateScannedVideoMediaAsync();
        await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.Metadata), 2, null, CancellationToken.None);
        var publicationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainingObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeline = CreateSafeStopPipeline(publicationProbe: async (_, _) =>
        {
            publicationEntered.TrySetResult(true);
            await releasePublication.Task;
        });
        pipeline.RunStateChanged += (_, args) =>
        {
            if (args.State == ProcessingRunState.Draining) drainingObserved.TrySetResult(true);
        };

        var run = pipeline.RequestRunAsync(CancellationToken.None);
        await publicationEntered.Task;
        var safeStop = pipeline.RequestStopAndDrainAsync(CancellationToken.None);
        await drainingObserved.Task;
        var shutdown = pipeline.RequestShutdownAndDrainAsync();
        var repeated = pipeline.RequestShutdownAndDrainAsync();

        Assert.Same(shutdown, repeated);
        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.False(shutdown.IsCompleted);
        releasePublication.TrySetResult(true);
        await Task.WhenAll(run, safeStop, shutdown);
        Assert.Equal(ProcessingRunState.ShuttingDown, pipeline.RunState);
        Assert.False(await pipeline.StartProcessingAsync(CancellationToken.None));
    }

    [Fact]
    public void SafeStopPresentationExposesTruthfulJapaneseStatesAndOneCoordinatorEntry()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"ProcessingStartStop\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ProcessingLifecycleStatus}", xaml, StringComparison.Ordinal);
        Assert.Contains("停止処理中 — 開始済みの処理を安全に完了しています。", viewModel, StringComparison.Ordinal);
        Assert.Contains("処理は停止しています。未開始の処理は保持されています。", viewModel, StringComparison.Ordinal);
        Assert.Contains("_pipeline.RequestRunAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_pipeline.RunUntilIdleAsync", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_lifetime.Cancel();\n        await _pipeline.RequestStop", viewModel, StringComparison.Ordinal);
    }

    private MediaPipeline CreateSafeStopPipeline(
        Func<ProcessingSourceSession, CancellationToken, Task>? publicationProbe = null,
        Func<CancellationToken, Task>? beforeAcquisition = null) =>
        new(_repository, _jobs, new FileSystemMediaScanner(),
            new FfmpegAdapter(ResolveBundledFfmpeg(), _paths, _log, TimeSpan.FromSeconds(30)), _log,
            new PipelineConcurrencyOptions(1, 1, 1, 1), paths: _paths,
            resourceAdmission: new AlwaysAdmitResourcePolicy(), protectedPublicationProbe: publicationProbe,
            beforeJobStoreAcquisition: beforeAcquisition);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LocalMediaAI.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private async Task<MediaFile> CreateScannedByteMediaAsync(string name)
    {
        var directory = Path.Combine(_root, "synthetic-media");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 32_768).Select(index => (byte)(index % 251)).ToArray());
        return await ScanSingleAsync(directory, path);
    }

    private async Task<MediaFile> CreateScannedVideoMediaAsync()
    {
        var tools = ResolveBundledFfmpeg();
        Assert.NotNull(tools.FfmpegPath);
        var directory = Path.Combine(_root, "synthetic-video");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "generated-bars.mp4");
        var start = new ProcessStartInfo(tools.FfmpegPath!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10", "-t", "3", "-c:v", "mpeg4", "-q:v", "5", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Synthetic FFmpeg process did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return await ScanSingleAsync(directory, path);
    }

    private async Task<MediaFile> ScanSingleAsync(string directory, string path)
    {
        var root = await _repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var scanner = new FileSystemMediaScanner();
        var scanId = await _repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        ReconcileResult? result = null;
        await foreach (var scanned in scanner.EnumerateAsync(root, CancellationToken.None))
            if (string.Equals(scanned.FullPath, path, StringComparison.OrdinalIgnoreCase)) result = await _repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
        await _repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        return Assert.IsType<ReconcileResult>(result).MediaFile;
    }

    private async Task<MediaFile> ReconcileAsChangedAsync(MediaFile media, string quickFingerprint)
    {
        var scan = await _repository.StartScanAsync(media.LibraryRootId, CancellationToken.None);
        var changed = new ScannedMediaFile(media.LibraryRootId, media.Path, media.NormalizedPath, media.FileName, media.Extension,
            media.SizeBytes, media.CreationTimeUtc, media.LastWriteTimeUtc.AddSeconds(1), media.VolumeIdentity, media.FileIdentity, quickFingerprint);
        var result = await _repository.UpsertScannedFileAsync(changed, scan, CancellationToken.None);
        await _repository.CompleteScanAsync(scan, new ScanCompletion("Completed", 1, 0, 1, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        return result.MediaFile;
    }

    private async Task<Job> AcquireProtectedJobAsync(MediaFile media, ProtectedSourceStageEligibility stages)
    {
        var queued = await _jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, stages), 2, null, CancellationToken.None);
        var acquired = await _jobs.TryAcquireNextAsync(ProtectedPipelineDispatchMode.ProtectedActivated, CancellationToken.None);
        Assert.NotNull(acquired);
        Assert.Equal(queued.JobId, acquired.JobId);
        return acquired;
    }

    private async Task<ProtectedAnalysisPublication> PrepareSyntheticArtifactAsync(ProcessingSourceSession session, MediaFile media)
    {
        var staged = session.CreateUnpublishedArtifact(Path.Combine("thumbnail", "synthetic.txt"));
        await File.WriteAllTextAsync(staged.FullPath, "synthetic protected artifact");
        var now = DateTimeOffset.UtcNow;
        var analysis = new Analysis(Guid.NewGuid(), media.MediaContentId, "SyntheticProtectedArtifact", AnalysisStatus.Completed,
            "test", "v1", null, null, "{}", "config", "input", "protected-v1", now, now, null, null);
        var info = new FileInfo(staged.FullPath);
        var relative = Path.Combine("protected-source", media.MediaContentId.ToString("N"), "synthetic-" + session.SessionId.ToString("N") + ".txt").Replace(Path.DirectorySeparatorChar, '/');
        var artifact = new Artifact(Guid.NewGuid(), analysis.AnalysisId, "SyntheticProtected", relative,
            await FileHash.Sha256Async(staged.FullPath, CancellationToken.None), info.Length, "text/plain", ArtifactDurability.RebuildableCache, now);
        var prepared = await ProtectedArtifactPreparation.PrepareAsync(session, _paths, staged, artifact, CancellationToken.None);
        return new ProtectedAnalysisPublication(analysis, [prepared]);
    }

    private static ProtectedSourcePublicationBundle CreateBundle(ProcessingSourceSession session, MediaMetadata metadata, ProtectedAnalysisPublication analysis) =>
        new(session.SessionId, session.MediaFileId, session.ExpectedMediaContentId, metadata, [], [analysis]);

    private static MediaMetadata CreateMetadata(MediaFile media, string marker) =>
        new(media.MediaFileId, marker, 1d, 160, 90, "synthetic", "10/1", 1, 0, 0, "{\"synthetic\":true}", DateTimeOffset.UtcNow);

    private static VisualSample CreateSample(MediaFile media, string marker)
    {
        var now = DateTimeOffset.UtcNow;
        return new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, marker, 0, 0, 0,
            VisualSamplingPolicy.FixedIntervalV1.Version, marker + "-" + Guid.NewGuid().ToString("N"), null, null, null,
            VisualSampleStatus.Completed, null, null, now, now);
    }

    private static VisualEmbedding CreateEmbedding(VisualSample sample, SemanticModelProfile model, SemanticExecutionProfile execution, string marker)
    {
        var vector = new byte[model.Dimension * sizeof(float)];
        BitConverter.GetBytes(1f).CopyTo(vector, 0);
        var now = DateTimeOffset.UtcNow;
        return new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, sample.InputIdentityHash, marker + "-" + Guid.NewGuid().ToString("N"),
            model.ModelId, model.ModelRevision, model.ProcessorVersion, sample.SamplingVersion, execution.Dtype, model.Dimension, true,
            vector, VisualEmbeddingStatus.Completed, null, null, now, now);
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<string> TextAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertWriteBlocked(string path) => Assert.Throws<IOException>(() =>
    {
        using var ignored = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    });

    private static FfmpegPaths ResolveBundledFfmpeg()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var runtime = Path.Combine(current.FullName, "runtime", "ffmpeg");
            var ffmpeg = Path.Combine(runtime, "ffmpeg.exe");
            var ffprobe = Path.Combine(runtime, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegPaths(ffmpeg, ffprobe);
            current = current.Parent;
        }
        return new FfmpegPaths(null, null);
    }

    private sealed class AlwaysAdmitResourcePolicy : IResourceAdmissionPolicy
    {
        public AdmissionDecision Evaluate(ResourceCostEstimate estimate, ResourceSnapshot snapshot) =>
            new(AdmissionDecisionKind.Admit, JobReason.None, "TEST_RESOURCE_ADMIT", "Deterministic protected activation admission.");
    }
}
