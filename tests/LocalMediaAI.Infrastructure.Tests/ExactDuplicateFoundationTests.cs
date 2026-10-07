using System.Diagnostics;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ExactDuplicateFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIExactDuplicateTests", Guid.NewGuid().ToString("N"));
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
    public async Task ByteIdenticalFilesBecomeOneExactGroupAfterFullSha256Verification()
    {
        var fixture = await CreateScannedFixtureAsync(("a.mp4", Pattern(220_000, 7)), ("b.mp4", Pattern(220_000, 7)));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(DuplicateRelationType.Exact, group.RelationType);
        Assert.Equal(2, group.CurrentMemberCount);
        Assert.All(await fixture.Repository.GetDuplicateComparisonMembersAsync(group.DuplicateGroupId, CancellationToken.None), member => Assert.StartsWith("SHA-256 verified:", member.FullHashStatusDisplay, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameSizeDifferentBytesIsNeverAnExactDuplicateP0()
    {
        var fixture = await CreateScannedFixtureAsync(("same-size-a.mp4", Pattern(200_000, 3)), ("same-size-b.mp4", Pattern(200_000, 211)));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        var groups = await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None);
        Assert.Empty(groups.Items);
        var first = (await fixture.Repository.GetMediaFileAsync(fixture.Media[0].MediaFileId, CancellationToken.None))!;
        var second = (await fixture.Repository.GetMediaFileAsync(fixture.Media[1].MediaFileId, CancellationToken.None))!;
        Assert.NotEqual(first.FullSha256, second.FullSha256);
    }

    [Fact]
    public async Task SameFilenameDifferentContentIsNeverAnExactDuplicate()
    {
        var fixture = await CreateScannedFixtureAsync(("one/shared.mp4", Pattern(160_000, 1)), ("two/shared.mp4", Pattern(160_000, 2)));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ReencodedRepresentationDifferentBytesIsNotExact()
    {
        var sourceLikeBytes = Pattern(180_000, 13);
        var reencodedLikeBytes = Pattern(180_000, 13);
        reencodedLikeBytes[90_000] ^= 0x7f;
        var fixture = await CreateScannedFixtureAsync(("source.mp4", sourceLikeBytes), ("reencoded.mp4", reencodedLikeBytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ThreeByteIdenticalMembersBecomeOneGroup()
    {
        var bytes = Pattern(210_000, 88);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes), ("c.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(3, group.CurrentMemberCount);
        Assert.Equal(3, (await fixture.Repository.GetDuplicateComparisonMembersAsync(group.DuplicateGroupId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task RebuildIsIdempotentForGroupAndMembers()
    {
        var bytes = Pattern(170_000, 45);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        var first = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        await fixture.Repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        await fixture.Repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        var second = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);

        Assert.Equal(first.DuplicateGroupId, second.DuplicateGroupId);
        Assert.Equal(2, second.CurrentMemberCount);
        Assert.Equal(2, (await fixture.Repository.GetDuplicateComparisonMembersAsync(second.DuplicateGroupId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task FullHashJobUsesCandidateFilteringAndPersistsEvidence()
    {
        var bytes = Pattern(250_000, 89);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes), ("noncandidate.mp4", Pattern(250_000, 90)));
        var candidates = await fixture.Repository.GetExactDuplicateHashCandidatesAsync(fixture.Root.LibraryRootId, CancellationToken.None);
        Assert.Equal(2, candidates.Count);

        var jobs = new SqliteJobStore(_paths, _log);
        using var pipeline = CreatePipeline(fixture.Repository, jobs);
        foreach (var candidate in candidates) await pipeline.EnqueueFullHashAsync(candidate, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(2, group.CurrentMemberCount);
        var persistedJobs = await jobs.GetRecentAsync(10, CancellationToken.None);
        Assert.All(persistedJobs.Where(job => job.JobType == JobType.FullHash), job =>
        {
            Assert.Equal(JobState.Cancelled, job.State);
            Assert.Equal("SupersededByProtectedOrchestration", job.Phase);
        });
        Assert.All(persistedJobs.Where(job => job.JobType == JobType.ProtectedSourceOrchestration), job => Assert.Equal(JobState.Completed, job.State));
        Assert.Null((await fixture.Repository.GetMediaFileAsync(fixture.Media[2].MediaFileId, CancellationToken.None))!.FullSha256);
    }

    [Fact]
    public async Task FullHashJobsArePayloadIdempotentWhileActive()
    {
        var bytes = Pattern(140_000, 111);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        var jobs = new SqliteJobStore(_paths, _log);
        using var pipeline = CreatePipeline(fixture.Repository, jobs);

        var first = await pipeline.EnqueueFullHashAsync(fixture.Media[0], CancellationToken.None);
        var second = await pipeline.EnqueueFullHashAsync(fixture.Media[0], CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Single(await jobs.GetRecentAsync(10, CancellationToken.None), job => job.JobType == JobType.FullHash);
    }

    [Fact]
    public async Task InterruptedFullHashJobRecoversAndReusesCompletedEvidence()
    {
        var bytes = Pattern(130_000, 22);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        var jobs = new SqliteJobStore(_paths, _log);
        using var pipeline = CreatePipeline(fixture.Repository, jobs);
        foreach (var candidate in await fixture.Repository.GetExactDuplicateHashCandidatesAsync(fixture.Root.LibraryRootId, CancellationToken.None))
            await pipeline.EnqueueFullHashAsync(candidate, CancellationToken.None);

        Assert.NotNull(await jobs.TryAcquireNextAsync(CancellationToken.None));
        await jobs.RecoverInterruptedJobsAsync(CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);

        Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        Assert.DoesNotContain(await jobs.GetRecentAsync(10, CancellationToken.None), job => job.JobType == JobType.FullHash && job.State == JobState.Running);
    }

    [Fact]
    public async Task HashJobCanBePersistedAsCancelledWithoutWritingOrGrouping()
    {
        var bytes = Pattern(120_000, 4);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        var jobs = new SqliteJobStore(_paths, _log);
        var payload = new FullHashPayload(fixture.Media[0].MediaFileId, fixture.Media[0].MediaContentId, fixture.Media[0].SizeBytes, fixture.Media[0].LastWriteTimeUtc);
        var job = await jobs.EnqueueAsync(JobType.FullHash, payload, 2, null, CancellationToken.None);
        Assert.NotNull(await jobs.TryAcquireNextAsync(CancellationToken.None));
        await jobs.CompleteAsync(job.JobId, JobState.Cancelled, "Cancelled", CancellationToken.None);

        Assert.Equal(JobState.Cancelled, (await jobs.GetRecentAsync(10, CancellationToken.None)).Single(item => item.JobId == job.JobId).State);
        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ChangedFileInvalidatesActiveExactGroupUntilReverified()
    {
        var bytes = Pattern(150_000, 19);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);

        var changedPath = fixture.Media[1].Path;
        await File.WriteAllBytesAsync(changedPath, Pattern(150_000, 20));
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(5));
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Null((await fixture.Repository.GetMediaFileAsync(fixture.Media[1].MediaFileId, CancellationToken.None))!.FullSha256);
    }

    [Fact]
    public async Task SameSizeSameMtimeDifferentBytesInvalidatesExactEvidenceP0()
    {
        var bytes = Pattern(150_000, 25);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);

        var changedPath = fixture.Media[1].Path;
        var originalMtime = File.GetLastWriteTimeUtc(changedPath);
        var changed = Pattern(150_000, 25);
        changed[0] ^= 0x5a; // Head sample: Quick Fingerprint must detect content change despite retained stat fields.
        await File.WriteAllBytesAsync(changedPath, changed);
        File.SetLastWriteTimeUtc(changedPath, originalMtime);
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(changedPath));

        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        var current = (await fixture.Repository.GetMediaFileAsync(fixture.Media[1].MediaFileId, CancellationToken.None))!;
        Assert.NotEqual(fixture.Media[1].MediaContentId, current.MediaContentId);
        Assert.Null(current.FullSha256);
    }

    [Fact]
    public async Task OfflineRootKeepsExistingExactRelationAndDoesNotDeleteGroup()
    {
        var bytes = Pattern(150_000, 31);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        var expected = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items).DuplicateGroupId;

        await fixture.Repository.SetRootAvailabilityAsync(fixture.Root.LibraryRootId, RootAvailability.Offline, CancellationToken.None);
        var actual = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(expected, actual.DuplicateGroupId);
    }

    [Fact]
    public async Task MissingMemberIsSafelyExcludedFromCurrentExactGroup()
    {
        var bytes = Pattern(155_000, 33);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        File.Delete(fixture.Media[1].Path);
        await RescanAsync(fixture.Repository, fixture.Root);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.True(await fixture.Repository.IntegrityCheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HumanJudgmentPersistsAcrossGroupRebuild()
    {
        var bytes = Pattern(165_000, 71);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);

        var written = await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, DuplicateJudgmentDecision.KeepBoth, "Human reviewed both storage locations.", CancellationToken.None);
        await fixture.Repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        var persisted = Assert.Single(await fixture.Repository.GetDuplicateJudgmentsAsync(group.DuplicateGroupId, CancellationToken.None));

        Assert.Equal(written.DuplicateJudgmentId, persisted.DuplicateJudgmentId);
        Assert.Equal("Human", persisted.Origin);
        Assert.Equal(DuplicateJudgmentDecision.KeepBoth, persisted.Decision);
    }

    [Fact]
    public async Task IgnorePersistsButDoesNotDeleteUnderlyingExactRelation()
    {
        var bytes = Pattern(165_000, 72);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, DuplicateJudgmentDecision.Ignored, "Not actionable now.", CancellationToken.None);

        Assert.Empty((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items);
        var retained = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        Assert.Equal(group.DuplicateGroupId, retained.DuplicateGroupId);
        Assert.Equal(DuplicateJudgmentDecision.Ignored, retained.ReviewDecision);
    }

    [Fact]
    public async Task PreferredMemberIsHumanOnlyAndDoesNotChangeOtherMedia()
    {
        var bytes = Pattern(166_000, 73);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes), ("c.mp4", bytes));
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);
        var group = Assert.Single((await fixture.Repository.QueryDuplicateGroupsAsync(fixture.Root.LibraryRootId, true, 0, 10, CancellationToken.None)).Items);
        var originalPaths = fixture.Media.Select(item => item.Path).ToArray();

        await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, fixture.Media[1].MediaFileId, DuplicateJudgmentDecision.Preferred, "Higher-quality storage location selected by human.", CancellationToken.None);
        await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, fixture.Media[2].MediaFileId, DuplicateJudgmentDecision.Preferred, null, CancellationToken.None);
        var judgments = await fixture.Repository.GetDuplicateJudgmentsAsync(group.DuplicateGroupId, CancellationToken.None);

        Assert.Single(judgments, item => item.Decision == DuplicateJudgmentDecision.Preferred);
        Assert.All(originalPaths, path => Assert.True(File.Exists(path)));
        Assert.All(judgments, item => Assert.Equal("Human", item.Origin));
    }

    [Fact]
    public async Task HashingIsReadOnlyForOriginalMedia()
    {
        var bytes = Pattern(190_000, 144);
        var fixture = await CreateScannedFixtureAsync(("a.mp4", bytes), ("b.mp4", bytes));
        var before = fixture.Media.Select(media => new FileSnapshot(media.Path, FileHash.Sha256Async(media.Path, CancellationToken.None).GetAwaiter().GetResult(), new FileInfo(media.Path).Length, File.GetLastWriteTimeUtc(media.Path))).ToArray();
        await HashAndRebuildAsync(fixture.Repository, fixture.Media);

        foreach (var snapshot in before)
        {
            Assert.Equal(snapshot.Digest, await FileHash.Sha256Async(snapshot.Path, CancellationToken.None));
            Assert.Equal(snapshot.SizeBytes, new FileInfo(snapshot.Path).Length);
            Assert.Equal(snapshot.LastWriteTimeUtc, File.GetLastWriteTimeUtc(snapshot.Path));
        }
    }

    [Fact]
    public async Task TenThousandDistinctRowsProduceNoPairwiseHashCandidates()
    {
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var mediaDirectory = Path.Combine(_root, "ten-thousand");
        Directory.CreateDirectory(mediaDirectory);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        await SeedDistinctMediaRowsAsync(root);

        var stopwatch = Stopwatch.StartNew();
        var candidates = await repository.GetExactDuplicateHashCandidatesAsync(root.LibraryRootId, CancellationToken.None);
        stopwatch.Stop();

        Assert.Empty(candidates);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Candidate grouping unexpectedly took {stopwatch.Elapsed.TotalSeconds:F2}s for 10,000 rows.");
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, IReadOnlyList<MediaFile> Media)> CreateScannedFixtureAsync(params (string RelativePath, byte[] Bytes)[] files)
    {
        var mediaDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mediaDirectory);
        foreach (var (relativePath, bytes) in files)
        {
            var path = Path.Combine(mediaDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        await RescanAsync(repository, root);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
        var media = new List<MediaFile>();
        foreach (var item in page.Items) media.Add((await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!);
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

    private static async Task HashAndRebuildAsync(SqliteLibraryRepository repository, IReadOnlyList<MediaFile> media)
    {
        foreach (var item in media)
        {
            var digest = await FileHash.Sha256Async(item.Path, CancellationToken.None);
            Assert.True(await repository.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), item.MediaFileId, item.MediaContentId, "SHA-256", digest, item.SizeBytes, item.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None));
        }
        await repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
    }

    private MediaPipeline CreatePipeline(SqliteLibraryRepository repository, SqliteJobStore jobs) => new(repository, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log);

    private async Task SeedDistinctMediaRowsAsync(LibraryRoot root)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite }.ToString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($content,NULL,1,'Unverified',$now); INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($file,$root,$content,$path,$normalized,$name,'.mp4',1,$now,$now,NULL,NULL,$fingerprint,NULL,'Present',$now,$now,NULL);";
        command.Parameters.Add("$content", SqliteType.Text);
        command.Parameters.Add("$file", SqliteType.Text);
        command.Parameters.AddWithValue("$root", root.LibraryRootId.ToString("D"));
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$normalized", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$fingerprint", SqliteType.Text);
        command.Parameters.AddWithValue("$now", now);
        for (var index = 0; index < 10_000; index++)
        {
            var contentId = Guid.NewGuid().ToString("D");
            var path = Path.Combine(root.ConfiguredPath, $"unique-{index:D5}.mp4");
            command.Parameters["$content"].Value = contentId;
            command.Parameters["$file"].Value = Guid.NewGuid().ToString("D");
            command.Parameters["$path"].Value = path;
            command.Parameters["$normalized"].Value = PathPolicy.NormalizeForIdentity(path);
            command.Parameters["$name"].Value = Path.GetFileName(path);
            command.Parameters["$fingerprint"].Value = $"qf-v1-{index:D5}";
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private sealed record FileSnapshot(string Path, string Digest, long SizeBytes, DateTime LastWriteTimeUtc);
    private static byte[] Pattern(int size, int salt) => Enumerable.Range(0, size).Select(index => (byte)((index * 31 + salt) % 251)).ToArray();
}
