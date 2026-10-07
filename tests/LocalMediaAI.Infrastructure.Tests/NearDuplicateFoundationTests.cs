using System.Diagnostics;
using System.Globalization;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class NearDuplicateFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAINearDuplicateTests", Guid.NewGuid().ToString("N"));
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
    public void PerceptualHashIsStableForIdenticalRaster()
    {
        var raster = GradientRaster();
        Assert.Equal(PerceptualFrameFingerprint.ComputeDHashHex(raster), PerceptualFrameFingerprint.ComputeDHashHex(raster));
    }

    [Fact]
    public void PerceptualHashHammingDistanceExplainsSingleTransition()
    {
        Assert.Equal(1, PerceptualFrameFingerprint.HammingDistance("0000000000000000", "0000000000000001"));
        Assert.Equal(64, PerceptualFrameFingerprint.HammingDistance("0000000000000000", "ffffffffffffffff"));
    }

    [Fact]
    public void PerceptualHashLshBandsAreBoundedAndDeterministic()
    {
        var bands = PerceptualFrameFingerprint.CreateLshBands("0011223344556677");
        Assert.Equal(4, bands.Count);
        Assert.All(bands, band => Assert.Equal(4, band.Length));
        Assert.Equal(bands, PerceptualFrameFingerprint.CreateLshBands("0011223344556677"));
    }

    [Fact]
    public void NormalizedSamplingHasFiveOrderedPositions()
    {
        var samples = NearDuplicateSamplingPolicy.CreateTimestamps(100d);
        Assert.Equal(5, samples.Count);
        Assert.Equal([5d, 25d, 50d, 75d, 95d], samples.Select(item => item.RequestedTimestampSeconds));
    }

    [Fact]
    public void HighPrecisionProfileAcceptsReencodedVisualPattern()
    {
        Assert.True(Verify([1, 2, 3, 2, 1]).IsVerified);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsResolutionChangedVisualPattern()
    {
        Assert.True(Verify([2, 3, 4, 3, 2]).IsVerified);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsBitrateChangedVisualPattern()
    {
        Assert.True(Verify([0, 1, 2, 1, 0]).IsVerified);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsContainerChangedVisualPattern()
    {
        Assert.True(Verify([4, 5, 4, 5, 4]).IsVerified);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsAudioTrackChangedVisualPattern()
    {
        Assert.True(Verify([3, 3, 4, 3, 3]).IsVerified);
    }

    [Fact]
    public void HighPrecisionProfileAcceptsSubtitleChangedVisualPattern()
    {
        Assert.True(Verify([5, 4, 4, 5, 4]).IsVerified);
    }

    [Fact]
    public void SameDurationDifferentContentIsRejected()
    {
        var result = Verify([20, 21, 22, 20, 21]);
        Assert.False(result.IsVerified);
        Assert.Equal("Rejected", result.Status);
    }

    [Fact]
    public void SimilarOpeningDifferentRemainderIsRejected()
    {
        var result = Verify([0, 1, 20, 20, 20]);
        Assert.False(result.IsVerified);
        Assert.Equal(2, result.MatchedSampleCount);
    }

    [Fact]
    public void DurationGateRejectsOtherwiseMatchingFrames()
    {
        var profile = NearDuplicateVerificationProfile.HighPrecisionV1;
        var result = profile.Verify(100d, 106d, [
            (0, 0), (1, 1), (2, 1), (3, 0), (4, 1)]);
        Assert.False(result.IsVerified);
        Assert.Equal("InsufficientEvidence", result.Status);
    }

    [Fact]
    public void InsufficientSampleCoverageDoesNotVerify()
    {
        var result = NearDuplicateVerificationProfile.HighPrecisionV1.Verify(100d, 100d, [(0, 0), (1, 1), (2, 1), (3, 0)]);
        Assert.False(result.IsVerified);
        Assert.Equal("InsufficientEvidence", result.Status);
    }

    [Fact]
    public async Task NearFingerprintRunningJobRecoversAfterRestart()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var store = new SqliteJobStore(_paths, _log);
        var payload = new NearDuplicateFingerprintPayload(Guid.NewGuid(), Guid.NewGuid(), 123L, DateTimeOffset.UtcNow);
        var enqueued = await store.EnqueueUniqueAsync(JobType.NearDuplicateFingerprint, payload, 2, null, CancellationToken.None);
        var acquired = await store.TryAcquireNextAsync(CancellationToken.None);
        Assert.Equal(enqueued.JobId, acquired?.JobId);

        await store.RecoverInterruptedJobsAsync(CancellationToken.None);

        var recovered = Assert.Single(await store.GetRecentAsync(10, CancellationToken.None), job => job.JobId == enqueued.JobId);
        Assert.Equal(JobState.RetryWaiting, recovered.State);
        Assert.Equal(JobReason.AppRestartRecovery, recovered.Reason);
    }

    [Fact]
    public async Task NearEvidenceAndGroupPersistIdempotently()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 1)), ("b.mkv", Pattern(120_000, 2)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], ["0000000000000000", "0000000000000001", "0000000000000003", "0000000000000007", "000000000000000f"]);
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], ["0000000000000000", "0000000000000001", "0000000000000003", "0000000000000007", "000000000000000f"]);

        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);

        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(DuplicateRelationType.NearDuplicate, group.RelationType);
        Assert.Equal(2, group.CurrentMemberCount);
        Assert.Single(await fixture.Repository.GetNearDuplicateEvidenceForGroupAsync(group.DuplicateGroupId, CancellationToken.None));
        Assert.Equal(5, (await fixture.Repository.GetNearDuplicateFrameMatchesAsync(evidence.NearDuplicateEvidenceId, CancellationToken.None)).Count);

        var otherDirectory = Path.Combine(_root, "scope-other-root");
        Directory.CreateDirectory(otherDirectory);
        var otherRoot = await fixture.Repository.AddOrGetLibraryRootAsync(otherDirectory, CancellationToken.None);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_file SET library_root_id=$root WHERE media_file_id=$media;";
            command.Parameters.AddWithValue("$root", otherRoot.LibraryRootId.ToString("D"));
            command.Parameters.AddWithValue("$media", fixture.Media[1].MediaFileId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(2, Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(null, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items).CurrentMemberCount);
        Assert.Empty((await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.NearDuplicate), CancellationToken.None)).Items);
        Assert.Equal(2, Assert.Single((await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(null, UnifiedReviewFilter.NearDuplicate), CancellationToken.None)).Items).InvolvedMediaCount);
    }

    [Fact]
    public async Task ExactHashTakesPrecedenceOverNearCandidate()
    {
        var bytes = Pattern(120_000, 3);
        var fixture = await CreateFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        foreach (var media in fixture.Media)
        {
            var digest = await FileHash.Sha256Async(media.Path, CancellationToken.None);
            Assert.True(await fixture.Repository.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, media.SizeBytes, media.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None));
        }
        await fixture.Repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));

        Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Empty(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
    }

    [Fact]
    public async Task FingerprintVersionChangeDoesNotSilentlyReuseOldFrames()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 4)), ("b.mp4", Pattern(120_000, 5)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));

        var versionTwoCandidates = await fixture.Repository.GetNearDuplicateFingerprintCandidatesAsync(fixture.Root.LibraryRootId, "near-duplicate-normalized-v2", PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        Assert.Equal(2, versionTwoCandidates.Count);
        Assert.Equal(5, (await fixture.Repository.GetCurrentNearDuplicateFramesAsync(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task PartialFrameFailureDoesNotCreateNearRelation()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 6)), ("b.mp4", Pattern(120_000, 7)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0), failedIndex: 4);

        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        var framesA = await fixture.Repository.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileAId, candidate.MediaContentAId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        var framesB = await fixture.Repository.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileBId, candidate.MediaContentBId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        var result = NearDuplicateVerificationProfile.HighPrecisionV1.Verify(candidate.DurationASeconds, candidate.DurationBSeconds, framesA.Join(framesB, first => first.SampleIndex, second => second.SampleIndex, (first, second) => (first.SampleIndex, PerceptualFrameFingerprint.HammingDistance(first.FingerprintHex!, second.FingerprintHex!))).ToArray());
        Assert.False(result.IsVerified);
        Assert.Equal("InsufficientEvidence", result.Status);
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task InsufficientEvidencePersistsWithoutCreatingNearGroup()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 8)), ("b.mp4", Pattern(120_000, 9)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));
        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        var evidence = await PersistEvidenceAsync(fixture.Repository, candidate, "InsufficientEvidence", compared: 4, matched: 0, longest: 0, mean: 0d, max: 0);

        Assert.NotNull(await fixture.Repository.GetNearDuplicateEvidenceAsync(evidence.MediaFileAId, evidence.MediaContentAId, evidence.MediaFileBId, evidence.MediaContentBId, evidence.SamplingVersion, evidence.EvidenceAlgorithm, evidence.ThresholdVersion, CancellationToken.None));
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task HumanNotDuplicatePersistsWithoutDeletingNearEvidence()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 10)), ("b.mp4", Pattern(120_000, 11)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));
        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);
        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);

        var judgment = await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, DuplicateJudgmentDecision.NotDuplicate, "Human rejected candidate.", CancellationToken.None);
        Assert.Equal(DuplicateJudgmentDecision.NotDuplicate, judgment.Decision);
        Assert.Single(await fixture.Repository.GetNearDuplicateEvidenceForGroupAsync(group.DuplicateGroupId, CancellationToken.None));
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, false, 0, 10, CancellationToken.None)).Items);
        Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task PreferredNearMemberPersistsWithoutMediaMutation()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 12)), ("b.mp4", Pattern(120_000, 13)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));
        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);
        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        var paths = fixture.Media.Select(item => item.Path).ToArray();

        await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, fixture.Media[0].MediaFileId, DuplicateJudgmentDecision.Preferred, "Human preferred source.", CancellationToken.None);
        Assert.Single(await fixture.Repository.GetDuplicateJudgmentsAsync(group.DuplicateGroupId, CancellationToken.None), item => item.Decision == DuplicateJudgmentDecision.Preferred);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task OfflineRootKeepsNearEvidenceAndGroup()
    {
        var fixture = await CreateVerifiedNearFixtureAsync(14);
        var expected = Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items).DuplicateGroupId;
        await fixture.Repository.SetRootAvailabilityAsync(fixture.Root.LibraryRootId, RootAvailability.Offline, CancellationToken.None);
        Assert.Equal(expected, Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items).DuplicateGroupId);
    }

    [Fact]
    public async Task MissingMemberExcludesCurrentNearGroup()
    {
        var fixture = await CreateVerifiedNearFixtureAsync(15);
        File.Delete(fixture.Media[1].Path);
        await RescanAsync(fixture.Repository, fixture.Root);
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task SourceChangeMakesOldNearEvidenceNonCurrent()
    {
        var fixture = await CreateVerifiedNearFixtureAsync(16);
        var beforeGroup = Assert.Single((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        var changed = Pattern(120_000, 99);
        await File.WriteAllBytesAsync(fixture.Media[1].Path, changed);
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items);
        Assert.NotEmpty(await fixture.Repository.GetNearDuplicateEvidenceForGroupAsync(beforeGroup.DuplicateGroupId, CancellationToken.None));
    }

    [Fact]
    public async Task NearFingerprintingFixturesLeaveOriginalMediaUnchanged()
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, 16)), ("b.mp4", Pattern(120_000, 17)));
        var snapshots = fixture.Media.Select(media => new Snapshot(media.Path, FileHash.Sha256Async(media.Path, CancellationToken.None).GetAwaiter().GetResult(), new FileInfo(media.Path).Length, File.GetLastWriteTimeUtc(media.Path))).ToArray();
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));

        foreach (var snapshot in snapshots)
        {
            Assert.Equal(snapshot.Hash, await FileHash.Sha256Async(snapshot.Path, CancellationToken.None));
            Assert.Equal(snapshot.Size, new FileInfo(snapshot.Path).Length);
            Assert.Equal(snapshot.Mtime, File.GetLastWriteTimeUtc(snapshot.Path));
        }
    }

    [Fact]
    public async Task TenThousandDistinctFingerprintRowsProduceNoCandidates()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var directory = Path.Combine(_root, "ten-thousand");
        Directory.CreateDirectory(directory);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        await SeedDistinctNearRowsAsync(root);
        var watch = Stopwatch.StartNew();
        var candidates = await repository.GetNearDuplicateCandidatesAsync(root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        watch.Stop();
        Assert.Empty(candidates);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Candidate blocking unexpectedly took {watch.Elapsed.TotalSeconds:F2}s for 10,000 rows.");
    }

    private static NearDuplicateVerificationResult Verify(IReadOnlyList<int> distances) => NearDuplicateVerificationProfile.HighPrecisionV1.Verify(100d, 100d, distances.Select((distance, index) => (index, distance)).ToArray());

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, IReadOnlyList<MediaFile> Media)> CreateFixtureAsync(params (string RelativePath, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var (relativePath, bytes) in files)
        {
            var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        await RescanAsync(repository, root);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
        var media = new List<MediaFile>();
        foreach (var item in page.Items)
        {
            var value = (await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!;
            await repository.UpsertMetadataAsync(new MediaMetadata(value.MediaFileId, value.Extension.TrimStart('.'), 100d, 1920, 1080, "h264", "30/1", 1_000_000, 1, 0, "{}", DateTimeOffset.UtcNow), CancellationToken.None);
            media.Add(value);
        }
        return (repository, root, media.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, IReadOnlyList<MediaFile> Media)> CreateVerifiedNearFixtureAsync(int salt)
    {
        var fixture = await CreateFixtureAsync(("a.mp4", Pattern(120_000, salt)), ("b.mkv", Pattern(120_000, salt + 1)));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[0], FiveHashes(0));
        await PersistCurrentFramesAsync(fixture.Repository, fixture.Media[1], FiveHashes(0));
        var candidate = Assert.Single(await fixture.Repository.GetNearDuplicateCandidatesAsync(fixture.Root.LibraryRootId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None));
        await PersistEvidenceAsync(fixture.Repository, candidate, "Verified");
        await fixture.Repository.RebuildNearDuplicateGroupsAsync(CancellationToken.None);
        return fixture;
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

    private static async Task PersistCurrentFramesAsync(SqliteLibraryRepository repository, MediaFile media, string[] hashes, int? failedIndex = null)
    {
        var frames = NearDuplicateSamplingPolicy.CreateTimestamps(100d).Select(timestamp => new NearDuplicateFrame(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc, timestamp.SampleIndex, timestamp.NormalizedPosition, timestamp.RequestedTimestampSeconds, timestamp.RequestedTimestampSeconds, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, failedIndex == timestamp.SampleIndex ? null : hashes[timestamp.SampleIndex], failedIndex == timestamp.SampleIndex ? null : $"frames/{media.MediaFileId:N}-{timestamp.SampleIndex}.jpg", failedIndex == timestamp.SampleIndex ? null : "00", failedIndex == timestamp.SampleIndex ? null : 1, failedIndex == timestamp.SampleIndex ? AnalysisStatus.Failed : AnalysisStatus.Completed, failedIndex == timestamp.SampleIndex ? "FRAME_EXTRACTION_FAILED" : null, failedIndex == timestamp.SampleIndex ? "fixture failure" : null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        await repository.ReplaceNearDuplicateFramesAsync(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, frames, CancellationToken.None);
    }

    private static async Task<NearDuplicateEvidence> PersistEvidenceAsync(SqliteLibraryRepository repository, NearDuplicateCandidate candidate, string status, int? compared = null, int? matched = null, int? longest = null, double? mean = null, int? max = null)
    {
        var framesA = await repository.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileAId, candidate.MediaContentAId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        var framesB = await repository.GetCurrentNearDuplicateFramesAsync(candidate.MediaFileBId, candidate.MediaContentBId, NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, CancellationToken.None);
        var ordered = framesA.Join(framesB, first => first.SampleIndex, second => second.SampleIndex, (first, second) => (first, second)).OrderBy(item => item.first.SampleIndex).ToArray();
        var result = NearDuplicateVerificationProfile.HighPrecisionV1.Verify(candidate.DurationASeconds, candidate.DurationBSeconds, ordered.Select(item => (item.first.SampleIndex, PerceptualFrameFingerprint.HammingDistance(item.first.FingerprintHex!, item.second.FingerprintHex!))).ToArray());
        var evidenceId = Guid.NewGuid();
        var evidence = new NearDuplicateEvidence(evidenceId, candidate.MediaFileAId, candidate.MediaContentAId, candidate.MediaFileBId, candidate.MediaContentBId, PerceptualFrameFingerprint.Algorithm, NearDuplicateSamplingPolicy.Version, NearDuplicateVerificationProfile.HighPrecisionV1.ThresholdVersion, AnalysisKey.Sha256(evidenceId.ToString("D")), candidate.DurationASeconds, candidate.DurationBSeconds, result.DurationDifferenceRatio, compared ?? result.ComparedSampleCount, matched ?? result.MatchedSampleCount, longest ?? result.LongestConsecutiveMatchCount, mean ?? result.MeanHammingDistance, max ?? result.MaxHammingDistance, result.VisualMatchScore, status, null, null, DateTimeOffset.UtcNow);
        var matches = ordered.Select(item => new NearDuplicateFrameMatch(evidenceId, item.first.SampleIndex, item.first.NearDuplicateFrameId, item.second.NearDuplicateFrameId, PerceptualFrameFingerprint.HammingDistance(item.first.FingerprintHex!, item.second.FingerprintHex!), true)).ToArray();
        Assert.True(await repository.UpsertNearDuplicateEvidenceAsync(evidence, matches, CancellationToken.None));
        return evidence;
    }

    private async Task SeedDistinctNearRowsAsync(LibraryRoot root)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        for (var index = 0; index < 10_000; index++)
        {
            var contentId = Guid.NewGuid().ToString("D");
            var fileId = Guid.NewGuid().ToString("D");
            var frameId = Guid.NewGuid().ToString("D");
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($content,NULL,1,'Provisional',$now); INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($file,$root,$content,$path,$path,$name,'.mp4',1,$now,$now,NULL,NULL,$fingerprint,NULL,'Present',$now,$now,NULL); INSERT INTO media_metadata(media_file_id,container_format,duration_seconds,width,height,video_codec,fps_text,bitrate,audio_stream_count,subtitle_stream_count,raw_summary_json,updated_at) VALUES($file,'mp4',100,1920,1080,'h264','30/1',1000000,1,0,'{}',$now); INSERT INTO near_duplicate_frame(near_duplicate_frame_id,media_file_id,media_content_id,source_size_bytes,source_last_write_time_utc,sample_index,normalized_position,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,fingerprint_algorithm,fingerprint_hex,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($frame,$file,$content,1,$now,0,0.05,5,5,$sampling,$algorithm,$hash,'frames/x.jpg','00',1,'Completed',NULL,NULL,$now,$now); INSERT INTO near_duplicate_lsh_bucket(near_duplicate_frame_id,sampling_version,fingerprint_algorithm,sample_index,band_index,band_value) VALUES($frame,$sampling,$algorithm,0,0,$band);";
            command.Parameters.AddWithValue("$content", contentId);
            command.Parameters.AddWithValue("$file", fileId);
            command.Parameters.AddWithValue("$frame", frameId);
            command.Parameters.AddWithValue("$root", root.LibraryRootId.ToString("D"));
            command.Parameters.AddWithValue("$path", Path.Combine(root.ConfiguredPath, $"unique-{index:D5}.mp4"));
            command.Parameters.AddWithValue("$name", $"unique-{index:D5}.mp4");
            command.Parameters.AddWithValue("$fingerprint", $"qf-{index:D5}");
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$sampling", NearDuplicateSamplingPolicy.Version);
            command.Parameters.AddWithValue("$algorithm", PerceptualFrameFingerprint.Algorithm);
            command.Parameters.AddWithValue("$hash", index.ToString("x16", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$band", index.ToString("x4", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static byte[] GradientRaster() => Enumerable.Range(0, PerceptualFrameFingerprint.InputByteCount).Select(index => (byte)(index % PerceptualFrameFingerprint.Width * 20)).ToArray();
    private static string[] FiveHashes(int offset) => Enumerable.Range(0, 5).Select(index => (offset + index).ToString("x16", CultureInfo.InvariantCulture)).ToArray();
    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 31 + salt) % 251)).ToArray();
    private sealed record Snapshot(string Path, string Hash, long Size, DateTime Mtime);
}
