using System.Diagnostics;
using System.Globalization;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class ContentSimilarityFoundationTests : IAsyncLifetime, IDisposable
{
    private const string Model = "fixture/llm-jp-clip";
    private const string Revision = "fixture-revision";
    private const string Processor = "fixture-processor";
    private const string Sampling = "fixed-interval-v1";
    private const string Dtype = "float32";
    private const int Dimension = 4;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIContentSimilarityTests", Guid.NewGuid().ToString("N"));
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
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MeanUnitRepresentationIsNormalizedAndDeterministic()
    {
        var entries = SourceEntries([Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(0.98f, 0.02f, 0, 0)]);
        var first = ContentSimilarityVectors.BuildMeanUnit(entries, 3);
        var second = ContentSimilarityVectors.BuildMeanUnit(entries, 3);

        Assert.True(first.HasCompleteCoverage);
        Assert.Equal(first.SourceEmbeddingDigest, second.SourceEmbeddingDigest);
        Assert.InRange(Math.Sqrt(first.Vector.Sum(value => value * value)), 0.999d, 1.001d);
    }

    [Fact]
    public void SourceDigestChangesWhenEmbeddingSourceChanges()
    {
        var left = ContentSimilarityVectors.BuildMeanUnit(SourceEntries([Vector(1, 0, 0, 0)]), 1);
        var right = ContentSimilarityVectors.BuildMeanUnit(SourceEntries([Vector(0, 1, 0, 0)]), 1);
        Assert.NotEqual(left.SourceEmbeddingDigest, right.SourceEmbeddingDigest);
    }

    [Fact]
    public void PartialCoverageIsNotEligibleForContentSimilarity()
    {
        var result = ContentSimilarityVectors.BuildMeanUnit(SourceEntries([Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]), 3);
        Assert.False(result.HasCompleteCoverage);
        Assert.Equal(2d / 3d, result.CoverageRatio, 8);
    }

    [Fact]
    public void ExactCosineRejectsIncompatibleDimensions()
    {
        Assert.Throws<InvalidOperationException>(() => ContentSimilarityVectors.Cosine([1f, 0f], [1f]));
    }

    [Fact]
    public void SignLshIsDeterministicAndVersioned()
    {
        var signature = ContentSimilaritySignLsh.Signature(Vector(1, 0, 0, 0));
        Assert.Equal(signature, ContentSimilaritySignLsh.Signature(Vector(1, 0, 0, 0)));
        Assert.Equal(16, signature.Length);
        Assert.Equal(4, ContentSimilaritySignLsh.Bands(signature).Count);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsStronglySimilarPair()
    {
        var profile = ContentSimilarityVerificationProfile.HighPrecisionV1;
        var score = new ContentSimilarityPairScore(0.91d, 0.93d, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(profile.IsVerified(0.88d, score));
        Assert.Equal("High", profile.PriorityFor(score.TopSampleCosine));
    }

    [Fact]
    public void HighPrecisionProfileRejectsHardNegativeDespiteMeanCandidate()
    {
        var profile = ContentSimilarityVerificationProfile.HighPrecisionV1;
        var briefSharedObject = new ContentSimilarityPairScore(0.54d, 0.91d, Guid.NewGuid(), Guid.NewGuid());
        Assert.False(profile.IsVerified(0.86d, briefSharedObject));
    }

    [Fact]
    public void PairVerificationUsesTopThreeSamplesNotSingleBestFrame()
    {
        var left = SourceEntries([Vector(1, 0, 0, 0), Vector(0, 1, 0, 0), Vector(0, 0, 1, 0)]);
        var right = SourceEntries([Vector(1, 0, 0, 0), Vector(0, 0, 1, 0), Vector(0, 0, 0, 1)]);
        var result = ContentSimilarityVectors.VerifyTopSamples(left, right, 3);
        Assert.True(result.RepresentativeCosine > result.TopSampleCosine);
        Assert.InRange(result.TopSampleCosine, 0.65d, 0.68d);
    }

    [Fact]
    public async Task RepresentationPersistsFromExistingEmbeddingsWithoutInference()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, 1)));
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[0], [Vector(1, 0, 0, 0), Vector(0.98f, 0.02f, 0, 0), Vector(1, 0, 0, 0)]);
        var representation = await PersistRepresentationAsync(fixture.Repository, fixture.Media[0]);

        Assert.Equal("Completed", representation.Status);
        Assert.Equal(3, representation.AvailableSampleCount);
        Assert.Equal(3, representation.ExpectedSampleCount);
        Assert.True(representation.Normalized);
        Assert.NotNull(representation.VectorFloat32);
    }

    [Fact]
    public async Task RepresentationUpsertIsIdempotentForSameEmbeddingDigest()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, 2)));
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[0], [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var first = await PersistRepresentationAsync(fixture.Repository, fixture.Media[0]);
        var second = await PersistRepresentationAsync(fixture.Repository, fixture.Media[0]);

        Assert.Equal(first.ContentSimilarityRepresentationId, second.ContentSimilarityRepresentationId);
        Assert.Equal(first.SourceEmbeddingDigest, second.SourceEmbeddingDigest);
    }

    [Fact]
    public async Task IncompleteEmbeddingCoverageDoesNotPublishCompletedRepresentation()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, 3)));
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[0], [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), null]);
        var source = await fixture.Repository.GetContentSimilaritySourceEmbeddingsAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var aggregate = ContentSimilarityVectors.BuildMeanUnit(source.CompletedEmbeddings, source.ExpectedSampleCount);
        var incomplete = new ContentSimilarityRepresentation(Guid.NewGuid(), fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, Model, Revision, Processor, Sampling, ContentSimilarityVectors.AggregationVersion, aggregate.SourceEmbeddingDigest, Dtype, Dimension, false, aggregate.AvailableSampleCount, aggregate.ExpectedSampleCount, aggregate.CoverageRatio, null, "InsufficientEvidence", "CONTENT_SIMILARITY_SAMPLE_COVERAGE", "fixture", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(await fixture.Repository.UpsertContentSimilarityRepresentationAsync(incomplete, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetCurrentContentSimilarityRepresentationAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None));
    }

    [Fact]
    public async Task CandidateSearchUsesExistingMediaRepresentationsAndTopKBound()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, 4)), ("b.mp4", Pattern(150_000, 5)), ("c.mp4", Pattern(150_000, 6)));
        foreach (var media in fixture.Media) await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        foreach (var media in fixture.Media) await PersistRepresentationAsync(fixture.Repository, media);

        var candidates = await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 1, CancellationToken.None);
        Assert.InRange(candidates.Count, 1, 3);
        Assert.All(candidates, candidate => Assert.Equal(1d, candidate.MeanCosineSimilarity, 6));
    }

    [Fact]
    public async Task ContentSimilarityEvidenceIsCanonicalPairAndIdempotent()
    {
        var fixture = await CreateStrongPairAsync(7);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var first = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        var second = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");

        Assert.True(await fixture.Repository.UpsertContentSimilarityEvidenceAsync(first, CancellationToken.None));
        var persisted = await fixture.Repository.GetContentSimilarityEvidenceAsync(candidate.MediaFileBId, candidate.MediaContentBId, candidate.MediaFileAId, candidate.MediaContentAId, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(first.ContentSimilarityEvidenceId, persisted!.ContentSimilarityEvidenceId);
        Assert.Equal(first.MediaFileAId, first.MediaFileAId.CompareTo(first.MediaFileBId) < 0 ? first.MediaFileAId : first.MediaFileBId);
        _ = second;

        var otherDirectory = Path.Combine(_root, "scope-other-root");
        Directory.CreateDirectory(otherDirectory);
        var otherRoot = await fixture.Repository.AddOrGetLibraryRootAsync(otherDirectory, CancellationToken.None);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_file SET library_root_id=$root WHERE media_file_id=$media;";
            command.Parameters.AddWithValue("$root", otherRoot.LibraryRootId.ToString("D"));
            command.Parameters.AddWithValue("$media", candidate.MediaFileBId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Empty((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Contains((await fixture.Repository.QueryContentSimilarityEvidenceAsync(null, true, 0, 10, CancellationToken.None)).Items, item => item.ContentSimilarityEvidenceId == first.ContentSimilarityEvidenceId);
        Assert.Empty((await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.ContentSimilar), CancellationToken.None)).Items);
        Assert.Equal(2, Assert.Single((await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(null, UnifiedReviewFilter.ContentSimilar), CancellationToken.None)).Items).InvolvedMediaCount);
    }

    [Fact]
    public async Task ContentSimilarityEvidenceDoesNotMergeTransitiveEdges()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, 8)), ("b.mp4", Pattern(150_000, 9)), ("c.mp4", Pattern(150_000, 10)));
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[0], [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[1], [Vector(0.8f, 0.6f, 0, 0), Vector(0.8f, 0.6f, 0, 0), Vector(0.8f, 0.6f, 0, 0)]);
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[2], [Vector(0.28f, 0.96f, 0, 0), Vector(0.28f, 0.96f, 0, 0), Vector(0.28f, 0.96f, 0, 0)]);
        var reps = new Dictionary<Guid, ContentSimilarityRepresentation>();
        foreach (var media in fixture.Media) reps[media.MediaFileId] = await PersistRepresentationAsync(fixture.Repository, media);
        await PersistManualEvidenceAsync(fixture.Repository, fixture.Media[0], reps[fixture.Media[0].MediaFileId], fixture.Media[1], reps[fixture.Media[1].MediaFileId], 0.80d);
        await PersistManualEvidenceAsync(fixture.Repository, fixture.Media[1], reps[fixture.Media[1].MediaFileId], fixture.Media[2], reps[fixture.Media[2].MediaFileId], 0.80d);

        var queue = await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None);
        Assert.Equal(2, queue.TotalCount);
        Assert.Null(await fixture.Repository.GetContentSimilarityEvidenceAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, fixture.Media[2].MediaFileId, fixture.Media[2].MediaContentId, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, CancellationToken.None));
    }

    [Fact]
    public async Task ExactRelationTakesPrecedenceOverContentSimilarityQueue()
    {
        var shared = Pattern(150_000, 11);
        var fixture = await CreateFixtureAsync(("a.mp4", shared), ("b.mp4", shared));
        foreach (var media in fixture.Media)
        {
            await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
            var digest = await FileHash.Sha256Async(media.Path, CancellationToken.None);
            Assert.True(await fixture.Repository.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, media.SizeBytes, media.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None));
            await PersistRepresentationAsync(fixture.Repository, media);
        }
        await fixture.Repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        Assert.Empty(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
    }

    [Fact]
    public async Task HumanNotRelatedKeepsEvidenceButRemovesDefaultQueueEntry()
    {
        var fixture = await CreateStrongPairAsync(12);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        var judgment = await fixture.Repository.UpsertContentSimilarityJudgmentAsync(evidence.ContentSimilarityEvidenceId, ContentSimilarityJudgmentDecision.NotRelated, "Human rejected the relation.", CancellationToken.None);

        Assert.Equal("Human", judgment.Origin);
        Assert.Empty((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.Single((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task KeepBothJudgmentHasNoFilesystemSideEffect()
    {
        var fixture = await CreateStrongPairAsync(13);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        var before = new List<Snapshot>();
        foreach (var media in fixture.Media)
            before.Add(new Snapshot(media.Path, await FileHash.Sha256Async(media.Path, CancellationToken.None), new FileInfo(media.Path).Length, File.GetLastWriteTimeUtc(media.Path)));
        await fixture.Repository.UpsertContentSimilarityJudgmentAsync(evidence.ContentSimilarityEvidenceId, ContentSimilarityJudgmentDecision.KeepBoth, "Human keep both.", CancellationToken.None);

        foreach (var snapshot in before)
        {
            Assert.Equal(snapshot.Hash, await FileHash.Sha256Async(snapshot.Path, CancellationToken.None));
            Assert.Equal(snapshot.Length, new FileInfo(snapshot.Path).Length);
            Assert.Equal(snapshot.LastWriteUtc, File.GetLastWriteTimeUtc(snapshot.Path));
        }
    }

    [Fact]
    public async Task OfflineRootKeepsContentSimilarityAuditEvidence()
    {
        var fixture = await CreateStrongPairAsync(14);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await fixture.Repository.SetRootAvailabilityAsync(fixture.Root.LibraryRootId, RootAvailability.Offline, CancellationToken.None);

        Assert.NotNull(await fixture.Repository.GetContentSimilarityEvidenceAsync(evidence.MediaFileAId, evidence.MediaContentAId, evidence.MediaFileBId, evidence.MediaContentBId, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, CancellationToken.None));
    }

    [Fact]
    public async Task MissingMemberExcludesCurrentContentSimilarityQueue()
    {
        var fixture = await CreateStrongPairAsync(15);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        File.Delete(fixture.Media[1].Path);
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task SourceChangeMakesOldContentSimilarityEvidenceNonCurrent()
    {
        var fixture = await CreateStrongPairAsync(16);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await File.WriteAllBytesAsync(fixture.Media[1].Path, Pattern(150_000, 97));
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.NotNull(await fixture.Repository.GetContentSimilarityEvidenceAsync(evidence.MediaFileAId, evidence.MediaContentAId, evidence.MediaFileBId, evidence.MediaContentBId, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, CancellationToken.None));
        Assert.Empty((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ModelRevisionChangeDoesNotSilentlyReuseRepresentation()
    {
        var fixture = await CreateStrongPairAsync(17);
        var candidates = await fixture.Repository.GetContentSimilarityRepresentationCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, "fixture-revision-v2", Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        Assert.Empty(candidates);
        Assert.Null(await fixture.Repository.GetCurrentContentSimilarityRepresentationAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, ContentSimilarityVectors.AggregationVersion, Model, "fixture-revision-v2", Processor, Sampling, Dtype, Dimension, CancellationToken.None));
    }

    [Fact]
    public async Task RejectedContentSimilarityEvidenceIsAuditOnly()
    {
        var fixture = await CreateStrongPairAsync(18);
        var candidate = Assert.Single(await fixture.Repository.GetContentSimilarityCandidatesAsync(fixture.Root.LibraryRootId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, 20, CancellationToken.None));
        var rejected = await PersistEvidenceAsync(fixture.Repository, candidate, "Rejected");
        Assert.NotNull(await fixture.Repository.GetContentSimilarityEvidenceAsync(rejected.MediaFileAId, rejected.MediaContentAId, rejected.MediaFileBId, rejected.MediaContentBId, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, CancellationToken.None));
        Assert.Empty((await fixture.Repository.QueryContentSimilarityEvidenceAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ContentSimilarityRunningJobRecoversAfterRestart()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var store = new SqliteJobStore(_paths, _log);
        var payload = new ContentSimilarityRepresentationPayload(Guid.NewGuid(), Guid.NewGuid(), 123L, DateTimeOffset.UtcNow);
        var job = await store.EnqueueUniqueAsync(JobType.ContentSimilarityRepresentation, payload, 2, null, CancellationToken.None);
        Assert.Equal(job.JobId, (await store.TryAcquireNextAsync(CancellationToken.None))?.JobId);
        await store.RecoverInterruptedJobsAsync(CancellationToken.None);
        Assert.Equal(JobState.RetryWaiting, Assert.Single(await store.GetRecentAsync(10, CancellationToken.None), item => item.JobId == job.JobId).State);
    }

    [Fact]
    public void HumanLabeledFixedEvaluationAchievesHighPrecision()
    {
        var labels = new[]
        {
            (Label: true, Mean: 0.91d, Sample: 0.93d, Representative: 0.95d),
            (Label: true, Mean: 0.86d, Sample: 0.88d, Representative: 0.91d),
            (Label: false, Mean: 0.87d, Sample: 0.55d, Representative: 0.94d),
            (Label: false, Mean: 0.71d, Sample: 0.88d, Representative: 0.90d),
            (Label: false, Mean: 0.84d, Sample: 0.79d, Representative: 0.91d),
            (Label: false, Mean: 0.20d, Sample: 0.21d, Representative: 0.24d)
        };
        var profile = ContentSimilarityVerificationProfile.HighPrecisionV1;
        var selected = labels.Select(item => (item.Label, Selected: profile.IsVerified(item.Mean, new ContentSimilarityPairScore(item.Sample, item.Representative, Guid.Empty, Guid.Empty)))).ToArray();
        var tp = selected.Count(item => item.Label && item.Selected);
        var fp = selected.Count(item => !item.Label && item.Selected);
        var fn = selected.Count(item => item.Label && !item.Selected);
        var precision = tp / (double)Math.Max(1, tp + fp);
        var recall = tp / (double)Math.Max(1, tp + fn);
        Assert.Equal(1d, precision, 6);
        Assert.Equal(1d, recall, 6);
    }

    [Fact]
    public void TenThousandSignLshBuildIsBounded()
    {
        var watch = Stopwatch.StartNew();
        var buckets = new Dictionary<(int Band, string Value), int>();
        for (var index = 0; index < 10_000; index++)
            foreach (var (band, value) in ContentSimilaritySignLsh.Bands(ContentSimilaritySignLsh.Signature(DeterministicVector(index))).Select((value, band) => (band, value)))
                buckets[(band, value)] = buckets.TryGetValue((band, value), out var count) ? count + 1 : 1;
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"10k sign-LSH build unexpectedly took {watch.Elapsed.TotalSeconds:F2}s.");
        Assert.NotEmpty(buckets);
    }

    [Fact]
    public void HundredThousandSignLshBuildIsBoundedWithoutQuadraticPairs()
    {
        var watch = Stopwatch.StartNew();
        var checksum = 0;
        for (var index = 0; index < 100_000; index++)
            checksum ^= ContentSimilaritySignLsh.Signature(DeterministicVector(index)).GetHashCode(StringComparison.Ordinal);
        watch.Stop();
        Assert.NotEqual(0, checksum);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"100k sign-LSH build unexpectedly took {watch.Elapsed.TotalSeconds:F2}s.");
    }

    [Fact]
    public void TenThousandConcentratedBucketIsRejectedBeforePairExpansion()
    {
        const int mediaCount = 10_000;
        var watch = Stopwatch.StartNew();
        var bucketCounts = new Dictionary<(int Band, string Value), int>();
        var signature = ContentSimilaritySignLsh.Signature(Vector(1, 0, 0, 0));
        for (var index = 0; index < mediaCount; index++)
        {
            foreach (var (value, band) in ContentSimilaritySignLsh.Bands(signature).Select((value, band) => (value, band)))
                bucketCounts[(band, value)] = bucketCounts.TryGetValue((band, value), out var count) ? count + 1 : 1;
        }
        var eligibleBucketCount = bucketCounts.Values.Count(count => count <= 256);
        watch.Stop();

        Assert.Equal(4, bucketCounts.Count);
        Assert.All(bucketCounts.Values, count => Assert.Equal(mediaCount, count));
        Assert.Equal(0, eligibleBucketCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"10k concentrated-bucket blocking unexpectedly took {watch.Elapsed.TotalSeconds:F2}s.");
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, IReadOnlyList<MediaFile> Media)> CreateStrongPairAsync(int salt)
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(150_000, salt)), ("b.mkv", Pattern(150_000, salt + 1)));
        foreach (var media in fixture.Media)
        {
            await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(0.98f, 0.02f, 0, 0), Vector(1, 0, 0, 0)]);
            await PersistRepresentationAsync(fixture.Repository, media);
        }
        return fixture;
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, IReadOnlyList<MediaFile> Media)> CreateFixtureAsync(params (string RelativePath, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var (relativePath, bytes) in files) await File.WriteAllBytesAsync(Path.Combine(directory, relativePath), bytes);
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        await RescanAsync(repository, root);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
        var media = new List<MediaFile>();
        foreach (var item in page.Items)
        {
            var value = (await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!;
            await repository.UpsertMetadataAsync(new MediaMetadata(value.MediaFileId, value.Extension.TrimStart('.'), 30d, 1920, 1080, "h264", "30/1", 1_000_000, 1, 0, "{}", DateTimeOffset.UtcNow), CancellationToken.None);
            media.Add(value);
        }
        return (repository, root, media.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
    }

    private static async Task RescanAsync(SqliteLibraryRepository repository, LibraryRoot root)
    {
        var scanner = new FileSystemMediaScanner();
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var count = 0;
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None))
        {
            await repository.UpsertScannedFileAsync(file, scan, CancellationToken.None);
            count++;
        }
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scan, CancellationToken.None);
    }

    private static async Task SeedEmbeddingsAsync(SqliteLibraryRepository repository, MediaFile media, IReadOnlyList<float[]?> vectors)
    {
        var input = "fixture-input-" + media.MediaFileId.ToString("N");
        var now = DateTimeOffset.UtcNow;
        var samples = vectors.Select((_, index) => new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, input, index, index * 10d, index * 10d, Sampling, "sample-cache-" + media.MediaFileId.ToString("N") + "-" + index.ToString(CultureInfo.InvariantCulture), "frames/fixture-" + media.MediaFileId.ToString("N") + "-" + index.ToString(CultureInfo.InvariantCulture) + ".jpg", "00", 1, VisualSampleStatus.Completed, null, null, now, now)).ToArray();
        await repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, input, Sampling, samples, CancellationToken.None);
        for (var index = 0; index < samples.Length; index++)
        {
            if (vectors[index] is not { } vector) continue;
            ContentSimilarityVectors.NormalizeInPlace(vector);
            await repository.UpsertVisualEmbeddingAsync(new VisualEmbedding(Guid.NewGuid(), samples[index].VisualSampleId, input, "embedding-cache-" + samples[index].VisualSampleId.ToString("N"), Model, Revision, Processor, Sampling, Dtype, Dimension, true, ContentSimilarityVectors.Encode(vector), VisualEmbeddingStatus.Completed, null, null, now, now), CancellationToken.None);
        }
    }

    private static async Task<ContentSimilarityRepresentation> PersistRepresentationAsync(SqliteLibraryRepository repository, MediaFile media)
    {
        var source = await repository.GetContentSimilaritySourceEmbeddingsAsync(media.MediaFileId, media.MediaContentId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var aggregate = ContentSimilarityVectors.BuildMeanUnit(source.CompletedEmbeddings, source.ExpectedSampleCount);
        var representation = new ContentSimilarityRepresentation(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, Model, Revision, Processor, Sampling, ContentSimilarityVectors.AggregationVersion, aggregate.SourceEmbeddingDigest, Dtype, Dimension, aggregate.HasCompleteCoverage, aggregate.AvailableSampleCount, aggregate.ExpectedSampleCount, aggregate.CoverageRatio, aggregate.HasCompleteCoverage ? ContentSimilarityVectors.Encode(aggregate.Vector) : null, aggregate.HasCompleteCoverage ? "Completed" : "InsufficientEvidence", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(await repository.UpsertContentSimilarityRepresentationAsync(representation, CancellationToken.None));
        return (await repository.GetCurrentContentSimilarityRepresentationAsync(media.MediaFileId, media.MediaContentId, ContentSimilarityVectors.AggregationVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None))!;
    }

    private static async Task<ContentSimilarityEvidence> PersistEvidenceAsync(SqliteLibraryRepository repository, ContentSimilarityCandidate candidate, string status)
    {
        var left = await repository.GetContentSimilaritySourceEmbeddingsAsync(candidate.MediaFileAId, candidate.MediaContentAId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var right = await repository.GetContentSimilaritySourceEmbeddingsAsync(candidate.MediaFileBId, candidate.MediaContentBId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var score = ContentSimilarityVectors.VerifyTopSamples(left.CompletedEmbeddings, right.CompletedEmbeddings, 3);
        var evidence = new ContentSimilarityEvidence(Guid.NewGuid(), candidate.MediaFileAId, candidate.MediaContentAId, candidate.ContentSimilarityRepresentationAId, candidate.MediaFileBId, candidate.MediaContentBId, candidate.ContentSimilarityRepresentationBId, score.RepresentativeVisualSampleAId, score.RepresentativeVisualSampleBId, Model, Revision, Processor, Sampling, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, ContentSimilarityVectors.SimilarityMetric, candidate.MeanCosineSimilarity, score.TopSampleCosine, score.RepresentativeCosine, left.CompletedEmbeddings.Count, left.ExpectedSampleCount, right.CompletedEmbeddings.Count, right.ExpectedSampleCount, AnalysisKey.Sha256(Guid.NewGuid().ToString("N")), status, null, null, DateTimeOffset.UtcNow);
        Assert.True(await repository.UpsertContentSimilarityEvidenceAsync(evidence, CancellationToken.None));
        return evidence;
    }

    private static async Task PersistManualEvidenceAsync(SqliteLibraryRepository repository, MediaFile firstMedia, ContentSimilarityRepresentation firstRepresentation, MediaFile secondMedia, ContentSimilarityRepresentation secondRepresentation, double score)
    {
        var firstSource = await repository.GetContentSimilaritySourceEmbeddingsAsync(firstMedia.MediaFileId, firstMedia.MediaContentId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var secondSource = await repository.GetContentSimilaritySourceEmbeddingsAsync(secondMedia.MediaFileId, secondMedia.MediaContentId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var sampleScore = ContentSimilarityVectors.VerifyTopSamples(firstSource.CompletedEmbeddings, secondSource.CompletedEmbeddings, 3);
        var ordered = firstMedia.MediaFileId.CompareTo(secondMedia.MediaFileId) < 0;
        var evidence = new ContentSimilarityEvidence(Guid.NewGuid(), ordered ? firstMedia.MediaFileId : secondMedia.MediaFileId, ordered ? firstMedia.MediaContentId : secondMedia.MediaContentId, ordered ? firstRepresentation.ContentSimilarityRepresentationId : secondRepresentation.ContentSimilarityRepresentationId, ordered ? secondMedia.MediaFileId : firstMedia.MediaFileId, ordered ? secondMedia.MediaContentId : firstMedia.MediaContentId, ordered ? secondRepresentation.ContentSimilarityRepresentationId : firstRepresentation.ContentSimilarityRepresentationId, ordered ? sampleScore.RepresentativeVisualSampleAId : sampleScore.RepresentativeVisualSampleBId, ordered ? sampleScore.RepresentativeVisualSampleBId : sampleScore.RepresentativeVisualSampleAId, Model, Revision, Processor, Sampling, ContentSimilarityVectors.AggregationVersion, ContentSimilarityVerificationProfile.HighPrecisionV1.ThresholdVersion, ContentSimilarityVectors.SimilarityMetric, score, Math.Max(score, 0.81d), Math.Max(score, 0.82d), firstSource.CompletedEmbeddings.Count, firstSource.ExpectedSampleCount, secondSource.CompletedEmbeddings.Count, secondSource.ExpectedSampleCount, AnalysisKey.Sha256(Guid.NewGuid().ToString("N")), "Verified", null, null, DateTimeOffset.UtcNow);
        Assert.True(await repository.UpsertContentSimilarityEvidenceAsync(evidence, CancellationToken.None));
    }

    private static (VisualEmbedding Embedding, VisualSample Sample)[] SourceEntries(IReadOnlyList<float[]> vectors)
    {
        var now = DateTimeOffset.UtcNow;
        return vectors.Select((vector, index) =>
        {
            ContentSimilarityVectors.NormalizeInPlace(vector);
            var sample = new VisualSample(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fixture", index, index, index, Sampling, "sample-" + index.ToString(CultureInfo.InvariantCulture), null, null, null, VisualSampleStatus.Completed, null, null, now, now);
            var embedding = new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, "fixture", "embedding-" + index.ToString(CultureInfo.InvariantCulture), Model, Revision, Processor, Sampling, Dtype, Dimension, true, ContentSimilarityVectors.Encode(vector), VisualEmbeddingStatus.Completed, null, null, now, now);
            return (embedding, sample);
        }).ToArray();
    }

    private static float[] Vector(float x, float y, float z, float w) => [x, y, z, w];
    private static float[] DeterministicVector(int index) => Vector(((index * 17) % 101) + 1, ((index * 31) % 103) + 1, ((index * 43) % 107) + 1, ((index * 59) % 109) + 1);
    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 37 + salt) % 251)).ToArray();
    private sealed record Snapshot(string Path, string Hash, long Length, DateTime LastWriteUtc);
}
