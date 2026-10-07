using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class TaggingFoundationTests : IAsyncLifetime, IDisposable
{
    private const string Model = "fixture/llm-jp-clip";
    private const string Revision = "fixture-revision";
    private const string Processor = "fixture-processor";
    private const string Sampling = "fixed-interval-v1";
    private const string Dtype = "float32";
    private const int Dimension = 4;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAITaggingTests", Guid.NewGuid().ToString("N"));
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
    public void TagSuggestionPayloadIdentityIncludesVersionedThreshold()
    {
        var current = new TagSuggestionPayload(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch);
        var older = current with { ThresholdVersion = "tagging-embedding-zero-shot-v0" };
        Assert.Equal(TaggingVerificationProfile.DescriptiveHighPrecisionV1.ThresholdVersion, current.ThresholdVersion);
        Assert.NotEqual(JsonSerializer.Serialize(current), JsonSerializer.Serialize(older));
    }

    [Fact]
    public void FixedTaxonomyHasFifteenStableFlatJapaneseTags()
    {
        Assert.Equal(15, TaggingTaxonomy.All.Count);
        Assert.Equal(15, TaggingTaxonomy.All.Select(tag => tag.TagId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(TaggingTaxonomy.All, tag => tag.TagId == "ocean" && tag.DisplayName == "海");
        Assert.Contains(TaggingTaxonomy.All, tag => tag.TagId == "dog" && tag.DisplayName == "犬");
        Assert.All(TaggingTaxonomy.All, tag => Assert.False(string.IsNullOrWhiteSpace(tag.Group)));
    }

    [Fact]
    public void OnlyPredeclaredRawAndDescriptiveTagTextFormsAreAllowed()
    {
        var ocean = TaggingTaxonomy.GetRequired("ocean");
        Assert.Equal("海", TaggingTaxonomy.TextFor(ocean, TaggingTaxonomy.RawTextRepresentationVersion));
        Assert.Equal("海または海岸が映っている", TaggingTaxonomy.TextFor(ocean, TaggingTaxonomy.DescriptiveTextRepresentationVersion));
        Assert.Throws<InvalidOperationException>(() => TaggingTaxonomy.TextFor(ocean, "prompt-ensemble-v1"));
    }

    [Fact]
    public void JapaneseAndUnicodeManualTagCanonicalizationIsStable()
    {
        Assert.Equal("夜 景", TagNormalization.CanonicalizeManualTag(" 夜\u3000景 "));
        Assert.Equal("café", TagNormalization.CanonicalizeManualTag("Cafe\u0301"));
        Assert.Equal("dog", TagNormalization.CanonicalizeManualTag("DOG"));
    }

    [Fact]
    public void TagAggregationUsesRepresentativeMaximumAndCompleteCoverage()
    {
        var text = TextEmbedding(Vector(1, 0, 0, 0));
        var entries = SourceEntries([Vector(1, 0, 0, 0), Vector(0.9f, 0.1f, 0, 0), Vector(0.8f, 0.2f, 0, 0)]);
        var result = TaggingVectors.Aggregate(entries, 3, text, TaggingVerificationProfile.DescriptiveHighPrecisionV1);

        Assert.True(result.HasCompleteCoverage);
        Assert.Equal(3, result.CompletedSampleCount);
        Assert.Equal(3, result.MatchedSampleCount);
        Assert.NotNull(result.RepresentativeVisualSampleId);
        Assert.InRange(result.MaximumScore, 0.99d, 1.001d);
        Assert.NotEmpty(result.SourceEmbeddingDigest);
    }

    [Fact]
    public void HighPrecisionProfileRejectsHardNegativeTagging()
    {
        var text = TextEmbedding(Vector(1, 0, 0, 0));
        var entries = SourceEntries([Vector(0, 1, 0, 0), Vector(0, 1, 0, 0), Vector(0.05f, 0.99f, 0, 0)]);
        var aggregate = TaggingVectors.Aggregate(entries, 3, text, TaggingVerificationProfile.DescriptiveHighPrecisionV1);
        Assert.False(TaggingVerificationProfile.DescriptiveHighPrecisionV1.IsSuggested(aggregate));
    }

    [Fact]
    public void PartialCoverageIsNeverSuggested()
    {
        var text = TextEmbedding(Vector(1, 0, 0, 0));
        var entries = SourceEntries([Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var aggregate = TaggingVectors.Aggregate(entries, 3, text, TaggingVerificationProfile.DescriptiveHighPrecisionV1);
        Assert.False(aggregate.HasCompleteCoverage);
        Assert.False(TaggingVerificationProfile.DescriptiveHighPrecisionV1.IsSuggested(aggregate));
    }

    [Fact]
    public async Task SuggestedEvidenceIsSeparateFromHumanManualTagAsync()
    {
        var fixture = await CreateFixtureAsync(("ocean.mp4", Pattern(128_000, 1)));
        await SeedEmbeddingsAsync(fixture.Repository, fixture.Media[0], [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var evidence = await PersistEvidenceAsync(fixture.Repository, fixture.Media[0], "ocean");
        var manual = await fixture.Repository.AddManualMediaTagAsync(fixture.Media[0].MediaFileId, "お気に入り", "fixture", CancellationToken.None);

        Assert.Equal(MediaTagEvidenceStatus.Suggested, evidence.Status);
        Assert.Equal(MediaTagSource.EmbeddingZeroShot, evidence.Source);
        Assert.Equal(MediaTagSource.Human, manual.Source);
        Assert.NotEqual(evidence.DisplayName, manual.DisplayTag);
    }

    [Fact]
    public async Task ManualTagDuplicatePreventionIsUnicodeAwareAsync()
    {
        var fixture = await CreateFixtureAsync(("manual.mp4", Pattern(128_000, 2)));
        var first = await fixture.Repository.AddManualMediaTagAsync(fixture.Media[0].MediaFileId, "Cafe\u0301", null, CancellationToken.None);
        var second = await fixture.Repository.AddManualMediaTagAsync(fixture.Media[0].MediaFileId, "Café", "same tag", CancellationToken.None);
        var tags = await fixture.Repository.GetManualMediaTagsAsync(fixture.Media[0].MediaFileId, CancellationToken.None);

        Assert.Equal(first.ManualMediaTagId, second.ManualMediaTagId);
        Assert.Single(tags);
        Assert.Equal("café", tags[0].CanonicalTag);
    }

    [Fact]
    public async Task ConfirmedTagJudgmentPersistsWithoutChangingEvidenceAsync()
    {
        var fixture = await PreparedSingleAsync(3);
        var judgment = await fixture.Repository.UpsertMediaTagJudgmentAsync(fixture.Evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, "human verified", CancellationToken.None);
        var queue = await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None);

        Assert.Equal(MediaTagJudgmentDecision.Confirmed, judgment.Decision);
        Assert.Single(queue.Items);
        Assert.Equal("Confirmed (Human)", queue.Items[0].StateDisplay);
        Assert.Equal(fixture.Evidence.MediaTagEvidenceId, queue.Items[0].Evidence.MediaTagEvidenceId);
    }

    [Fact]
    public async Task RejectedTagRemovesDefaultQueueButKeepsAuditEvidenceAsync()
    {
        var fixture = await PreparedSingleAsync(4);
        await fixture.Repository.UpsertMediaTagJudgmentAsync(fixture.Evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Rejected, "not ocean", CancellationToken.None);

        Assert.Empty((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        var audit = await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None);
        Assert.Single(audit.Items);
        Assert.Equal(MediaTagJudgmentDecision.Rejected, audit.Items[0].JudgmentDecision);
    }

    [Fact]
    public async Task IgnoredTagRemovesDefaultQueueButKeepsAuditEvidenceAsync()
    {
        var fixture = await PreparedSingleAsync(5);
        await fixture.Repository.UpsertMediaTagJudgmentAsync(fixture.Evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Ignored, null, CancellationToken.None);

        Assert.Empty((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.Single((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task EvidenceUpsertIsIdempotentForSameSourceDigestAsync()
    {
        var fixture = await PreparedSingleAsync(6);
        var duplicate = fixture.Evidence with { MediaTagEvidenceId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        Assert.True(await fixture.Repository.UpsertMediaTagEvidenceAsync(duplicate, CancellationToken.None));
        var current = await fixture.Repository.GetCurrentMediaTagEvidenceAsync(fixture.Media.MediaFileId, fixture.Media.MediaContentId, fixture.Evidence.TagId, fixture.Evidence.TaxonomyVersion, fixture.Evidence.TextRepresentationVersion, fixture.Evidence.AggregationVersion, fixture.Evidence.ThresholdVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, fixture.Evidence.SourceEmbeddingDigest, CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal(fixture.Evidence.MediaTagEvidenceId, current!.MediaTagEvidenceId);
    }

    [Fact]
    public async Task StaleContentHidesAiSuggestionButPreservesManualTagAsync()
    {
        var fixture = await PreparedSingleAsync(7);
        await fixture.Repository.AddManualMediaTagAsync(fixture.Media.MediaFileId, "検証済み", null, CancellationToken.None);
        await File.WriteAllBytesAsync(fixture.Media.Path, Pattern(128_000, 77));
        await RescanAsync(fixture.Repository, fixture.Root);
        var changed = (await fixture.Repository.GetMediaFileAsync(fixture.Media.MediaFileId, CancellationToken.None))!;

        Assert.NotEqual(fixture.Media.MediaContentId, changed.MediaContentId);
        Assert.Empty((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Single(await fixture.Repository.GetManualMediaTagsAsync(changed.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task ModelRevisionAndTextVersionDoNotSilentlyReuseEvidenceAsync()
    {
        var fixture = await PreparedSingleAsync(8);
        Assert.Null(await fixture.Repository.GetCurrentMediaTagEvidenceAsync(fixture.Media.MediaFileId, fixture.Media.MediaContentId, "ocean", TaggingTaxonomy.TaxonomyVersion, TaggingTaxonomy.RawTextRepresentationVersion, fixture.Evidence.AggregationVersion, fixture.Evidence.ThresholdVersion, Model, Revision, Processor, Sampling, Dtype, Dimension, fixture.Evidence.SourceEmbeddingDigest, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetCurrentMediaTagEvidenceAsync(fixture.Media.MediaFileId, fixture.Media.MediaContentId, "ocean", TaggingTaxonomy.TaxonomyVersion, fixture.Evidence.TextRepresentationVersion, fixture.Evidence.AggregationVersion, fixture.Evidence.ThresholdVersion, Model, "other-revision", Processor, Sampling, Dtype, Dimension, fixture.Evidence.SourceEmbeddingDigest, CancellationToken.None));
    }

    [Fact]
    public async Task RepresentativeEvidenceIncludesFrameAndTimestampAsync()
    {
        var fixture = await PreparedSingleAsync(9);
        var item = Assert.Single((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.NotNull(item.Evidence.RepresentativeVisualSampleId);
        Assert.NotNull(item.RepresentativeTimestampSeconds);
        Assert.NotNull(item.RepresentativeFrameRelativePath);
        Assert.Contains("Cosine similarity", item.ScoreDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedAndManualTagFilterAreSeparatedFromSuggestedFilterAsync()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(128_000, 10)), ("b.mp4", Pattern(128_000, 11)), ("c.mp4", Pattern(128_000, 12)));
        foreach (var media in fixture.Media) await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var suggested = await PersistEvidenceAsync(fixture.Repository, fixture.Media[0], "ocean");
        var confirmed = await PersistEvidenceAsync(fixture.Repository, fixture.Media[1], "ocean");
        await fixture.Repository.UpsertMediaTagJudgmentAsync(confirmed.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, null, CancellationToken.None);
        await fixture.Repository.AddManualMediaTagAsync(fixture.Media[2].MediaFileId, "海", null, CancellationToken.None);

        var humanOnly = await fixture.Repository.QueryMediaByTagAsync(fixture.Root.LibraryRootId, "ocean", false, 0, 10, CancellationToken.None);
        var withSuggested = await fixture.Repository.QueryMediaByTagAsync(fixture.Root.LibraryRootId, "ocean", true, 0, 10, CancellationToken.None);
        Assert.Equal(2, humanOnly.TotalCount);
        Assert.Equal(3, withSuggested.TotalCount);
        Assert.DoesNotContain(humanOnly.Items, item => item.MediaFileId == suggested.MediaFileId);
    }

    [Fact]
    public async Task SemanticRankingOrderIsPreservedAfterTagFilterAsync()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(128_000, 13)), ("b.mp4", Pattern(128_000, 14)), ("c.mp4", Pattern(128_000, 15)));
        foreach (var media in fixture.Media) await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var evidence = await PersistEvidenceAsync(fixture.Repository, fixture.Media[1], "ocean");
        await fixture.Repository.UpsertMediaTagJudgmentAsync(evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, null, CancellationToken.None);
        var orderedSemanticResultIds = new[] { fixture.Media[2].MediaFileId, fixture.Media[1].MediaFileId, fixture.Media[0].MediaFileId };
        var filtered = await fixture.Repository.FilterMediaIdsByTagAsync(orderedSemanticResultIds, "ocean", false, CancellationToken.None);

        Assert.Equal([fixture.Media[1].MediaFileId], filtered);
    }

    [Fact]
    public async Task BulkConfirmSafetyChangesOnlyExplicitlySelectedEvidenceAsync()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(128_000, 16)), ("b.mp4", Pattern(128_000, 17)));
        foreach (var media in fixture.Media) await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        var selected = await PersistEvidenceAsync(fixture.Repository, fixture.Media[0], "ocean");
        var unselected = await PersistEvidenceAsync(fixture.Repository, fixture.Media[1], "ocean");
        await fixture.Repository.UpsertMediaTagJudgmentAsync(selected.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, "explicit selection", CancellationToken.None);

        Assert.Equal(MediaTagJudgmentDecision.Confirmed, Assert.Single(await fixture.Repository.GetMediaTagJudgmentsAsync(selected.MediaTagEvidenceId, CancellationToken.None)).Decision);
        Assert.Empty(await fixture.Repository.GetMediaTagJudgmentsAsync(unselected.MediaTagEvidenceId, CancellationToken.None));
    }

    [Fact]
    public async Task RootOfflineDoesNotMassDeleteTagEvidenceOrManualTagAsync()
    {
        var fixture = await PreparedSingleAsync(18);
        await fixture.Repository.AddManualMediaTagAsync(fixture.Media.MediaFileId, "ローカル", null, CancellationToken.None);
        await fixture.Repository.SetRootAvailabilityAsync(fixture.Root.LibraryRootId, RootAvailability.Offline, CancellationToken.None);

        Assert.Single((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Single(await fixture.Repository.GetManualMediaTagsAsync(fixture.Media.MediaFileId, CancellationToken.None));
    }

    [Fact]
    public async Task MissingMediaIsExcludedFromCurrentTagReviewAsync()
    {
        var fixture = await PreparedSingleAsync(19);
        File.Delete(fixture.Media.Path);
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task TagTextEmbeddingCacheIsVersionedAndReusedAsync()
    {
        var fixture = await CreateFixtureAsync(("cache.mp4", Pattern(128_000, 20)));
        var first = TextEmbedding(Vector(1, 0, 0, 0));
        await fixture.Repository.UpsertTagTextEmbeddingAsync(first, CancellationToken.None);
        await fixture.Repository.UpsertTagTextEmbeddingAsync(first with { TagTextEmbeddingId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, CancellationToken.None);
        var stored = await fixture.Repository.GetTagTextEmbeddingAsync(TaggingTaxonomy.TaxonomyVersion, "ocean", TaggingTaxonomy.DescriptiveTextRepresentationVersion, Model, Revision, Processor, Dtype, Dimension, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(first.TagTextEmbeddingId, stored!.TagTextEmbeddingId);
        Assert.Equal(first.VectorFloat32, stored.VectorFloat32);
    }

    [Fact]
    public async Task TagSuggestionJobRecoversAfterRestartAsync()
    {
        var jobRecoveryRoot = Path.Combine(_root, "job-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobRecoveryRoot);
        File.WriteAllText(Path.Combine(jobRecoveryRoot, "portable.flag"), string.Empty);
        var paths = AppPaths.Create(jobRecoveryRoot);
        using var log = new JsonFileLogger(paths);
        var repository = new SqliteLibraryRepository(paths, log);
        await repository.InitializeAsync(CancellationToken.None);
        var jobs = new SqliteJobStore(paths, log);
        var job = await jobs.EnqueueUniqueAsync(JobType.TagSuggestion, new TagSuggestionPayload(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow), 2, null, CancellationToken.None);
        Assert.NotNull(await jobs.TryAcquireNextAsync(CancellationToken.None));
        await jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        var recovered = Assert.Single(await jobs.GetRecentAsync(10, CancellationToken.None));

        Assert.Equal(job.JobId, recovered.JobId);
        Assert.Equal(JobState.RetryWaiting, recovered.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovered.Reason);
    }

    [Fact]
    public async Task OriginalMediaRemainsUnchangedByTagMetadataAsync()
    {
        var fixture = await PreparedSingleAsync(21);
        var before = await SnapshotAsync(fixture.Media.Path);
        await fixture.Repository.AddManualMediaTagAsync(fixture.Media.MediaFileId, "read only", null, CancellationToken.None);
        await fixture.Repository.UpsertMediaTagJudgmentAsync(fixture.Evidence.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, null, CancellationToken.None);
        var after = await SnapshotAsync(fixture.Media.Path);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task TenThousandMediaTagFilterSanityUsesIndexedRowsAsync()
    {
        var fixture = await CreateFixtureAsync(("seed.mp4", Pattern(128_000, 22)));
        await SeedSyntheticMediaAsync(fixture.Root.LibraryRootId, 10_000, 0);
        var watch = Stopwatch.StartNew();
        var page = await fixture.Repository.QueryMediaByTagAsync(fixture.Root.LibraryRootId, "ocean", false, 0, 100, CancellationToken.None);
        watch.Stop();

        Assert.Equal(100, page.TotalCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"10k tag filter unexpectedly took {watch.Elapsed.TotalSeconds:F2}s.");
    }

    [Fact]
    public async Task HundredThousandSyntheticTagRowsLookupAndReviewSanityAsync()
    {
        var fixture = await CreateFixtureAsync(("seed.mp4", Pattern(128_000, 23)));
        await SeedSyntheticMediaAsync(fixture.Root.LibraryRootId, 1, 100_000);
        var watch = Stopwatch.StartNew();
        var page = await fixture.Repository.QueryMediaTagReviewAsync(fixture.Root.LibraryRootId, false, 0, 100, CancellationToken.None);
        watch.Stop();

        Assert.Equal(100_000, page.TotalCount);
        Assert.Equal(100, page.Items.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"100k tag review unexpectedly took {watch.Elapsed.TotalSeconds:F2}s.");
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

    [Fact]
    public void HighPrecisionProfileValuesRemainFrozenForTaggingEvaluation()
    {
        var profile = TaggingVerificationProfile.DescriptiveHighPrecisionV1;

        Assert.Equal("tagging-embedding-zero-shot-v1-high-precision", profile.ThresholdVersion);
        Assert.Equal(TaggingTaxonomy.DescriptiveTextRepresentationVersion, profile.TextRepresentationVersion);
        Assert.Equal(0.24d, profile.MinimumSampleCosine);
        Assert.Equal(0.27d, profile.MinimumMaximumCosine);
        Assert.Equal(0.18d, profile.MinimumMeanCosine);
        Assert.Equal(2, profile.MinimumMatchedSamples);
    }

    [Fact]
    public void ASingleHighScoringSampleCannotBecomeSuggestedEvidence()
    {
        var text = TextEmbedding(Vector(1, 0, 0, 0));
        var entries = SourceEntries([Vector(1, 0, 0, 0), Vector(0, 1, 0, 0)]);
        var aggregate = TaggingVectors.Aggregate(entries, 2, text, TaggingVerificationProfile.DescriptiveHighPrecisionV1);

        Assert.True(aggregate.HasCompleteCoverage);
        Assert.Equal(1, aggregate.MatchedSampleCount);
        Assert.False(TaggingVerificationProfile.DescriptiveHighPrecisionV1.IsSuggested(aggregate));
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, MediaFile Media, MediaTagEvidence Evidence)> PreparedSingleAsync(int salt)
    {
        var fixture = await CreateFixtureAsync(("prepared.mp4", Pattern(128_000, salt)));
        var media = fixture.Media[0];
        await SeedEmbeddingsAsync(fixture.Repository, media, [Vector(1, 0, 0, 0), Vector(1, 0, 0, 0), Vector(1, 0, 0, 0)]);
        return (fixture.Repository, fixture.Root, media, await PersistEvidenceAsync(fixture.Repository, media, "ocean"));
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

    private static async Task<MediaTagEvidence> PersistEvidenceAsync(SqliteLibraryRepository repository, MediaFile media, string tagId)
    {
        var source = await repository.GetMediaTagSourceEmbeddingsAsync(media.MediaFileId, media.MediaContentId, Model, Revision, Processor, Sampling, Dtype, Dimension, CancellationToken.None);
        var tag = TaggingTaxonomy.GetRequired(tagId);
        var aggregate = TaggingVectors.Aggregate(source.CompletedEmbeddings, source.ExpectedSampleCount, TextEmbedding(Vector(1, 0, 0, 0), tag), TaggingVerificationProfile.DescriptiveHighPrecisionV1);
        var status = TaggingVerificationProfile.DescriptiveHighPrecisionV1.IsSuggested(aggregate) ? MediaTagEvidenceStatus.Suggested : MediaTagEvidenceStatus.NotSuggested;
        var evidence = new MediaTagEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, aggregate.RepresentativeVisualSampleId, TaggingTaxonomy.TaxonomyVersion, tag.TagId, tag.DisplayName, tag.Group, MediaTagSource.EmbeddingZeroShot, Model, Revision, Processor, Dtype, Dimension, Sampling, TaggingTaxonomy.DescriptiveTextRepresentationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.AggregationVersion, TaggingVerificationProfile.DescriptiveHighPrecisionV1.ThresholdVersion, TaggingVectors.ScoreMetric, aggregate.MaximumScore, aggregate.MeanScore, aggregate.MatchedSampleCount, aggregate.CompletedSampleCount, aggregate.ExpectedSampleCount, aggregate.CoverageRatio, aggregate.SourceEmbeddingDigest, status, null, null, DateTimeOffset.UtcNow);
        Assert.True(await repository.UpsertMediaTagEvidenceAsync(evidence, CancellationToken.None));
        return evidence;
    }

    private static TagTextEmbedding TextEmbedding(float[] vector, TagDefinition? tag = null)
    {
        ContentSimilarityVectors.NormalizeInPlace(vector);
        var definition = tag ?? TaggingTaxonomy.GetRequired("ocean");
        var now = DateTimeOffset.UtcNow;
        return new TagTextEmbedding(Guid.NewGuid(), TaggingTaxonomy.TaxonomyVersion, definition.TagId, definition.DisplayName, definition.Group, TaggingTaxonomy.DescriptiveTextRepresentationVersion, definition.DescriptiveText, Model, Revision, Processor, Dtype, Dimension, true, ContentSimilarityVectors.Encode(vector), TagTextEmbeddingStatus.Completed, null, null, now, now);
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

    private async Task SeedSyntheticMediaAsync(Guid rootId, int mediaCount, int evidenceCount)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var seedContent = Guid.NewGuid();
        var seedMedia = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($id,NULL,1,'Synthetic',$now);", ("$id", seedContent.ToString("D")), ("$now", now));
        await ExecuteSeedAsync(connection, transaction, "INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($id,$root,$content,$path,$path,$name,'.mp4',1,$now,$now,NULL,NULL,NULL,NULL,'Present',$now,$now,NULL);", ("$id", seedMedia.ToString("D")), ("$root", rootId.ToString("D")), ("$content", seedContent.ToString("D")), ("$path", "synthetic://seed"), ("$name", "synthetic-seed.mp4"), ("$now", now));
        for (var index = 0; index < mediaCount; index++)
        {
            var content = Guid.NewGuid();
            var media = index == 0 ? seedMedia : Guid.NewGuid();
            if (index > 0)
            {
                await ExecuteSeedAsync(connection, transaction, "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($id,NULL,1,'Synthetic',$now);", ("$id", content.ToString("D")), ("$now", now));
                await ExecuteSeedAsync(connection, transaction, "INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($id,$root,$content,$path,$path,$name,'.mp4',1,$now,$now,NULL,NULL,NULL,NULL,'Present',$now,$now,NULL);", ("$id", media.ToString("D")), ("$root", rootId.ToString("D")), ("$content", content.ToString("D")), ("$path", "synthetic://" + index.ToString(CultureInfo.InvariantCulture)), ("$name", "synthetic-" + index.ToString("D5", CultureInfo.InvariantCulture) + ".mp4"), ("$now", now));
            }
            if (index % 100 == 0)
                await ExecuteSeedAsync(connection, transaction, "INSERT INTO manual_media_tag(manual_media_tag_id,media_file_id,canonical_tag,display_tag,source,note,created_at,updated_at) VALUES($id,$file,'海','海','Human',NULL,$now,$now);", ("$id", Guid.NewGuid().ToString("D")), ("$file", media.ToString("D")), ("$now", now));
        }
        for (var index = 0; index < evidenceCount; index++)
            await ExecuteSeedAsync(connection, transaction, "INSERT INTO media_tag_evidence(media_tag_evidence_id,media_file_id,media_content_id,representative_visual_sample_id,taxonomy_version,tag_id,display_name,group_name,source,model_name,model_revision,processor_version,dtype,dimension,source_sampling_version,text_representation_version,aggregation_version,threshold_version,score_metric,maximum_score,mean_score,matched_sample_count,completed_sample_count,expected_sample_count,coverage_ratio,source_embedding_digest,status,error_code,error_message,created_at) VALUES($id,$file,$content,NULL,$taxonomy,'ocean','海','Environment','EmbeddingZeroShot',$model,$revision,$processor,'float32',4,'fixed-interval-v1','tag-text-descriptive-ja-v1','media-sample-max-mean-v1','tagging-embedding-zero-shot-v1-high-precision','normalized-embedding-exact-cosine-v1',0.9,0.8,3,3,3,1.0,$digest,'Suggested',NULL,NULL,$now);", ("$id", Guid.NewGuid().ToString("D")), ("$file", seedMedia.ToString("D")), ("$content", seedContent.ToString("D")), ("$taxonomy", TaggingTaxonomy.TaxonomyVersion), ("$model", Model), ("$revision", Revision), ("$processor", Processor), ("$digest", "synthetic-" + index.ToString(CultureInfo.InvariantCulture)), ("$now", now));
        await transaction.CommitAsync();
    }

    private static async Task ExecuteSeedAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Hash, long Length, DateTime LastWriteUtc)> SnapshotAsync(string path) => (await FileHash.Sha256Async(path, CancellationToken.None), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
    private static float[] Vector(float x, float y, float z, float w) => [x, y, z, w];
    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 37 + salt) % 251)).ToArray();
}
